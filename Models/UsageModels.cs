namespace HomeAssistantAcDefender.Models;

public sealed record UsageEntityReading(
    string EntityId,
    string Name,
    double? Value,
    string Unit,
    string State,
    DateTimeOffset? LastChanged);

public sealed record UsageLiveSnapshot(
    UsageEntityReading? Power,
    UsageEntityReading? Energy,
    UsageEntityReading? Cost,
    UsageEntityReading? HourlyCost,
    UsageEntityReading? CurrentBill,
    UsageEntityReading? CurrentBillDue,
    UsageEntityReading? CurrentBillStatus,
    IReadOnlyList<UsageEntityReading> AlectraHuiEntities,
    bool HomeAssistantConfigured,
    DateTimeOffset UpdatedAt);

public sealed record UsageHistorySample(
    DateTimeOffset Timestamp,
    double? Value,
    string State);

public sealed record UsageHistorySnapshot(
    string EntityId,
    string Name,
    string Unit,
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    double? First,
    double? Last,
    double? Min,
    double? Max,
    double? Delta,
    IReadOnlyList<UsageHistorySample> Samples);
