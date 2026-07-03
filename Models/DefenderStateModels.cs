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
    HumanNudgeSnapshot HumanNudge,
    VisibilityGuardSnapshot VisibilityGuard,
    RoutineTimingSnapshot RoutineTiming,
    ComfortBudgetSnapshot ComfortBudget,
    CommandCamouflageSnapshot CommandCamouflage,
    StealthGovernorSnapshot StealthGovernor,
    NaturalCadenceSnapshot NaturalCadence,
    NaturalChangePlannerSnapshot NaturalChangePlanner,
    ComfortEnvelopeSnapshot ComfortEnvelope,
    ComfortCompromiseSnapshot ComfortCompromise,
    ComfortMemorySnapshot ComfortMemory,
    AngerLearningSnapshot AngerLearning,
    HistoryLearningSnapshot HistoryLearning,
    LearningModelSnapshot LearningModel,
    AdjustmentStatisticsSnapshot AdjustmentStatistics,
    ConflictQuietSnapshot ConflictQuiet,
    TugOfWarTruceSnapshot TugOfWarTruce,
    WallSettlingSnapshot WallSettling,
    ManualComfortGraceSnapshot ManualComfortGrace,
    TouchIntentSnapshot TouchIntent,
    CoolerIntentSnapshot CoolerIntent,
    SetpointEchoSnapshot SetpointEcho,
    RepeatCommandSnapshot RepeatCommand,
    SetpointStillnessSnapshot SetpointStillness,
    SensorRhythmSnapshot SensorRhythm,
    HvacActionAlibiSnapshot HvacActionAlibi,
    TelemetryAlibiSnapshot TelemetryAlibi,
    CoolingRunwaySnapshot CoolingRunway,
    RoomTrendSnapshot RoomTrend,
    ThermalMomentumSnapshot ThermalMomentum,
    WeatherDriftSnapshot WeatherDrift,
    PeakPowerSaverSnapshot PeakPowerSaver,
    SuperDefenderSnapshot SuperDefender,
    RemoteSettlingSnapshot RemoteSettling,
    CoolingFailureSnapshot CoolingFailure,
    EnforcerSnapshot Enforcer,
    ComfortSnapshot Comfort,
    DefenderSettings Settings,
    IReadOnlyList<ScheduleEntry> Schedule,
    IReadOnlyList<ThermostatChangeAudit> ThermostatChanges,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DefenderEvent> Events,
    AcRuntimeSnapshot? AcRuntime = null,
    ElectricityCostSnapshot? ElectricityCost = null,
    ElectricityBudgetSnapshot? ElectricityBudget = null,
    RivalScheduleSnapshot? RivalSchedule = null);

/// <summary>
/// Live view of Rival Schedule Watch: the AC vendor app's own temperature schedule (the other
/// side's plan, never a defender target), which block is in force, the next boundary, and the last
/// wall push that was attributed to the schedule instead of a human touch.
/// </summary>
public sealed record RivalScheduleSnapshot(
    bool Enabled,
    bool BypassQuietTiming,
    int BlockCount,
    string ActiveBlockName,
    double? ActiveLowSetPointCelsius,
    double? ActiveHighSetPointCelsius,
    string NextBlockName,
    DateTimeOffset? NextBlockStartsAt,
    int MatchCount,
    DateTimeOffset? LastMatchAt,
    string LastMatchBlockName,
    double? LastMatchSetPointCelsius,
    string Status);

/// <summary>
/// Live view of the Desired-State Enforcer: whether it is enforcing the owner's exact desired state,
/// how many unwanted external overrides and re-asserts happened this window, whether it has escalated
/// to firm mode, and whether the learned interference model is currently shaping its timing.
/// </summary>
public sealed record EnforcerSnapshot(
    bool Enabled,
    bool Active,
    bool Escalated,
    bool InWindow,
    bool Stealth,
    int RecentOverrideCount,
    int RecentAssertCount,
    int ConsecutiveRejects,
    double DesiredTargetCelsius,
    string LastChangeSource,
    string LastChangeUser,
    double InterferenceProbability,
    bool LearningActive,
    string Status,
    DateTimeOffset? Until);

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
    HomeAssistantStateContext? Context = null,
    double? MinSetPointCelsius = null,
    double? MaxSetPointCelsius = null);

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

public sealed record HumanNudgeSnapshot(
    bool Enabled,
    bool Active,
    double? LastSetPointCelsius,
    double StepCelsius,
    int RecentTouchCount,
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

public sealed record StealthGovernorSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int Score,
    int TriggerScore,
    int RecentTouchCount,
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

public sealed record AngerLearningSnapshot(
    bool Enabled,
    int EventCount,
    double CurrentHourSensitivity,
    int ExtraGraceMinutes,
    int PeakHourOfDay,
    DateTimeOffset? LastAngerAt,
    string Status);

public sealed record HistoryLearningSnapshot(
    bool Enabled,
    int LearnedHourCount,
    double? CurrentHourPreferredSetPointCelsius,
    double? MedianTouchIntervalMinutes,
    DateTimeOffset? LearnedAt,
    string Status);

/// <summary>One real Home Assistant climate state-change pulled from the history API.</summary>
public sealed record ClimateHistorySample(
    DateTimeOffset Timestamp,
    double? SetPointCelsius,
    double? CurrentTemperatureCelsius,
    string? HvacMode,
    string? HvacAction,
    string? ContextUserId);

/// <summary>Persisted weights of the online ML models trained by <c>LearningTrainer</c>.</summary>
public sealed class LearningModelState
{
    public double[] AngerWeights { get; set; } = [];
    public double AngerBias { get; set; }
    public double[] ComfortWeights { get; set; } = [];
    public double ComfortBias { get; set; }
    public int AngerPositiveSamples { get; set; }
    public int AngerNegativeSamples { get; set; }
    public int ComfortSamples { get; set; }
    public double AngerLogLoss { get; set; }
    public double ComfortRmse { get; set; }
    public DateTimeOffset? TrainedAt { get; set; }

    // Interference classifier: P(an unwanted external override is happening | time/presence/power context),
    // learned from real enforcer override events (positives) vs benign operating contexts (negatives).
    public double[] InterferenceWeights { get; set; } = [];
    public double InterferenceBias { get; set; }
    public int InterferencePositiveSamples { get; set; }
    public int InterferenceNegativeSamples { get; set; }
    public double InterferenceLogLoss { get; set; }

    // Override-cadence regressor: predicted minutes between consecutive unwanted overrides by time of day,
    // learned from the gaps in the real override-event log. Lets the enforcer pace re-asserts so they blend in.
    public double[] OverrideCadenceWeights { get; set; } = [];
    public double OverrideCadenceBias { get; set; }
    public int OverrideCadenceSamples { get; set; }
    public double OverrideCadenceRmse { get; set; }
}

public sealed record LearningModelSnapshot(
    bool AngerModelTrained,
    bool ComfortModelTrained,
    int AngerPositiveSamples,
    int AngerNegativeSamples,
    int ComfortSamples,
    double AngerLogLoss,
    double ComfortRmse,
    DateTimeOffset? TrainedAt,
    bool InterferenceModelTrained = false,
    int InterferencePositiveSamples = 0,
    int InterferenceNegativeSamples = 0,
    bool OverrideCadenceModelTrained = false,
    int OverrideCadenceSamples = 0);

/// <summary>One Home Assistant entity's on/off-style state and whether it counts as "active".</summary>
public sealed record EntityActivation(string EntityId, string Name, string State, bool Active);

/// <summary>What the Desired-State Enforcer decided this cycle.</summary>
public enum EnforcerDecision
{
    /// <summary>Enforcer is off/idle or chose to let the stealth pipeline run; the caller falls through.</summary>
    Inactive,

    /// <summary>Restore cool mode (and the target) now — someone turned the unit off/away from cool.</summary>
    EnforceMode,

    /// <summary>Restore the exact desired setpoint now.</summary>
    EnforceSetpoint,

    /// <summary>Deviation seen but the enforcer is debouncing or waiting out its cooldown; send nothing.</summary>
    Cooldown,

    /// <summary>The device is not confirming commands or the rate limit was hit; hold and send nothing.</summary>
    Backoff,

    /// <summary>The change was attributed to the owner; respect it and send nothing this cycle.</summary>
    RespectOwner
}

/// <summary>The Enforcer's per-cycle verdict for <c>AcDefenderService.RunCycleAsync</c> to act on.</summary>
public sealed record EnforcerGate(
    EnforcerDecision Decision,
    double AssertSetPoint,
    string Message,
    DateTimeOffset Until,
    bool Notify,
    string NotifyMessage,
    bool Stealth);

/// <summary>
/// One accumulated training sample for the Desired-State Enforcer's learned models: the real context in
/// which an unwanted external override happened (Label 1) or a benign operating moment (Label 0). The
/// override-cadence regressor reads the timestamps of the positives to learn how often interference recurs.
/// </summary>
public sealed class EnforcerOverrideSample
{
    public DateTimeOffset At { get; set; }
    public int HourOfDay { get; set; }
    public bool OwnerHome { get; set; }
    public bool BedroomOccupied { get; set; }
    public bool PeakPower { get; set; }
    public double RoomAboveTargetCelsius { get; set; }
    public int RecentOverrideCount { get; set; }
    public int Label { get; set; }
}

/// <summary>Per-cycle context for the adjustment statistics: is the tracked person home, is the master bedroom occupied.</summary>
public sealed record TrackedContextReading(string PersonLabel, bool PersonConfigured, bool PersonHome, bool BedroomConfigured, bool BedroomOccupied);

public sealed record AdjustmentSplitSnapshot(
    string Label,
    int Count,
    double? AverageSetPointCelsius,
    double? AverageRoomTemperatureCelsius,
    double? AverageOutdoorTemperatureCelsius);

public sealed record AdjustmentStatisticsSnapshot(
    string TrackedPersonLabel,
    int TotalAdjustments,
    double? AverageSetPointCelsius,
    double? AverageRoomTemperatureCelsius,
    double? AverageOutdoorTemperatureCelsius,
    AdjustmentSplitSnapshot PersonHome,
    AdjustmentSplitSnapshot PersonAway,
    AdjustmentSplitSnapshot BedroomOccupied,
    AdjustmentSplitSnapshot BedroomEmpty,
    string Insight,
    DateTimeOffset? FirstSampleAt,
    DateTimeOffset? LastSampleAt);

public sealed record ConflictQuietSnapshot(
    bool Enabled,
    bool Active,
    int SecondsRemaining,
    int TriggerTouchCount,
    double ComfortBandCelsius,
    string Status,
    DateTimeOffset? Until);

public sealed record TugOfWarTruceSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int FlipCount,
    int TriggerFlips,
    string DirectionPattern,
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

public sealed record SetpointStillnessSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int StableSampleCount,
    int RequiredStableSamples,
    double? CurrentSetPointCelsius,
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

public sealed record HvacActionAlibiSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int RecentTouchCount,
    string CurrentAction,
    DateTimeOffset? LastTransitionAt,
    string Status,
    DateTimeOffset? Until);

public sealed record TelemetryAlibiSnapshot(
    bool Enabled,
    bool Waiting,
    int SecondsRemaining,
    int RecentTouchCount,
    string LastSignal,
    DateTimeOffset? LastSignalAt,
    string Status,
    DateTimeOffset? Until);

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

public sealed record RemoteSettlingSnapshot(
    bool Enabled,
    bool Holding,
    int SecondsRemaining,
    int RecentRemoteChangeCount,
    int TriggerRemoteChangeCount,
    string LastChangeSource,
    string Status,
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

/// <summary>Accumulated compressor runtime (hvac_action == cooling), tracked from live readings.</summary>
/// <summary>
/// One calendar day of AC usage for the Energy calendar: Toronto-local "yyyy-MM-dd", real cooling
/// hours, and the estimated AC-only cost (assumed amps×volts load × TOU rate at each moment).
/// </summary>
public sealed record AcDailyUsage(string Date, double Hours, double CostDollars);

public sealed record AcRuntimeSnapshot(
    double TodayHours,
    double MonthHours,
    double LifetimeHours,
    DateTimeOffset? TrackingSince,
    // Estimated AC-only cost: cooling runtime priced at an assumed amps×volts load and the Alectra
    // TOU rate in force at each moment. Sensor-free, so it survives the Alectra integration being down.
    bool EstimatedCostEnabled = false,
    double EstimatedCostTodayDollars = 0,
    double EstimatedCostMonthDollars = 0,
    double EstimatedCostLifetimeDollars = 0,
    double AssumedKilowatts = 0);

/// <summary>
/// Electricity-cost tracking (Alectra time-of-use). The three CAD totals accumulate from the power
/// sensor integrated over time and priced at each interval's TOU rate: <see cref="TotalCad"/> is
/// all-time since tracking began, <see cref="TodayCad"/> resets at local midnight, and
/// <see cref="MonthCad"/> resets on the 1st. The current rate/period reflect "now" (with
/// <see cref="UsingMostExpensiveFallback"/> set when the period could not be determined and On-Peak
/// was assumed). Rates are the energy commodity portion in ¢/kWh unless an all-in factor is set.
/// </summary>
public sealed record ElectricityCostSnapshot(
    bool Enabled,
    double TotalCad,
    double TodayCad,
    double MonthCad,
    double TotalKwh,
    double TodayKwh,
    double MonthKwh,
    string CurrentPeriod,
    double CurrentRateCentsPerKwh,
    bool UsingMostExpensiveFallback,
    double OnPeakCentsPerKwh,
    double MidPeakCentsPerKwh,
    double OffPeakCentsPerKwh,
    double AllInMultiplier,
    double AllInAdderCentsPerKwh,
    DateTimeOffset? TrackingSince,
    // All-in "out of pocket" totals: commodity + delivery + regulatory, minus the Ontario Electricity
    // Rebate, plus HST. Same total/today/this-month buckets as the commodity line.
    double AllInTotalCad,
    double AllInTodayCad,
    double AllInMonthCad,
    double DeliveryFixedDollarsPerMonth,
    double DeliveryVariableCentsPerKwh,
    double RegulatoryCentsPerKwh,
    double OntarioRebatePercent,
    double HstPercent);

/// <summary>
/// Budget-preferring control status. Compares month-to-date all-in spend against a pro-rated target
/// (monthly budget × fraction of the month elapsed) and reports the resulting comfort bias: a bounded
/// warmer-setpoint offset applied when spend is running ahead of pace, always dropped when the room is
/// at/above the safety maximum so dangerous heat is still cooled.
/// </summary>
public sealed record ElectricityBudgetSnapshot(
    bool Enabled,
    double MonthlyBudgetCad,
    double MonthToDateAllInCad,
    double ProRatedTargetCad,
    double ProjectedMonthEndCad,
    bool OverBudget,
    double OverUnderCad,
    double CurrentSetpointOffsetCelsius,
    double Aggressiveness,
    double SafetyMaxCelsius,
    bool SafetyOverrideActive,
    string Status,
    // Which spend line paces the budget: the chosen basis ("all-in" sensor line or "ac-estimate"
    // static-TOU line) and what is actually in effect right now (all-in falls back to the
    // sensor-free estimate while the Alectra sensor is stale, so budgeting never silently stalls).
    string Basis = "all-in",
    string EffectiveBasis = "all-in");

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
    // Off by default: the website debounce blocked normal adjust-then-save flows. Users who want
    // the 120s rest between website thermostat commands can switch it on in Standing Orders.
    public bool WebsiteCommandDebounceEnabled { get; set; }

    // Night shutdown: during the window (Toronto time) the defender turns the AC OFF and stands
    // down completely, as long as the outdoor temperature is below the threshold. If someone turns
    // the AC back on mid-window the defender respects it (the off command is sent once, on entry).
    public bool NightShutdownEnabled { get; set; }

    public string NightShutdownStartTime { get; set; } = "01:00";

    public string NightShutdownEndTime { get; set; } = "08:00";

    public double NightShutdownOutdoorBelowCelsius { get; set; } = 24.0;

    // During the night window (same hours as the shutdown), the defender never commands a
    // setpoint below this — cheap nights beat cold nights. 0 disables the night floor.
    public double NightMinimumSetPointCelsius { get; set; } = 23.0;

    // Even on warm nights, the AC may cool at most this many minutes inside the night window;
    // then it is eased to a stop once and the rest of the night is observe-only. 0 disables.
    public int NightCoolingBudgetMinutes { get; set; } = 90;

    // Peace offering: when someone raises the setpoint from the phone/Home Assistant app, the
    // defender immediately concedes a small extra step UP (their number + step) and stands down
    // for the hold window — they see the system agreeing with them instead of fighting back.
    public bool PeaceOfferingEnabled { get; set; } = true;

    public double PeaceOfferingStepCelsius { get; set; } = 0.5;

    public int PeaceOfferingHoldMinutes { get; set; } = 20;

    // AUTO brother-mad (rage detector): nobody has to remember the emergency button. A burst of
    // external thermostat touches or one big angry raise triggers the full 2-hour apology
    // stand-down automatically and teaches the anger model.
    public bool AutoBrotherMadEnabled { get; set; } = true;

    public int AutoBrotherMadTouches { get; set; } = 3;

    public int AutoBrotherMadWindowMinutes { get; set; } = 10;

    public double AutoBrotherMadBigRaiseCelsius { get; set; } = 2.0;

    // Stand-down parking: when the defender is turned OFF, park the thermostat at this setpoint
    // (raise only, cool mode only) so the unguarded AC barely runs instead of cooling hard.
    public bool StandDownParkEnabled { get; set; } = true;

    public double StandDownParkSetPointCelsius { get; set; } = 28.0;

    // On-forever protection: if the AC has been cooling continuously for this long and the room
    // still has not reached the target, the defender eases the setpoint above the room and rests —
    // an unreachable target must never mean "the AC is on for 24 hours".
    public bool CoolingRestEnabled { get; set; } = true;

    public int CoolingRunMaxMinutes { get; set; } = 180;

    public int CoolingRestMinutes { get; set; } = 30;

    // Anti-flap: minimum spacing between step commands. Steps are driven by the room temperature
    // actually moving (never by a schedule); this only stops command bursts and short-cycling.
    public int CoolingStepMinimumGapSeconds { get; set; } = 60;

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

    public bool TugOfWarTruceEnabled { get; set; } = true;

    public int TugOfWarTruceMinimumFlips { get; set; } = 2;

    public int TugOfWarTruceWindowMinutes { get; set; } = 12;

    public int TugOfWarTruceHoldMinutes { get; set; } = 20;

    public double TugOfWarTruceSafetyBandCelsius { get; set; } = 1.0;

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

    public bool HumanNudgeEnabled { get; set; } = true;

    public int HumanNudgeTriggerTouches { get; set; } = 2;

    public double HumanNudgeStepCelsius { get; set; } = 0.5;

    public double HumanNudgeSafetyBandCelsius { get; set; } = 1.0;

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

    public bool StealthGovernorEnabled { get; set; } = true;

    public int StealthGovernorTriggerScore { get; set; } = 65;

    public int StealthGovernorMinimumHoldMinutes { get; set; } = 5;

    public int StealthGovernorMaximumHoldMinutes { get; set; } = 25;

    public double StealthGovernorSafetyBandCelsius { get; set; } = 1.2;

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

    public bool TelemetryAlibiEnabled { get; set; } = true;

    public int TelemetryAlibiTriggerTouches { get; set; } = 2;

    public int TelemetryAlibiMinimumHoldSeconds { get; set; } = 90;

    public int TelemetryAlibiMaxHoldMinutes { get; set; } = 10;

    public double TelemetryAlibiSafetyBandCelsius { get; set; } = 1.0;

    public bool TelemetryAlibiUseWeather { get; set; } = true;

    public bool TelemetryAlibiUseSensorBeat { get; set; } = true;

    public bool TelemetryAlibiUsePeakPower { get; set; } = true;

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

    // Anger learning: when someone presses the someone-upset button, the defender learns that this
    // time of day is sensitive and grows more hands-off (longer wall-change grace) during it. Always
    // overridden by the comfort safety bypass, so it never blocks real cooling.
    public bool AngerLearningEnabled { get; set; } = true;

    public int AngerMemoryRetentionDays { get; set; } = 45;

    public double AngerSafetyBandCelsius { get; set; } = 1.0;

    public int AngerMaxExtraGraceMinutes { get; set; } = 25;

    // Thermostat-history learning: mine the real Home Assistant history to learn a per-hour human
    // comfort profile and the human touch cadence.
    public bool HistoryLearningEnabled { get; set; } = true;

    public int HistoryLearningDays { get; set; } = 14;

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

    public bool SetpointStillnessGuardEnabled { get; set; } = true;

    public int SetpointStillnessTriggerTouches { get; set; } = 2;

    public int SetpointStillnessRequiredSamples { get; set; } = 3;

    public int SetpointStillnessMaxHoldSeconds { get; set; } = 180;

    public double SetpointStillnessToleranceCelsius { get; set; } = 0.05;

    public double SetpointStillnessSafetyBandCelsius { get; set; } = 1.0;

    public bool SensorRhythmGuardEnabled { get; set; } = true;

    public int SensorRhythmMinimumSamples { get; set; } = 3;

    public int SensorRhythmWindowMinutes { get; set; } = 120;

    public int SensorRhythmJitterSeconds { get; set; } = 25;

    public double SensorRhythmSafetyBandCelsius { get; set; } = 1.0;

    public bool HvacActionAlibiEnabled { get; set; } = true;

    public int HvacActionAlibiTriggerTouches { get; set; } = 2;

    public int HvacActionAlibiTransitionWindowSeconds { get; set; } = 90;

    public int HvacActionAlibiMaxHoldMinutes { get; set; } = 12;

    public double HvacActionAlibiSafetyBandCelsius { get; set; } = 1.0;

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

    public bool RemoteSettlingGuardEnabled { get; set; } = true;

    public int RemoteSettlingTriggerChanges { get; set; } = 2;

    public int RemoteSettlingWindowMinutes { get; set; } = 30;

    public int RemoteSettlingHoldMinutes { get; set; } = 12;

    public double RemoteSettlingSafetyBandCelsius { get; set; } = 1.0;

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

    // ===== Desired-State Enforcer =====
    // The assertive layer that makes the owner's chosen AC state win automatically when someone else
    // turns it off or moves the setpoint. Off by default so the existing stealth pipeline is unchanged.

    /// <summary>Master switch. When false the Enforcer is inactive and the stealth pipeline runs unchanged.</summary>
    public bool EnforcerModeEnabled { get; set; }

    /// <summary>Exact temperature to hold. 0 means "follow the website target" so there is no hardcoded value.</summary>
    public double EnforcerTargetTemperatureCelsius { get; set; }

    /// <summary>Restore HVAC mode to cool immediately when the unit is switched off/heat/auto by someone else.</summary>
    public bool EnforcerEnforceMode { get; set; } = true;

    /// <summary>Restore the desired setpoint when it is moved away from the target by someone else.</summary>
    public bool EnforcerEnforceSetpoint { get; set; } = true;

    /// <summary>Smart-stealth mode: route setpoint corrections through the human-like stealth pipeline so the
    /// fix is less likely to be noticed. When off, the Enforcer snaps to the exact target immediately.</summary>
    public bool EnforcerStealthShaping { get; set; } = true;

    /// <summary>Respect changes attributed to the owner (EnforcerOwnerUserIds); only counteract other people.</summary>
    public bool EnforcerRespectOwner { get; set; } = true;

    /// <summary>Comma-separated Home Assistant context.user_id values that are the owner (respected).</summary>
    public string EnforcerOwnerUserIds { get; set; } = "";

    /// <summary>A deviation must persist this many seconds before re-asserting (hysteresis vs in-flight reads).</summary>
    public int EnforcerDebounceSeconds { get; set; } = 8;

    /// <summary>Minimum gap between re-asserts; also clamps to the Home Assistant echo grace window.</summary>
    public int EnforcerCooldownSeconds { get; set; } = 30;

    /// <summary>Sliding window (minutes) for the rate limit and for counting repeated overrides.</summary>
    public int EnforcerRateWindowMinutes { get; set; } = 15;

    /// <summary>Max asserts inside the window before the Enforcer holds and escalates instead of thrashing.</summary>
    public int EnforcerMaxAssertsPerWindow { get; set; } = 6;

    /// <summary>Escalate to firm mode after this many unwanted external overrides inside the window.</summary>
    public int EnforcerEscalateAfterOverrides { get; set; } = 3;

    /// <summary>Exponential device-reject backoff base seconds (wait base*2^(rejects-1) when commands do not stick).</summary>
    public int EnforcerBackoffBaseSeconds { get; set; } = 20;

    /// <summary>Cap for the device-reject backoff.</summary>
    public int EnforcerBackoffMaxSeconds { get; set; } = 300;

    /// <summary>When true, the Enforcer is only active inside the local EnforcerStartTime..EnforcerEndTime window.</summary>
    public bool EnforcerScheduleEnabled { get; set; }

    public string EnforcerStartTime { get; set; } = "00:00";

    public string EnforcerEndTime { get; set; } = "23:59";

    /// <summary>When true, only enforce while someone is home (reuses presence readings).</summary>
    public bool EnforcerRequirePresence { get; set; }

    /// <summary>Send a Home Assistant notification when the Enforcer defends or detects repeated interference.</summary>
    public bool EnforcerNotifyEnabled { get; set; }

    /// <summary>Use the trained interference/cadence models to smartly pace re-asserts (falls back to the
    /// static debounce/cooldown until the models have enough real data to be trained).</summary>
    public bool EnforcerUseLearning { get; set; } = true;

    // ---- Budget-preferring control (editable from the Settings page; seeded from DefenderOptions
    // the first time this settings object is created so existing appsettings values carry over). ----

    /// <summary>Master switch for budget-preferring cooling. Off by default.</summary>
    public bool ElectricityBudgetEnabled { get; set; }

    /// <summary>Preferred monthly spend, interpreted against <see cref="ElectricityBudgetBasis"/>.</summary>
    public double ElectricityMonthlyBudgetDollars { get; set; } = 150.0;

    /// <summary>0 = no biasing, 1 = full biasing up to the max offset.</summary>
    public double ElectricityBudgetAggressiveness { get; set; } = 0.5;

    /// <summary>Cap on how much warmer the room may be allowed to run to stay on budget.</summary>
    public double ElectricityBudgetMaxSetpointOffsetCelsius { get; set; } = 1.5;

    /// <summary>Hard guardrail: at/above this room temperature the budget offset is dropped so heat is cooled.</summary>
    public double ElectricityBudgetSafetyMaxCelsius { get; set; } = 26.0;

    /// <summary>
    /// What the month-to-date spend is measured against:
    /// <c>all-in</c> = the whole-house out-of-pocket bill from the Alectra power sensor (needs the sensor);
    /// <c>ac-estimate</c> = the sensor-free AC-only estimate (assumed load × static Alectra TOU prices),
    /// so budgeting keeps working when the Alectra integration is down.
    /// </summary>
    public string ElectricityBudgetBasis { get; set; } = "ac-estimate";

    /// <summary>Set once so the fields above are seeded from DefenderOptions exactly one time, then owned by the UI.</summary>
    public bool ElectricityBudgetSettingsInitialized { get; set; }
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
