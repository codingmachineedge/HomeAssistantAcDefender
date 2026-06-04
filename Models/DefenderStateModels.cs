namespace HomeAssistantAcDefender.Models;

public sealed record DefenderSnapshot(
    double TargetTemperatureCelsius,
    bool DefenderEnabled,
    double BoostOffsetCelsius,
    string ConnectionState,
    ThermostatSnapshot? HomeAssistantThermostat,
    string? HomeAssistantEntityId,
    bool HomeAssistantConfigured,
    string? LastCommand,
    string? LastError,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DefenderEvent> Events);

public sealed record ThermostatSnapshot(
    double CurrentTemperatureCelsius,
    double SetPointCelsius,
    string HvacMode,
    string HvacAction,
    DateTimeOffset UpdatedAt);

public sealed record DefenderEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record ThermostatReading(
    string EntityId,
    double CurrentTemperatureCelsius,
    double SetPointCelsius,
    string HvacMode,
    string HvacAction);
