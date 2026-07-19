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
    private static readonly TimeSpan GitOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutomaticRecoveryRetryDelay = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private readonly bool enabled;
    private readonly ILogger<SettingsGitRepository> logger;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string repositoryPath;
    private DateTimeOffset? gitOperationDeadline;
    private DateTimeOffset? nextAutomaticRecoveryAttemptAt;
    private SnapshotJournalHealth snapshotJournalHealth = SnapshotJournalHealth.Unknown;
    private int gitProcessStartCount;

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
            var gitProcessesAtEntry = gitProcessStartCount;
            using var operationBudget = BeginGitOperationBudget();
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
                    && string.Equals(currentReadme, readmeContent, StringComparison.Ordinal)
                    && SnapshotJournalIsHealthy())
                {
                    return new SettingsRepositoryActionResult(true, "Settings repository already has the latest snapshot.");
                }

                snapshotJournalHealth = SnapshotJournalHealth.Unhealthy;
                if (!string.Equals(currentJson, nextJson, StringComparison.Ordinal))
                {
                    WriteAtomic(settingsPath, nextJson);
                }

                if (!string.Equals(currentReadme, readmeContent, StringComparison.Ordinal))
                {
                    WriteAtomic(readmePath, readmeContent);
                }

                var now = DateTimeOffset.UtcNow;
                if (nextAutomaticRecoveryAttemptAt is { } retryAt && retryAt > now)
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling((retryAt - now).TotalSeconds));
                    return new SettingsRepositoryActionResult(
                        true,
                        $"Settings snapshot saved atomically; Git journal recovery will retry in {seconds} seconds.");
                }

                EnsureRepository();
                RunGit("add", SettingsFileName, ReadmeFileName);
                var diff = RunGitAllowExit([0, 1], "diff", "--cached", "--quiet", "--", SettingsFileName, ReadmeFileName);
                if (diff.ExitCode == 0)
                {
                    MarkSnapshotJournalHealthy();
                    return new SettingsRepositoryActionResult(true, "Settings repository already has the latest snapshot.");
                }

                var message = string.IsNullOrWhiteSpace(reason)
                    ? "Save AC Defender settings"
                    : SanitizeCommitMessage(reason);
                RunGit("commit", "-m", message);
                MarkSnapshotJournalHealthy();
                return new SettingsRepositoryActionResult(true, $"Committed settings snapshot: {message}");
            }
            catch (Exception ex)
            {
                snapshotJournalHealth = SnapshotJournalHealth.Unhealthy;
                if (gitProcessStartCount > gitProcessesAtEntry)
                {
                    nextAutomaticRecoveryAttemptAt = DateTimeOffset.UtcNow.Add(AutomaticRecoveryRetryDelay);
                }

                logger.LogWarning(ex, "Could not commit AC Defender settings to {RepositoryPath}", repositoryPath);
                return new SettingsRepositoryActionResult(false, ex.Message);
            }
        }
    }

    public SettingsRepositoryState GetState()
    {
        lock (gate)
        {
            using var operationBudget = BeginGitOperationBudget();
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
            using var operationBudget = BeginGitOperationBudget();
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
            using var operationBudget = BeginGitOperationBudget();
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

                snapshotJournalHealth = SnapshotJournalHealth.Unhealthy;
                RunGit("revert", "--no-edit", "HEAD");
                var result = TryReadCurrentSnapshot();
                if (result.Success)
                {
                    MarkSnapshotJournalHealthy();
                }

                return result;
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
            using var operationBudget = BeginGitOperationBudget();
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

                snapshotJournalHealth = SnapshotJournalHealth.Unhealthy;
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
                    MarkSnapshotJournalHealthy();
                    return new SettingsRepositoryActionResult(true, "That commit already matches the current settings.", snapshotResult.Snapshot);
                }

                var shortHash = hash.Length > 12 ? hash[..12] : hash;
                RunGit("commit", "-m", $"Restore AC Defender settings from {shortHash}");
                MarkSnapshotJournalHealthy();
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

    private bool SnapshotJournalIsHealthy()
    {
        if (snapshotJournalHealth == SnapshotJournalHealth.Healthy)
        {
            return true;
        }

        if (snapshotJournalHealth == SnapshotJournalHealth.Unhealthy)
        {
            return false;
        }

        // Unknown occurs only for a fresh process/service instance. Mark it unhealthy before
        // probing so a timeout or Git failure cannot accidentally restore the equality fast path.
        snapshotJournalHealth = SnapshotJournalHealth.Unhealthy;
        var tracked = RunGitAllowExit(
            [0, 1],
            "ls-files",
            "--error-unmatch",
            "--",
            SettingsFileName,
            ReadmeFileName);
        if (tracked.ExitCode != 0)
        {
            return false;
        }

        var status = RunGitAllowExit(
            [0],
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
            "--",
            SettingsFileName,
            ReadmeFileName);
        if (!string.IsNullOrWhiteSpace(status.StdOut))
        {
            return false;
        }

        var committed = RunGitAllowExit(
            [0, 1, 128],
            "diff",
            "--quiet",
            "HEAD",
            "--",
            SettingsFileName,
            ReadmeFileName);
        if (committed.ExitCode != 0)
        {
            return false;
        }

        MarkSnapshotJournalHealthy();
        return true;
    }

    private void MarkSnapshotJournalHealthy()
    {
        snapshotJournalHealth = SnapshotJournalHealth.Healthy;
        nextAutomaticRecoveryAttemptAt = null;
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

        // This repository is an internal settings journal, never an interactive Git surface.
        // Per-process config avoids a signing prompt or user hook holding the defender gate.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("commit.gpgSign=false");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("tag.gpgSign=false");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"core.hooksPath={Path.Combine(repositoryPath, ".git", "ac-defender-empty-hooks")}");

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var remaining = (gitOperationDeadline ?? DateTimeOffset.UtcNow.Add(GitOperationTimeout)) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                $"Settings repository operation exceeded the {GitOperationTimeout.TotalSeconds:0}-second safety timeout before git {string.Join(' ', args)}.");
        }

        gitProcessStartCount++;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(remaining);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
            }
            catch (Exception killError)
            {
                logger.LogWarning(killError, "Could not terminate timed-out settings Git process");
            }

            throw new TimeoutException(
                $"Settings repository operation exceeded the shared {GitOperationTimeout.TotalSeconds:0}-second safety timeout during git {string.Join(' ', args)}.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        var result = new GitResult(process.ExitCode, stdout, stderr);
        if (!allowedExitCodes.Contains(result.ExitCode))
        {
            throw new InvalidOperationException(result.StdErr.Length > 0 ? result.StdErr : $"git {string.Join(' ', args)} failed with exit code {result.ExitCode}.");
        }

        return result;
    }

    private IDisposable BeginGitOperationBudget()
    {
        if (gitOperationDeadline is not null)
        {
            return NoopDisposable.Instance;
        }

        gitOperationDeadline = DateTimeOffset.UtcNow.Add(GitOperationTimeout);
        return new GitOperationBudget(this);
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

    private enum SnapshotJournalHealth
    {
        Unknown,
        Healthy,
        Unhealthy,
    }

    private sealed class GitOperationBudget(SettingsGitRepository owner) : IDisposable
    {
        public void Dispose() => owner.gitOperationDeadline = null;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
