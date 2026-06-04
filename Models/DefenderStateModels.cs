namespace HomeAssistantAcDefender.Models;

public sealed record DefenderSnapshot(
    double TargetTemperatureCelsius,
    bool DefenderEnabled,
    double BoostOffsetCelsius,
    string ConnectionState,
    ThermostatSnapshot? HomeAssistantThermostat,
    WeatherSnapshot? Weather,
    string? HomeAssistantEntityId,
    string? WeatherEntityId,
    bool HomeAssistantConfigured,
    string? LastCommand,
    string? LastError,
    string NextAction,
    DateTimeOffset? NextActionAt,
    int CooldownSeconds,
    DefenderSettings Settings,
    IReadOnlyList<ScheduleEntry> Schedule,
    IReadOnlyList<ThermostatChangeAudit> ThermostatChanges,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DefenderEvent> Events);

public sealed record ThermostatSnapshot(
    double CurrentTemperatureCelsius,
    double SetPointCelsius,
    string HvacMode,
    string HvacAction,
    string? FanMode,
    IReadOnlyList<string> AvailableFanModes,
    DateTimeOffset UpdatedAt);

public sealed record WeatherSnapshot(
    double? OutdoorTemperatureCelsius,
    string? Condition,
    string EntityId,
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
    string HvacAction,
    string? FanMode,
    IReadOnlyList<string> AvailableFanModes);

public sealed record WeatherReading(
    string EntityId,
    double? OutdoorTemperatureCelsius,
    string? Condition);

public sealed record ThermostatChangeAudit(
    DateTimeOffset Timestamp,
    string EntityId,
    double PreviousSetPointCelsius,
    double NewSetPointCelsius,
    double? RoomTemperatureCelsius,
    double? OutdoorTemperatureCelsius,
    string? WeatherCondition);

public sealed class DefenderSettings
{
    public bool ScheduleEnabled { get; set; }

    public string WeatherActivationMode { get; set; } = "always";

    public int BaseCooldownSeconds { get; set; } = 45;

    public int MaxCooldownSeconds { get; set; } = 600;

    public int TouchFrequencyWindowMinutes { get; set; } = 30;

    public bool FanEnergySaverEnabled { get; set; }

    public double FanEnergySaverThresholdCelsius { get; set; } = 0.6;

    public string FanEnergySaverMode { get; set; } = "auto";

    public bool DefenderRunsContinuously { get; set; } = true;
}

public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = "Schedule";

    public string Days { get; set; } = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";

    public string StartTime { get; set; } = "00:00";

    public string EndTime { get; set; } = "23:59";

    public double TargetTemperatureCelsius { get; set; } = 22.0;

    public string WeatherActivationMode { get; set; } = "always";
}
