using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal sealed class SettingsGitRepositoryRegressionTests
{
    public void IdenticalSaveRecoversDirtySnapshotAfterGitFailureAndRestart()
    {
        var contentRoot = DefenderStoreFixture.CreateContentRoot();
        try
        {
            var options = new DefenderOptions
            {
                StateFilePath = Path.Combine(contentRoot, "state.json"),
                SettingsRepositoryEnabled = true,
                SettingsRepositoryPath = Path.Combine(contentRoot, "settings-repo"),
            };
            var environment = new TestWebHostEnvironment(contentRoot);
            var firstRepository = CreateRepository(options, environment);
            var firstSnapshot = Snapshot(22.0);
            var firstCommit = firstRepository.CommitSnapshot(firstSnapshot, "Initial settings snapshot");
            if (!firstCommit.Success)
            {
                throw new InvalidOperationException($"Could not create the initial settings commit: {firstCommit.Message}");
            }

            var repositoryPath = firstRepository.RepositoryPath;
            var indexLockPath = Path.Combine(repositoryPath, ".git", "index.lock");
            File.WriteAllText(indexLockPath, "simulate interrupted git add");

            var changedSnapshot = Snapshot(23.0);
            var interruptedCommit = firstRepository.CommitSnapshot(changedSnapshot, "Interrupted settings snapshot");
            if (interruptedCommit.Success)
            {
                throw new InvalidOperationException("The synthetic index lock should make git add fail after the snapshot file is written.");
            }

            var writtenSnapshot = firstRepository.TryReadCurrentSnapshot();
            if (!writtenSnapshot.Success || writtenSnapshot.Snapshot?.TargetTemperatureCelsius != 23.0)
            {
                throw new InvalidOperationException("The interrupted save should leave the new atomic snapshot on disk for the recovery path.");
            }

            var gitProcessesAfterFailure = GitProcessStartCount(firstRepository);
            var deferredSameSnapshot = firstRepository.CommitSnapshot(changedSnapshot, "Deferred identical retry");
            var deferredSnapshot = Snapshot(24.0);
            var deferredChangedSnapshot = firstRepository.CommitSnapshot(deferredSnapshot, "Deferred changed retry");
            var deferredWrittenSnapshot = firstRepository.TryReadCurrentSnapshot();
            if (!deferredSameSnapshot.Success
                || !deferredChangedSnapshot.Success
                || GitProcessStartCount(firstRepository) != gitProcessesAfterFailure
                || !deferredWrittenSnapshot.Success
                || deferredWrittenSnapshot.Snapshot?.TargetTemperatureCelsius != 24.0)
            {
                throw new InvalidOperationException(
                    "Automatic saves inside recovery backoff must keep atomically writing the latest snapshot while starting zero Git processes.");
            }

            File.Delete(indexLockPath);

            // A fresh service instance represents process restart. The file contents already equal
            // the requested snapshot, but the repository is dirty and must be committed rather than
            // accepted by the equality fast path.
            var restartedRepository = CreateRepository(options, environment);
            var recoveredCommit = restartedRepository.CommitSnapshot(deferredSnapshot, "Recovered settings snapshot");
            if (!recoveredCommit.Success
                || !recoveredCommit.Message.Contains("Committed settings snapshot", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The restart recovery save should commit the dirty snapshot: {recoveredCommit.Message}");
            }

            var recoveredState = restartedRepository.GetState();
            if (!recoveredState.Clean || recoveredState.History.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Recovered settings repository should be clean with two commits; clean={recoveredState.Clean}, commits={recoveredState.History.Count}.");
            }

            var gitProcessesBeforeFastPath = GitProcessStartCount(restartedRepository);
            var fastPath = restartedRepository.CommitSnapshot(deferredSnapshot, "No-op settings snapshot");
            var gitProcessesAfterFastPath = GitProcessStartCount(restartedRepository);
            var finalState = restartedRepository.GetState();
            if (!fastPath.Success
                || !fastPath.Message.Contains("already has the latest snapshot", StringComparison.OrdinalIgnoreCase)
                || gitProcessesAfterFastPath != gitProcessesBeforeFastPath
                || !finalState.Clean
                || finalState.History.Count != 2)
            {
                throw new InvalidOperationException("A healthy equality fast path should activate only after commit and should start zero Git processes.");
            }

            // A second restart begins with unknown journal health. Its first equality match must
            // verify Git once; subsequent matches use the cached healthy state with zero Git work.
            var cleanRestart = CreateRepository(options, environment);
            var firstRestartSave = cleanRestart.CommitSnapshot(deferredSnapshot, "Verify clean restart");
            var gitProcessesAfterRestartVerification = GitProcessStartCount(cleanRestart);
            var secondRestartSave = cleanRestart.CommitSnapshot(deferredSnapshot, "Cached clean restart");
            if (!firstRestartSave.Success
                || !secondRestartSave.Success
                || gitProcessesAfterRestartVerification <= 0
                || GitProcessStartCount(cleanRestart) != gitProcessesAfterRestartVerification)
            {
                throw new InvalidOperationException("A restart should verify journal health once, then cache it for repeated identical saves.");
            }
        }
        finally
        {
            DefenderStoreFixture.DeleteContentRoot(contentRoot);
        }
    }

    private static SettingsGitRepository CreateRepository(
        DefenderOptions options,
        TestWebHostEnvironment environment) =>
        new(
            Options.Create(options),
            environment,
            NullLogger<SettingsGitRepository>.Instance);

    private static int GitProcessStartCount(SettingsGitRepository repository)
    {
        var field = typeof(SettingsGitRepository).GetField(
            "gitProcessStartCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find the settings Git process counter.");
        return (int)(field.GetValue(repository)
            ?? throw new InvalidOperationException("Could not read the settings Git process counter."));
    }

    private static SettingsRepositorySnapshot Snapshot(double targetTemperatureCelsius) =>
        new()
        {
            TargetTemperatureCelsius = targetTemperatureCelsius,
            DefenderEnabled = true,
            Settings = new DefenderSettings(),
            Schedule = [],
        };
}
