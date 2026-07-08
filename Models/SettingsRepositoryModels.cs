namespace HomeAssistantAcDefender.Models;

public sealed class SettingsRepositorySnapshot
{
    public int SchemaVersion { get; set; } = 1;

    public double TargetTemperatureCelsius { get; set; }

    public bool DefenderEnabled { get; set; }

    public DefenderSettings Settings { get; set; } = new();

    public List<ScheduleEntry> Schedule { get; set; } = [];
}

public sealed record SettingsRepositoryState(
    string RepositoryPath,
    bool Available,
    bool Initialized,
    bool Clean,
    string Branch,
    string Head,
    string Status,
    string? Error,
    IReadOnlyList<SettingsRepositoryCommit> History,
    IReadOnlyList<SettingsRepositoryFileStatus> Files);

public sealed record SettingsRepositoryCommit(
    string Hash,
    string ShortHash,
    string Timestamp,
    string Message);

public sealed record SettingsRepositoryFileStatus(
    string Code,
    string Path);

public sealed record SettingsRepositoryActionResult(
    bool Success,
    string Message,
    SettingsRepositorySnapshot? Snapshot = null);
