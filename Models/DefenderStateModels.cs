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
    WebsiteCommandDebounceSnapshot WebsiteCommandDebounce,
    EmergencySnapshot Emergency,
    FrontDoorKillSwitchSnapshot FrontDoorKillSwitch,
    CoolModeRestoreSnapshot CoolModeRestore,
    NaturalRecoverySnapshot NaturalRecovery,
    NaturalWalkbackSnapshot NaturalWalkback,
    TouchSignatureSnapshot TouchSignature,
    VisibilityGuardSnapshot VisibilityGuard,
    RoutineTimingSnapshot RoutineTiming,
    ComfortBudgetSnapshot ComfortBudget,
    CommandCamouflageSnapshot CommandCamouflage,
    NaturalCadenceSnapshot NaturalCadence,
    NaturalChangePlannerSnapshot NaturalChangePlanner,
    ComfortEnvelopeSnapshot ComfortEnvelope,
    ComfortCompromiseSnapshot ComfortCompromise,
    ComfortMemorySnapshot ComfortMemory,
    ConflictQuietSnapshot ConflictQuiet,
    WallSettlingSnapshot WallSettling,
    ManualComfortGraceSnapshot ManualComfortGrace,
    TouchIntentSnapshot TouchIntent,
    CoolerIntentSnapshot CoolerIntent,
    SetpointEchoSnapshot SetpointEcho,
    RepeatCommandSnapshot RepeatCommand,
    SensorRhythmSnapshot SensorRhythm,
    CoolingRunwaySnapshot CoolingRunway,
    RoomTrendSnapshot RoomTrend,
    ThermalMomentumSnapshot ThermalMomentum,
    WeatherDriftSnapshot WeatherDrift,
    PeakPowerSaverSnapshot PeakPowerSaver,
    SuperDefenderSnapshot SuperDefender,
    CoolingFailureSnapshot CoolingFailure,
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
    DateTimeOffset UpdatedAt,
    HomeAssistantStateContext? Context);

public sealed record WeatherSnapshot(
    double? OutdoorTemperatureCelsius,
    string? Condition,
    string EntityId,
    DateTimeOffset UpdatedAt);

public sealed record DefenderEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record WebsiteCommandDebounceSnapshot(
    bool Active,
    int SecondsRemaining,
    int DebounceSeconds,
    string Status,
    string? LastCommand,
    DateTimeOffset? Until);

public sealed record WebsiteCommandGateResult(
    bool Accepted,
    string Message,
    DefenderSnapshot Snapshot);

public sealed record EmergencySnapshot(
    bool Active,
    int SecondsRemaining,
    string Protocol,
    string Status,
    DateTimeOffset? Until);

public sealed record FrontDoorKillSwitchSnapshot(
    bool Enabled,
    bool Active,
    bool PersonDetected,
    bool ThermostatOffCommanded,
    int SecondsRemaining,
    int DetectorCount,
    string EntityIds,
    string LastDetectedBy,
    string Status,
    DateTimeOffset? Until,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<FrontDoorPersonReading> Detectors);

public sealed record ThermostatReading(
    string EntityId,
    double CurrentTemperatureCelsius,
    double SetPointCelsius,
    string HvacMode,
    string HvacAction,
    string? FanMode,
    IReadOnlyList<string> AvailableFanModes,
    HomeAssistantStateContext? Context = null);

public sealed record HomeAssistantStateContext(
    string? Id,
    string? ParentId,
    string? UserId);

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

public sealed record FrontDoorPersonReading(
    string EntityId,
    string Name,
    string State,
    bool PersonDetected,
    DateTimeOffset? UpdatedAt);

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

public sealed record TouchSignatureSnapshot(
    bool Enabled,
    bool Active,
    int SampleCount,
    double? LearnedStepCelsius,
    double EffectiveStepCelsius,
    string Status);

public sealed record VisibilityGuardSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    int NoticeCount,
    int Pressure,
    string Status,
    DateTimeOffset? Until);

public sealed record RoutineTimingSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int IntervalMinutes,
    int JitterMinutes,
    string Status,
    DateTimeOffset? DueAt);

public sealed record ComfortBudgetSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int RecentCommandCount,
    int MaxCommands,
    string Status,
    DateTimeOffset? Until);

public sealed record CommandCamouflageSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int Pressure,
    int RecentCommandCount,
    string Status,
    DateTimeOffset? Until);

public sealed record NaturalCadenceSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int TouchPressure,
    int RecentCommandCount,
    string Status,
    DateTimeOffset? DueAt);

public sealed record NaturalChangePlannerSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int TouchPressure,
    int RecentTouchCount,
    int RecentCommandCount,
    string PlannedReason,
    string Status,
    DateTimeOffset? DueAt);

public sealed record ComfortEnvelopeSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    int RecentTouchCount,
    double? PreferredSetPointCelsius,
    double? MinimumAllowedSetPointCelsius,
    double? MaximumAllowedSetPointCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record ComfortCompromiseSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    double? PreferredSetPointCelsius,
    double? EffectiveTargetCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record ComfortMemorySnapshot(
    bool Enabled,
    bool Active,
    int SampleCount,
    double? LearnedOffsetCelsius,
    double? EffectiveTargetCelsius,
    string Status);

public sealed record ConflictQuietSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    int TriggerTouchCount,
    double ComfortBandCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record WallSettlingSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int RecentTouchCount,
    int SettleSeconds,
    string Status,
    DateTimeOffset? Until);

public sealed record ManualComfortGraceSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    string Status,
    double ComfortBandCelsius,
    DateTimeOffset? Until);

public sealed record TouchIntentSnapshot(
    bool Enabled,
    bool Active,
    int RecentTouchCount,
    string Direction,
    double NetChangeCelsius,
    int ExtraGraceMinutes,
    string Status);

public sealed record CoolerIntentSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    int RecentTouchCount,
    double NetChangeCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record SetpointEchoSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    double? PendingSetPointCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record RepeatCommandSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int Pressure,
    double? LastSetPointCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record SensorRhythmSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int SampleCount,
    int MedianIntervalSeconds,
    string Status,
    DateTimeOffset? DueAt);

public sealed record CoolingRunwaySnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int Pressure,
    DateTimeOffset? StartedAt,
    string Status,
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

public sealed record WeatherDriftSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    string Direction,
    double? OutdoorDeltaCelsius,
    int SampleCount,
    string Status,
    DateTimeOffset? Until);

public sealed record PeakPowerSaverSnapshot(
    bool Enabled,
    bool Active,
    bool Holding,
    bool FanSaverEnabled,
    int SecondsRemaining,
    double? CurrentPowerKilowatts,
    double PowerThresholdKilowatts,
    double? CurrentPriceCentsPerKwh,
    double PriceThresholdCentsPerKwh,
    string TouPeriod,
    string CurrentPlan,
    string Status,
    DateTimeOffset? Until,
    DateTimeOffset? UpdatedAt);

public sealed record SuperDefenderSnapshot(
    bool Enabled,
    bool Active,
    bool BypassingQuietTiming,
    int SecondsRemaining,
    int RecentRemoteChangeCount,
    string LastChangeSource,
    string LastChangeDetail,
    string Status,
    string NetworkLockdownStatus,
    DateTimeOffset? Until);

public sealed record CoolingFailureSnapshot(
    bool Enabled,
    bool Alerting,
    int SecondsActive,
    int AlertCount,
    string Status,
    DateTimeOffset? SuspectedAt,
    DateTimeOffset? NextAlertAt,
    bool OmegaAlerting,
    int OmegaSecondsActive,
    double? RoomRiseCelsius);

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
    string? WeatherCondition,
    string ChangeSource = "thermostat-device",
    string SourceDetail = "Home Assistant did not attach a user context.",
    string? ContextId = null,
    string? ContextParentId = null,
    string? ContextUserId = null);

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

    public bool WallSettlingGuardEnabled { get; set; } = true;

    public int WallSettlingMinimumTouches { get; set; } = 2;

    public int WallSettlingWindowMinutes { get; set; } = 10;

    public int WallSettlingBaseSeconds { get; set; } = 45;

    public int WallSettlingPressureExtraSeconds { get; set; } = 120;

    public double WallSettlingSafetyBandCelsius { get; set; } = 1.0;

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

    public bool TouchSignatureEnabled { get; set; } = true;

    public int TouchSignatureTriggerTouches { get; set; } = 2;

    public int TouchSignatureRetentionMinutes { get; set; } = 90;

    public double TouchSignatureMinimumStepCelsius { get; set; } = 0.3;

    public double TouchSignatureMaximumStepCelsius { get; set; } = 1.0;

    public double TouchSignatureSafetyBandCelsius { get; set; } = 1.0;

    public bool VisibilityGuardEnabled { get; set; } = true;

    public int VisibilityGuardTriggerNotices { get; set; } = 1;

    public int VisibilityGuardNoticeWindowMinutes { get; set; } = 45;

    public int VisibilityGuardAfterCommandSeconds { get; set; } = 180;

    public int VisibilityGuardMinimumHoldMinutes { get; set; } = 8;

    public int VisibilityGuardMaximumHoldMinutes { get; set; } = 35;

    public double VisibilityGuardSafetyBandCelsius { get; set; } = 1.0;

    public bool RoutineTimingEnabled { get; set; } = true;

    public int RoutineTimingTriggerTouches { get; set; } = 2;

    public int RoutineTimingIntervalMinutes { get; set; } = 5;

    public int RoutineTimingJitterMinutes { get; set; } = 2;

    public int RoutineTimingMaxDelayMinutes { get; set; } = 12;

    public double RoutineTimingSafetyBandCelsius { get; set; } = 1.0;

    public bool ComfortBudgetEnabled { get; set; } = true;

    public int ComfortBudgetWindowMinutes { get; set; } = 30;

    public int ComfortBudgetMaxCommands { get; set; } = 3;

    public double ComfortBudgetSafetyBandCelsius { get; set; } = 1.2;

    public bool CommandCamouflageEnabled { get; set; } = true;

    public int CommandCamouflageMinimumGapSeconds { get; set; } = 180;

    public int CommandCamouflagePressureExtraSeconds { get; set; } = 360;

    public double CommandCamouflageSafetyBandCelsius { get; set; } = 1.0;

    public bool NaturalCadenceEnabled { get; set; } = true;

    public int NaturalCadenceTriggerTouches { get; set; } = 2;

    public int NaturalCadenceMinimumMinutes { get; set; } = 3;

    public int NaturalCadenceMaximumMinutes { get; set; } = 18;

    public int NaturalCadenceJitterMinutes { get; set; } = 4;

    public double NaturalCadenceSafetyBandCelsius { get; set; } = 1.0;

    public bool NaturalChangePlannerEnabled { get; set; } = true;

    public int NaturalChangePlannerTriggerTouches { get; set; } = 3;

    public int NaturalChangePlannerMinimumMinutes { get; set; } = 8;

    public int NaturalChangePlannerMaximumMinutes { get; set; } = 45;

    public int NaturalChangePlannerJitterMinutes { get; set; } = 6;

    public double NaturalChangePlannerSafetyBandCelsius { get; set; } = 1.1;

    public bool NaturalChangePlannerPreferWeatherSlots { get; set; } = true;

    public bool NaturalChangePlannerPreferSensorBeat { get; set; } = true;

    public bool ComfortEnvelopeEnabled { get; set; } = true;

    public int ComfortEnvelopeTriggerTouches { get; set; } = 2;

    public int ComfortEnvelopeHoldMinutes { get; set; } = 18;

    public double ComfortEnvelopeMaxOffsetCelsius { get; set; } = 0.8;

    public double ComfortEnvelopeSafetyBandCelsius { get; set; } = 1.0;

    public bool ComfortCompromiseEnabled { get; set; } = true;

    public int ComfortCompromiseTriggerTouches { get; set; } = 2;

    public int ComfortCompromiseHoldMinutes { get; set; } = 20;

    public int ComfortCompromiseDecayMinutes { get; set; } = 30;

    public double ComfortCompromiseMaxOffsetCelsius { get; set; } = 1.0;

    public double ComfortCompromiseSafetyBandCelsius { get; set; } = 1.0;

    public bool ComfortMemoryEnabled { get; set; } = true;

    public int ComfortMemoryLearningTouches { get; set; } = 2;

    public int ComfortMemoryRetentionHours { get; set; } = 24;

    public double ComfortMemoryMaxOffsetCelsius { get; set; } = 0.6;

    public double ComfortMemorySafetyBandCelsius { get; set; } = 0.8;

    public bool ManualComfortGraceEnabled { get; set; } = true;

    public int ManualComfortGraceMinutes { get; set; } = 20;

    public double ManualComfortGraceBandCelsius { get; set; } = 0.8;

    public bool TouchIntentEnabled { get; set; } = true;

    public int TouchIntentMinimumTouches { get; set; } = 2;

    public int TouchIntentWindowMinutes { get; set; } = 90;

    public double TouchIntentNetWarmThresholdCelsius { get; set; } = 0.8;

    public int TouchIntentExtraGraceMinutes { get; set; } = 25;

    public double TouchIntentSafetyBandCelsius { get; set; } = 1.0;

    public bool CoolerIntentFastLaneEnabled { get; set; } = true;

    public int CoolerIntentMinimumTouches { get; set; } = 2;

    public int CoolerIntentWindowMinutes { get; set; } = 90;

    public int CoolerIntentHoldMinutes { get; set; } = 20;

    public double CoolerIntentNetCoolThresholdCelsius { get; set; } = 0.8;

    public double CoolerIntentSafetyBandCelsius { get; set; } = 2.0;

    public bool SetpointEchoGuardEnabled { get; set; } = true;

    public int SetpointEchoGraceSeconds { get; set; } = 30;

    public double SetpointEchoSafetyBandCelsius { get; set; } = 1.0;

    public bool RepeatCommandGuardEnabled { get; set; } = true;

    public int RepeatCommandMinimumWaitSeconds { get; set; } = 90;

    public int RepeatCommandPressureExtraSeconds { get; set; } = 240;

    public double RepeatCommandSafetyBandCelsius { get; set; } = 1.0;

    public bool SensorRhythmGuardEnabled { get; set; } = true;

    public int SensorRhythmMinimumSamples { get; set; } = 3;

    public int SensorRhythmWindowMinutes { get; set; } = 120;

    public int SensorRhythmJitterSeconds { get; set; } = 25;

    public double SensorRhythmSafetyBandCelsius { get; set; } = 1.0;

    public bool CoolingRunwayGuardEnabled { get; set; } = true;

    public int CoolingRunwayMinimumSeconds { get; set; } = 120;

    public int CoolingRunwayPressureExtraSeconds { get; set; } = 240;

    public double CoolingRunwaySafetyBandCelsius { get; set; } = 1.0;

    public bool RoomTrendGuardEnabled { get; set; } = true;

    public int RoomTrendWindowMinutes { get; set; } = 12;

    public double RoomTrendStableToleranceCelsius { get; set; } = 0.2;

    public int RoomTrendHoldMinutes { get; set; } = 8;

    public bool ThermalMomentumGuardEnabled { get; set; } = true;

    public double ThermalMomentumMinimumCoolingRateCelsiusPerHour { get; set; } = 0.4;

    public int ThermalMomentumLookAheadMinutes { get; set; } = 45;

    public int ThermalMomentumHoldMinutes { get; set; } = 6;

    public bool WeatherDriftGuardEnabled { get; set; } = true;

    public int WeatherDriftWindowMinutes { get; set; } = 45;

    public double WeatherDriftMinimumChangeCelsius { get; set; } = 0.3;

    public int WeatherDriftHoldMinutes { get; set; } = 7;

    public double WeatherDriftSafetyBandCelsius { get; set; } = 1.0;

    public bool PeakPowerSaverEnabled { get; set; } = true;

    public bool PeakPowerSaverOnPeakEnabled { get; set; } = true;

    public bool PeakPowerSaverHighPowerEnabled { get; set; } = true;

    public double PeakPowerSaverPowerThresholdKilowatts { get; set; } = 2.5;

    public double PeakPowerSaverPriceThresholdCentsPerKwh { get; set; } = 15.0;

    public int PeakPowerSaverHoldMinutes { get; set; } = 20;

    public int PeakPowerSaverRefreshSeconds { get; set; } = 120;

    public double PeakPowerSaverSafetyBandCelsius { get; set; } = 1.0;

    public bool PeakPowerSaverFanSaverEnabled { get; set; } = true;

    public string PeakPowerSaverFanMode { get; set; } = "auto";

    public bool FrontDoorKillSwitchEnabled { get; set; } = true;

    public string FrontDoorPersonEntityIds { get; set; } = "";

    public int FrontDoorKillSwitchHoldMinutes { get; set; } = 20;

    public int FrontDoorKillSwitchRefreshSeconds { get; set; } = 5;

    public bool FrontDoorKillSwitchTurnsThermostatOff { get; set; } = true;

    public bool SuperDefenderModeEnabled { get; set; } = true;

    public int SuperDefenderRemoteChangeThreshold { get; set; } = 2;

    public int SuperDefenderWindowMinutes { get; set; } = 30;

    public int SuperDefenderHoldMinutes { get; set; } = 45;

    public double SuperDefenderSafetyBandCelsius { get; set; } = 1.5;

    public bool SuperDefenderBypassQuietTiming { get; set; } = true;

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
