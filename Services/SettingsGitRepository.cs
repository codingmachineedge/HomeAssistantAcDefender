using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class SettingsGitRepository
{
    private const string SettingsFileName = "settings.json";
    private const string ReadmeFileName = "README.md";
    private readonly object gate = new();
    private readonly bool enabled;
    private readonly ILogger<SettingsGitRepository> logger;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string repositoryPath;

    public SettingsGitRepository(IOptions<DefenderOptions> options, IWebHostEnvironment environment, ILogger<SettingsGitRepository> logger)
    {
        this.logger = logger;
        enabled = options.Value.SettingsRepositoryEnabled;

        var statePath = ResolvePath(options.Value.StateFilePath, environment.ContentRootPath);
        var defaultPath = Path.Combine(Path.GetDirectoryName(statePath) ?? environment.ContentRootPath, "settings-repo");
        repositoryPath = ResolvePath(
            string.IsNullOrWhiteSpace(options.Value.SettingsRepositoryPath)
                ? defaultPath
                : options.Value.SettingsRepositoryPath,
            environment.ContentRootPath);
    }

    public string RepositoryPath => repositoryPath;

    public SettingsRepositoryActionResult CommitSnapshot(SettingsRepositorySnapshot snapshot, string reason)
    {
        lock (gate)
        {
            try
            {
                if (!enabled)
                {
                    return new SettingsRepositoryActionResult(true, "Settings repository is disabled.");
                }

                var readmeContent = ReadmeContent();
                var settingsPath = Path.Combine(repositoryPath, SettingsFileName);
                var readmePath = Path.Combine(repositoryPath, ReadmeFileName);
                var nextJson = JsonSerializer.Serialize(snapshot, jsonOptions) + Environment.NewLine;
                var currentJson = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
                var currentReadme = File.Exists(readmePath) ? File.ReadAllText(readmePath) : "";
                var repositoryAlreadyInitialized = Directory.Exists(Path.Combine(repositoryPath, ".git"));
                if (repositoryAlreadyInitialized
                    && string.Equals(currentJson, nextJson, StringComparison.Ordinal)
                    && string.Equals(currentReadme, readmeContent, StringComparison.Ordinal))
                {
                    return new SettingsRepositoryActionResult(true, "Settings repository already has the latest snapshot.");
                }

                EnsureRepository();
                if (!string.Equals(currentJson, nextJson, StringComparison.Ordinal))
                {
                    WriteAtomic(settingsPath, nextJson);
                }

                if (!string.Equals(currentReadme, readmeContent, StringComparison.Ordinal))
                {
                    WriteAtomic(readmePath, readmeContent);
                }

                RunGit("add", SettingsFileName, ReadmeFileName);
                var diff = RunGitAllowExit([0, 1], "diff", "--cached", "--quiet", "--", SettingsFileName, ReadmeFileName);
                if (diff.ExitCode == 0)
                {
                    return new SettingsRepositoryActionResult(true, "Settings repository already has the latest snapshot.");
                }

                var message = string.IsNullOrWhiteSpace(reason)
                    ? "Save AC Defender settings"
                    : SanitizeCommitMessage(reason);
                RunGit("commit", "-m", message);
                return new SettingsRepositoryActionResult(true, $"Committed settings snapshot: {message}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not commit AC Defender settings to {RepositoryPath}", repositoryPath);
                return new SettingsRepositoryActionResult(false, ex.Message);
            }
        }
    }

    public SettingsRepositoryState GetState()
    {
        lock (gate)
        {
            try
            {
                if (!enabled)
                {
                    return new SettingsRepositoryState(
                        repositoryPath,
                        false,
                        false,
                        true,
                        "",
                        "",
                        "Settings repository is disabled.",
                        null,
                        [],
                        []);
                }

                var initialized = Directory.Exists(Path.Combine(repositoryPath, ".git"));
                if (!initialized)
                {
                    return new SettingsRepositoryState(
                        repositoryPath,
                        true,
                        false,
                        true,
                        "",
                        "",
                        "Settings repository has not been initialized yet. Save settings once to create it.",
                        null,
                        [],
                        []);
                }

                var branch = RunGitAllowExit([0], "branch", "--show-current").StdOut.Trim();
                var headResult = RunGitAllowExit([0, 128], "rev-parse", "--short", "HEAD");
                var head = headResult.ExitCode == 0 ? headResult.StdOut.Trim() : "";
                var statusText = RunGitAllowExit([0], "status", "--short").StdOut;
                var files = ParseStatus(statusText);
                var history = ReadHistory(50);
                var clean = files.Count == 0;

                return new SettingsRepositoryState(
                    repositoryPath,
                    true,
                    true,
                    clean,
                    branch,
                    head,
                    clean ? "Clean" : "Pending file changes",
                    null,
                    history,
                    files);
            }
            catch (Exception ex)
            {
                return new SettingsRepositoryState(
                    repositoryPath,
                    false,
                    false,
                    false,
                    "",
                    "",
                    "Git is unavailable for the settings repository.",
                    ex.Message,
                    [],
                    []);
            }
        }
    }

    public SettingsRepositoryActionResult TryReadCurrentSnapshot()
    {
        lock (gate)
        {
            try
            {
                if (!enabled)
                {
                    return new SettingsRepositoryActionResult(false, "Settings repository is disabled.");
                }

                var path = Path.Combine(repositoryPath, SettingsFileName);
                if (!File.Exists(path))
                {
                    return new SettingsRepositoryActionResult(false, "No settings snapshot exists yet.");
                }

                var snapshot = JsonSerializer.Deserialize<SettingsRepositorySnapshot>(File.ReadAllText(path), jsonOptions);
                return snapshot is null
                    ? new SettingsRepositoryActionResult(false, "The settings snapshot is empty or invalid.")
                    : new SettingsRepositoryActionResult(true, "Loaded settings snapshot.", snapshot);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read AC Defender settings snapshot from {RepositoryPath}", repositoryPath);
                return new SettingsRepositoryActionResult(false, ex.Message);
            }
        }
    }

    public SettingsRepositoryActionResult RevertLastCommit()
    {
        lock (gate)
        {
            try
            {
                if (!enabled)
                {
                    return new SettingsRepositoryActionResult(false, "Settings repository is disabled.");
                }

                EnsureRepository();
                if (ReadCommitCount() < 2)
                {
                    return new SettingsRepositoryActionResult(false, "There is only one settings commit, so there is nothing safe to undo yet.");
                }

                var dirty = RunGitAllowExit([0], "status", "--short").StdOut;
                if (!string.IsNullOrWhiteSpace(dirty))
                {
                    return new SettingsRepositoryActionResult(false, "Commit or discard pending repository changes before undoing.");
                }

                RunGit("revert", "--no-edit", "HEAD");
                return TryReadCurrentSnapshot();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not revert the latest AC Defender settings commit in {RepositoryPath}", repositoryPath);
                return new SettingsRepositoryActionResult(false, ex.Message);
            }
        }
    }

    public SettingsRepositoryActionResult RestoreCommit(string hash)
    {
        lock (gate)
        {
            try
            {
                if (!enabled)
                {
                    return new SettingsRepositoryActionResult(false, "Settings repository is disabled.");
                }

                EnsureRepository();
                if (string.IsNullOrWhiteSpace(hash))
                {
                    return new SettingsRepositoryActionResult(false, "Choose a commit to restore.");
                }

                var dirty = RunGitAllowExit([0], "status", "--short").StdOut;
                if (!string.IsNullOrWhiteSpace(dirty))
                {
                    return new SettingsRepositoryActionResult(false, "Commit or discard pending repository changes before restoring.");
                }

                RunGit("checkout", hash, "--", SettingsFileName);
                var snapshotResult = TryReadCurrentSnapshot();
                if (!snapshotResult.Success || snapshotResult.Snapshot is null)
                {
                    return snapshotResult;
                }

                RunGit("add", SettingsFileName);
                var diff = RunGitAllowExit([0, 1], "diff", "--cached", "--quiet", "--", SettingsFileName);
                if (diff.ExitCode == 0)
                {
                    return new SettingsRepositoryActionResult(true, "That commit already matches the current settings.", snapshotResult.Snapshot);
                }

                var shortHash = hash.Length > 12 ? hash[..12] : hash;
                RunGit("commit", "-m", $"Restore AC Defender settings from {shortHash}");
                return new SettingsRepositoryActionResult(true, $"Restored settings from {shortHash}.", snapshotResult.Snapshot);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not restore AC Defender settings commit {Hash} in {RepositoryPath}", hash, repositoryPath);
                return new SettingsRepositoryActionResult(false, ex.Message);
            }
        }
    }

    private void EnsureRepository()
    {
        Directory.CreateDirectory(repositoryPath);
        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
        {
            RunGit("init");
            RunGitAllowExit([0, 128], "branch", "-M", "main");
        }

        RunGit("config", "user.name", "AC Defender Settings");
        RunGit("config", "user.email", "ac-defender-settings@local");
    }

    private static string ReadmeContent()
    {
        return """
            # AC Defender Settings Repository

            This local repository is managed by AC Defender.

            It stores the website target, defender switch, Settings page values, and schedule in `settings.json`.
            Secrets, Home Assistant tokens, login accounts, DataProtection keys, and raw runtime telemetry are not stored here.
            Runtime recovery still uses the persisted Docker data volume.
            """ + Environment.NewLine;
    }

    private IReadOnlyList<SettingsRepositoryCommit> ReadHistory(int count)
    {
        var result = RunGitAllowExit(
            [0, 128],
            "log",
            $"--max-count={count}",
            "--date=iso-strict",
            "--pretty=format:%H%x1f%h%x1f%ci%x1f%s",
            "--",
            SettingsFileName);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return [];
        }

        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\u001f'))
            .Where(parts => parts.Length >= 4)
            .Select(parts => new SettingsRepositoryCommit(parts[0], parts[1], parts[2], parts[3]))
            .ToArray();
    }

    private int ReadCommitCount()
    {
        var result = RunGitAllowExit([0, 128], "rev-list", "--count", "HEAD", "--", SettingsFileName);
        return result.ExitCode == 0 && int.TryParse(result.StdOut.Trim(), out var count) ? count : 0;
    }

    private IReadOnlyList<SettingsRepositoryFileStatus> ParseStatus(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return [];
        }

        return statusText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Length > 3
                ? new SettingsRepositoryFileStatus(line[..2].Trim(), line[3..].Trim())
                : new SettingsRepositoryFileStatus(line.Trim(), ""))
            .ToArray();
    }

    private GitResult RunGit(params string[] args)
    {
        var result = RunGitAllowExit([0], args);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StdErr.Length > 0 ? result.StdErr : $"git {string.Join(' ', args)} failed with exit code {result.ExitCode}.");
        }

        return result;
    }

    private GitResult RunGitAllowExit(int[] allowedExitCodes, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var result = new GitResult(process.ExitCode, stdout, stderr);
        if (!allowedExitCodes.Contains(result.ExitCode))
        {
            throw new InvalidOperationException(result.StdErr.Length > 0 ? result.StdErr : $"git {string.Join(' ', args)} failed with exit code {result.ExitCode}.");
        }

        return result;
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string ResolvePath(string configuredPath, string contentRoot)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(contentRoot, configuredPath);
    }

    private static string SanitizeCommitMessage(string value)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    private sealed record GitResult(int ExitCode, string StdOut, string StdErr);
}
