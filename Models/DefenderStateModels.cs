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
    CoolModeRestoreSnapshot CoolModeRestore,
    NaturalRecoverySnapshot NaturalRecovery,
    NaturalWalkbackSnapshot NaturalWalkback,
    ComfortCompromiseSnapshot ComfortCompromise,
    ConflictQuietSnapshot ConflictQuiet,
    ManualComfortGraceSnapshot ManualComfortGrace,
    RoomTrendSnapshot RoomTrend,
    ThermalMomentumSnapshot ThermalMomentum,
    ComfortSnapshot Comfort,
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

public sealed record TemperatureSensorReading(
    string EntityId,
    string Name,
    double? TemperatureCelsius,
    string State);

public sealed record PresenceReading(
    string EntityId,
    string Name,
    string State,
    bool IsHome);

public sealed record CoolModeRestoreSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    string Status,
    DateTimeOffset? DueAt);

public sealed record NaturalRecoverySnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int RecentTouchCount,
    string Status,
    string QuietLevel,
    double StepCelsius,
    double EffectiveStepCelsius,
    int HoldChancePercent,
    int EffectiveHoldChancePercent,
    int EffectiveCommandGapSeconds);

public sealed record NaturalWalkbackSnapshot(
    bool Enabled,
    bool Active,
    int SuspicionScore,
    double StepCelsius,
    string Status);

public sealed record ComfortCompromiseSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    double? PreferredSetPointCelsius,
    double? EffectiveTargetCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record ConflictQuietSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    int TriggerTouchCount,
    double ComfortBandCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record ManualComfortGraceSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    string Status,
    double ComfortBandCelsius,
    DateTimeOffset? Until);

public sealed record RoomTrendSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    string Direction,
    double? DeltaCelsius,
    string Status,
    int SampleCount);

public sealed record ThermalMomentumSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    double? CoolingRateCelsiusPerHour,
    double? EstimatedMinutesToTarget,
    string Status);

public sealed record ComfortSnapshot(
    bool UpstairsComfortEnabled,
    bool HomePresenceRequired,
    bool IsHome,
    bool UpstairsTooHot,
    double? HottestUpstairsTemperatureCelsius,
    string? HottestUpstairsEntityId,
    string Status,
    IReadOnlyList<TemperatureSensorReading> UpstairsSensors,
    IReadOnlyList<PresenceReading> Presence);

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

    public bool CoolModeRestoreDelayEnabled { get; set; } = true;

    public int CoolModeRestoreMinimumDelaySeconds { get; set; } = 8;

    public int CoolModeRestoreMaximumDelaySeconds { get; set; } = 60;

    public double CoolModeRestoreComfortBandCelsius { get; set; } = 0.6;

    public bool ConflictQuietModeEnabled { get; set; } = true;

    public int ConflictQuietTouchThreshold { get; set; } = 4;

    public int ConflictQuietMinutes { get; set; } = 35;

    public double ConflictQuietComfortBandCelsius { get; set; } = 1.2;

    public bool NaturalRecoveryEnabled { get; set; } = true;

    public bool AdaptiveQuietnessEnabled { get; set; } = true;

    public int AdaptiveQuietTouchThreshold { get; set; } = 2;

    public int MaximumAdaptiveDelaySeconds { get; set; } = 900;

    public double MinimumAdaptiveStepCelsius { get; set; } = 0.5;

    public int MaximumAdaptiveHoldChancePercent { get; set; } = 75;

    public int MaximumAdaptiveCommandGapSeconds { get; set; } = 180;

    public int MinimumNaturalDelaySeconds { get; set; } = 20;

    public int MaximumNaturalDelaySeconds { get; set; } = 180;

    public double NaturalStepCelsius { get; set; } = 1.0;

    public int NaturalHoldChancePercent { get; set; } = 25;

    public int MaxNaturalHolds { get; set; } = 2;

    public int MinimumCommandGapSeconds { get; set; } = 30;

    public double NaturalSafetyOverrideCelsius { get; set; } = 2.0;

    public bool NaturalWalkbackEnabled { get; set; } = true;

    public int NaturalWalkbackTriggerTouches { get; set; } = 2;

    public double NaturalWalkbackStepCelsius { get; set; } = 0.5;

    public double NaturalWalkbackJitterCelsius { get; set; } = 0.1;

    public double NaturalWalkbackSafetyBandCelsius { get; set; } = 1.0;

    public bool ComfortCompromiseEnabled { get; set; } = true;

    public int ComfortCompromiseTriggerTouches { get; set; } = 2;

    public int ComfortCompromiseHoldMinutes { get; set; } = 20;

    public int ComfortCompromiseDecayMinutes { get; set; } = 30;

    public double ComfortCompromiseMaxOffsetCelsius { get; set; } = 1.0;

    public double ComfortCompromiseSafetyBandCelsius { get; set; } = 1.0;

    public bool ManualComfortGraceEnabled { get; set; } = true;

    public int ManualComfortGraceMinutes { get; set; } = 20;

    public double ManualComfortGraceBandCelsius { get; set; } = 0.8;

    public bool RoomTrendGuardEnabled { get; set; } = true;

    public int RoomTrendWindowMinutes { get; set; } = 12;

    public double RoomTrendStableToleranceCelsius { get; set; } = 0.2;

    public int RoomTrendHoldMinutes { get; set; } = 8;

    public bool ThermalMomentumGuardEnabled { get; set; } = true;

    public double ThermalMomentumMinimumCoolingRateCelsiusPerHour { get; set; } = 0.4;

    public int ThermalMomentumLookAheadMinutes { get; set; } = 45;

    public int ThermalMomentumHoldMinutes { get; set; } = 6;

    public bool FanEnergySaverEnabled { get; set; }

    public double FanEnergySaverThresholdCelsius { get; set; } = 0.6;

    public string FanEnergySaverMode { get; set; } = "auto";

    public bool UpstairsComfortEnabled { get; set; } = true;

    public string UpstairsTemperatureEntityIds { get; set; } = "";

    public double UpstairsMaxComfortCelsius { get; set; } = 24.0;

    public double UpstairsComfortTargetCelsius { get; set; } = 22.0;

    public double UpstairsComfortBoostCelsius { get; set; } = 1.0;

    public bool HomePresenceRequired { get; set; }

    public string PresenceEntityIds { get; set; } = "";

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
