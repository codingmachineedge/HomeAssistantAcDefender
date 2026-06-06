using System.Globalization;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class DefenderStateStore
{
    private const int WebsiteCommandDebounceSeconds = 120;
    private const int CoolingFailureIdleSeconds = 360;
    private const int CoolingFailureNoDropSeconds = 1200;
    private const int CoolingFailureRepeatAlertSeconds = 60;
    private const double CoolingFailureDemandBandCelsius = 0.6;
    private const double CoolingFailureMinimumDropCelsius = 0.2;

    // OMEGA: a confirmed cooling failure. The mega alert only proves the AC is not cooling; OMEGA adds
    // proof that the room is actually getting WARMER over a sustained window, which is what a dead
    // breaker (no power to the unit) looks like. A merely stuck/idle compressor that still has power
    // tends to hold the room steady, so requiring a real rise sharply cuts false positives.
    private const int OmegaRiseWindowSeconds = 300;
    private const double OmegaMinimumRiseCelsius = 0.4;

    private readonly object gate = new();
    private readonly DefenderOptions options;
    private readonly ILogger<DefenderStateStore> logger;
    private readonly string stateFilePath;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Random random = new();
    private DefenderRuntimeState state;

    public DefenderStateStore(IOptions<DefenderOptions> options, IWebHostEnvironment environment, ILogger<DefenderStateStore> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        stateFilePath = ResolveStatePath(this.options.StateFilePath, environment.ContentRootPath);
        state = LoadState();
    }

    public DefenderSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot GenerateTarget()
    {
        lock (gate)
        {
            var min = options.MinimumGeneratedTargetCelsius;
            var max = options.MaximumGeneratedTargetCelsius;
            var step = options.GeneratedTargetStepCelsius <= 0 ? 0.5 : options.GeneratedTargetStepCelsius;
            var steps = Math.Max(0, (int)Math.Round((max - min) / step));
            state.TargetTemperatureCelsius = Math.Round(min + random.Next(steps + 1) * step, 1);
            ResetCoolingDefenderStep();
            ResetNaturalRecovery("Website generated a target; comfort sync is ready.");
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("info", $"Generated target {state.TargetTemperatureCelsius:0.0} C.");
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot SetTarget(double temperatureCelsius)
    {
        lock (gate)
        {
            state.TargetTemperatureCelsius = Math.Round(temperatureCelsius, 1);
            ResetCoolingDefenderStep();
            ResetNaturalRecovery("Website target changed; comfort sync is ready.");
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("info", $"Target set to {state.TargetTemperatureCelsius:0.0} C.");
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot SetDefenderEnabled(bool enabled)
    {
        lock (gate)
        {
            state.DefenderEnabled = enabled;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("info", enabled ? "Defender enabled." : "Defender paused.");
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot UpdateSettings(SettingsRequest request)
    {
        lock (gate)
        {
            state.Settings.ScheduleEnabled = request.ScheduleEnabled;
            state.Settings.WeatherActivationMode = NormalizeWeatherMode(request.WeatherActivationMode);
            state.Settings.BaseCooldownSeconds = Math.Clamp(request.BaseCooldownSeconds, 0, 3600);
            state.Settings.MaxCooldownSeconds = Math.Clamp(request.MaxCooldownSeconds, 0, 7200);
            state.Settings.TouchFrequencyWindowMinutes = Math.Clamp(request.TouchFrequencyWindowMinutes, 1, 1440);
            state.Settings.CoolModeRestoreDelayEnabled = request.CoolModeRestoreDelayEnabled;
            state.Settings.CoolModeRestoreMinimumDelaySeconds = Math.Clamp(request.CoolModeRestoreMinimumDelaySeconds, 0, 3600);
            state.Settings.CoolModeRestoreMaximumDelaySeconds = Math.Clamp(
                request.CoolModeRestoreMaximumDelaySeconds,
                state.Settings.CoolModeRestoreMinimumDelaySeconds,
                7200);
            state.Settings.CoolModeRestoreComfortBandCelsius = Math.Round(Math.Clamp(request.CoolModeRestoreComfortBandCelsius, 0.1, 5.0), 1);
            state.Settings.ConflictQuietModeEnabled = request.ConflictQuietModeEnabled;
            state.Settings.ConflictQuietTouchThreshold = Math.Clamp(request.ConflictQuietTouchThreshold, 2, 20);
            state.Settings.ConflictQuietMinutes = Math.Clamp(request.ConflictQuietMinutes, 1, 240);
            state.Settings.ConflictQuietComfortBandCelsius = Math.Round(Math.Clamp(request.ConflictQuietComfortBandCelsius, 0.1, 5.0), 1);
            state.Settings.WallSettlingGuardEnabled = request.WallSettlingGuardEnabled;
            state.Settings.WallSettlingMinimumTouches = Math.Clamp(request.WallSettlingMinimumTouches, 1, 20);
            state.Settings.WallSettlingWindowMinutes = Math.Clamp(request.WallSettlingWindowMinutes, 1, 1440);
            state.Settings.WallSettlingBaseSeconds = Math.Clamp(request.WallSettlingBaseSeconds, 0, 1800);
            state.Settings.WallSettlingPressureExtraSeconds = Math.Clamp(request.WallSettlingPressureExtraSeconds, 0, 3600);
            state.Settings.WallSettlingSafetyBandCelsius = Math.Round(Math.Clamp(request.WallSettlingSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.WallSettlingGuardEnabled)
            {
                ClearWallSettling("Wall settling guard is off.");
            }
            state.Settings.NaturalRecoveryEnabled = request.NaturalRecoveryEnabled;
            state.Settings.AdaptiveQuietnessEnabled = request.AdaptiveQuietnessEnabled;
            state.Settings.AdaptiveQuietTouchThreshold = Math.Clamp(request.AdaptiveQuietTouchThreshold, 1, 20);
            state.Settings.MinimumNaturalDelaySeconds = Math.Clamp(request.MinimumNaturalDelaySeconds, 0, 3600);
            state.Settings.MaximumNaturalDelaySeconds = Math.Clamp(
                request.MaximumNaturalDelaySeconds,
                state.Settings.MinimumNaturalDelaySeconds,
                7200);
            state.Settings.MaximumAdaptiveDelaySeconds = Math.Clamp(
                request.MaximumAdaptiveDelaySeconds,
                state.Settings.MaximumNaturalDelaySeconds,
                7200);
            state.Settings.MinimumAdaptiveStepCelsius = Math.Round(Math.Clamp(request.MinimumAdaptiveStepCelsius, 0.1, 5.0), 1);
            state.Settings.MaximumAdaptiveHoldChancePercent = Math.Clamp(request.MaximumAdaptiveHoldChancePercent, 0, 100);
            state.Settings.MaximumAdaptiveCommandGapSeconds = Math.Clamp(request.MaximumAdaptiveCommandGapSeconds, 0, 7200);
            state.Settings.MaximumAdaptiveDelaySeconds = Math.Max(
                state.Settings.MaximumNaturalDelaySeconds,
                state.Settings.MaximumAdaptiveDelaySeconds);
            state.Settings.NaturalStepCelsius = Math.Round(Math.Clamp(request.NaturalStepCelsius, 0.1, 5.0), 1);
            state.Settings.MinimumAdaptiveStepCelsius = Math.Min(
                state.Settings.NaturalStepCelsius,
                state.Settings.MinimumAdaptiveStepCelsius);
            state.Settings.NaturalHoldChancePercent = Math.Clamp(request.NaturalHoldChancePercent, 0, 100);
            state.Settings.MaximumAdaptiveHoldChancePercent = Math.Max(
                state.Settings.NaturalHoldChancePercent,
                state.Settings.MaximumAdaptiveHoldChancePercent);
            state.Settings.MaxNaturalHolds = Math.Clamp(request.MaxNaturalHolds, 0, 10);
            state.Settings.MinimumCommandGapSeconds = Math.Clamp(request.MinimumCommandGapSeconds, 0, 3600);
            state.Settings.MaximumAdaptiveCommandGapSeconds = Math.Max(
                state.Settings.MinimumCommandGapSeconds,
                state.Settings.MaximumAdaptiveCommandGapSeconds);
            state.Settings.NaturalSafetyOverrideCelsius = Math.Round(Math.Clamp(request.NaturalSafetyOverrideCelsius, 0.1, 10.0), 1);
            state.Settings.NaturalWalkbackEnabled = request.NaturalWalkbackEnabled;
            state.Settings.NaturalWalkbackTriggerTouches = Math.Clamp(request.NaturalWalkbackTriggerTouches, 1, 20);
            state.Settings.NaturalWalkbackStepCelsius = Math.Round(Math.Clamp(request.NaturalWalkbackStepCelsius, 0.1, 5.0), 1);
            state.Settings.NaturalWalkbackJitterCelsius = Math.Round(Math.Clamp(request.NaturalWalkbackJitterCelsius, 0.0, 0.5), 1);
            state.Settings.NaturalWalkbackSafetyBandCelsius = Math.Round(Math.Clamp(request.NaturalWalkbackSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.TouchSignatureEnabled = request.TouchSignatureEnabled;
            state.Settings.TouchSignatureTriggerTouches = Math.Clamp(request.TouchSignatureTriggerTouches, 1, 20);
            state.Settings.TouchSignatureRetentionMinutes = Math.Clamp(request.TouchSignatureRetentionMinutes, 1, 1440);
            state.Settings.TouchSignatureMinimumStepCelsius = Math.Round(Math.Clamp(request.TouchSignatureMinimumStepCelsius, 0.1, 5.0), 1);
            state.Settings.TouchSignatureMaximumStepCelsius = Math.Round(Math.Clamp(
                request.TouchSignatureMaximumStepCelsius,
                state.Settings.TouchSignatureMinimumStepCelsius,
                5.0), 1);
            state.Settings.TouchSignatureSafetyBandCelsius = Math.Round(Math.Clamp(request.TouchSignatureSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.HumanNudgeEnabled = request.HumanNudgeEnabled;
            state.Settings.HumanNudgeTriggerTouches = Math.Clamp(request.HumanNudgeTriggerTouches, 1, 20);
            state.Settings.HumanNudgeStepCelsius = Math.Round(Math.Clamp(request.HumanNudgeStepCelsius, 0.1, 2.0), 1);
            state.Settings.HumanNudgeSafetyBandCelsius = Math.Round(Math.Clamp(request.HumanNudgeSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.HumanNudgeEnabled)
            {
                ClearHumanNudge("Human nudge is off.");
            }
            state.Settings.VisibilityGuardEnabled = request.VisibilityGuardEnabled;
            state.Settings.VisibilityGuardTriggerNotices = Math.Clamp(request.VisibilityGuardTriggerNotices, 1, 20);
            state.Settings.VisibilityGuardNoticeWindowMinutes = Math.Clamp(request.VisibilityGuardNoticeWindowMinutes, 1, 1440);
            state.Settings.VisibilityGuardAfterCommandSeconds = Math.Clamp(request.VisibilityGuardAfterCommandSeconds, 15, 3600);
            state.Settings.VisibilityGuardMinimumHoldMinutes = Math.Clamp(request.VisibilityGuardMinimumHoldMinutes, 1, 240);
            state.Settings.VisibilityGuardMaximumHoldMinutes = Math.Clamp(
                request.VisibilityGuardMaximumHoldMinutes,
                state.Settings.VisibilityGuardMinimumHoldMinutes,
                480);
            state.Settings.VisibilityGuardSafetyBandCelsius = Math.Round(Math.Clamp(request.VisibilityGuardSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.RoutineTimingEnabled = request.RoutineTimingEnabled;
            state.Settings.RoutineTimingTriggerTouches = Math.Clamp(request.RoutineTimingTriggerTouches, 1, 20);
            state.Settings.RoutineTimingIntervalMinutes = Math.Clamp(request.RoutineTimingIntervalMinutes, 1, 60);
            state.Settings.RoutineTimingJitterMinutes = Math.Clamp(request.RoutineTimingJitterMinutes, 0, 30);
            state.Settings.RoutineTimingMaxDelayMinutes = Math.Clamp(request.RoutineTimingMaxDelayMinutes, 1, 180);
            state.Settings.RoutineTimingSafetyBandCelsius = Math.Round(Math.Clamp(request.RoutineTimingSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.ComfortBudgetEnabled = request.ComfortBudgetEnabled;
            state.Settings.ComfortBudgetWindowMinutes = Math.Clamp(request.ComfortBudgetWindowMinutes, 1, 240);
            state.Settings.ComfortBudgetMaxCommands = Math.Clamp(request.ComfortBudgetMaxCommands, 1, 30);
            state.Settings.ComfortBudgetSafetyBandCelsius = Math.Round(Math.Clamp(request.ComfortBudgetSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.CommandCamouflageEnabled = request.CommandCamouflageEnabled;
            state.Settings.CommandCamouflageMinimumGapSeconds = Math.Clamp(request.CommandCamouflageMinimumGapSeconds, 0, 1800);
            state.Settings.CommandCamouflagePressureExtraSeconds = Math.Clamp(request.CommandCamouflagePressureExtraSeconds, 0, 3600);
            state.Settings.CommandCamouflageSafetyBandCelsius = Math.Round(Math.Clamp(request.CommandCamouflageSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.CommandCamouflageEnabled)
            {
                ClearCommandCamouflage("Command camouflage is off.");
            }
            state.Settings.StealthGovernorEnabled = request.StealthGovernorEnabled;
            state.Settings.StealthGovernorTriggerScore = Math.Clamp(request.StealthGovernorTriggerScore, 1, 100);
            state.Settings.StealthGovernorMinimumHoldMinutes = Math.Clamp(request.StealthGovernorMinimumHoldMinutes, 1, 240);
            state.Settings.StealthGovernorMaximumHoldMinutes = Math.Clamp(
                request.StealthGovernorMaximumHoldMinutes,
                state.Settings.StealthGovernorMinimumHoldMinutes,
                480);
            state.Settings.StealthGovernorSafetyBandCelsius = Math.Round(Math.Clamp(request.StealthGovernorSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.StealthGovernorEnabled)
            {
                ClearStealthGovernor("Stealth governor is off.");
            }
            state.Settings.NaturalCadenceEnabled = request.NaturalCadenceEnabled;
            state.Settings.NaturalCadenceTriggerTouches = Math.Clamp(request.NaturalCadenceTriggerTouches, 1, 20);
            state.Settings.NaturalCadenceMinimumMinutes = Math.Clamp(request.NaturalCadenceMinimumMinutes, 1, 120);
            state.Settings.NaturalCadenceMaximumMinutes = Math.Clamp(
                request.NaturalCadenceMaximumMinutes,
                state.Settings.NaturalCadenceMinimumMinutes,
                240);
            state.Settings.NaturalCadenceJitterMinutes = Math.Clamp(request.NaturalCadenceJitterMinutes, 0, 60);
            state.Settings.NaturalCadenceSafetyBandCelsius = Math.Round(Math.Clamp(request.NaturalCadenceSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.NaturalChangePlannerEnabled = request.NaturalChangePlannerEnabled;
            state.Settings.NaturalChangePlannerTriggerTouches = Math.Clamp(request.NaturalChangePlannerTriggerTouches, 1, 20);
            state.Settings.NaturalChangePlannerMinimumMinutes = Math.Clamp(request.NaturalChangePlannerMinimumMinutes, 1, 240);
            state.Settings.NaturalChangePlannerMaximumMinutes = Math.Clamp(
                request.NaturalChangePlannerMaximumMinutes,
                state.Settings.NaturalChangePlannerMinimumMinutes,
                480);
            state.Settings.NaturalChangePlannerJitterMinutes = Math.Clamp(request.NaturalChangePlannerJitterMinutes, 0, 120);
            state.Settings.NaturalChangePlannerSafetyBandCelsius = Math.Round(Math.Clamp(request.NaturalChangePlannerSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.NaturalChangePlannerPreferWeatherSlots = request.NaturalChangePlannerPreferWeatherSlots;
            state.Settings.NaturalChangePlannerPreferSensorBeat = request.NaturalChangePlannerPreferSensorBeat;
            if (!state.Settings.NaturalChangePlannerEnabled)
            {
                ClearNaturalChangePlanner("Comfort Pace is off.");
            }
            state.Settings.ComfortEnvelopeEnabled = request.ComfortEnvelopeEnabled;
            state.Settings.ComfortEnvelopeTriggerTouches = Math.Clamp(request.ComfortEnvelopeTriggerTouches, 1, 20);
            state.Settings.ComfortEnvelopeHoldMinutes = Math.Clamp(request.ComfortEnvelopeHoldMinutes, 0, 240);
            state.Settings.ComfortEnvelopeMaxOffsetCelsius = Math.Round(Math.Clamp(request.ComfortEnvelopeMaxOffsetCelsius, 0.1, 5.0), 1);
            state.Settings.ComfortEnvelopeSafetyBandCelsius = Math.Round(Math.Clamp(request.ComfortEnvelopeSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.ComfortEnvelopeEnabled)
            {
                ClearComfortEnvelope("Comfort envelope is off.");
            }
            state.Settings.ComfortCompromiseEnabled = request.ComfortCompromiseEnabled;
            state.Settings.ComfortCompromiseTriggerTouches = Math.Clamp(request.ComfortCompromiseTriggerTouches, 1, 20);
            state.Settings.ComfortCompromiseHoldMinutes = Math.Clamp(request.ComfortCompromiseHoldMinutes, 0, 240);
            state.Settings.ComfortCompromiseDecayMinutes = Math.Clamp(request.ComfortCompromiseDecayMinutes, 1, 240);
            state.Settings.ComfortCompromiseMaxOffsetCelsius = Math.Round(Math.Clamp(request.ComfortCompromiseMaxOffsetCelsius, 0.1, 5.0), 1);
            state.Settings.ComfortCompromiseSafetyBandCelsius = Math.Round(Math.Clamp(request.ComfortCompromiseSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.ComfortMemoryEnabled = request.ComfortMemoryEnabled;
            state.Settings.ComfortMemoryLearningTouches = Math.Clamp(request.ComfortMemoryLearningTouches, 1, 20);
            state.Settings.ComfortMemoryRetentionHours = Math.Clamp(request.ComfortMemoryRetentionHours, 1, 168);
            state.Settings.ComfortMemoryMaxOffsetCelsius = Math.Round(Math.Clamp(request.ComfortMemoryMaxOffsetCelsius, 0.1, 3.0), 1);
            state.Settings.ComfortMemorySafetyBandCelsius = Math.Round(Math.Clamp(request.ComfortMemorySafetyBandCelsius, 0.1, 3.0), 1);
            state.Settings.ManualComfortGraceEnabled = request.ManualComfortGraceEnabled;
            state.Settings.ManualComfortGraceMinutes = Math.Clamp(request.ManualComfortGraceMinutes, 0, 240);
            state.Settings.ManualComfortGraceBandCelsius = Math.Round(Math.Clamp(request.ManualComfortGraceBandCelsius, 0.1, 5.0), 1);
            state.Settings.TouchIntentEnabled = request.TouchIntentEnabled;
            state.Settings.TouchIntentMinimumTouches = Math.Clamp(request.TouchIntentMinimumTouches, 1, 20);
            state.Settings.TouchIntentWindowMinutes = Math.Clamp(request.TouchIntentWindowMinutes, 1, 1440);
            state.Settings.TouchIntentNetWarmThresholdCelsius = Math.Round(Math.Clamp(request.TouchIntentNetWarmThresholdCelsius, 0.1, 5.0), 1);
            state.Settings.TouchIntentExtraGraceMinutes = Math.Clamp(request.TouchIntentExtraGraceMinutes, 0, 240);
            state.Settings.TouchIntentSafetyBandCelsius = Math.Round(Math.Clamp(request.TouchIntentSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.CoolerIntentFastLaneEnabled = request.CoolerIntentFastLaneEnabled;
            state.Settings.CoolerIntentMinimumTouches = Math.Clamp(request.CoolerIntentMinimumTouches, 1, 20);
            state.Settings.CoolerIntentWindowMinutes = Math.Clamp(request.CoolerIntentWindowMinutes, 1, 1440);
            state.Settings.CoolerIntentHoldMinutes = Math.Clamp(request.CoolerIntentHoldMinutes, 0, 240);
            state.Settings.CoolerIntentNetCoolThresholdCelsius = Math.Round(Math.Clamp(request.CoolerIntentNetCoolThresholdCelsius, 0.1, 5.0), 1);
            state.Settings.CoolerIntentSafetyBandCelsius = Math.Round(Math.Clamp(request.CoolerIntentSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.CoolerIntentFastLaneEnabled)
            {
                ClearCoolerIntent("Cooler intent fast lane is off.");
            }
            state.Settings.SetpointEchoGuardEnabled = request.SetpointEchoGuardEnabled;
            state.Settings.SetpointEchoGraceSeconds = Math.Clamp(request.SetpointEchoGraceSeconds, 5, 300);
            state.Settings.SetpointEchoSafetyBandCelsius = Math.Round(Math.Clamp(request.SetpointEchoSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.RepeatCommandGuardEnabled = request.RepeatCommandGuardEnabled;
            state.Settings.RepeatCommandMinimumWaitSeconds = Math.Clamp(request.RepeatCommandMinimumWaitSeconds, 0, 1800);
            state.Settings.RepeatCommandPressureExtraSeconds = Math.Clamp(request.RepeatCommandPressureExtraSeconds, 0, 3600);
            state.Settings.RepeatCommandSafetyBandCelsius = Math.Round(Math.Clamp(request.RepeatCommandSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.SensorRhythmGuardEnabled = request.SensorRhythmGuardEnabled;
            state.Settings.SensorRhythmMinimumSamples = Math.Clamp(request.SensorRhythmMinimumSamples, 2, 60);
            state.Settings.SensorRhythmWindowMinutes = Math.Clamp(request.SensorRhythmWindowMinutes, 5, 1440);
            state.Settings.SensorRhythmJitterSeconds = Math.Clamp(request.SensorRhythmJitterSeconds, 0, 300);
            state.Settings.SensorRhythmSafetyBandCelsius = Math.Round(Math.Clamp(request.SensorRhythmSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.HvacActionAlibiEnabled = request.HvacActionAlibiEnabled;
            state.Settings.HvacActionAlibiTriggerTouches = Math.Clamp(request.HvacActionAlibiTriggerTouches, 1, 20);
            state.Settings.HvacActionAlibiTransitionWindowSeconds = Math.Clamp(request.HvacActionAlibiTransitionWindowSeconds, 5, 1800);
            state.Settings.HvacActionAlibiMaxHoldMinutes = Math.Clamp(request.HvacActionAlibiMaxHoldMinutes, 1, 240);
            state.Settings.HvacActionAlibiSafetyBandCelsius = Math.Round(Math.Clamp(request.HvacActionAlibiSafetyBandCelsius, 0.1, 5.0), 1);
            if (!state.Settings.HvacActionAlibiEnabled)
            {
                ClearHvacActionAlibi("HVAC alibi is off.");
            }
            state.Settings.CoolingRunwayGuardEnabled = request.CoolingRunwayGuardEnabled;
            state.Settings.CoolingRunwayMinimumSeconds = Math.Clamp(request.CoolingRunwayMinimumSeconds, 0, 1800);
            state.Settings.CoolingRunwayPressureExtraSeconds = Math.Clamp(request.CoolingRunwayPressureExtraSeconds, 0, 3600);
            state.Settings.CoolingRunwaySafetyBandCelsius = Math.Round(Math.Clamp(request.CoolingRunwaySafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.RoomTrendGuardEnabled = request.RoomTrendGuardEnabled;
            state.Settings.RoomTrendWindowMinutes = Math.Clamp(request.RoomTrendWindowMinutes, 2, 240);
            state.Settings.RoomTrendStableToleranceCelsius = Math.Round(Math.Clamp(request.RoomTrendStableToleranceCelsius, 0.05, 2.0), 2);
            state.Settings.RoomTrendHoldMinutes = Math.Clamp(request.RoomTrendHoldMinutes, 1, 120);
            state.Settings.ThermalMomentumGuardEnabled = request.ThermalMomentumGuardEnabled;
            state.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour = Math.Round(Math.Clamp(request.ThermalMomentumMinimumCoolingRateCelsiusPerHour, 0.1, 5.0), 2);
            state.Settings.ThermalMomentumLookAheadMinutes = Math.Clamp(request.ThermalMomentumLookAheadMinutes, 5, 240);
            state.Settings.ThermalMomentumHoldMinutes = Math.Clamp(request.ThermalMomentumHoldMinutes, 1, 120);
            state.Settings.WeatherDriftGuardEnabled = request.WeatherDriftGuardEnabled;
            state.Settings.WeatherDriftWindowMinutes = Math.Clamp(request.WeatherDriftWindowMinutes, 5, 1440);
            state.Settings.WeatherDriftMinimumChangeCelsius = Math.Round(Math.Clamp(request.WeatherDriftMinimumChangeCelsius, 0.1, 5.0), 1);
            state.Settings.WeatherDriftHoldMinutes = Math.Clamp(request.WeatherDriftHoldMinutes, 1, 120);
            state.Settings.WeatherDriftSafetyBandCelsius = Math.Round(Math.Clamp(request.WeatherDriftSafetyBandCelsius, 0.1, 5.0), 1);
            state.Settings.PeakPowerSaverEnabled = request.PeakPowerSaverEnabled;
            state.Settings.PeakPowerSaverOnPeakEnabled = request.PeakPowerSaverOnPeakEnabled;
            state.Settings.PeakPowerSaverHighPowerEnabled = request.PeakPowerSaverHighPowerEnabled;
            state.Settings.PeakPowerSaverPowerThresholdKilowatts = Math.Round(Math.Clamp(request.PeakPowerSaverPowerThresholdKilowatts, 0.1, 50.0), 1);
            state.Settings.PeakPowerSaverPriceThresholdCentsPerKwh = Math.Round(Math.Clamp(request.PeakPowerSaverPriceThresholdCentsPerKwh, 0.0, 200.0), 1);
            state.Settings.PeakPowerSaverHoldMinutes = Math.Clamp(request.PeakPowerSaverHoldMinutes, 1, 240);
            state.Settings.PeakPowerSaverRefreshSeconds = Math.Clamp(request.PeakPowerSaverRefreshSeconds, 30, 3600);
            state.Settings.PeakPowerSaverSafetyBandCelsius = Math.Round(Math.Clamp(request.PeakPowerSaverSafetyBandCelsius, 0.1, 10.0), 1);
            state.Settings.PeakPowerSaverFanSaverEnabled = request.PeakPowerSaverFanSaverEnabled;
            state.Settings.PeakPowerSaverFanMode = string.IsNullOrWhiteSpace(request.PeakPowerSaverFanMode)
                ? "auto"
                : request.PeakPowerSaverFanMode.Trim();
            if (!state.Settings.PeakPowerSaverEnabled)
            {
                ClearPeakPowerSaver("Alectra Peak Power Saver is off.");
            }
            state.Settings.FrontDoorKillSwitchEnabled = request.FrontDoorKillSwitchEnabled;
            state.Settings.FrontDoorPersonEntityIds = request.FrontDoorPersonEntityIds?.Trim() ?? string.Empty;
            state.Settings.FrontDoorKillSwitchHoldMinutes = Math.Clamp(request.FrontDoorKillSwitchHoldMinutes, 1, 240);
            state.Settings.FrontDoorKillSwitchRefreshSeconds = Math.Clamp(request.FrontDoorKillSwitchRefreshSeconds, 2, 300);
            state.Settings.FrontDoorKillSwitchTurnsThermostatOff = request.FrontDoorKillSwitchTurnsThermostatOff;
            if (!state.Settings.FrontDoorKillSwitchEnabled)
            {
                ClearFrontDoorKillSwitch("Front-door guard post is off. The little sentry went for juice.");
            }
            state.Settings.SuperDefenderModeEnabled = request.SuperDefenderModeEnabled;
            state.Settings.SuperDefenderRemoteChangeThreshold = Math.Clamp(request.SuperDefenderRemoteChangeThreshold, 1, 20);
            state.Settings.SuperDefenderWindowMinutes = Math.Clamp(request.SuperDefenderWindowMinutes, 1, 1440);
            state.Settings.SuperDefenderHoldMinutes = Math.Clamp(request.SuperDefenderHoldMinutes, 0, 240);
            state.Settings.SuperDefenderSafetyBandCelsius = Math.Round(Math.Clamp(request.SuperDefenderSafetyBandCelsius, 0.1, 10.0), 1);
            state.Settings.SuperDefenderBypassQuietTiming = request.SuperDefenderBypassQuietTiming;
            if (!state.Settings.SuperDefenderModeEnabled)
            {
                ClearSuperDefender("Super Defender is off.");
            }
            state.Settings.FanEnergySaverEnabled = request.FanEnergySaverEnabled;
            state.Settings.FanEnergySaverThresholdCelsius = Math.Clamp(request.FanEnergySaverThresholdCelsius, 0.1, 5.0);
            state.Settings.FanEnergySaverMode = string.IsNullOrWhiteSpace(request.FanEnergySaverMode)
                ? "auto"
                : request.FanEnergySaverMode.Trim();
            state.Settings.UpstairsComfortEnabled = request.UpstairsComfortEnabled;
            state.Settings.UpstairsTemperatureEntityIds = request.UpstairsTemperatureEntityIds?.Trim() ?? string.Empty;
            state.Settings.UpstairsMaxComfortCelsius = Math.Round(Math.Clamp(request.UpstairsMaxComfortCelsius, 15, 35), 1);
            state.Settings.UpstairsComfortTargetCelsius = Math.Round(Math.Clamp(request.UpstairsComfortTargetCelsius, 10, 30), 1);
            state.Settings.UpstairsComfortBoostCelsius = Math.Round(Math.Clamp(request.UpstairsComfortBoostCelsius, 0, 5), 1);
            state.Settings.HomePresenceRequired = request.HomePresenceRequired;
            state.Settings.PresenceEntityIds = request.PresenceEntityIds?.Trim() ?? string.Empty;
            state.Settings.DefenderRunsContinuously = true;
            state.Schedule = request.Schedule
                .Select(NormalizeScheduleEntry)
                .Take(24)
                .ToList();
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("info", "Settings updated.");
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordWeatherReading(WeatherReading? reading)
    {
        lock (gate)
        {
            if (reading is not null)
            {
                state.Weather = new WeatherRuntimeState
                {
                    OutdoorTemperatureCelsius = reading.OutdoorTemperatureCelsius,
                    Condition = reading.Condition,
                    EntityId = reading.EntityId,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                RecordWeatherSample(reading, state.Weather.UpdatedAt);
            }

            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    public bool ShouldRefreshPeakPowerSaver(DateTimeOffset now)
    {
        lock (gate)
        {
            if (!state.Settings.PeakPowerSaverEnabled)
            {
                ClearPeakPowerSaver("Alectra Peak Power Saver is off.");
                return false;
            }

            if (state.AlectraPeakPower is null)
            {
                return true;
            }

            var refreshSeconds = Math.Clamp(state.Settings.PeakPowerSaverRefreshSeconds, 30, 3600);
            return now - state.AlectraPeakPower.UpdatedAt >= TimeSpan.FromSeconds(refreshSeconds);
        }
    }

    public DefenderSnapshot RecordAlectraPeakPowerReading(AlectraPeakPowerReading reading)
    {
        lock (gate)
        {
            state.AlectraPeakPower = reading;
            if (!state.Settings.PeakPowerSaverEnabled)
            {
                ClearPeakPowerSaver("Alectra Peak Power Saver is off.");
                state.UpdatedAt = DateTimeOffset.UtcNow;
                SaveState();
                return CreateSnapshot();
            }

            if (BuildPeakPowerReasons(reading).Count > 0)
            {
                var wasActive = IsPeakPowerSaverActive(DateTimeOffset.UtcNow);
                state.PeakPowerSaverUntil = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, state.Settings.PeakPowerSaverHoldMinutes));
                state.PeakPowerSaverStatus = $"Alectra Peak Power Saver active: {BuildPeakPowerSummary(reading)}.";
                if (!wasActive)
                {
                    AddEvent("warning", $"Alectra Peak Power Saver active: {BuildPeakPowerSummary(reading)}.");
                }
            }
            else
            {
                state.PeakPowerSaverUntil = null;
                state.PeakPowerSaverStatus = $"Alectra power is normal: {BuildPeakPowerSummary(reading)}.";
            }

            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordAlectraPeakPowerUnavailable(string message)
    {
        lock (gate)
        {
            state.AlectraPeakPower = null;
            state.PeakPowerSaverUntil = null;
            state.PeakPowerSaverStatus = $"Alectra Peak Power Saver could not read usage sensors: {message}";
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    public bool ShouldRefreshFrontDoorKillSwitch(DateTimeOffset now)
    {
        lock (gate)
        {
            if (!state.Settings.FrontDoorKillSwitchEnabled)
            {
                ClearFrontDoorKillSwitch("Front-door guard post is off. The little sentry went for juice.");
                return false;
            }

            if (state.FrontDoorPersonReadings.Count == 0)
            {
                return true;
            }

            if (state.FrontDoorKillSwitchUpdatedAt is not { } lastPollAt)
            {
                return true;
            }

            var refreshSeconds = Math.Clamp(state.Settings.FrontDoorKillSwitchRefreshSeconds, 2, 300);
            return now - lastPollAt >= TimeSpan.FromSeconds(refreshSeconds);
        }
    }

    public DefenderSnapshot RecordFrontDoorPersonReadings(IReadOnlyList<FrontDoorPersonReading> readings)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            state.FrontDoorPersonReadings = readings
                .OrderByDescending(item => item.PersonDetected)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            state.FrontDoorKillSwitchUpdatedAt = now;

            if (!state.Settings.FrontDoorKillSwitchEnabled)
            {
                ClearFrontDoorKillSwitch("Front-door guard post is off. The little sentry went for juice.");
                state.UpdatedAt = now;
                SaveState();
                return CreateSnapshot();
            }

            var detected = state.FrontDoorPersonReadings.FirstOrDefault(item => item.PersonDetected);
            if (detected is not null)
            {
                var wasActive = IsFrontDoorKillSwitchActive(now);
                var holdMinutes = Math.Max(1, state.Settings.FrontDoorKillSwitchHoldMinutes);
                state.FrontDoorKillSwitchTriggeredAt = now;
                state.FrontDoorKillSwitchUntil = now.AddMinutes(holdMinutes);
                state.FrontDoorKillSwitchLastDetector = $"{detected.Name} ({detected.EntityId})";
                state.DefenderEnabled = false;
                var offPlan = state.Settings.FrontDoorKillSwitchTurnsThermostatOff
                    ? "thermostat off order is ready"
                    : "thermostat off is disabled";
                state.FrontDoorKillSwitchStatus =
                    $"Front-door guard post saw {detected.Name}; defender paused and {offPlan}. Guard report: totally normal hallway business.";
                state.NextAction = state.FrontDoorKillSwitchStatus;
                state.NextActionAt = state.FrontDoorKillSwitchUntil;
                if (!wasActive)
                {
                    AddEvent("warning", $"Front-door kill switch fired from {detected.EntityId}; defender paused.");
                }
            }
            else if (state.FrontDoorKillSwitchUntil is { } until && until > now)
            {
                state.FrontDoorKillSwitchStatus =
                    $"Front door is clear; guard post is keeping the defender paused until {until.ToLocalTime():HH:mm:ss}. Very official, very tiny clipboard.";
            }
            else
            {
                ClearFrontDoorKillSwitch(readings.Count == 0
                    ? "Front-door guard post is armed, but no matching real Home Assistant detector has reported yet."
                    : "Front-door guard post is armed and clear.");
            }

            state.UpdatedAt = now;
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordFrontDoorKillSwitchUnavailable(string message)
    {
        lock (gate)
        {
            state.FrontDoorPersonReadings = [];
            state.FrontDoorKillSwitchUpdatedAt = DateTimeOffset.UtcNow;
            state.FrontDoorKillSwitchStatus = $"Front-door guard post could not read detectors: {message}";
            state.UpdatedAt = state.FrontDoorKillSwitchUpdatedAt.Value;
            SaveState();
            return CreateSnapshot();
        }
    }

    public bool TryRespectFrontDoorKillSwitch(
        ThermostatReading reading,
        DateTimeOffset now,
        out bool shouldTurnThermostatOff,
        out DateTimeOffset? waitUntil,
        out string message)
    {
        lock (gate)
        {
            shouldTurnThermostatOff = false;
            waitUntil = null;
            message = string.Empty;

            if (!state.Settings.FrontDoorKillSwitchEnabled)
            {
                ClearFrontDoorKillSwitch("Front-door guard post is off. The little sentry went for juice.");
                return false;
            }

            if (!IsFrontDoorKillSwitchActive(now))
            {
                return false;
            }

            waitUntil = state.FrontDoorKillSwitchUntil;
            message = string.IsNullOrWhiteSpace(state.FrontDoorKillSwitchStatus)
                ? "Front-door kill switch is active; defender is paused."
                : state.FrontDoorKillSwitchStatus;
            state.FrontDoorKillSwitchStatus = message;
            state.DefenderEnabled = false;
            state.NextAction = message;
            state.NextActionAt = waitUntil;

            shouldTurnThermostatOff = state.Settings.FrontDoorKillSwitchTurnsThermostatOff
                && !string.Equals(reading.HvacMode, "off", StringComparison.OrdinalIgnoreCase)
                && (state.FrontDoorThermostatOffCommandedAt is null
                    || now - state.FrontDoorThermostatOffCommandedAt.Value > TimeSpan.FromSeconds(60));

            SaveState();
            return true;
        }
    }

    public DefenderSnapshot RecordFrontDoorThermostatOffCommand(string entityId)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            state.FrontDoorThermostatOffCommandedAt = now;
            RecordPendingThermostatCommand(
                commandedSetPointCelsius: null,
                commandedHvacMode: "off",
                commandedFanMode: null,
                commandSourceKind: "front-door-kill-switch",
                commandSourceLabel: "Front-door guard post",
                commandSourceDetail: "Front-door person detector triggered the kill switch; thermostat off was sent by AC Defender.");
            state.LastCommand = $"Home Assistant {entityId} thermostat turned off by front-door guard post.";
            state.FrontDoorKillSwitchStatus = "Front-door guard post sent thermostat OFF. The guards are whispering into plastic walkie-talkies.";
            state.NextAction = state.FrontDoorKillSwitchStatus;
            state.NextActionAt = state.FrontDoorKillSwitchUntil;
            state.UpdatedAt = now;
            AddEvent("warning", $"Front-door kill switch turned {entityId} off.");
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordComfortReadings(
        IReadOnlyList<TemperatureSensorReading> upstairsSensors,
        IReadOnlyList<PresenceReading> presence)
    {
        lock (gate)
        {
            var hottest = upstairsSensors
                .Where(sensor => sensor.TemperatureCelsius is not null)
                .OrderByDescending(sensor => sensor.TemperatureCelsius)
                .FirstOrDefault();
            var hasPresenceSignals = presence.Count > 0;
            state.IsHome = !hasPresenceSignals || presence.Any(item => item.IsHome);
            state.UpstairsSensors = upstairsSensors.ToList();
            state.Presence = presence.ToList();
            state.HottestUpstairsTemperatureCelsius = hottest?.TemperatureCelsius;
            state.HottestUpstairsEntityId = hottest?.EntityId;
            state.UpstairsTooHot = state.Settings.UpstairsComfortEnabled
                && (!state.Settings.HomePresenceRequired || state.IsHome)
                && hottest?.TemperatureCelsius is { } temp
                && temp > state.Settings.UpstairsMaxComfortCelsius;
            state.ComfortStatus = BuildComfortStatus();
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordHomeAssistantReading(ThermostatReading reading)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var previousHvacAction = state.HomeAssistantThermostat?.HvacAction;
            DetectExternalSetPointChange(reading, now);
            RecordRoomTemperatureSample(reading, now);
            RecordHomeAssistantReadingTime(now);

            state.ConnectionState = "home-assistant";
            state.HomeAssistantEntityId = reading.EntityId;
            state.HomeAssistantThermostat = new ThermostatRuntimeState
            {
                CurrentTemperatureCelsius = reading.CurrentTemperatureCelsius,
                SetPointCelsius = reading.SetPointCelsius,
                HvacMode = reading.HvacMode,
                HvacAction = reading.HvacAction,
                FanMode = reading.FanMode,
                AvailableFanModes = reading.AvailableFanModes.ToList(),
                ContextId = reading.Context?.Id,
                ContextParentId = reading.Context?.ParentId,
                ContextUserId = reading.Context?.UserId,
                UpdatedAt = now
            };
            TrackHvacActionAlibiTransition(reading, previousHvacAction, now);
            TrackCoolingRunway(reading, previousHvacAction, now);
            UpdateCoolingFailureDetection(reading, now);
            state.LastObservedSetPointCelsius = reading.SetPointCelsius;
            if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
            {
                state.CoolModeRestoreDueAt = null;
                state.CoolModeRestoreCommandedAt = null;
                state.CoolModeRestoreStatus = state.Settings.CoolModeRestoreDelayEnabled
                    ? "Cool mode is lined up."
                    : "Cool mode restore delay is off.";
            }

            state.LastError = null;
            state.UpdatedAt = now;
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordHomeAssistantUnavailable(string message)
    {
        lock (gate)
        {
            state.ConnectionState = "unavailable";
            state.LastError = message;
            state.NextAction = "Waiting for Home Assistant connection.";
            state.NextActionAt = DateTimeOffset.UtcNow.AddSeconds(options.PollIntervalSeconds);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("warning", message);
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordCommand(
        string message,
        double? commandedSetPointCelsius = null,
        string? commandedHvacMode = null,
        string? commandedFanMode = null,
        string commandSourceKind = "defender-service",
        string commandSourceLabel = "AC Defender",
        string commandSourceDetail = "AC Defender background service sent this Home Assistant command.")
    {
        lock (gate)
        {
            state.LastCommand = message;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            if (commandedSetPointCelsius is not null
                || !string.IsNullOrWhiteSpace(commandedHvacMode)
                || !string.IsNullOrWhiteSpace(commandedFanMode))
            {
                RecordPendingThermostatCommand(
                    commandedSetPointCelsius,
                    commandedHvacMode,
                    commandedFanMode,
                    commandSourceKind,
                    commandSourceLabel,
                    commandSourceDetail);
            }

            AddEvent("info", message);
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot SetNextAction(string message, DateTimeOffset? when = null)
    {
        lock (gate)
        {
            state.NextAction = message;
            state.NextActionAt = when;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    /// <summary>Dynamic Cooldown: true while the post-touch quiet period (min(max, base × touches) + jitter) is still running.</summary>
    public bool TryGetCooldown(DateTimeOffset now, out DateTimeOffset cooldownUntil)
    {
        lock (gate)
        {
            cooldownUntil = state.CooldownUntil ?? DateTimeOffset.MinValue;
            return state.CooldownUntil is { } until && until > now;
        }
    }

    public WebsiteCommandGateResult TryBeginWebsiteCommand(string commandName, bool bypassDebounce = false)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var cleanName = string.IsNullOrWhiteSpace(commandName)
                ? "website command"
                : commandName.Trim();

            if (!bypassDebounce
                && state.WebsiteCommandDebounceUntil is { } activeUntil
                && activeUntil > now)
            {
                var waitSeconds = (int)Math.Ceiling((activeUntil - now).TotalSeconds);
                var previousCommand = string.IsNullOrWhiteSpace(state.LastWebsiteCommandName)
                    ? "the last website command"
                    : state.LastWebsiteCommandName;
                var blockedMessage = $"Website debounce is active after {previousCommand}; wait {waitSeconds}s before {cleanName}.";
                state.WebsiteCommandDebounceStatus = blockedMessage;
                state.UpdatedAt = now;
                SaveState();
                return new WebsiteCommandGateResult(false, blockedMessage, CreateSnapshot());
            }

            if (bypassDebounce)
            {
                state.WebsiteCommandDebounceStatus = state.WebsiteCommandDebounceUntil is { } stillActiveUntil && stillActiveUntil > now
                    ? $"Website accepted {cleanName}; thermostat buttons still rest until {stillActiveUntil.ToLocalTime():HH:mm:ss}."
                    : $"Website accepted {cleanName}; no thermostat debounce needed.";
                state.UpdatedAt = now;
                AddEvent("info", $"Website command accepted without thermostat debounce: {cleanName}.");
                SaveState();
                return new WebsiteCommandGateResult(true, state.WebsiteCommandDebounceStatus, CreateSnapshot());
            }

            var debounceUntil = now.AddSeconds(WebsiteCommandDebounceSeconds);
            state.LastWebsiteCommandName = cleanName;
            state.LastWebsiteCommandAt = now;
            state.WebsiteCommandDebounceUntil = debounceUntil;
            state.WebsiteCommandDebounceStatus = $"Website accepted {cleanName}; controls rest until {debounceUntil.ToLocalTime():HH:mm:ss}.";
            state.UpdatedAt = now;
            AddEvent("info", $"Website command accepted: {cleanName}. Debounce active for {WebsiteCommandDebounceSeconds} seconds.");
            SaveState();
            return new WebsiteCommandGateResult(true, state.WebsiteCommandDebounceStatus, CreateSnapshot());
        }
    }

    public DefenderSnapshot ActivateEmergencyQuiet(string protocol, TimeSpan duration, string status, bool pauseDefender)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var cleanProtocol = string.IsNullOrWhiteSpace(protocol) ? "Emergency quiet" : protocol.Trim();
            state.EmergencyProtocol = cleanProtocol;
            state.EmergencyQuietUntil = now.Add(duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : duration);
            state.EmergencyStatus = status;
            if (pauseDefender)
            {
                state.DefenderEnabled = false;
            }

            state.NextAction = status;
            state.NextActionAt = state.EmergencyQuietUntil;
            state.UpdatedAt = now;
            AddEvent("warning", $"{cleanProtocol}: {status}");
            SaveState();
            return CreateSnapshot();
        }
    }

    /// <summary>Emergency Protocols: true while a too-cold, someone-upset, or suspicion window is suppressing corrections.</summary>
    public bool TryRespectEmergencyQuiet(DateTimeOffset now, out DateTimeOffset waitUntil, out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (state.EmergencyQuietUntil is not { } until)
            {
                state.EmergencyStatus = "No emergency quiet mode active.";
                return false;
            }

            if (until <= now)
            {
                ClearEmergencyQuiet("Emergency quiet ended; normal defender rules can resume.");
                SaveState();
                return false;
            }

            waitUntil = until;
            message = string.IsNullOrWhiteSpace(state.EmergencyStatus)
                ? $"Emergency quiet is active until {until.ToLocalTime():HH:mm:ss}; still reading the real thermostat."
                : state.EmergencyStatus;
            state.EmergencyStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Cool Mode Restore: true while the short safe delay before forcing the HVAC mode back to cool is still running.</summary>
    public bool TryDelayCoolModeRestore(
        ThermostatReading reading,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
            {
                state.CoolModeRestoreDueAt = null;
                state.CoolModeRestoreCommandedAt = null;
                state.CoolModeRestoreStatus = "Cool mode is lined up.";
                SaveState();
                return true;
            }

            if (state.CoolModeRestoreCommandedAt is { } commandedAt)
            {
                var commandGraceUntil = commandedAt.AddSeconds(Math.Max(15, options.CommandGraceSeconds));
                if (commandGraceUntil > now)
                {
                    waitUntil = commandGraceUntil;
                    message = $"Cool mode restore was sent; waiting for Home Assistant confirmation until {commandGraceUntil.ToLocalTime():HH:mm:ss}.";
                    state.CoolModeRestoreStatus = message;
                    SaveState();
                    return true;
                }

                state.CoolModeRestoreCommandedAt = null;
            }

            if (!state.Settings.CoolModeRestoreDelayEnabled)
            {
                state.CoolModeRestoreDueAt = null;
                state.CoolModeRestoreStatus = "Cool mode restore delay is off.";
                SaveState();
                return false;
            }

            if (ShouldBypassCoolModeRestoreDelay(reading))
            {
                state.CoolModeRestoreDueAt = null;
                state.CoolModeRestoreStatus = "Room comfort needs cool mode now, so restore delay is stepping aside.";
                SaveState();
                return false;
            }

            if (state.CoolModeRestoreDueAt is not { } dueAt || dueAt <= now)
            {
                dueAt = now.AddSeconds(CalculateCoolModeRestoreDelaySeconds());
                state.CoolModeRestoreDueAt = dueAt;
            }

            waitUntil = dueAt;
            message = $"Thermostat mode is {reading.HvacMode}; restoring cool mode at {dueAt.ToLocalTime():HH:mm:ss} unless comfort needs it sooner.";
            state.CoolModeRestoreStatus = message;
            SaveState();
            return dueAt > now;
        }
    }

    public void RecordCoolModeRestoreCommand(string previousMode)
    {
        lock (gate)
        {
            state.CoolModeRestoreDueAt = null;
            state.CoolModeRestoreCommandedAt = DateTimeOffset.UtcNow;
            state.CoolModeRestoreStatus = $"Cool mode restore sent from {previousMode}.";
            SaveState();
        }
    }

    /// <summary>Conflict Quiet: true while standing down after repeated wall touches, as long as the room stays inside the comfort band.</summary>
    public bool TryRespectConflictQuietMode(
        ThermostatReading reading,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.ConflictQuietModeEnabled)
            {
                state.ConflictQuietUntil = null;
                state.ConflictQuietStatus = "Conflict quiet is off.";
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            var recentTouches = state.ExternalTouchTimes.Count;
            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                state.ConflictQuietUntil = null;
                state.ConflictQuietStatus = "Room comfort needs help now, so conflict quiet is stepping aside.";
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.ConflictQuietComfortBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                state.ConflictQuietUntil = null;
                state.ConflictQuietStatus = $"Room rose above {allowedRoomTemperature:0.0} C, so conflict quiet ended.";
                SaveState();
                return false;
            }

            if (state.ConflictQuietUntil is { } activeUntil && activeUntil > now)
            {
                waitUntil = activeUntil;
                message = $"Repeated wall touches noticed; standing down until {activeUntil.ToLocalTime():HH:mm:ss} unless room rises above {allowedRoomTemperature:0.0} C.";
                state.ConflictQuietStatus = message;
                SaveState();
                return true;
            }

            if (recentTouches < state.Settings.ConflictQuietTouchThreshold)
            {
                state.ConflictQuietUntil = null;
                state.ConflictQuietStatus = $"Conflict quiet is watching for repeated wall touches ({recentTouches}/{state.Settings.ConflictQuietTouchThreshold}).";
                SaveState();
                return false;
            }

            var holdUntil = now.AddMinutes(state.Settings.ConflictQuietMinutes);
            state.ConflictQuietUntil = holdUntil;
            waitUntil = holdUntil;
            message = $"Repeated wall touches noticed; standing down until {holdUntil.ToLocalTime():HH:mm:ss} unless room rises above {allowedRoomTemperature:0.0} C.";
            state.ConflictQuietStatus = message;
            AddEvent("warning", $"Conflict quiet activated after {recentTouches} wall touches.");
            SaveState();
            return true;
        }
    }

    public bool TryRespectWallSettlingGuard(
        ThermostatReading reading,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.WallSettlingGuardEnabled)
            {
                ClearWallSettling("Wall settling guard is off.");
                SaveState();
                return false;
            }

            var recentTouches = GetRecentWallSettlingTouches(now);
            var recentTouchCount = recentTouches.Count;
            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                ClearWallSettling("Room comfort needs help now, so wall settling is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.WallSettlingSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearWallSettling($"Room rose above {allowedRoomTemperature:0.0} C, so wall settling ended.");
                SaveState();
                return false;
            }

            var minimumTouches = Math.Max(1, state.Settings.WallSettlingMinimumTouches);
            if (recentTouchCount < minimumTouches)
            {
                ClearWallSettling($"Wall settling is watching for active wall adjustments ({recentTouchCount}/{minimumTouches}).");
                SaveState();
                return false;
            }

            var settleSeconds = CalculateWallSettlingSeconds(recentTouchCount);
            if (settleSeconds <= 0)
            {
                ClearWallSettling("Wall settling has 0 seconds configured, so it is only watching.");
                SaveState();
                return false;
            }

            var latestTouch = recentTouches.Max();
            var settleUntil = latestTouch.AddSeconds(settleSeconds);
            if (settleUntil <= now)
            {
                ClearWallSettling($"Wall thermostat settled after {recentTouchCount} recent touches; the helper can continue.");
                SaveState();
                return false;
            }

            state.WallSettlingUntil = settleUntil;
            waitUntil = settleUntil;
            message = $"Wall thermostat is still settling after {recentTouchCount} touches; waiting until {settleUntil.ToLocalTime():HH:mm:ss} before correcting unless room rises above {allowedRoomTemperature:0.0} C.";
            state.WallSettlingStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Manual Comfort Grace: true while leaving a wall change alone in a still-comfortable room (can be extended by Touch Intent).</summary>
    public bool TryRespectManualComfortGrace(
        ThermostatReading reading,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.ManualComfortGraceEnabled
                || state.ManualComfortGraceUntil is not { } graceUntil
                || graceUntil <= now)
            {
                ClearManualComfortGrace();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                state.ManualComfortGraceStatus = "Room comfort needs help now, so wall-change grace is stepping aside.";
                state.ManualComfortGraceUntil = null;
                SaveState();
                return false;
            }

            var intent = BuildTouchIntentAnalysis(now);
            var warmerIntentActive = state.Settings.TouchIntentEnabled
                && intent.Active
                && intent.Direction == "warmer";
            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.ManualComfortGraceBandCelsius;
            if (warmerIntentActive)
            {
                allowedRoomTemperature = Math.Max(
                    allowedRoomTemperature,
                    state.TargetTemperatureCelsius + state.Settings.TouchIntentSafetyBandCelsius);
                state.TouchIntentStatus = intent.Status;
            }

            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                state.ManualComfortGraceStatus = $"Room rose above {allowedRoomTemperature:0.0} C, so wall-change grace ended.";
                state.ManualComfortGraceUntil = null;
                SaveState();
                return false;
            }

            waitUntil = graceUntil;
            var intentText = warmerIntentActive ? " with warmer touch intent" : string.Empty;
            message = $"Room is still comfortable after wall change{intentText}; holding until {graceUntil.ToLocalTime():HH:mm:ss} unless room rises above {allowedRoomTemperature:0.0} C.";
            state.ManualComfortGraceStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Cooler Intent Fast Lane: true when repeated cooler wall touches should skip the quiet waits while the room is above target.</summary>
    public bool ShouldBypassQuietTimingForCoolerIntent(ThermostatReading reading, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!state.Settings.CoolerIntentFastLaneEnabled)
            {
                ClearCoolerIntent("Cooler intent fast lane is off.");
                SaveState();
                return false;
            }

            if (state.CoolerIntentUntil is not { } until || until <= now)
            {
                ClearCoolerIntent("Cooler intent fast lane is watching for repeated cooler wall touches.");
                SaveState();
                return false;
            }

            if (reading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius)
            {
                ClearCoolerIntent("Room reached the website target, so cooler intent fast lane is resting.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.CoolerIntentSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature && !ShouldBypassNaturalRecovery(reading))
            {
                state.CoolerIntentStatus = $"Cooler intent fast lane is active, but room is above {allowedRoomTemperature:0.0} C so normal comfort safety is leading.";
                SaveState();
                return false;
            }

            var analysis = BuildCoolerIntentAnalysis(now);
            state.CoolerIntentStatus = $"Cooler intent fast lane is active until {until.ToLocalTime():HH:mm:ss}; repeated cooler touches skip quiet waits while the room is above target.";
            state.TouchIntentStatus = analysis.Status;
            SaveState();
            return true;
        }
    }

    /// <summary>Super Defender bypasses subtle waits after repeated Home Assistant-origin changes while the room still needs cooling.</summary>
    public bool ShouldBypassQuietTimingForSuperDefender(ThermostatReading reading, DateTimeOffset now)
    {
        lock (gate)
        {
            PruneRemoteChangeTimes(now);

            if (!state.Settings.SuperDefenderModeEnabled)
            {
                ClearSuperDefender("Super Defender is off.");
                SaveState();
                return false;
            }

            if (state.SuperDefenderUntil is not { } until || until <= now)
            {
                state.SuperDefenderUntil = null;
                state.SuperDefenderStatus = "Super Defender is watching for repeated phone or Home Assistant changes.";
                SaveState();
                return false;
            }

            if (!state.Settings.SuperDefenderBypassQuietTiming)
            {
                state.SuperDefenderStatus = $"Super Defender is active until {until.ToLocalTime():HH:mm:ss}, but quiet bypass is off.";
                SaveState();
                return false;
            }

            if (reading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius)
            {
                state.SuperDefenderStatus = "Super Defender is active, but the room is already at target so it is only watching.";
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.SuperDefenderSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature && !ShouldBypassNaturalRecovery(reading))
            {
                state.SuperDefenderStatus = $"Super Defender is active, but room is above {allowedRoomTemperature:0.0} C so normal comfort safety is leading.";
                SaveState();
                return false;
            }

            state.SuperDefenderStatus = $"Super Defender is active until {until.ToLocalTime():HH:mm:ss}; repeated phone/Home Assistant changes are bypassing quiet waits while cooling is needed.";
            SaveState();
            return true;
        }
    }

    /// <summary>Room Trend Guard: true while real room samples show the room is already stable or cooling on its own.</summary>
    public bool TryRespectRoomTrendGuard(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.RoomTrendGuardEnabled)
            {
                state.RoomTrendStatus = "Room trend guard is off.";
                state.RoomTrendHoldUntil = null;
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            if (state.ExternalTouchTimes.Count == 0)
            {
                state.RoomTrendHoldUntil = null;
                state.RoomTrendStatus = "No recent wall touch, so trend guard is only watching.";
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                state.RoomTrendHoldUntil = null;
                state.RoomTrendStatus = "Room comfort needs help now, so trend guard is stepping aside.";
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.ManualComfortGraceBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                state.RoomTrendHoldUntil = null;
                state.RoomTrendStatus = $"Room is above {allowedRoomTemperature:0.0} C, so trend guard lets correction continue.";
                SaveState();
                return false;
            }

            var trend = BuildRoomTrend(now);
            if (trend.SampleCount < 2)
            {
                state.RoomTrendStatus = "Room trend guard is collecting more real temperature readings.";
                SaveState();
                return false;
            }

            if (trend.Direction == "warming")
            {
                state.RoomTrendHoldUntil = null;
                state.RoomTrendStatus = $"Room is warming by {trend.DeltaCelsius:0.0} C, so correction can continue.";
                SaveState();
                return false;
            }

            if (state.RoomTrendHoldUntil is not { } holdUntil || holdUntil <= now)
            {
                holdUntil = now.AddMinutes(state.Settings.RoomTrendHoldMinutes);
                state.RoomTrendHoldUntil = holdUntil;
            }

            waitUntil = holdUntil;
            message = $"Room trend is {trend.Direction}; observing until {holdUntil.ToLocalTime():HH:mm:ss} before nudging toward {expectedSetPointCelsius:0.0} C.";
            state.RoomTrendStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Thermal Momentum: true while the room is cooling fast enough (≥ min rate) to reach target within the look-ahead window.</summary>
    public bool TryRespectThermalMomentumGuard(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.ThermalMomentumGuardEnabled)
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = "Thermal momentum guard is off.";
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            if (state.ExternalTouchTimes.Count == 0)
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = "No recent wall touch, so thermal momentum is only watching.";
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = "Room comfort needs help now, so thermal momentum is stepping aside.";
                SaveState();
                return false;
            }

            if (reading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius)
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = "Room is already near target, so thermal momentum does not need to hold.";
                SaveState();
                return false;
            }

            if (reading.SetPointCelsius < expectedSetPointCelsius - 0.05)
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = "Thermostat is colder than the defender target, so thermal momentum lets it line up.";
                SaveState();
                return false;
            }

            var momentum = BuildThermalMomentum(now, reading.CurrentTemperatureCelsius);
            if (momentum.SampleCount < 2)
            {
                state.ThermalMomentumStatus = "Thermal momentum is collecting more real room readings.";
                SaveState();
                return false;
            }

            var rate = momentum.CoolingRateCelsiusPerHour;
            if (rate is null || rate.Value < state.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour)
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = rate is null
                    ? "Room is not cooling yet, so thermal momentum lets correction continue."
                    : $"Room is cooling at {rate.Value:0.0} C/hour, below the momentum threshold.";
                SaveState();
                return false;
            }

            var eta = momentum.EstimatedMinutesToTarget;
            if (eta is null || eta.Value > state.Settings.ThermalMomentumLookAheadMinutes)
            {
                state.ThermalMomentumHoldUntil = null;
                state.ThermalMomentumStatus = eta is null
                    ? "Thermal momentum cannot estimate arrival yet."
                    : $"Room may take about {eta.Value:0} min to reach target, so correction can continue.";
                SaveState();
                return false;
            }

            if (state.ThermalMomentumHoldUntil is not { } holdUntil || holdUntil <= now)
            {
                holdUntil = now.AddMinutes(state.Settings.ThermalMomentumHoldMinutes);
                state.ThermalMomentumHoldUntil = holdUntil;
            }

            waitUntil = holdUntil;
            message = $"Room is already cooling at {rate.Value:0.0} C/hour; holding until {holdUntil.ToLocalTime():HH:mm:ss} because target is estimated in {eta.Value:0} min.";
            state.ThermalMomentumStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Weather Drift Timing: true while waiting for real outdoor-temperature movement before a safe correction.</summary>
    public bool TryRespectWeatherDriftGuard(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.WeatherDriftGuardEnabled)
            {
                ClearWeatherDrift("Weather drift guard is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            if (state.ExternalTouchTimes.Count == 0)
            {
                ClearWeatherDrift("No recent wall touch, so weather drift is only watching.");
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                ClearWeatherDrift("Room comfort needs help now, so weather drift is stepping aside.");
                SaveState();
                return false;
            }

            if (reading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius)
            {
                ClearWeatherDrift("Room is already near target, so weather drift does not need to hold.");
                SaveState();
                return false;
            }

            if (reading.SetPointCelsius < expectedSetPointCelsius - 0.05)
            {
                ClearWeatherDrift("Thermostat is already colder than the defender target, so weather drift lets it line up.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.WeatherDriftSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearWeatherDrift($"Room is above {allowedRoomTemperature:0.0} C, so weather drift lets correction continue.");
                SaveState();
                return false;
            }

            var drift = BuildWeatherDrift(now);
            if (drift.SampleCount < 2)
            {
                state.WeatherDriftStatus = "Weather drift is collecting more real outdoor readings.";
                SaveState();
                return false;
            }

            var minimumChange = Math.Max(0.1, state.Settings.WeatherDriftMinimumChangeCelsius);
            if (drift.Direction == "warming" && drift.OutdoorDeltaCelsius is { } warmingDelta && warmingDelta >= minimumChange)
            {
                ClearWeatherDrift($"Outdoor temperature warmed by {warmingDelta:0.0} C, so the next correction can ride real weather drift.");
                SaveState();
                return false;
            }

            if (drift.ConditionChanged)
            {
                ClearWeatherDrift("Weather condition changed, so the next safe correction can ride a real weather update.");
                SaveState();
                return false;
            }

            if (state.WeatherDriftHoldUntil is { } activeUntil)
            {
                if (activeUntil > now)
                {
                    waitUntil = activeUntil;
                    message = $"Outdoor weather is {drift.Direction}; holding safe correction until {activeUntil.ToLocalTime():HH:mm:ss} for a more natural weather-drift slot.";
                    state.WeatherDriftStatus = message;
                    SaveState();
                    return true;
                }

                ClearWeatherDrift("Weather drift hold ended; the safe correction can continue.");
                SaveState();
                return false;
            }

            var holdMinutes = Math.Max(1, state.Settings.WeatherDriftHoldMinutes);
            waitUntil = now.AddMinutes(holdMinutes);
            state.WeatherDriftHoldUntil = waitUntil;
            message = $"Outdoor weather is {drift.Direction} ({drift.OutdoorDeltaCelsius:+0.0;-0.0;0.0} C); holding safe correction until {waitUntil.ToLocalTime():HH:mm:ss} unless the room warms.";
            state.WeatherDriftStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Sensor Rhythm: true while waiting until just after the learned Home Assistant reading beat plus jitter.</summary>
    public bool TryRespectSensorRhythm(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.SensorRhythmGuardEnabled)
            {
                ClearSensorRhythm("Sensor rhythm is off.");
                SaveState();
                return false;
            }

            var analysis = BuildSensorRhythmAnalysis(now);
            var minimumSamples = Math.Max(2, state.Settings.SensorRhythmMinimumSamples);
            if (analysis.SampleCount < minimumSamples || analysis.MedianIntervalSeconds <= 0)
            {
                ClearSensorRhythm($"Sensor rhythm is learning Home Assistant beats ({analysis.SampleCount}/{minimumSamples}).");
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                ClearSensorRhythm("Room comfort needs help now, so sensor rhythm is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.SensorRhythmSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearSensorRhythm($"Room is above {allowedRoomTemperature:0.0} C, so sensor rhythm is stepping aside.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearSensorRhythm("Sensor rhythm is lined up; no beat wait needed.");
                SaveState();
                return false;
            }

            if (state.SensorRhythmDueAt is { } dueAt)
            {
                if (dueAt > now)
                {
                    waitUntil = dueAt;
                    message = $"Sensor rhythm is waiting until {dueAt.ToLocalTime():HH:mm:ss}, just after the learned {analysis.MedianIntervalSeconds}s Home Assistant beat.";
                    state.SensorRhythmStatus = message;
                    SaveState();
                    return true;
                }

                ClearSensorRhythm("Sensor rhythm beat arrived; safe correction can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateSensorRhythmDueAt(now, analysis);
            state.SensorRhythmDueAt = waitUntil;
            message = $"Sensor rhythm picked {waitUntil.ToLocalTime():HH:mm:ss} after the learned {analysis.MedianIntervalSeconds}s Home Assistant beat before the next safe nudge.";
            state.SensorRhythmStatus = message;
            SaveState();
            return waitUntil > now;
        }
    }

    /// <summary>HVAC Alibi: true while waiting for a real Home Assistant hvac_action transition before a safe correction.</summary>
    public bool TryRespectHvacActionAlibi(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.HvacActionAlibiEnabled)
            {
                ClearHvacActionAlibi("HVAC alibi is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            var triggerTouches = Math.Max(1, state.Settings.HvacActionAlibiTriggerTouches);
            var recentTouches = state.ExternalTouchTimes.Count;
            if (recentTouches < triggerTouches)
            {
                ClearHvacActionAlibi($"HVAC alibi is watching for repeated wall touches ({recentTouches}/{triggerTouches}).");
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                ClearHvacActionAlibi("Room comfort needs help now, so HVAC alibi is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.HvacActionAlibiSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearHvacActionAlibi($"Room is above {allowedRoomTemperature:0.0} C, so HVAC alibi lets direct comfort continue.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearHvacActionAlibi("HVAC alibi is lined up; no action transition wait is needed.");
                SaveState();
                return false;
            }

            var currentAction = NormalizeHvacAction(reading.HvacAction);
            var transitionWindow = TimeSpan.FromSeconds(Math.Clamp(state.Settings.HvacActionAlibiTransitionWindowSeconds, 5, 1800));
            if (state.HvacActionAlibiStartedAt is { } startedAt
                && state.HvacActionAlibiLastTransitionAt is { } transitionAt
                && transitionAt >= startedAt
                && now - transitionAt <= transitionWindow)
            {
                ClearHvacActionAlibi($"Real HVAC action changed to {currentAction}; safe correction can ride that normal transition.");
                SaveState();
                return false;
            }

            if (state.HvacActionAlibiStartedAt is null
                && state.HvacActionAlibiLastTransitionAt is { } recentTransitionAt
                && now - recentTransitionAt <= transitionWindow)
            {
                ClearHvacActionAlibi($"Recent real HVAC action transition gives this safe correction an alibi.");
                SaveState();
                return false;
            }

            if (state.HvacActionAlibiUntil is { } holdUntil)
            {
                if (holdUntil > now)
                {
                    waitUntil = holdUntil;
                    message = $"HVAC alibi is waiting until {holdUntil.ToLocalTime():HH:mm:ss} for the real action to move from '{currentAction}' before the next safe correction.";
                    state.HvacActionAlibiStatus = message;
                    SaveState();
                    return true;
                }

                ClearHvacActionAlibi("HVAC alibi max wait ended; safe correction can continue if still needed.");
                SaveState();
                return false;
            }

            state.HvacActionAlibiStartedAt = now;
            waitUntil = now.AddMinutes(Math.Clamp(state.Settings.HvacActionAlibiMaxHoldMinutes, 1, 240));
            state.HvacActionAlibiUntil = waitUntil;
            message = $"HVAC alibi is waiting until {waitUntil.ToLocalTime():HH:mm:ss} for a real hvac_action change from '{currentAction}' before the next safe correction.";
            state.HvacActionAlibiStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Cooling Runway: true while giving a fresh cooling cycle (min + pressure seconds from cooling start) time to work.</summary>
    public bool TryRespectCoolingRunway(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.CoolingRunwayGuardEnabled)
            {
                ClearCoolingRunway("Cooling runway is off.");
                SaveState();
                return false;
            }

            if (!IsCoolingAction(reading.HvacAction))
            {
                state.CoolingRunwayStartedAt = null;
                ClearCoolingRunway("Cooling runway is watching for a fresh cooling start.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearCoolingRunway("Cooling runway is lined up; no extra nudge is needed.");
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                ClearCoolingRunway("Room comfort needs help now, so cooling runway is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.CoolingRunwaySafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearCoolingRunway($"Room is above {allowedRoomTemperature:0.0} C, so cooling runway is stepping aside.");
                SaveState();
                return false;
            }

            if (state.CoolingRunwayStartedAt is not { } startedAt)
            {
                startedAt = now;
                state.CoolingRunwayStartedAt = startedAt;
            }

            if (state.CoolingRunwayHoldUntil is { } holdUntil)
            {
                if (holdUntil > now)
                {
                    waitUntil = holdUntil;
                    var pressure = CalculateCoolingRunwayPressure(now);
                    message = $"Cooling runway is letting the AC work until {holdUntil.ToLocalTime():HH:mm:ss} before another safe nudge from pressure {pressure}/100.";
                    state.CoolingRunwayStatus = message;
                    SaveState();
                    return true;
                }

                ClearCoolingRunway("Cooling runway finished; safe correction can continue if still needed.");
                SaveState();
                return false;
            }

            waitUntil = CalculateCoolingRunwayUntil(startedAt, now);
            if (waitUntil <= now)
            {
                ClearCoolingRunway("Cooling runway already had enough time; no hold needed.");
                SaveState();
                return false;
            }

            state.CoolingRunwayHoldUntil = waitUntil;
            var runwayPressure = CalculateCoolingRunwayPressure(now);
            message = $"Cooling runway is letting the AC work until {waitUntil.ToLocalTime():HH:mm:ss} before another safe nudge from pressure {runwayPressure}/100.";
            state.CoolingRunwayStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Setpoint Echo: true while waiting (up to the grace seconds) for Home Assistant to echo the last commanded setpoint.</summary>
    public bool TryRespectSetpointEcho(
        ThermostatReading reading,
        bool bypassForComfort,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.SetpointEchoGuardEnabled)
            {
                state.SetpointEchoStatus = "Setpoint echo guard is off.";
                SaveState();
                return false;
            }

            if (state.PendingCommandSetPointCelsius is not { } pendingSetPoint
                || state.PendingCommandAt is not { } pendingAt)
            {
                state.SetpointEchoStatus = "Setpoint echo is watching.";
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - pendingSetPoint) <= 0.15)
            {
                ClearPendingSetpointEcho($"Home Assistant echoed {pendingSetPoint:0.0} C; another correction can wait for normal rules.");
                SaveState();
                return false;
            }

            if (bypassForComfort || ShouldBypassNaturalRecovery(reading))
            {
                state.SetpointEchoStatus = "Room comfort needs help now, so setpoint echo is stepping aside.";
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.SetpointEchoSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                state.SetpointEchoStatus = $"Room is above {allowedRoomTemperature:0.0} C, so setpoint echo is stepping aside.";
                SaveState();
                return false;
            }

            waitUntil = pendingAt.AddSeconds(Math.Max(5, state.Settings.SetpointEchoGraceSeconds));
            if (waitUntil <= now)
            {
                if (now - pendingAt > TimeSpan.FromSeconds(Math.Max(options.CommandGraceSeconds, state.Settings.SetpointEchoGraceSeconds)))
                {
                    state.PendingCommandSetPointCelsius = null;
                    state.PendingCommandAt = null;
                }

                state.SetpointEchoStatus = $"Setpoint echo window ended for {pendingSetPoint:0.0} C; safe correction can continue.";
                SaveState();
                return false;
            }

            message = $"Setpoint echo is waiting until {waitUntil.ToLocalTime():HH:mm:ss} for Home Assistant to report {pendingSetPoint:0.0} C before another safe command.";
            state.SetpointEchoStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Repeat Quiet: true while holding an identical follow-up command (min wait + pressure seconds); distinct step-downs pass.</summary>
    public bool TryRespectRepeatCommandGuard(
        ThermostatReading reading,
        double commandSetPointCelsius,
        bool bypassRepeatGuard,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.RepeatCommandGuardEnabled)
            {
                ClearRepeatCommand("Repeat quiet is off.");
                SaveState();
                return false;
            }

            if (bypassRepeatGuard || ShouldBypassNaturalRecovery(reading))
            {
                ClearRepeatCommand("Room comfort needs help now, so repeat quiet is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.RepeatCommandSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearRepeatCommand($"Room is above {allowedRoomTemperature:0.0} C, so repeat quiet is stepping aside.");
                SaveState();
                return false;
            }

            if (state.LastDefenderCommandAt is not { } lastCommandAt
                || state.LastDefenderCommandSetPointCelsius is not { } lastSetPoint)
            {
                ClearRepeatCommand("Repeat quiet is watching for identical follow-up commands.");
                SaveState();
                return false;
            }

            if (Math.Abs(commandSetPointCelsius - lastSetPoint) > 0.15)
            {
                ClearRepeatCommand($"Repeat quiet sees a new {commandSetPointCelsius:0.0} C setpoint, so no repeat hold is needed.");
                SaveState();
                return false;
            }

            if (state.RepeatCommandHoldUntil is { } holdUntil)
            {
                if (holdUntil > now)
                {
                    waitUntil = holdUntil;
                    var pressure = CalculateRepeatCommandPressure(now);
                    message = $"Repeat quiet is holding another {commandSetPointCelsius:0.0} C command until {holdUntil.ToLocalTime():HH:mm:ss} from repeat pressure {pressure}/100.";
                    state.RepeatCommandStatus = message;
                    SaveState();
                    return true;
                }

                ClearRepeatCommand("Repeat quiet slot arrived; the identical command can continue if still needed.");
                SaveState();
                return false;
            }

            waitUntil = CalculateRepeatCommandHoldUntil(lastCommandAt, now);
            if (waitUntil <= now)
            {
                ClearRepeatCommand("Repeat quiet has enough spacing; no repeat hold is needed.");
                SaveState();
                return false;
            }

            state.RepeatCommandHoldUntil = waitUntil;
            var repeatPressure = CalculateRepeatCommandPressure(now);
            message = $"Repeat quiet is holding another {commandSetPointCelsius:0.0} C command until {waitUntil.ToLocalTime():HH:mm:ss} from repeat pressure {repeatPressure}/100.";
            state.RepeatCommandStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Comfort Sync (quiet recovery): true while a randomized wait, brief hold, or minimum command gap delays the correction.</summary>
    public bool TryDelayNaturalCorrection(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassNaturalTiming,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.NaturalRecoveryEnabled)
            {
                state.NaturalRecoveryStatus = "Quiet recovery is off; corrections happen as soon as rules allow.";
                SaveState();
                return false;
            }

            var plan = BuildNaturalRecoveryPlan(now);
            if (bypassNaturalTiming || ShouldBypassNaturalRecovery(reading))
            {
                state.NaturalHoldUntil = null;
                state.NaturalRecoveryStatus = $"Comfort is too warm, so {plan.QuietLevel.ToLowerInvariant()} recovery is stepping aside.";
                SaveState();
                return false;
            }

            if (state.NaturalHoldUntil is { } holdUntil && holdUntil > now)
            {
                waitUntil = holdUntil;
                message = $"Quiet recovery is waiting until {holdUntil.ToLocalTime():HH:mm:ss} before nudging the thermostat.";
                state.NaturalRecoveryStatus = message;
                SaveState();
                return true;
            }

            if (state.LastDefenderCommandAt is { } lastCommandAt)
            {
                var minimumGap = TimeSpan.FromSeconds(Math.Max(0, plan.CommandGapSeconds));
                var nextAllowed = lastCommandAt.Add(minimumGap);
                if (nextAllowed > now)
                {
                    waitUntil = nextAllowed;
                    message = $"Comfort sync is in {plan.QuietLevel.ToLowerInvariant()} mode and spacing commands until {nextAllowed.ToLocalTime():HH:mm:ss}.";
                    state.NaturalRecoveryStatus = message;
                    SaveState();
                    return true;
                }
            }

            if (state.NaturalHoldCount < plan.MaxHolds
                && random.Next(100) < plan.HoldChancePercent)
            {
                var holdSeconds = CalculateNaturalHoldSeconds();
                if (holdSeconds > 0)
                {
                    state.NaturalHoldCount++;
                    state.NaturalHoldUntil = now.AddSeconds(holdSeconds);
                    waitUntil = state.NaturalHoldUntil.Value;
                    message = $"{plan.QuietLevel} recovery is holding briefly until {waitUntil.ToLocalTime():HH:mm:ss}.";
                    state.NaturalRecoveryStatus = message;
                    SaveState();
                    return true;
                }
            }

            state.NaturalHoldUntil = null;
            message = $"{plan.QuietLevel} recovery ready; next correction uses the room-temperature target {expectedSetPointCelsius:0.0} C.";
            state.NaturalRecoveryStatus = message;
            SaveState();
            return false;
        }
    }

    /// <summary>Routine Timing: true while aligning a safe correction to the next interval boundary plus wiggle.</summary>
    public bool TryRespectRoutineTiming(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassRoutineTiming,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.RoutineTimingEnabled)
            {
                ClearRoutineTiming("Routine timing is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            var triggerTouches = Math.Max(1, state.Settings.RoutineTimingTriggerTouches);
            if (state.ExternalTouchTimes.Count < triggerTouches)
            {
                ClearRoutineTiming($"Routine timing is watching for repeated wall changes ({state.ExternalTouchTimes.Count}/{triggerTouches}).");
                SaveState();
                return false;
            }

            if (bypassRoutineTiming || ShouldBypassNaturalRecovery(reading))
            {
                ClearRoutineTiming("Room comfort needs help now, so routine timing is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.RoutineTimingSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearRoutineTiming($"Room is above {allowedRoomTemperature:0.0} C, so routine timing is stepping aside.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearRoutineTiming("Routine timing is lined up; no comfort-check slot needed.");
                SaveState();
                return false;
            }

            if (state.RoutineTimingDueAt is { } dueAt)
            {
                if (dueAt > now)
                {
                    waitUntil = dueAt;
                    message = $"Routine timing is holding until {dueAt.ToLocalTime():HH:mm:ss} so the next comfort adjustment lands on a normal rhythm.";
                    state.RoutineTimingStatus = message;
                    SaveState();
                    return true;
                }

                ClearRoutineTiming("Routine timing slot arrived; the next comfort adjustment can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateRoutineTimingDueAt(now);
            state.RoutineTimingDueAt = waitUntil;
            message = $"Routine timing is holding until {waitUntil.ToLocalTime():HH:mm:ss} so the next comfort adjustment lands on a normal rhythm.";
            state.RoutineTimingStatus = message;
            SaveState();
            return waitUntil > now;
        }
    }

    /// <summary>Comfort Budget: true while resting because too many safe corrections happened inside the rolling window.</summary>
    public bool TryRespectComfortBudget(
        ThermostatReading reading,
        bool bypassComfortBudget,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.ComfortBudgetEnabled)
            {
                ClearComfortBudget("Comfort budget is off.");
                SaveState();
                return false;
            }

            PruneDefenderCommandTimes(now);
            if (bypassComfortBudget || ShouldBypassNaturalRecovery(reading))
            {
                ClearComfortBudget("Room comfort needs help now, so comfort budget is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.ComfortBudgetSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearComfortBudget($"Room is above {allowedRoomTemperature:0.0} C, so comfort budget is stepping aside.");
                SaveState();
                return false;
            }

            var maxCommands = Math.Max(1, state.Settings.ComfortBudgetMaxCommands);
            if (state.DefenderCommandTimes.Count < maxCommands)
            {
                ClearComfortBudget($"Comfort budget has room for {maxCommands - state.DefenderCommandTimes.Count} more safe adjustments.");
                SaveState();
                return false;
            }

            var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.ComfortBudgetWindowMinutes));
            waitUntil = state.DefenderCommandTimes.Min().Add(window);
            if (waitUntil <= now)
            {
                PruneDefenderCommandTimes(now);
                ClearComfortBudget("Comfort budget window cleared; the next safe adjustment can continue.");
                SaveState();
                return false;
            }

            state.ComfortBudgetHoldUntil = waitUntil;
            message = $"Comfort budget is resting until {waitUntil.ToLocalTime():HH:mm:ss} after {state.DefenderCommandTimes.Count} recent safe adjustments.";
            state.ComfortBudgetStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Command Camouflage: true while spacing a safe follow-up after a recent visible helper command.</summary>
    public bool TryRespectCommandCamouflage(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassCommandCamouflage,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.CommandCamouflageEnabled)
            {
                ClearCommandCamouflage("Command camouflage is off.");
                SaveState();
                return false;
            }

            PruneDefenderCommandTimes(now);
            if (bypassCommandCamouflage || ShouldBypassNaturalRecovery(reading))
            {
                ClearCommandCamouflage("Room comfort needs help now, so command camouflage is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.CommandCamouflageSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearCommandCamouflage($"Room is above {allowedRoomTemperature:0.0} C, so command camouflage is stepping aside.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearCommandCamouflage("Command camouflage is lined up; no follow-up correction is needed.");
                SaveState();
                return false;
            }

            if (state.LastDefenderCommandAt is not { } lastCommandAt)
            {
                ClearCommandCamouflage("Command camouflage is watching for a recent helper command.");
                SaveState();
                return false;
            }

            if (state.CommandCamouflageHoldUntil is { } holdUntil)
            {
                if (holdUntil > now)
                {
                    waitUntil = holdUntil;
                    var pressure = CalculateCommandCamouflagePressure(now);
                    message = $"Command camouflage is letting the last helper command look normal until {holdUntil.ToLocalTime():HH:mm:ss} before another safe move toward {expectedSetPointCelsius:0.0} C. Pressure is {pressure}/100.";
                    state.CommandCamouflageStatus = message;
                    SaveState();
                    return true;
                }

                ClearCommandCamouflage("Command camouflage slot arrived; the next safe correction can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateCommandCamouflageUntil(lastCommandAt, now);
            if (waitUntil <= now)
            {
                ClearCommandCamouflage("Command camouflage has enough spacing; no helper-command cover is needed.");
                SaveState();
                return false;
            }

            state.CommandCamouflageHoldUntil = waitUntil;
            var camouflagePressure = CalculateCommandCamouflagePressure(now);
            message = $"Command camouflage is letting the last helper command look normal until {waitUntil.ToLocalTime():HH:mm:ss} before another safe move toward {expectedSetPointCelsius:0.0} C. Pressure is {camouflagePressure}/100.";
            state.CommandCamouflageStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Stealth Governor: true while high overall activity pressure calls for a low-profile safe-correction hold.</summary>
    public bool TryRespectStealthGovernor(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassStealthGovernor,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.StealthGovernorEnabled)
            {
                ClearStealthGovernor("Stealth governor is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            PruneDefenderCommandTimes(now);
            PruneRemoteChangeTimes(now);
            PruneVisibilityNoticeTimes(now);

            if (bypassStealthGovernor || ShouldBypassNaturalRecovery(reading))
            {
                ClearStealthGovernor("Room comfort needs help now, so stealth governor is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.StealthGovernorSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearStealthGovernor($"Room is above {allowedRoomTemperature:0.0} C, so stealth governor is stepping aside.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearStealthGovernor("Stealth governor is lined up; no low-profile hold is needed.");
                SaveState();
                return false;
            }

            var score = CalculateStealthGovernorScore(now);
            var triggerScore = Math.Clamp(state.Settings.StealthGovernorTriggerScore, 1, 100);
            if (score < triggerScore && state.StealthGovernorHoldUntil is null)
            {
                ClearStealthGovernor($"Stealth governor is watching pressure {score}/100 below trigger {triggerScore}/100.");
                SaveState();
                return false;
            }

            if (state.StealthGovernorHoldUntil is { } holdUntil)
            {
                if (holdUntil > now)
                {
                    waitUntil = holdUntil;
                    message = $"Stealth governor is holding safe correction until {holdUntil.ToLocalTime():HH:mm:ss}; pressure {score}/100 crossed trigger {triggerScore}/100.";
                    state.StealthGovernorStatus = message;
                    SaveState();
                    return true;
                }

                ClearStealthGovernor("Stealth governor low-profile window ended; the next safe correction can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateStealthGovernorUntil(score, now);
            state.StealthGovernorHoldUntil = waitUntil;
            message = $"Stealth governor chose a low-profile hold until {waitUntil.ToLocalTime():HH:mm:ss}; pressure {score}/100 crossed trigger {triggerScore}/100.";
            state.StealthGovernorStatus = message;
            SaveState();
            return waitUntil > now;
        }
    }

    /// <summary>Natural Cadence: true while waiting for a varied future slot (min-max minutes by pressure, plus jitter).</summary>
    public bool TryRespectNaturalCadence(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassNaturalCadence,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.NaturalCadenceEnabled)
            {
                ClearNaturalCadence("Natural cadence is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            PruneDefenderCommandTimes(now);
            var triggerTouches = Math.Max(1, state.Settings.NaturalCadenceTriggerTouches);
            if (state.ExternalTouchTimes.Count < triggerTouches)
            {
                ClearNaturalCadence($"Natural cadence is watching for repeated wall changes ({state.ExternalTouchTimes.Count}/{triggerTouches}).");
                SaveState();
                return false;
            }

            if (bypassNaturalCadence || ShouldBypassNaturalRecovery(reading))
            {
                ClearNaturalCadence("Room comfort needs help now, so natural cadence is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.NaturalCadenceSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearNaturalCadence($"Room is above {allowedRoomTemperature:0.0} C, so natural cadence is stepping aside.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearNaturalCadence("Natural cadence is lined up; no quiet slot needed.");
                SaveState();
                return false;
            }

            if (state.NaturalCadenceDueAt is { } dueAt)
            {
                if (dueAt > now)
                {
                    waitUntil = dueAt;
                    message = $"Natural cadence is waiting until {dueAt.ToLocalTime():HH:mm:ss} before the next safe comfort nudge.";
                    state.NaturalCadenceStatus = message;
                    SaveState();
                    return true;
                }

                ClearNaturalCadence("Natural cadence slot arrived; the next safe comfort nudge can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateNaturalCadenceDueAt(now);
            state.NaturalCadenceDueAt = waitUntil;
            var pressure = CalculateTouchSuspicionScore(now);
            message = $"Natural cadence picked {waitUntil.ToLocalTime():HH:mm:ss} from touch pressure {pressure}/100 before the next safe comfort nudge.";
            state.NaturalCadenceStatus = message;
            SaveState();
            return waitUntil > now;
        }
    }

    /// <summary>Comfort Pace: true while pacing frequent wall fighting into a calm weather, sensor-beat, or clock-aligned slot.</summary>
    public bool TryRespectNaturalChangePlanner(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassNaturalChange,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.NaturalChangePlannerEnabled)
            {
                ClearNaturalChangePlanner("Comfort Pace is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            PruneDefenderCommandTimes(now);
            var triggerTouches = Math.Max(1, state.Settings.NaturalChangePlannerTriggerTouches);
            var recentTouches = state.ExternalTouchTimes.Count;
            if (recentTouches < triggerTouches)
            {
                ClearNaturalChangePlanner($"Comfort Pace is watching for frequent wall changes ({recentTouches}/{triggerTouches}).");
                SaveState();
                return false;
            }

            if (bypassNaturalChange || ShouldBypassNaturalRecovery(reading))
            {
                ClearNaturalChangePlanner("Room comfort needs help now, so Comfort Pace is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.NaturalChangePlannerSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearNaturalChangePlanner($"Room is above {allowedRoomTemperature:0.0} C, so Comfort Pace lets the correction continue.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearNaturalChangePlanner("Comfort Pace is lined up; no natural slot needed.");
                SaveState();
                return false;
            }

            if (state.NaturalChangePlannerDueAt is { } dueAt)
            {
                if (dueAt > now)
                {
                    waitUntil = dueAt;
                    var pressure = CalculateTouchSuspicionScore(now);
                    message = $"Comfort Pace is waiting until {dueAt.ToLocalTime():HH:mm:ss} for a {state.NaturalChangePlannerReason} climate slot after {recentTouches} wall changes.";
                    state.NaturalChangePlannerStatus = $"{message} Touch pressure is {pressure}/100.";
                    SaveState();
                    return true;
                }

                ClearNaturalChangePlanner("Comfort Pace slot arrived; the next safe comfort nudge can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateNaturalChangePlannerDueAt(reading, now, out var reason);
            state.NaturalChangePlannerDueAt = waitUntil;
            state.NaturalChangePlannerReason = reason;
            var touchPressure = CalculateTouchSuspicionScore(now);
            message = $"Comfort Pace picked {waitUntil.ToLocalTime():HH:mm:ss} for a {reason} climate slot after {recentTouches} wall changes.";
            state.NaturalChangePlannerStatus = $"{message} Touch pressure is {touchPressure}/100.";
            SaveState();
            return waitUntil > now;
        }
    }

    /// <summary>Comfort Envelope: true while observing a tiny safe wall preference inside target ± max offset for the hold minutes.</summary>
    public bool TryRespectComfortEnvelope(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassEnvelope,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.ComfortEnvelopeEnabled)
            {
                ClearComfortEnvelope("Comfort envelope is off.");
                SaveState();
                return false;
            }

            PruneTouchTimes(now);
            var triggerTouches = Math.Max(1, state.Settings.ComfortEnvelopeTriggerTouches);
            var recentTouches = state.ExternalTouchTimes.Count;
            if (recentTouches < triggerTouches)
            {
                ClearComfortEnvelope($"Comfort envelope is watching for repeated wall preferences ({recentTouches}/{triggerTouches}).");
                SaveState();
                return false;
            }

            if (bypassEnvelope || ShouldBypassNaturalRecovery(reading))
            {
                ClearComfortEnvelope("Room comfort needs help now, so comfort envelope is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.ComfortEnvelopeSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearComfortEnvelope($"Room is above {allowedRoomTemperature:0.0} C, so comfort envelope lets correction continue.");
                SaveState();
                return false;
            }

            var maxOffset = Math.Max(0.1, state.Settings.ComfortEnvelopeMaxOffsetCelsius);
            var minimumAllowed = Math.Round(expectedSetPointCelsius - maxOffset, 1);
            var maximumAllowed = Math.Round(expectedSetPointCelsius + maxOffset, 1);
            if (reading.SetPointCelsius < minimumAllowed - 0.05 || reading.SetPointCelsius > maximumAllowed + 0.05)
            {
                ClearComfortEnvelope($"Wall preference is outside the {minimumAllowed:0.0}-{maximumAllowed:0.0} C comfort envelope.");
                SaveState();
                return false;
            }

            var holdMinutes = Math.Max(0, state.Settings.ComfortEnvelopeHoldMinutes);
            if (holdMinutes <= 0)
            {
                ClearComfortEnvelope("Comfort envelope hold is set to 0 min.");
                SaveState();
                return false;
            }

            if (state.ComfortEnvelopeUntil is not { } until || until <= now)
            {
                until = now.AddMinutes(holdMinutes);
                state.ComfortEnvelopeUntil = until;
            }

            state.ComfortEnvelopePreferredSetPointCelsius = Math.Round(reading.SetPointCelsius, 1);
            state.ComfortEnvelopeMinimumAllowedSetPointCelsius = minimumAllowed;
            state.ComfortEnvelopeMaximumAllowedSetPointCelsius = maximumAllowed;
            waitUntil = until;
            message = $"Comfort envelope is observing {reading.SetPointCelsius:0.0} C until {until.ToLocalTime():HH:mm:ss}; it is inside the safe {minimumAllowed:0.0}-{maximumAllowed:0.0} C range.";
            state.ComfortEnvelopeStatus = message;
            SaveState();
            return true;
        }
    }

    /// <summary>Visibility Guard: true while holding after a wall touch landed soon after a defender command (pressure-scaled hold).</summary>
    public bool TryRespectVisibilityGuard(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassVisibilityGuard,
        DateTimeOffset now,
        out DateTimeOffset waitUntil,
        out string message)
    {
        lock (gate)
        {
            waitUntil = DateTimeOffset.MinValue;
            message = string.Empty;

            if (!state.Settings.VisibilityGuardEnabled)
            {
                ClearVisibilityGuard("Visibility guard is off.");
                SaveState();
                return false;
            }

            PruneVisibilityNoticeTimes(now);
            var triggerNotices = Math.Max(1, state.Settings.VisibilityGuardTriggerNotices);
            if (state.VisibilityNoticeTimes.Count < triggerNotices)
            {
                ClearVisibilityGuard($"Visibility guard is watching for noticed corrections ({state.VisibilityNoticeTimes.Count}/{triggerNotices}).");
                SaveState();
                return false;
            }

            if (bypassVisibilityGuard || ShouldBypassNaturalRecovery(reading))
            {
                ClearVisibilityGuard("Room comfort needs help now, so visibility guard is stepping aside.");
                SaveState();
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.VisibilityGuardSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearVisibilityGuard($"Room is above {allowedRoomTemperature:0.0} C, so visibility guard is stepping aside.");
                SaveState();
                return false;
            }

            if (Math.Abs(reading.SetPointCelsius - expectedSetPointCelsius) <= 0.05)
            {
                ClearVisibilityGuard("Visibility guard is lined up; no quiet hold needed.");
                SaveState();
                return false;
            }

            if (state.VisibilityGuardUntil is { } until)
            {
                if (until > now)
                {
                    waitUntil = until;
                    message = $"Visibility guard is holding safe correction until {until.ToLocalTime():HH:mm:ss} after noticed thermostat activity.";
                    state.VisibilityGuardStatus = message;
                    SaveState();
                    return true;
                }

                ClearVisibilityGuard("Visibility guard hold ended; safe correction can continue.");
                SaveState();
                return false;
            }

            waitUntil = CalculateVisibilityGuardUntil(now);
            state.VisibilityGuardUntil = waitUntil;
            var pressure = CalculateVisibilityPressure(now);
            message = $"Visibility guard is holding safe correction until {waitUntil.ToLocalTime():HH:mm:ss} from visibility pressure {pressure}/100.";
            state.VisibilityGuardStatus = message;
            SaveState();
            return waitUntil > now;
        }
    }

    /// <summary>Shapes the final setpoint command size with Natural Walkback (small varied steps) and the Touch Signature cap.</summary>
    public double CalculateNaturalCommandSetPoint(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassNaturalStep)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var plan = BuildNaturalRecoveryPlan(now);
            if (!state.Settings.NaturalRecoveryEnabled
                || bypassNaturalStep
                || ShouldBypassNaturalRecovery(reading))
            {
                state.NaturalWalkbackStatus = state.Settings.NaturalWalkbackEnabled
                    ? "Natural walkback is standing aside because comfort needs a direct correction."
                    : "Natural walkback is off.";
                SaveState();
                return Math.Round(expectedSetPointCelsius, 1);
            }

            if (reading.CurrentTemperatureCelsius > state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius
                && reading.SetPointCelsius > expectedSetPointCelsius + 0.05)
            {
                // CalculateExpectedSetPoint anchors warm-room defense at room temp minus the active boost.
                state.NaturalWalkbackStatus = "Room needs cooling now, so warm-room defense uses the 1 C-below-room target.";
                SaveState();
                return Math.Round(expectedSetPointCelsius, 1);
            }

            var delta = expectedSetPointCelsius - reading.SetPointCelsius;
            var step = CalculateNaturalWalkbackStep(reading, expectedSetPointCelsius, plan, now);
            if (Math.Abs(delta) <= step)
            {
                state.NaturalWalkbackStatus = !state.Settings.NaturalWalkbackEnabled
                    ? "Natural walkback is off."
                    : Math.Abs(delta) <= 0.05
                    ? "Natural walkback is lined up; no setpoint walk needed."
                    : $"Natural walkback can finish with a {Math.Abs(delta):0.0} C nudge.";
                SaveState();
                return Math.Round(expectedSetPointCelsius, 1);
            }

            var commandSetPoint = reading.SetPointCelsius + Math.Sign(delta) * step;
            if (delta > 0)
            {
                commandSetPoint = Math.Min(commandSetPoint, expectedSetPointCelsius);
            }
            else
            {
                commandSetPoint = Math.Max(commandSetPoint, expectedSetPointCelsius);
            }

            commandSetPoint = Math.Round(commandSetPoint, 1);
            if (Math.Abs(commandSetPoint - reading.SetPointCelsius) <= 0.05)
            {
                commandSetPoint = Math.Round(reading.SetPointCelsius + Math.Sign(delta) * 0.1, 1);
            }

            state.NaturalWalkbackStatus = $"Natural walkback is using a {Math.Abs(commandSetPoint - reading.SetPointCelsius):0.0} C nudge toward {expectedSetPointCelsius:0.0} C after {state.ExternalTouchTimes.Count} recent wall touches.";
            SaveState();
            return commandSetPoint;
        }
    }

    /// <summary>Human Nudge: snaps safe follow-up commands to a normal thermostat-looking step.</summary>
    public double CalculateHumanNudgeCommandSetPoint(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        double candidateSetPointCelsius,
        bool bypassHumanNudge)
    {
        lock (gate)
        {
            var candidate = Math.Round(candidateSetPointCelsius, 1);
            if (!state.Settings.HumanNudgeEnabled)
            {
                ClearHumanNudge("Human nudge is off.");
                SaveState();
                return candidate;
            }

            var now = DateTimeOffset.UtcNow;
            PruneTouchTimes(now);
            var touches = state.ExternalTouchTimes.Count;
            var triggerTouches = Math.Max(1, state.Settings.HumanNudgeTriggerTouches);
            if (touches < triggerTouches)
            {
                ClearHumanNudge($"Human nudge is watching for repeated wall touches ({touches}/{triggerTouches}).");
                SaveState();
                return candidate;
            }

            if (bypassHumanNudge || ShouldBypassNaturalRecovery(reading))
            {
                ClearHumanNudge("Room comfort needs help now, so human nudge is stepping aside.");
                SaveState();
                return candidate;
            }

            if (reading.CurrentTemperatureCelsius > state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius
                && reading.SetPointCelsius > expectedSetPointCelsius + 0.05)
            {
                ClearHumanNudge("Warm-room defense is active, so the current-room-minus-1 C correction is not reshaped.");
                SaveState();
                return candidate;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.HumanNudgeSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                ClearHumanNudge($"Room is above {allowedRoomTemperature:0.0} C, so human nudge lets direct comfort continue.");
                SaveState();
                return candidate;
            }

            var delta = candidate - reading.SetPointCelsius;
            if (Math.Abs(delta) <= 0.05)
            {
                ClearHumanNudge("Human nudge is lined up; no command shaping is needed.");
                SaveState();
                return candidate;
            }

            var step = Math.Round(Math.Clamp(state.Settings.HumanNudgeStepCelsius, 0.1, 2.0), 1);
            if (Math.Abs(delta) <= step + 0.05)
            {
                ClearHumanNudge($"Human nudge sees a natural {Math.Abs(delta):0.0} C step toward {expectedSetPointCelsius:0.0} C.");
                SaveState();
                return candidate;
            }

            var direction = Math.Sign(delta);
            var oneStep = RoundToHumanStep(reading.SetPointCelsius + direction * step, step);
            double shaped;
            if (direction < 0)
            {
                if (oneStep >= reading.SetPointCelsius - 0.05)
                {
                    oneStep = Math.Round(reading.SetPointCelsius - step, 1);
                }

                shaped = Math.Max(candidate, oneStep);
                shaped = Math.Min(shaped, Math.Round(reading.SetPointCelsius - 0.1, 1));
            }
            else
            {
                if (oneStep <= reading.SetPointCelsius + 0.05)
                {
                    oneStep = Math.Round(reading.SetPointCelsius + step, 1);
                }

                shaped = Math.Min(candidate, oneStep);
                shaped = Math.Max(shaped, Math.Round(reading.SetPointCelsius + 0.1, 1));
            }

            shaped = Math.Round(shaped, 1);
            if (Math.Abs(shaped - reading.SetPointCelsius) <= 0.05)
            {
                ClearHumanNudge("Human nudge could not make a visible safe step, so it left the command unchanged.");
                SaveState();
                return candidate;
            }

            state.HumanNudgeActive = Math.Abs(shaped - candidate) > 0.05;
            state.HumanNudgeLastSetPointCelsius = shaped;
            state.HumanNudgeStatus = state.HumanNudgeActive
                ? $"Human nudge shaped {candidate:0.0} C into a normal {step:0.0} C step at {shaped:0.0} C after {touches} wall touches."
                : $"Human nudge allowed {candidate:0.0} C because it already looks like a normal thermostat step.";
            SaveState();
            return shaped;
        }
    }

    private double CalculateNaturalWalkbackStep(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        NaturalRecoveryPlan plan,
        DateTimeOffset now)
    {
        var baseStep = Math.Round(Math.Max(0.1, plan.StepCelsius), 1);
        if (!state.Settings.NaturalWalkbackEnabled)
        {
            state.NaturalWalkbackStatus = "Natural walkback is off.";
            return ApplyTouchSignatureStep(reading, baseStep, now);
        }

        PruneTouchTimes(now);
        var touches = state.ExternalTouchTimes.Count;
        var triggerTouches = Math.Max(1, state.Settings.NaturalWalkbackTriggerTouches);
        if (touches < triggerTouches)
        {
            state.NaturalWalkbackStatus = $"Natural walkback is watching for repeated wall touches ({touches}/{triggerTouches}).";
            return ApplyTouchSignatureStep(reading, baseStep, now);
        }

        var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.NaturalWalkbackSafetyBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
        {
            state.NaturalWalkbackStatus = $"Room is above {allowedRoomTemperature:0.0} C, so natural walkback lets direct comfort correction continue.";
            return ApplyTouchSignatureStep(reading, baseStep, now);
        }

        var walkStep = Math.Min(baseStep, Math.Round(Math.Max(0.1, state.Settings.NaturalWalkbackStepCelsius), 1));
        var jitter = Math.Clamp(state.Settings.NaturalWalkbackJitterCelsius, 0.0, 0.5);
        var delta = Math.Abs(expectedSetPointCelsius - reading.SetPointCelsius);
        if (jitter > 0 && delta > walkStep + 0.05)
        {
            var offset = (random.NextDouble() * 2.0 - 1.0) * jitter;
            walkStep = Math.Round(Math.Clamp(walkStep + offset, 0.1, baseStep), 1);
        }

        var score = CalculateTouchSuspicionScore(now);
        state.NaturalWalkbackStatus = $"Natural walkback is active at score {score}/100 and will use about {walkStep:0.0} C nudges.";
        return ApplyTouchSignatureStep(reading, Math.Max(0.1, walkStep), now);
    }

    private double ApplyTouchSignatureStep(ThermostatReading reading, double candidateStepCelsius, DateTimeOffset now)
    {
        var analysis = BuildTouchSignatureAnalysis(reading, candidateStepCelsius, now);
        state.TouchSignatureStatus = analysis.Status;
        if (!analysis.Active || analysis.LearnedStepCelsius is not { } learnedStep)
        {
            return Math.Round(Math.Max(0.1, candidateStepCelsius), 1);
        }

        return Math.Round(Math.Clamp(
            Math.Min(candidateStepCelsius, learnedStep),
            0.1,
            Math.Max(0.1, candidateStepCelsius)), 1);
    }

    private TouchSignatureAnalysis BuildTouchSignatureAnalysis(
        ThermostatReading? reading,
        double candidateStepCelsius,
        DateTimeOffset now)
    {
        if (!state.Settings.TouchSignatureEnabled)
        {
            return new TouchSignatureAnalysis(false, 0, null, Math.Round(Math.Max(0.1, candidateStepCelsius), 1), "Touch signature is off.");
        }

        var samples = GetTouchSignatureSamples(now);
        var triggerTouches = Math.Max(1, state.Settings.TouchSignatureTriggerTouches);
        if (samples.Count < triggerTouches)
        {
            return new TouchSignatureAnalysis(
                false,
                samples.Count,
                null,
                Math.Round(Math.Max(0.1, candidateStepCelsius), 1),
                $"Touch signature is learning wall steps ({samples.Count}/{triggerTouches}).");
        }

        if (reading is null)
        {
            return new TouchSignatureAnalysis(
                false,
                samples.Count,
                CalculateTouchSignatureStep(samples),
                Math.Round(Math.Max(0.1, candidateStepCelsius), 1),
                "Touch signature is waiting for a real thermostat reading.");
        }

        if (ShouldBypassNaturalRecovery(reading))
        {
            return new TouchSignatureAnalysis(
                false,
                samples.Count,
                CalculateTouchSignatureStep(samples),
                Math.Round(Math.Max(0.1, candidateStepCelsius), 1),
                "Room comfort needs help now, so touch signature is stepping aside.");
        }

        var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.TouchSignatureSafetyBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
        {
            return new TouchSignatureAnalysis(
                false,
                samples.Count,
                CalculateTouchSignatureStep(samples),
                Math.Round(Math.Max(0.1, candidateStepCelsius), 1),
                $"Room is above {allowedRoomTemperature:0.0} C, so touch signature is stepping aside.");
        }

        var learnedStep = CalculateTouchSignatureStep(samples);
        var effectiveStep = Math.Round(Math.Min(candidateStepCelsius, learnedStep), 1);
        return new TouchSignatureAnalysis(
            true,
            samples.Count,
            learnedStep,
            Math.Max(0.1, effectiveStep),
            $"Touch signature learned {learnedStep:0.0} C from {samples.Count} wall steps; safe nudges use about {Math.Max(0.1, effectiveStep):0.0} C.");
    }

    private List<double> GetTouchSignatureSamples(DateTimeOffset now)
    {
        var retention = TimeSpan.FromMinutes(Math.Max(1, state.Settings.TouchSignatureRetentionMinutes));
        return state.ThermostatChanges
            .Where(change => now - change.Timestamp <= retention)
            .Select(change => Math.Abs(change.NewSetPointCelsius - change.PreviousSetPointCelsius))
            .Where(delta => delta >= 0.05)
            .Select(delta => Math.Round(delta, 1))
            .Take(50)
            .ToList();
    }

    private double CalculateTouchSignatureStep(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return Math.Round(Math.Max(0.1, state.Settings.TouchSignatureMaximumStepCelsius), 1);
        }

        var sorted = samples
            .Select(sample => Math.Clamp(
                sample,
                state.Settings.TouchSignatureMinimumStepCelsius,
                state.Settings.TouchSignatureMaximumStepCelsius))
            .OrderBy(sample => sample)
            .ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;

        return Math.Round(Math.Clamp(
            median,
            state.Settings.TouchSignatureMinimumStepCelsius,
            state.Settings.TouchSignatureMaximumStepCelsius), 1);
    }

    private static double RoundToHumanStep(double value, double step)
    {
        if (step <= 0)
        {
            return Math.Round(value, 1);
        }

        return Math.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, 1);
    }

    public void RecordNaturalRecoverySettled()
    {
        lock (gate)
        {
            var status = state.Settings.NaturalRecoveryEnabled
                ? "Comfort sync is lined up; no quiet nudge needed."
                : "Quiet recovery is off.";
            if (state.NaturalRecoveryStatus == status
                && state.NaturalHoldUntil is null
                && state.NaturalHoldCount == 0
                && state.RoutineTimingDueAt is null
                && state.ComfortBudgetHoldUntil is null
                && state.CommandCamouflageHoldUntil is null
                && state.StealthGovernorHoldUntil is null
                && state.NaturalCadenceDueAt is null
                && state.NaturalChangePlannerDueAt is null
                && state.ComfortEnvelopeUntil is null
                && state.RepeatCommandHoldUntil is null
                && state.VisibilityGuardUntil is null
                && state.SensorRhythmDueAt is null
                && state.CoolingRunwayHoldUntil is null
                && state.ConflictQuietUntil is null
                && state.WallSettlingUntil is null
                && state.CoolerIntentUntil is null
                && state.RoomTrendHoldUntil is null
                && state.ThermalMomentumHoldUntil is null
                && state.WeatherDriftHoldUntil is null)
            {
                return;
            }

            state.NaturalHoldUntil = null;
            state.NaturalHoldCount = 0;
            state.NaturalRecoveryStatus = status;
            state.ConflictQuietUntil = null;
            state.ConflictQuietStatus = state.Settings.ConflictQuietModeEnabled
                ? "Conflict quiet is lined up; no stand-down needed."
                : "Conflict quiet is off.";
            ClearWallSettling(state.Settings.WallSettlingGuardEnabled
                ? "Wall settling is lined up; no active wall adjustments."
                : "Wall settling guard is off.");
            ClearCoolerIntent(state.Settings.CoolerIntentFastLaneEnabled
                ? "Cooler intent fast lane is lined up; no fast lane needed."
                : "Cooler intent fast lane is off.");
            state.RoomTrendHoldUntil = null;
            state.RoomTrendStatus = state.Settings.RoomTrendGuardEnabled
                ? "Room trend is lined up; no hold needed."
                : "Room trend guard is off.";
            state.ThermalMomentumHoldUntil = null;
            state.ThermalMomentumStatus = state.Settings.ThermalMomentumGuardEnabled
                ? "Thermal momentum is lined up; no hold needed."
                : "Thermal momentum guard is off.";
            ClearWeatherDrift(state.Settings.WeatherDriftGuardEnabled
                ? "Weather drift is lined up; no weather slot needed."
                : "Weather drift guard is off.");
            state.NaturalWalkbackStatus = state.Settings.NaturalWalkbackEnabled
                ? "Natural walkback is lined up; no setpoint walk needed."
                : "Natural walkback is off.";
            state.TouchSignatureStatus = state.Settings.TouchSignatureEnabled
                ? "Touch signature is lined up; no safe nudge needed."
                : "Touch signature is off.";
            ClearVisibilityGuard(state.Settings.VisibilityGuardEnabled
                ? "Visibility guard is lined up; no quiet hold needed."
                : "Visibility guard is off.");
            ClearRoutineTiming(state.Settings.RoutineTimingEnabled
                ? "Routine timing is lined up; no comfort-check slot needed."
                : "Routine timing is off.");
            ClearComfortBudget(state.Settings.ComfortBudgetEnabled
                ? "Comfort budget is lined up; no adjustment rest needed."
                : "Comfort budget is off.");
            ClearCommandCamouflage(state.Settings.CommandCamouflageEnabled
                ? "Command camouflage is lined up; no helper-command gap needed."
                : "Command camouflage is off.");
            ClearStealthGovernor(state.Settings.StealthGovernorEnabled
                ? "Stealth governor is lined up; no low-profile hold needed."
                : "Stealth governor is off.");
            ClearNaturalCadence(state.Settings.NaturalCadenceEnabled
                ? "Natural cadence is lined up; no quiet slot needed."
                : "Natural cadence is off.");
            ClearNaturalChangePlanner(state.Settings.NaturalChangePlannerEnabled
                ? "Comfort Pace is lined up; no natural slot needed."
                : "Comfort Pace is off.");
            ClearComfortEnvelope(state.Settings.ComfortEnvelopeEnabled
                ? "Comfort envelope is lined up; no safe preference hold needed."
                : "Comfort envelope is off.");
            ClearRepeatCommand(state.Settings.RepeatCommandGuardEnabled
                ? "Repeat quiet is lined up; no identical command hold needed."
                : "Repeat quiet is off.");
            ClearSensorRhythm(state.Settings.SensorRhythmGuardEnabled
                ? "Sensor rhythm is lined up; no beat wait needed."
                : "Sensor rhythm is off.");
            ClearCoolingRunway(state.Settings.CoolingRunwayGuardEnabled
                ? "Cooling runway is lined up; no fresh cooling hold needed."
                : "Cooling runway is off.");
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
        }
    }

    public ActiveRuleResult ApplyScheduleAndWeatherRules(ThermostatReading reading)
    {
        lock (gate)
        {
            var now = DateTimeOffset.Now;
            var activeSchedule = state.Settings.ScheduleEnabled
                ? state.Schedule.FirstOrDefault(item => IsScheduleActive(item, now))
                : null;

            var weatherMode = state.Settings.WeatherActivationMode;
            if (activeSchedule is not null)
            {
                weatherMode = NormalizeWeatherMode(activeSchedule.WeatherActivationMode);
                if (Math.Abs(state.TargetTemperatureCelsius - activeSchedule.TargetTemperatureCelsius) > 0.05)
                {
                    state.TargetTemperatureCelsius = Math.Round(activeSchedule.TargetTemperatureCelsius, 1);
                    ResetCoolingDefenderStep();
                    ClearNaturalCadence($"Schedule {activeSchedule.Name} changed the target, so natural cadence reset.");
                    ClearCommandCamouflage($"Schedule {activeSchedule.Name} changed the target, so command camouflage reset.");
                    ClearStealthGovernor($"Schedule {activeSchedule.Name} changed the target, so stealth governor reset.");
                    ClearNaturalChangePlanner($"Schedule {activeSchedule.Name} changed the target, so Comfort Pace reset.");
                    ClearComfortEnvelope($"Schedule {activeSchedule.Name} changed the target, so comfort envelope reset.");
                    ClearRepeatCommand($"Schedule {activeSchedule.Name} changed the target, so repeat quiet reset.");
                    ClearCoolingRunway($"Schedule {activeSchedule.Name} changed the target, so cooling runway reset.");
                    ClearComfortCompromise($"Schedule {activeSchedule.Name} changed the target, so comfort compromise reset.");
                    AddEvent("info", $"Schedule {activeSchedule.Name} set target to {state.TargetTemperatureCelsius:0.0} C.");
                }
            }

            var allowed = IsWeatherRuleAllowed(weatherMode, reading, state.Weather);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return new ActiveRuleResult(activeSchedule, weatherMode, allowed);
        }
    }

    /// <summary>Upstairs Comfort Guard: lowers the target toward the comfort target and adds boost (and may bypass cooldown) when the hottest upstairs sensor is hot and someone is home.</summary>
    public ComfortRuleResult ApplyComfortRules()
    {
        lock (gate)
        {
            if (!state.Settings.UpstairsComfortEnabled)
            {
                state.ComfortStatus = "Upstairs comfort guard is disabled.";
                return new ComfortRuleResult(false, false, state.TargetTemperatureCelsius, state.ComfortStatus);
            }

            if (state.Settings.HomePresenceRequired && !state.IsHome)
            {
                state.ComfortStatus = "No one is home; upstairs comfort guard is watching only.";
                return new ComfortRuleResult(false, false, state.TargetTemperatureCelsius, state.ComfortStatus);
            }

            if (!state.UpstairsTooHot)
            {
                state.ComfortStatus = BuildComfortStatus();
                return new ComfortRuleResult(false, false, state.TargetTemperatureCelsius, state.ComfortStatus);
            }

            var comfortTarget = Math.Min(state.TargetTemperatureCelsius, state.Settings.UpstairsComfortTargetCelsius);
            if (Math.Abs(state.TargetTemperatureCelsius - comfortTarget) > 0.05)
            {
                state.TargetTemperatureCelsius = Math.Round(comfortTarget, 1);
                state.BoostOffsetCelsius = Math.Max(state.BoostOffsetCelsius, state.Settings.UpstairsComfortBoostCelsius);
                ClearNaturalCadence("Upstairs comfort changed the target, so natural cadence reset.");
                ClearCommandCamouflage("Upstairs comfort changed the target, so command camouflage reset.");
                ClearStealthGovernor("Upstairs comfort changed the target, so stealth governor reset.");
                ClearNaturalChangePlanner("Upstairs comfort changed the target, so Comfort Pace reset.");
                ClearComfortEnvelope("Upstairs comfort changed the target, so comfort envelope reset.");
                ClearRepeatCommand("Upstairs comfort changed the target, so repeat quiet reset.");
                ClearCoolingRunway("Upstairs comfort changed the target, so cooling runway reset.");
                ClearComfortCompromise("Upstairs comfort changed the target, so comfort compromise reset.");
                AddEvent("warning", $"Upstairs comfort guard lowered target to {state.TargetTemperatureCelsius:0.0} C.");
            }
            else
            {
                state.BoostOffsetCelsius = Math.Max(state.BoostOffsetCelsius, state.Settings.UpstairsComfortBoostCelsius);
            }

            state.ComfortStatus = BuildComfortStatus();
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            var bypassCooldown = state.HottestUpstairsTemperatureCelsius is { } hottest
                && hottest >= state.Settings.UpstairsMaxComfortCelsius + 1.0;
            return new ComfortRuleResult(true, bypassCooldown, state.TargetTemperatureCelsius, state.ComfortStatus);
        }
    }

    public DefenderSettings GetSettings()
    {
        lock (gate)
        {
            return CloneSettings(state.Settings);
        }
    }

    public double GetTargetTemperature()
    {
        lock (gate)
        {
            return state.TargetTemperatureCelsius;
        }
    }

    /// <summary>Computes the defender target: applies Comfort Memory/Compromise modifiers, then the warm-room "1 °C below current room temperature" rule, stepping down a degree per cycle while cooling stalls but never below the website target.</summary>
    public double CalculateExpectedSetPoint(double currentTemperatureCelsius, string hvacAction)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var target = CalculateComfortMemoryTarget(currentTemperatureCelsius, now);
            target = CalculateComfortCompromiseTarget(target, currentTemperatureCelsius, now);
            if (currentTemperatureCelsius <= target + options.TemperatureToleranceCelsius)
            {
                ResetCoolingDefenderStep();
                return Math.Round(target, 1);
            }

            var action = (hvacAction ?? string.Empty).Trim().ToLowerInvariant();
            var isCooling = action is "cooling" or "cool";
            var lowestNormalSetPoint = Math.Max(options.MinimumCoolingSetPointCelsius, target);
            double activeSetPoint;
            if (state.ActiveCoolingSetPointCelsius is not { } previousSetPoint)
            {
                activeSetPoint = Math.Max(lowestNormalSetPoint, currentTemperatureCelsius - 1.0);
                state.ActiveCoolingStartedInSafeBand = activeSetPoint <= lowestNormalSetPoint + 0.05;
            }
            else if (!isCooling
                && state.ActiveCoolingStartedInSafeBand
                && previousSetPoint <= lowestNormalSetPoint + 0.05
                && currentTemperatureCelsius > target + 1.0)
            {
                activeSetPoint = Math.Max(lowestNormalSetPoint, currentTemperatureCelsius - 1.0);
                state.ActiveCoolingStartedInSafeBand = false;
            }
            else if (!isCooling)
            {
                activeSetPoint = Math.Max(lowestNormalSetPoint, previousSetPoint - 1.0);
                if (activeSetPoint > lowestNormalSetPoint + 0.05)
                {
                    state.ActiveCoolingStartedInSafeBand = false;
                }
            }
            else
            {
                activeSetPoint = Math.Max(lowestNormalSetPoint, previousSetPoint);
            }

            state.ActiveCoolingSetPointCelsius = Math.Round(activeSetPoint, 1);
            state.BoostOffsetCelsius = Math.Round(
                Math.Clamp(
                    currentTemperatureCelsius - state.ActiveCoolingSetPointCelsius.Value,
                    0.0,
                    options.MaximumBoostOffsetCelsius),
                1);

            return state.ActiveCoolingSetPointCelsius.Value;
        }
    }

    /// <summary>Fan Energy Saver: true when the room is within the threshold of target and the configured saver fan mode is available but not yet set.</summary>
    public bool ShouldUseFanSaver(ThermostatReading reading)
    {
        lock (gate)
        {
            return state.Settings.FanEnergySaverEnabled
                && !string.IsNullOrWhiteSpace(state.Settings.FanEnergySaverMode)
                && Math.Abs(reading.CurrentTemperatureCelsius - state.TargetTemperatureCelsius) <= state.Settings.FanEnergySaverThresholdCelsius
                && !string.Equals(reading.FanMode, state.Settings.FanEnergySaverMode, StringComparison.OrdinalIgnoreCase)
                && (reading.AvailableFanModes.Count == 0
                    || reading.AvailableFanModes.Contains(state.Settings.FanEnergySaverMode, StringComparer.OrdinalIgnoreCase));
        }
    }

    public bool ShouldUsePeakPowerFanSaver(ThermostatReading reading)
    {
        lock (gate)
        {
            return state.Settings.PeakPowerSaverEnabled
                && state.Settings.PeakPowerSaverFanSaverEnabled
                && IsPeakPowerSaverActive(DateTimeOffset.UtcNow)
                && !string.IsNullOrWhiteSpace(state.Settings.PeakPowerSaverFanMode)
                && reading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + state.Settings.PeakPowerSaverSafetyBandCelsius
                && !string.Equals(reading.FanMode, state.Settings.PeakPowerSaverFanMode, StringComparison.OrdinalIgnoreCase)
                && (reading.AvailableFanModes.Count == 0
                    || reading.AvailableFanModes.Contains(state.Settings.PeakPowerSaverFanMode, StringComparer.OrdinalIgnoreCase));
        }
    }

    public string GetFanSaverMode()
    {
        lock (gate)
        {
            return state.Settings.FanEnergySaverMode;
        }
    }

    public string GetPeakPowerFanSaverMode()
    {
        lock (gate)
        {
            return string.IsNullOrWhiteSpace(state.Settings.PeakPowerSaverFanMode)
                ? "auto"
                : state.Settings.PeakPowerSaverFanMode;
        }
    }

    public bool TryRespectPeakPowerSaver(
        ThermostatReading reading,
        double expectedSetPoint,
        bool bypassQuietTiming,
        DateTimeOffset now,
        out DateTimeOffset? until,
        out string message)
    {
        lock (gate)
        {
            until = null;
            message = string.Empty;
            if (!state.Settings.PeakPowerSaverEnabled)
            {
                state.PeakPowerSaverStatus = "Alectra Peak Power Saver is off.";
                return false;
            }

            if (!IsPeakPowerSaverActive(now))
            {
                state.PeakPowerSaverStatus = state.AlectraPeakPower is null
                    ? "Alectra Peak Power Saver is waiting for usage data."
                    : $"Alectra power is normal: {BuildPeakPowerSummary(state.AlectraPeakPower)}.";
                return false;
            }

            if (bypassQuietTiming)
            {
                state.PeakPowerSaverStatus = "Alectra Peak Power Saver is stepping aside because comfort safety is bypassing quiet timing.";
                return false;
            }

            if (expectedSetPoint >= reading.SetPointCelsius - 0.05)
            {
                state.PeakPowerSaverStatus = "Alectra Peak Power Saver is allowing this command because it will not demand more cooling.";
                return false;
            }

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.PeakPowerSaverSafetyBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature
                || reading.CurrentTemperatureCelsius >= state.TargetTemperatureCelsius + state.Settings.NaturalSafetyOverrideCelsius)
            {
                state.PeakPowerSaverStatus = $"Room is {reading.CurrentTemperatureCelsius:0.0} C, above the peak-power safe band {allowedRoomTemperature:0.0} C, so comfort wins.";
                return false;
            }

            until = state.PeakPowerSaverUntil ?? now.AddMinutes(Math.Max(1, state.Settings.PeakPowerSaverHoldMinutes));
            message = $"Alectra Peak Power Saver is holding safe cooling until {until.Value.ToLocalTime():HH:mm:ss}: {BuildPeakPowerSummary(state.AlectraPeakPower)}.";
            state.PeakPowerSaverStatus = message;
            return true;
        }
    }

    private double CalculateComfortMemoryTarget(double currentTemperatureCelsius, DateTimeOffset now)
    {
        var target = state.TargetTemperatureCelsius;
        if (!state.Settings.ComfortMemoryEnabled)
        {
            state.ComfortMemoryEffectiveTargetCelsius = null;
            state.ComfortMemoryStatus = "Comfort memory is off.";
            return target;
        }

        PruneComfortMemory(now);
        var allowedRoomTemperature = target + state.Settings.ComfortMemorySafetyBandCelsius;
        if (currentTemperatureCelsius > allowedRoomTemperature || currentTemperatureCelsius >= target + state.Settings.NaturalSafetyOverrideCelsius)
        {
            state.ComfortMemoryEffectiveTargetCelsius = null;
            state.ComfortMemoryStatus = $"Room is above {allowedRoomTemperature:0.0} C, so comfort memory is only watching.";
            return target;
        }

        var slot = FindComfortMemorySlot(now);
        if (slot is null)
        {
            state.ComfortMemoryEffectiveTargetCelsius = null;
            state.ComfortMemoryStatus = $"Comfort memory is watching this time window.";
            return target;
        }

        var maxOffset = Math.Max(0.1, state.Settings.ComfortMemoryMaxOffsetCelsius);
        var offset = Math.Round(Math.Clamp(slot.OffsetCelsius, -maxOffset, maxOffset), 1);
        if (offset > 0 && state.UpstairsTooHot)
        {
            state.ComfortMemoryEffectiveTargetCelsius = null;
            state.ComfortMemoryStatus = "Upstairs is warm, so comfort memory will not relax cooling.";
            return target;
        }

        var effectiveTarget = Math.Round(target + offset, 1);
        state.ComfortMemoryEffectiveTargetCelsius = effectiveTarget;
        state.ComfortMemoryStatus = $"Comfort memory learned {offset:+0.0;-0.0;0.0} C for this time window; effective target is {effectiveTarget:0.0} C.";
        return effectiveTarget;
    }

    private double CalculateComfortCompromiseTarget(double target, double currentTemperatureCelsius, DateTimeOffset now)
    {
        if (!state.Settings.ComfortCompromiseEnabled)
        {
            ClearComfortCompromise("Comfort compromise is off.");
            return target;
        }

        if (state.ComfortCompromiseUntil is not { } until
            || state.ComfortCompromiseStartedAt is not { } startedAt
            || state.ComfortCompromisePreferredSetPointCelsius is not { } preferredSetPoint)
        {
            state.ComfortCompromiseStatus = "Comfort compromise is watching for repeated wall changes.";
            return target;
        }

        if (until <= now)
        {
            ClearComfortCompromise("Comfort compromise finished; easing back to the website target.");
            return target;
        }

        var baseTarget = state.TargetTemperatureCelsius;
        var allowedRoomTemperature = baseTarget + state.Settings.ComfortCompromiseSafetyBandCelsius;
        if (currentTemperatureCelsius > allowedRoomTemperature || currentTemperatureCelsius >= baseTarget + state.Settings.NaturalSafetyOverrideCelsius)
        {
            ClearComfortCompromise($"Room rose above {allowedRoomTemperature:0.0} C, so comfort compromise stepped aside.");
            return target;
        }

        var maxOffset = Math.Max(0.1, state.Settings.ComfortCompromiseMaxOffsetCelsius);
        var boundedPreference = Math.Round(Math.Clamp(preferredSetPoint, target - maxOffset, target + maxOffset), 1);
        var holdUntil = startedAt.AddMinutes(Math.Max(0, state.Settings.ComfortCompromiseHoldMinutes));
        var factor = 1.0;
        if (now > holdUntil)
        {
            var decayMinutes = Math.Max(1, state.Settings.ComfortCompromiseDecayMinutes);
            factor = Math.Clamp(1.0 - ((now - holdUntil).TotalMinutes / decayMinutes), 0.0, 1.0);
        }

        var effectiveTarget = Math.Round(target + (boundedPreference - target) * factor, 1);
        state.ComfortCompromiseEffectiveTargetCelsius = effectiveTarget;
        state.ComfortCompromiseStatus = $"Comfort compromise is easing from {boundedPreference:0.0} C toward {target:0.0} C; effective target is {effectiveTarget:0.0} C.";
        return effectiveTarget;
    }

    private void DetectExternalSetPointChange(ThermostatReading reading, DateTimeOffset now)
    {
        if (state.LastObservedSetPointCelsius is not { } previous
            || Math.Abs(previous - reading.SetPointCelsius) <= 0.05)
        {
            return;
        }

        if (TryClassifyPendingThermostatCommandEcho(reading, now, out var pendingSource, out var pendingEchoDetail))
        {
            AddEvent(
                "info",
                $"Known thermostat command echoed ({pendingSource.Label}): {previous:0.0} C to {reading.SetPointCelsius:0.0} C. {pendingEchoDetail}");
            return;
        }

        var source = ClassifyChangeSource(reading);
        state.LastChangeSource = source.Kind;
        state.LastChangeSourceDetail = source.Detail;
        state.LastChangeContextId = reading.Context?.Id;
        state.LastChangeContextParentId = reading.Context?.ParentId;
        state.LastChangeContextUserId = reading.Context?.UserId;
        TrackSuperDefenderSource(source, now);

        PruneTouchTimes(now);
        state.ExternalTouchTimes.Add(now);
        RecordVisibilityNoticeIfNeeded(now);
        ResetCoolingDefenderStep();
        state.NaturalHoldUntil = null;
        state.NaturalHoldCount = 0;
        if (state.Settings.ManualComfortGraceEnabled && state.Settings.ManualComfortGraceMinutes > 0)
        {
            state.ManualComfortGraceUntil = now.AddMinutes(state.Settings.ManualComfortGraceMinutes);
            state.ManualComfortGraceSetPointCelsius = reading.SetPointCelsius;
            state.ManualComfortGraceStatus = $"Wall change noticed; room gets a comfort grace window until {state.ManualComfortGraceUntil.Value.ToLocalTime():HH:mm:ss}.";
        }
        else
        {
            ClearManualComfortGrace();
        }

        var plan = BuildNaturalRecoveryPlan(now);
        var cooldownSeconds = CalculateDynamicCooldownSeconds(now) + CalculateNaturalDelaySeconds(now);
        if (cooldownSeconds > 0)
        {
            state.CooldownUntil = now.AddSeconds(cooldownSeconds);
        }
        else
        {
            state.CooldownUntil = null;
        }

        state.NaturalRecoveryStatus = state.Settings.NaturalRecoveryEnabled
            ? $"Manual {source.Label} change noticed; comfort sync is in {plan.QuietLevel.ToLowerInvariant()} mode and will wait {cooldownSeconds}s before a quiet nudge."
            : "Manual thermostat touch noticed; quiet recovery is off.";
        var touchScore = CalculateTouchSuspicionScore(now);
        state.NaturalWalkbackStatus = state.Settings.NaturalWalkbackEnabled
            ? $"Manual touch score is {touchScore}/100; natural walkback will use small safe-band nudges if correction is needed."
            : "Natural walkback is off.";
        state.NaturalCadenceDueAt = null;
        state.NaturalCadenceStatus = state.Settings.NaturalCadenceEnabled
            ? $"Manual touch pressure is {touchScore}/100; natural cadence will pick a quiet slot if a safe correction is needed."
            : "Natural cadence is off.";
        state.NaturalChangePlannerDueAt = null;
        state.NaturalChangePlannerReason = "watching";
        state.NaturalChangePlannerStatus = state.Settings.NaturalChangePlannerEnabled
            ? $"Comfort Pace saw touch pressure {touchScore}/100; frequent wall changes will use a calmer climate slot if the room stays safe."
            : "Comfort Pace is off.";
        state.ComfortEnvelopeUntil = null;
        state.ComfortEnvelopePreferredSetPointCelsius = Math.Round(reading.SetPointCelsius, 1);
        state.ComfortEnvelopeMinimumAllowedSetPointCelsius = null;
        state.ComfortEnvelopeMaximumAllowedSetPointCelsius = null;
        state.ComfortEnvelopeStatus = state.Settings.ComfortEnvelopeEnabled
            ? $"Wall preference {reading.SetPointCelsius:0.0} C logged; comfort envelope can observe small safe differences."
            : "Comfort envelope is off.";
        state.TouchSignatureStatus = state.Settings.TouchSignatureEnabled
            ? "Manual wall step logged; touch signature will shape safe nudges after enough samples."
            : "Touch signature is off.";
        state.HumanNudgeActive = false;
        state.HumanNudgeLastSetPointCelsius = null;
        state.HumanNudgeStatus = state.Settings.HumanNudgeEnabled
            ? $"Manual wall touch logged; human nudge can make the next safe command look like a normal {state.Settings.HumanNudgeStepCelsius:0.0} C thermostat step."
            : "Human nudge is off.";
        state.VisibilityGuardStatus = state.Settings.VisibilityGuardEnabled
            ? $"Visibility guard has {state.VisibilityNoticeTimes.Count} noticed correction signal(s)."
            : "Visibility guard is off.";
        var wallSettlingTouches = GetRecentWallSettlingTouches(now).Count;
        state.WallSettlingStatus = state.Settings.WallSettlingGuardEnabled
            ? $"Wall settling logged {wallSettlingTouches} recent wall touch(es); waiting for the thermostat to stop moving before any safe correction."
            : "Wall settling guard is off.";

        var audit = new ThermostatChangeAudit(
            now,
            reading.EntityId,
            Math.Round(previous, 1),
            Math.Round(reading.SetPointCelsius, 1),
            reading.CurrentTemperatureCelsius,
            state.Weather?.OutdoorTemperatureCelsius,
            state.Weather?.Condition,
            source.Kind,
            source.Detail,
            reading.Context?.Id,
            reading.Context?.ParentId,
            reading.Context?.UserId);

        state.ThermostatChanges.Insert(0, audit);
        if (state.ThermostatChanges.Count > 100)
        {
            state.ThermostatChanges.RemoveRange(100, state.ThermostatChanges.Count - 100);
        }

        ApplyTouchIntentGrace(reading, now);
        ApplyCoolerIntentFastLane(reading, now);
        TryUpdateComfortMemory(reading, now);
        TryStartComfortCompromise(reading, now);

        AddEvent("warning",
            $"External thermostat change ({source.Label}): {previous:0.0} C to {reading.SetPointCelsius:0.0} C at {now:yyyy-MM-dd HH:mm:ss}.");
    }

    private ChangeSourceClassification ClassifyChangeSource(ThermostatReading reading)
    {
        var context = reading.Context;
        if (!string.IsNullOrWhiteSpace(context?.UserId))
        {
            return new ChangeSourceClassification(
                "home-assistant-user",
                "Home Assistant user or phone",
                $"Home Assistant attached user_id {context.UserId}; this usually means a phone/app/dashboard/service user changed it.",
                true);
        }

        if (!string.IsNullOrWhiteSpace(context?.ParentId))
        {
            return new ChangeSourceClassification(
                "home-assistant-automation",
                "Home Assistant automation",
                $"Home Assistant attached parent_id {context.ParentId}; this usually means an automation/script/service chain changed it.",
                true);
        }

        if (!string.IsNullOrWhiteSpace(context?.Id))
        {
            return new ChangeSourceClassification(
                "thermostat-device",
                "thermostat/device",
                $"Home Assistant context {context.Id} had no user_id or parent_id; this is most likely the thermostat, Nest cloud, or device-origin sync.",
                false);
        }

        return new ChangeSourceClassification(
            "unknown",
            "unknown source",
            "Home Assistant did not include context on this climate state, so the source cannot be proven.",
            false);
    }

    private bool TryClassifyPendingThermostatCommandEcho(
        ThermostatReading reading,
        DateTimeOffset now,
        out ChangeSourceClassification source,
        out string detail)
    {
        source = new ChangeSourceClassification(
            "unknown",
            "unknown source",
            "No pending AC Defender command matched this state.",
            false);
        detail = string.Empty;

        if (state.PendingCommandAt is not { } pendingAt
            || now - pendingAt > TimeSpan.FromSeconds(Math.Max(15, options.CommandGraceSeconds)))
        {
            return false;
        }

        var setpointEcho = state.PendingCommandSetPointCelsius is { } pendingSetPoint
            && Math.Abs(pendingSetPoint - reading.SetPointCelsius) <= 0.15;
        var modeEcho = !string.IsNullOrWhiteSpace(state.PendingCommandHvacMode)
            && string.Equals(state.PendingCommandHvacMode, reading.HvacMode, StringComparison.OrdinalIgnoreCase);
        var fanEcho = !string.IsNullOrWhiteSpace(state.PendingCommandFanMode)
            && string.Equals(state.PendingCommandFanMode, reading.FanMode, StringComparison.OrdinalIgnoreCase);

        if (!setpointEcho && !modeEcho && !fanEcho)
        {
            return false;
        }

        var kind = string.IsNullOrWhiteSpace(state.PendingCommandSourceKind)
            ? "website-command"
            : state.PendingCommandSourceKind!;
        var label = string.IsNullOrWhiteSpace(state.PendingCommandSourceLabel)
            ? "Website command"
            : state.PendingCommandSourceLabel!;
        detail = string.IsNullOrWhiteSpace(state.PendingCommandSourceDetail)
            ? $"{label} sent this command through AC Defender."
            : state.PendingCommandSourceDetail!;
        source = new ChangeSourceClassification(kind, label, detail, false);

        var echoedParts = string.Join(", ", new[]
        {
            setpointEcho ? $"{reading.SetPointCelsius:0.0} C" : null,
            modeEcho ? $"mode {reading.HvacMode}" : null,
            fanEcho ? $"fan {reading.FanMode}" : null
        }.Where(item => item is not null));
        ClearPendingSetpointEcho($"Home Assistant echoed {echoedParts} from {label}.");
        return true;
    }

    private void TrackSuperDefenderSource(ChangeSourceClassification source, DateTimeOffset now)
    {
        PruneRemoteChangeTimes(now);

        if (!state.Settings.SuperDefenderModeEnabled)
        {
            ClearSuperDefender("Super Defender is off.");
            return;
        }

        if (!source.CountsAsRemote)
        {
            state.SuperDefenderStatus = $"Last change looked like {source.Label}; Super Defender is watching for phone/Home Assistant changes.";
            return;
        }

        state.RemoteChangeTimes.Add(now);
        PruneRemoteChangeTimes(now);

        var threshold = Math.Max(1, state.Settings.SuperDefenderRemoteChangeThreshold);
        var count = state.RemoteChangeTimes.Count;
        if (count < threshold)
        {
            state.SuperDefenderStatus = $"Remote-style change logged ({count}/{threshold}); Super Defender is watching.";
            return;
        }

        var holdMinutes = Math.Max(0, state.Settings.SuperDefenderHoldMinutes);
        if (holdMinutes <= 0)
        {
            state.SuperDefenderStatus = "Remote-style pattern reached, but Super Defender hold is set to 0 min.";
            return;
        }

        state.SuperDefenderUntil = now.AddMinutes(holdMinutes);
        state.SuperDefenderStatus = $"Super Defender armed until {state.SuperDefenderUntil.Value.ToLocalTime():HH:mm:ss} after {count} phone/Home Assistant-style changes.";
        AddEvent(
            "warning",
            $"Super Defender armed after {count} remote-style thermostat changes. Automatic Wi-Fi blocking is not enabled; use router controls manually only if you accept the HVAC connectivity risk.");
    }

    private void ApplyTouchIntentGrace(ThermostatReading reading, DateTimeOffset now)
    {
        var intent = BuildTouchIntentAnalysis(now);
        state.TouchIntentStatus = intent.Status;
        if (!state.Settings.TouchIntentEnabled)
        {
            return;
        }

        if (!intent.Active || intent.Direction != "warmer")
        {
            return;
        }

        var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.TouchIntentSafetyBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature || ShouldBypassNaturalRecovery(reading))
        {
            state.TouchIntentStatus = $"Touch intent sees warmer wall choices, but room is above {allowedRoomTemperature:0.0} C so comfort wins.";
            return;
        }

        if (state.ManualComfortGraceUntil is not { } graceUntil)
        {
            state.TouchIntentStatus = "Touch intent sees warmer wall choices, but wall-change grace is off.";
            return;
        }

        var extraMinutes = Math.Max(0, state.Settings.TouchIntentExtraGraceMinutes);
        if (extraMinutes <= 0)
        {
            state.TouchIntentStatus = "Touch intent sees warmer wall choices, but extra grace is set to 0 min.";
            return;
        }

        var desiredUntil = now.AddMinutes(Math.Max(0, state.Settings.ManualComfortGraceMinutes) + extraMinutes);
        if (desiredUntil > graceUntil)
        {
            state.ManualComfortGraceUntil = desiredUntil;
            state.ManualComfortGraceStatus = $"Warmer wall intent is consistent, so grace extends until {desiredUntil.ToLocalTime():HH:mm:ss} while room stays below {allowedRoomTemperature:0.0} C.";
        }

        state.TouchIntentStatus = $"Touch intent reads {intent.RecentTouchCount} warmer wall choices ({intent.NetChangeCelsius:+0.0;-0.0;0.0} C net); safe grace can use +{extraMinutes} min.";
    }

    private void ApplyCoolerIntentFastLane(ThermostatReading reading, DateTimeOffset now)
    {
        var analysis = BuildCoolerIntentAnalysis(now);
        if (!state.Settings.CoolerIntentFastLaneEnabled)
        {
            ClearCoolerIntent("Cooler intent fast lane is off.");
            return;
        }

        if (!analysis.Active)
        {
            if (state.CoolerIntentUntil is null || state.CoolerIntentUntil <= now)
            {
                state.CoolerIntentStatus = analysis.Status;
            }

            return;
        }

        if (reading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius)
        {
            ClearCoolerIntent("Cooler wall intent is clear, but the room is already at the website target.");
            return;
        }

        var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.CoolerIntentSafetyBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature && !ShouldBypassNaturalRecovery(reading))
        {
            state.CoolerIntentStatus = $"Cooler wall intent is clear, but fast lane waits inside {allowedRoomTemperature:0.0} C so normal comfort safety can lead.";
            return;
        }

        var holdMinutes = Math.Max(0, state.Settings.CoolerIntentHoldMinutes);
        if (holdMinutes <= 0)
        {
            ClearCoolerIntent("Cooler intent fast lane saw cooler touches, but hold minutes is set to 0.");
            return;
        }

        var until = now.AddMinutes(holdMinutes);
        var wasInactive = state.CoolerIntentUntil is null || state.CoolerIntentUntil <= now;
        state.CoolerIntentUntil = until;
        state.CoolerIntentStatus = $"Cooler intent fast lane accepted {analysis.RecentTouchCount} cooler wall touches ({analysis.NetChangeCelsius:+0.0;-0.0;0.0} C net) until {until.ToLocalTime():HH:mm:ss}.";
        state.CooldownUntil = null;
        state.NaturalHoldUntil = null;
        state.NaturalHoldCount = 0;
        state.ConflictQuietUntil = null;
        state.ConflictQuietStatus = "Cooler intent fast lane is active, so conflict quiet is stepping aside.";
        ClearWallSettling("Cooler intent fast lane is active, so wall settling is stepping aside.");
        ClearManualComfortGrace();
        ClearRoutineTiming("Cooler intent fast lane is active, so routine timing is stepping aside.");
        ClearComfortBudget("Cooler intent fast lane is active, so comfort budget is stepping aside.");
        ClearCommandCamouflage("Cooler intent fast lane is active, so command camouflage is stepping aside.");
        ClearStealthGovernor("Cooler intent fast lane is active, so stealth governor is stepping aside.");
        ClearNaturalCadence("Cooler intent fast lane is active, so natural cadence is stepping aside.");
        ClearNaturalChangePlanner("Cooler intent fast lane is active, so Comfort Pace is stepping aside.");
        ClearComfortEnvelope("Cooler intent fast lane is active, so comfort envelope is stepping aside.");
        ClearVisibilityGuard("Cooler intent fast lane is active, so visibility guard is stepping aside.");
        ClearRepeatCommand("Cooler intent fast lane is active, so repeat quiet is stepping aside.");
        ClearCoolingRunway("Cooler intent fast lane is active, so cooling runway is stepping aside.");
        ClearSensorRhythm("Cooler intent fast lane is active, so sensor rhythm is stepping aside.");
        state.RoomTrendHoldUntil = null;
        state.RoomTrendStatus = "Cooler intent fast lane is active, so room trend guard is stepping aside.";
        state.ThermalMomentumHoldUntil = null;
        state.ThermalMomentumStatus = "Cooler intent fast lane is active, so thermal momentum is stepping aside.";
        ClearWeatherDrift("Cooler intent fast lane is active, so weather drift is stepping aside.");
        state.NaturalRecoveryStatus = "Cooler intent fast lane is active, so quiet recovery is stepping aside.";
        state.TouchIntentStatus = analysis.Status;

        if (wasInactive)
        {
            AddEvent("info", $"Cooler intent fast lane activated from {analysis.RecentTouchCount} cooler wall touches.");
        }
    }

    private CoolerIntentAnalysis BuildCoolerIntentAnalysis(DateTimeOffset now)
    {
        if (!state.Settings.CoolerIntentFastLaneEnabled)
        {
            return new CoolerIntentAnalysis(false, false, 0, 0.0, "Cooler intent fast lane is off.");
        }

        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.CoolerIntentWindowMinutes));
        var recent = state.ThermostatChanges
            .Where(change => now - change.Timestamp <= window)
            .Take(50)
            .ToList();
        var count = recent.Count;
        if (count == 0)
        {
            return new CoolerIntentAnalysis(true, false, 0, 0.0, "Cooler intent fast lane is watching for wall touches.");
        }

        var netChange = Math.Round(recent.Sum(change => change.NewSetPointCelsius - change.PreviousSetPointCelsius), 1);
        var triggerTouches = Math.Max(1, state.Settings.CoolerIntentMinimumTouches);
        if (count < triggerTouches)
        {
            return new CoolerIntentAnalysis(
                true,
                false,
                count,
                netChange,
                $"Cooler intent fast lane is learning ({count}/{triggerTouches}, {netChange:+0.0;-0.0;0.0} C net).");
        }

        var threshold = Math.Max(0.1, state.Settings.CoolerIntentNetCoolThresholdCelsius);
        if (netChange <= -threshold)
        {
            return new CoolerIntentAnalysis(
                true,
                true,
                count,
                netChange,
                $"Cooler intent fast lane sees a cooler pattern ({netChange:+0.0;-0.0;0.0} C net from {count} touches).");
        }

        return new CoolerIntentAnalysis(
            true,
            false,
            count,
            netChange,
            $"Cooler intent fast lane sees no cooler pattern yet ({netChange:+0.0;-0.0;0.0} C net).");
    }

    private TouchIntentAnalysis BuildTouchIntentAnalysis(DateTimeOffset now)
    {
        if (!state.Settings.TouchIntentEnabled)
        {
            return new TouchIntentAnalysis(false, false, 0, "off", 0.0, 0, "Touch intent is off.");
        }

        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.TouchIntentWindowMinutes));
        var recent = state.ThermostatChanges
            .Where(change => now - change.Timestamp <= window)
            .Take(50)
            .ToList();
        var count = recent.Count;
        var extraMinutes = Math.Max(0, state.Settings.TouchIntentExtraGraceMinutes);
        if (count == 0)
        {
            return new TouchIntentAnalysis(true, false, 0, "watching", 0.0, extraMinutes, "Touch intent is watching for wall choices.");
        }

        var netChange = Math.Round(recent.Sum(change => change.NewSetPointCelsius - change.PreviousSetPointCelsius), 1);
        var triggerTouches = Math.Max(1, state.Settings.TouchIntentMinimumTouches);
        if (count < triggerTouches)
        {
            return new TouchIntentAnalysis(
                true,
                false,
                count,
                "learning",
                netChange,
                extraMinutes,
                $"Touch intent is learning wall choices ({count}/{triggerTouches}, {netChange:+0.0;-0.0;0.0} C net).");
        }

        var threshold = Math.Max(0.1, state.Settings.TouchIntentNetWarmThresholdCelsius);
        if (netChange >= threshold)
        {
            return new TouchIntentAnalysis(
                true,
                true,
                count,
                "warmer",
                netChange,
                extraMinutes,
                $"Touch intent sees a warmer pattern ({netChange:+0.0;-0.0;0.0} C net from {count} touches).");
        }

        if (netChange <= -threshold)
        {
            return new TouchIntentAnalysis(
                true,
                true,
                count,
                "cooler",
                netChange,
                extraMinutes,
                $"Touch intent sees a cooler pattern ({netChange:+0.0;-0.0;0.0} C net), so it will not add warm grace.");
        }

        return new TouchIntentAnalysis(
            true,
            false,
            count,
            "mixed",
            netChange,
            extraMinutes,
            $"Touch intent sees mixed wall choices ({netChange:+0.0;-0.0;0.0} C net).");
    }

    private void TryStartComfortCompromise(ThermostatReading reading, DateTimeOffset now)
    {
        if (!state.Settings.ComfortCompromiseEnabled)
        {
            ClearComfortCompromise("Comfort compromise is off.");
            return;
        }

        PruneTouchTimes(now);
        var triggerTouches = Math.Max(1, state.Settings.ComfortCompromiseTriggerTouches);
        if (state.ExternalTouchTimes.Count < triggerTouches)
        {
            state.ComfortCompromiseStatus = $"Comfort compromise is watching wall changes ({state.ExternalTouchTimes.Count}/{triggerTouches}).";
            return;
        }

        var target = state.TargetTemperatureCelsius;
        var allowedRoomTemperature = target + state.Settings.ComfortCompromiseSafetyBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature || ShouldBypassNaturalRecovery(reading))
        {
            ClearComfortCompromise($"Room is above {allowedRoomTemperature:0.0} C, so comfort compromise will not hold the warmer wall setting.");
            return;
        }

        if (Math.Abs(reading.SetPointCelsius - target) <= 0.05)
        {
            ClearComfortCompromise("Wall setting already matches the website target.");
            return;
        }

        state.ComfortCompromiseStartedAt = now;
        state.ComfortCompromiseUntil = now.AddMinutes(
            Math.Max(0, state.Settings.ComfortCompromiseHoldMinutes)
            + Math.Max(1, state.Settings.ComfortCompromiseDecayMinutes));
        state.ComfortCompromisePreferredSetPointCelsius = Math.Round(reading.SetPointCelsius, 1);
        var maxOffset = Math.Max(0.1, state.Settings.ComfortCompromiseMaxOffsetCelsius);
        state.ComfortCompromiseEffectiveTargetCelsius = Math.Round(Math.Clamp(
            reading.SetPointCelsius,
            target - maxOffset,
            target + maxOffset), 1);
        state.ComfortCompromiseStatus = $"Comfort compromise accepted {reading.SetPointCelsius:0.0} C temporarily and will fade back by {state.ComfortCompromiseUntil.Value.ToLocalTime():HH:mm:ss}.";
    }

    private void TryUpdateComfortMemory(ThermostatReading reading, DateTimeOffset now)
    {
        if (!state.Settings.ComfortMemoryEnabled)
        {
            state.ComfortMemoryStatus = "Comfort memory is off.";
            return;
        }

        PruneTouchTimes(now);
        var triggerTouches = Math.Max(1, state.Settings.ComfortMemoryLearningTouches);
        if (state.ExternalTouchTimes.Count < triggerTouches)
        {
            state.ComfortMemoryStatus = $"Comfort memory is watching wall choices ({state.ExternalTouchTimes.Count}/{triggerTouches}).";
            return;
        }

        var target = state.TargetTemperatureCelsius;
        var allowedRoomTemperature = target + state.Settings.ComfortMemorySafetyBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature || ShouldBypassNaturalRecovery(reading))
        {
            state.ComfortMemoryStatus = $"Room is above {allowedRoomTemperature:0.0} C, so comfort memory will not learn this touch.";
            return;
        }

        var maxOffset = Math.Max(0.1, state.Settings.ComfortMemoryMaxOffsetCelsius);
        var offset = Math.Round(Math.Clamp(reading.SetPointCelsius - target, -maxOffset, maxOffset), 1);
        if (Math.Abs(offset) <= 0.05)
        {
            state.ComfortMemoryStatus = "Wall choice already matches the website target, so comfort memory did not change.";
            return;
        }

        if (offset > 0 && state.UpstairsTooHot)
        {
            state.ComfortMemoryStatus = "Upstairs is warm, so comfort memory will not learn a warmer preference.";
            return;
        }

        PruneComfortMemory(now);
        var slot = FindComfortMemorySlot(now);
        if (slot is null)
        {
            slot = new ComfortMemorySlot
            {
                HourOfDay = now.ToLocalTime().Hour,
                OffsetCelsius = offset,
                Samples = 0,
                UpdatedAt = now
            };
            state.ComfortMemorySlots.Add(slot);
        }

        var samples = Math.Clamp(slot.Samples, 0, 12);
        slot.OffsetCelsius = Math.Round(((slot.OffsetCelsius * samples) + offset) / (samples + 1), 1);
        slot.Samples = Math.Min(24, slot.Samples + 1);
        slot.UpdatedAt = now;
        state.ComfortMemoryStatus = $"Comfort memory learned {slot.OffsetCelsius:+0.0;-0.0;0.0} C for this time window from {slot.Samples} wall choices.";
    }

    private int CalculateDynamicCooldownSeconds(DateTimeOffset now)
    {
        PruneTouchTimes(now);
        var touches = Math.Max(1, state.ExternalTouchTimes.Count);
        var baseSeconds = Math.Max(0, state.Settings.BaseCooldownSeconds);
        var maxSeconds = Math.Max(baseSeconds, state.Settings.MaxCooldownSeconds);
        return Math.Min(maxSeconds, baseSeconds * touches);
    }

    private void RecordRoomTemperatureSample(ThermostatReading reading, DateTimeOffset now)
    {
        state.RoomTemperatureSamples.Add(new RoomTemperatureSample(
            now,
            Math.Round(reading.CurrentTemperatureCelsius, 2)));
        PruneRoomTemperatureSamples(now);
    }

    private void RecordWeatherSample(WeatherReading reading, DateTimeOffset now)
    {
        if (reading.OutdoorTemperatureCelsius is not { } outdoorTemperature)
        {
            return;
        }

        state.WeatherSamples.Add(new WeatherSample(
            now,
            Math.Round(outdoorTemperature, 2),
            reading.Condition));
        PruneWeatherSamples(now);
    }

    private void RecordHomeAssistantReadingTime(DateTimeOffset now)
    {
        if (state.HomeAssistantReadingTimes.LastOrDefault() is { } latest
            && now - latest < TimeSpan.FromSeconds(1))
        {
            return;
        }

        state.HomeAssistantReadingTimes.Add(now);
        PruneHomeAssistantReadingTimes(now);
    }

    private void TrackCoolingRunway(ThermostatReading reading, string? previousHvacAction, DateTimeOffset now)
    {
        if (!state.Settings.CoolingRunwayGuardEnabled)
        {
            ClearCoolingRunway("Cooling runway is off.");
            state.CoolingRunwayStartedAt = null;
            return;
        }

        if (!IsCoolingAction(reading.HvacAction))
        {
            state.CoolingRunwayStartedAt = null;
            ClearCoolingRunway("Cooling runway is watching for a fresh cooling start.");
            return;
        }

        if (!IsCoolingAction(previousHvacAction))
        {
            state.CoolingRunwayStartedAt = now;
            state.CoolingRunwayHoldUntil = null;
            state.CoolingRunwayStatus = "Cooling just started; runway can let it work before another safe nudge.";
            return;
        }

        state.CoolingRunwayStartedAt ??= now;
    }

    private void TrackHvacActionAlibiTransition(ThermostatReading reading, string? previousHvacAction, DateTimeOffset now)
    {
        var currentAction = NormalizeHvacAction(reading.HvacAction);
        state.HvacActionAlibiCurrentAction = currentAction;

        var previousAction = NormalizeHvacAction(previousHvacAction);
        if (string.IsNullOrWhiteSpace(previousHvacAction) || previousAction == currentAction)
        {
            state.HvacActionAlibiStatus = string.IsNullOrWhiteSpace(state.HvacActionAlibiStatus)
                ? $"HVAC alibi is watching real action '{currentAction}'."
                : state.HvacActionAlibiStatus;
            return;
        }

        state.HvacActionAlibiLastTransitionAt = now;
        state.HvacActionAlibiLastTransitionFrom = previousAction;
        state.HvacActionAlibiLastTransitionTo = currentAction;
        state.HvacActionAlibiStatus = $"Real HVAC action changed from '{previousAction}' to '{currentAction}'; safe corrections can use that natural timing cue.";
    }

    private void UpdateCoolingFailureDetection(ThermostatReading reading, DateTimeOffset now)
    {
        if (!CoolingIsDemanded(reading))
        {
            ClearCoolingFailure("Cooling failure watch is ready.");
            return;
        }

        state.CoolingDemandStartedAt ??= now;
        if (!IsCoolingAction(reading.HvacAction))
        {
            state.CoolingFailureSuspectedAt ??= now;
            var seconds = (int)Math.Ceiling((now - state.CoolingFailureSuspectedAt.Value).TotalSeconds);
            if (seconds >= CoolingFailureIdleSeconds)
            {
                // The mega alert is armed (cooling demanded, action idle). Check whether the room is
                // actually rising over the confirmation window before escalating to OMEGA.
                var rise = TryGetRoomRiseCelsius(reading.CurrentTemperatureCelsius, now);
                state.OmegaRoomRiseCelsius = rise;
                if (rise is { } confirmedRise && confirmedRise >= OmegaMinimumRiseCelsius)
                {
                    state.OmegaConfirmedAt ??= now;
                    RaiseCoolingFailure(
                        now,
                        $"OMEGA ALERT: Cooling demanded but '{reading.HvacAction}' for {seconds}s AND the room has risen {confirmedRise:0.0} C in the last {OmegaRiseWindowSeconds / 60} min. The AC breaker is most likely OFF.");
                    return;
                }

                state.OmegaConfirmedAt = null;
                RaiseCoolingFailure(
                    now,
                    $"MEGA ALERT: Cooling is demanded but Home Assistant still reports '{reading.HvacAction}' after {seconds}s. Breaker or equipment failure may be possible.");
                return;
            }

            state.OmegaConfirmedAt = null;
            state.OmegaRoomRiseCelsius = null;
            state.CoolingFailureStatus = $"Cooling demand is active but action is '{reading.HvacAction}'; mega alert arms in {CoolingFailureIdleSeconds - seconds}s if it stays idle.";
            return;
        }

        var oldSample = state.RoomTemperatureSamples
            .Where(sample => now - sample.Timestamp >= TimeSpan.FromSeconds(CoolingFailureNoDropSeconds))
            .OrderByDescending(sample => sample.Timestamp)
            .FirstOrDefault();
        if (oldSample is not null)
        {
            var drop = oldSample.TemperatureCelsius - reading.CurrentTemperatureCelsius;
            if (drop < CoolingFailureMinimumDropCelsius)
            {
                // "Cooling but not dropping" means the unit still reports cooling, so it has power; this
                // is a compressor/airflow problem, not a dead breaker. It never escalates to OMEGA.
                state.OmegaConfirmedAt = null;
                state.OmegaRoomRiseCelsius = null;
                RaiseCoolingFailure(
                    now,
                    $"MEGA ALERT: Thermostat says cooling, but room temperature only changed {drop:0.0} C in {CoolingFailureNoDropSeconds / 60} minutes. Breaker, compressor, or airflow failure may be possible.");
                return;
            }
        }

        ClearCoolingFailure("Cooling is demanded and Home Assistant reports cooling; watching for room temperature drop.");
    }

    private bool CoolingIsDemanded(ThermostatReading reading)
    {
        return string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase)
            && reading.CurrentTemperatureCelsius >= reading.SetPointCelsius + CoolingFailureDemandBandCelsius;
    }

    private void RaiseCoolingFailure(DateTimeOffset now, string message)
    {
        var firstAlert = state.CoolingFailureSuspectedAt is null;
        state.CoolingFailureSuspectedAt ??= now;
        state.CoolingFailureStatus = message;
        if (firstAlert || state.CoolingFailureNextAlertAt is null || state.CoolingFailureNextAlertAt <= now)
        {
            state.CoolingFailureAlertCount++;
            state.CoolingFailureNextAlertAt = now.AddSeconds(CoolingFailureRepeatAlertSeconds);
            AddEvent("error", message);
        }
    }

    private static bool IsCoolingAction(string? hvacAction)
    {
        return (hvacAction ?? string.Empty).Trim().ToLowerInvariant() is "cooling" or "cool";
    }

    private static string NormalizeHvacAction(string? hvacAction)
    {
        var normalized = (hvacAction ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    /// <summary>
    /// Net room-temperature change (current minus the sample closest to <see cref="OmegaRiseWindowSeconds"/>
    /// ago). Positive means the room is warming. Returns null until enough history exists, so OMEGA can
    /// only confirm on a real, sustained rise rather than a single noisy reading.
    /// </summary>
    private double? TryGetRoomRiseCelsius(double currentTemperatureCelsius, DateTimeOffset now)
    {
        var oldSample = state.RoomTemperatureSamples
            .Where(sample => now - sample.Timestamp >= TimeSpan.FromSeconds(OmegaRiseWindowSeconds))
            .OrderByDescending(sample => sample.Timestamp)
            .FirstOrDefault();
        return oldSample is null ? null : currentTemperatureCelsius - oldSample.TemperatureCelsius;
    }

    private SensorRhythmAnalysis BuildSensorRhythmAnalysis(DateTimeOffset now)
    {
        PruneHomeAssistantReadingTimes(now);
        if (state.HomeAssistantReadingTimes.Count < 2)
        {
            return new SensorRhythmAnalysis(state.HomeAssistantReadingTimes.Count, 0, state.HomeAssistantReadingTimes.LastOrDefault());
        }

        var intervals = state.HomeAssistantReadingTimes
            .Zip(state.HomeAssistantReadingTimes.Skip(1), (previous, next) => (int)Math.Round((next - previous).TotalSeconds))
            .Where(seconds => seconds is >= 2 and <= 3600)
            .Order()
            .ToArray();

        if (intervals.Length == 0)
        {
            return new SensorRhythmAnalysis(state.HomeAssistantReadingTimes.Count, 0, state.HomeAssistantReadingTimes.LastOrDefault());
        }

        var middle = intervals.Length / 2;
        var median = intervals.Length % 2 == 1
            ? intervals[middle]
            : (int)Math.Round((intervals[middle - 1] + intervals[middle]) / 2.0);

        return new SensorRhythmAnalysis(
            state.HomeAssistantReadingTimes.Count,
            Math.Clamp(median, 2, 3600),
            state.HomeAssistantReadingTimes.LastOrDefault());
    }

    private DateTimeOffset CalculateSensorRhythmDueAt(DateTimeOffset now, SensorRhythmAnalysis analysis)
    {
        var medianSeconds = Math.Clamp(analysis.MedianIntervalSeconds, 2, 3600);
        var dueAt = analysis.LastReadingAt ?? now;
        while (dueAt <= now)
        {
            dueAt = dueAt.AddSeconds(medianSeconds);
        }

        var jitterSeconds = Math.Clamp(state.Settings.SensorRhythmJitterSeconds, 0, 300);
        if (jitterSeconds > 0)
        {
            dueAt = dueAt.AddSeconds(random.Next(0, jitterSeconds + 1));
        }

        var earliest = now.AddSeconds(Math.Min(5, medianSeconds));
        return dueAt < earliest ? earliest : dueAt;
    }

    private void PruneHomeAssistantReadingTimes(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(5, state.Settings.SensorRhythmWindowMinutes));
        state.HomeAssistantReadingTimes.RemoveAll(item => now - item > window);
        if (state.HomeAssistantReadingTimes.Count > 500)
        {
            state.HomeAssistantReadingTimes.RemoveRange(0, state.HomeAssistantReadingTimes.Count - 500);
        }
    }

    private RoomTrendAnalysis BuildRoomTrend(DateTimeOffset now)
    {
        PruneRoomTemperatureSamples(now);
        if (state.RoomTemperatureSamples.Count < 2)
        {
            return new RoomTrendAnalysis("collecting", null, state.RoomTemperatureSamples.Count);
        }

        var oldest = state.RoomTemperatureSamples.First();
        var newest = state.RoomTemperatureSamples.Last();
        var delta = Math.Round(newest.TemperatureCelsius - oldest.TemperatureCelsius, 2);
        var tolerance = Math.Max(0.05, state.Settings.RoomTrendStableToleranceCelsius);
        var direction = delta > tolerance
            ? "warming"
            : delta < -tolerance ? "cooling" : "stable";

        return new RoomTrendAnalysis(direction, delta, state.RoomTemperatureSamples.Count);
    }

    private ThermalMomentumAnalysis BuildThermalMomentum(DateTimeOffset now, double? currentTemperatureCelsius = null)
    {
        PruneRoomTemperatureSamples(now);
        if (state.RoomTemperatureSamples.Count < 2)
        {
            return new ThermalMomentumAnalysis(null, null, state.RoomTemperatureSamples.Count);
        }

        var oldest = state.RoomTemperatureSamples.First();
        var newest = state.RoomTemperatureSamples.Last();
        var elapsedHours = (newest.Timestamp - oldest.Timestamp).TotalHours;
        if (elapsedHours <= 0)
        {
            return new ThermalMomentumAnalysis(null, null, state.RoomTemperatureSamples.Count);
        }

        var delta = newest.TemperatureCelsius - oldest.TemperatureCelsius;
        if (delta >= 0)
        {
            return new ThermalMomentumAnalysis(null, null, state.RoomTemperatureSamples.Count);
        }

        var rate = Math.Round(-delta / elapsedHours, 2);
        var current = currentTemperatureCelsius ?? newest.TemperatureCelsius;
        var distanceToTarget = current - state.TargetTemperatureCelsius;
        var eta = distanceToTarget <= 0
            ? 0.0
            : Math.Round(distanceToTarget / rate * 60.0, 1);

        return new ThermalMomentumAnalysis(rate, eta, state.RoomTemperatureSamples.Count);
    }

    private WeatherDriftAnalysis BuildWeatherDrift(DateTimeOffset now)
    {
        PruneWeatherSamples(now);
        if (state.WeatherSamples.Count < 2)
        {
            return new WeatherDriftAnalysis(
                state.WeatherSamples.Count,
                "collecting",
                null,
                false);
        }

        var oldest = state.WeatherSamples.First();
        var newest = state.WeatherSamples.Last();
        var delta = Math.Round(newest.OutdoorTemperatureCelsius - oldest.OutdoorTemperatureCelsius, 2);
        var minimumChange = Math.Max(0.1, state.Settings.WeatherDriftMinimumChangeCelsius);
        var direction = delta >= minimumChange
            ? "warming"
            : delta <= -minimumChange ? "cooling" : "stable";
        var conditionChanged = !string.Equals(
            oldest.Condition,
            newest.Condition,
            StringComparison.OrdinalIgnoreCase);

        return new WeatherDriftAnalysis(
            state.WeatherSamples.Count,
            direction,
            delta,
            conditionChanged);
    }

    private void PruneRoomTemperatureSamples(DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Math.Max(
            CoolingFailureNoDropSeconds + 60,
            Math.Max(2, state.Settings.RoomTrendWindowMinutes) * 60));
        state.RoomTemperatureSamples.RemoveAll(item => now - item.Timestamp > window);
        if (state.RoomTemperatureSamples.Count > 300)
        {
            state.RoomTemperatureSamples.RemoveRange(0, state.RoomTemperatureSamples.Count - 300);
        }
    }

    private void PruneWeatherSamples(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(5, state.Settings.WeatherDriftWindowMinutes));
        state.WeatherSamples.RemoveAll(item => now - item.Timestamp > window);
        if (state.WeatherSamples.Count > 300)
        {
            state.WeatherSamples.RemoveRange(0, state.WeatherSamples.Count - 300);
        }
    }

    private int CalculateNaturalDelaySeconds(DateTimeOffset now)
    {
        if (!state.Settings.NaturalRecoveryEnabled)
        {
            return 0;
        }

        var plan = BuildNaturalRecoveryPlan(now);
        return plan.MaximumDelaySeconds == plan.MinimumDelaySeconds
            ? plan.MinimumDelaySeconds
            : random.Next(plan.MinimumDelaySeconds, plan.MaximumDelaySeconds + 1);
    }

    private int CalculateCoolModeRestoreDelaySeconds()
    {
        var minDelay = Math.Max(0, state.Settings.CoolModeRestoreMinimumDelaySeconds);
        var maxDelay = Math.Max(minDelay, state.Settings.CoolModeRestoreMaximumDelaySeconds);
        return maxDelay == minDelay
            ? minDelay
            : random.Next(minDelay, maxDelay + 1);
    }

    private DateTimeOffset CalculateRoutineTimingDueAt(DateTimeOffset now)
    {
        var intervalMinutes = Math.Clamp(state.Settings.RoutineTimingIntervalMinutes, 1, 60);
        var jitterMinutes = Math.Clamp(state.Settings.RoutineTimingJitterMinutes, 0, 30);
        var localNow = now.ToLocalTime();
        var localMinute = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            localNow.Hour,
            localNow.Minute,
            0,
            localNow.Offset);

        var remainder = localMinute.Minute % intervalMinutes;
        var minutesUntilBoundary = remainder == 0
            ? intervalMinutes
            : intervalMinutes - remainder;
        var jitterSeconds = jitterMinutes <= 0
            ? 0
            : random.Next(0, jitterMinutes * 60 + 1);

        var dueAt = localMinute
            .AddMinutes(minutesUntilBoundary)
            .AddSeconds(jitterSeconds)
            .ToUniversalTime();
        var maxDueAt = now.AddMinutes(Math.Max(1, state.Settings.RoutineTimingMaxDelayMinutes));
        if (dueAt > maxDueAt)
        {
            dueAt = maxDueAt;
        }

        var minimumDueAt = now.AddSeconds(15);
        return dueAt < minimumDueAt ? minimumDueAt : dueAt;
    }

    private DateTimeOffset CalculateNaturalCadenceDueAt(DateTimeOffset now)
    {
        var minMinutes = Math.Clamp(state.Settings.NaturalCadenceMinimumMinutes, 1, 120);
        var maxMinutes = Math.Clamp(state.Settings.NaturalCadenceMaximumMinutes, minMinutes, 240);
        var jitterMinutes = Math.Clamp(state.Settings.NaturalCadenceJitterMinutes, 0, 60);
        var touchPressure = CalculateTouchSuspicionScore(now) / 100.0;
        var commandPressure = Math.Clamp(
            state.DefenderCommandTimes.Count / Math.Max(1.0, state.Settings.ComfortBudgetMaxCommands),
            0.0,
            1.0);
        var intensity = Math.Clamp(Math.Max(touchPressure, commandPressure), 0.0, 1.0);
        var baseSeconds = (int)Math.Round(Lerp(minMinutes * 60.0, maxMinutes * 60.0, intensity));
        var jitterSeconds = jitterMinutes <= 0
            ? 0
            : random.Next(-jitterMinutes * 60, jitterMinutes * 60 + 1);
        var delaySeconds = Math.Clamp(
            baseSeconds + jitterSeconds,
            30,
            Math.Max(30, (maxMinutes + jitterMinutes) * 60));

        return now.AddSeconds(delaySeconds);
    }

    private DateTimeOffset CalculateNaturalChangePlannerDueAt(ThermostatReading reading, DateTimeOffset now, out string reason)
    {
        var minMinutes = Math.Clamp(state.Settings.NaturalChangePlannerMinimumMinutes, 1, 240);
        var maxMinutes = Math.Clamp(state.Settings.NaturalChangePlannerMaximumMinutes, minMinutes, 480);
        var jitterMinutes = Math.Clamp(state.Settings.NaturalChangePlannerJitterMinutes, 0, 120);
        var touchPressure = CalculateTouchSuspicionScore(now) / 100.0;
        var commandPressure = Math.Clamp(
            state.DefenderCommandTimes.Count / Math.Max(1.0, state.Settings.ComfortBudgetMaxCommands),
            0.0,
            1.0);
        var intensity = Math.Clamp(Math.Max(touchPressure, commandPressure), 0.0, 1.0);
        var baseDelayMinutes = Lerp(minMinutes, maxMinutes, intensity);
        var candidate = now.AddMinutes(baseDelayMinutes);
        reason = "routine comfort-check";

        if (state.Settings.NaturalChangePlannerPreferWeatherSlots)
        {
            var drift = BuildWeatherDrift(now);
            if (drift.SampleCount >= 2 && (drift.Direction == "warming" || drift.ConditionChanged))
            {
                var weatherDelayMinutes = Math.Min(maxMinutes, minMinutes + Math.Max(1, state.Settings.WeatherDriftHoldMinutes / 2));
                candidate = now.AddMinutes(weatherDelayMinutes);
                reason = drift.ConditionChanged ? "weather update" : "outdoor warming";
            }
        }

        if (state.Settings.NaturalChangePlannerPreferSensorBeat)
        {
            var sensor = BuildSensorRhythmAnalysis(now);
            if (sensor.SampleCount >= Math.Max(2, state.Settings.SensorRhythmMinimumSamples)
                && sensor.MedianIntervalSeconds > 0)
            {
                var sensorCandidate = CalculateSensorRhythmDueAt(candidate, sensor);
                var maxSensorCandidate = now.AddMinutes(maxMinutes + jitterMinutes);
                if (sensorCandidate <= maxSensorCandidate)
                {
                    candidate = sensorCandidate;
                    reason = reason == "routine comfort-check"
                        ? "Home Assistant sensor beat"
                        : $"{reason} plus sensor beat";
                }
            }
        }

        if (reason == "routine comfort-check")
        {
            var boundaryMinutes = intensity >= 0.65 ? 10 : 5;
            candidate = AlignNaturalChangeToLocalBoundary(candidate, boundaryMinutes);
        }

        if (jitterMinutes > 0)
        {
            candidate = candidate.AddSeconds(random.Next(-jitterMinutes * 60, jitterMinutes * 60 + 1));
        }

        var earliest = now.AddMinutes(minMinutes);
        var latest = now.AddMinutes(maxMinutes + jitterMinutes);
        if (candidate < earliest)
        {
            candidate = earliest;
        }

        if (candidate > latest)
        {
            candidate = latest;
        }

        var absoluteMinimum = now.AddSeconds(30);
        return candidate < absoluteMinimum ? absoluteMinimum : candidate;
    }

    private static DateTimeOffset AlignNaturalChangeToLocalBoundary(DateTimeOffset candidate, int intervalMinutes)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 1, 60);
        var local = candidate.ToLocalTime();
        var localMinute = new DateTimeOffset(
            local.Year,
            local.Month,
            local.Day,
            local.Hour,
            local.Minute,
            0,
            local.Offset);
        var remainder = localMinute.Minute % intervalMinutes;
        var minutesUntilBoundary = remainder == 0
            ? intervalMinutes
            : intervalMinutes - remainder;

        return localMinute.AddMinutes(minutesUntilBoundary).ToUniversalTime();
    }

    private DateTimeOffset CalculateRepeatCommandHoldUntil(DateTimeOffset lastCommandAt, DateTimeOffset now)
    {
        var baseSeconds = Math.Clamp(state.Settings.RepeatCommandMinimumWaitSeconds, 0, 1800);
        var pressure = CalculateRepeatCommandPressure(now) / 100.0;
        var extraSeconds = (int)Math.Round(Math.Clamp(state.Settings.RepeatCommandPressureExtraSeconds, 0, 3600) * pressure);
        return lastCommandAt.AddSeconds(baseSeconds + extraSeconds);
    }

    private DateTimeOffset CalculateCommandCamouflageUntil(DateTimeOffset lastCommandAt, DateTimeOffset now)
    {
        var baseSeconds = Math.Clamp(state.Settings.CommandCamouflageMinimumGapSeconds, 0, 1800);
        var pressure = CalculateCommandCamouflagePressure(now) / 100.0;
        var extraSeconds = (int)Math.Round(Math.Clamp(state.Settings.CommandCamouflagePressureExtraSeconds, 0, 3600) * pressure);
        return lastCommandAt.AddSeconds(baseSeconds + extraSeconds);
    }

    private DateTimeOffset CalculateStealthGovernorUntil(int score, DateTimeOffset now)
    {
        var minMinutes = Math.Clamp(state.Settings.StealthGovernorMinimumHoldMinutes, 1, 240);
        var maxMinutes = Math.Clamp(state.Settings.StealthGovernorMaximumHoldMinutes, minMinutes, 480);
        var intensity = Math.Clamp(score / 100.0, 0.0, 1.0);
        var baseSeconds = (int)Math.Round(Lerp(minMinutes * 60.0, maxMinutes * 60.0, intensity));
        var jitterSeconds = random.Next(-45, 46);
        return now.AddSeconds(Math.Clamp(baseSeconds + jitterSeconds, 60, maxMinutes * 60));
    }

    private DateTimeOffset CalculateCoolingRunwayUntil(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var baseSeconds = Math.Clamp(state.Settings.CoolingRunwayMinimumSeconds, 0, 1800);
        var pressure = CalculateCoolingRunwayPressure(now) / 100.0;
        var extraSeconds = (int)Math.Round(Math.Clamp(state.Settings.CoolingRunwayPressureExtraSeconds, 0, 3600) * pressure);
        return startedAt.AddSeconds(baseSeconds + extraSeconds);
    }

    private int CalculateCoolingRunwayPressure(DateTimeOffset now)
    {
        PruneTouchTimes(now);
        PruneDefenderCommandTimes(now);
        var touchPressure = CalculateTouchSuspicionScore(now) / 100.0;
        var commandPressure = Math.Clamp(
            state.DefenderCommandTimes.Count / Math.Max(1.0, state.Settings.ComfortBudgetMaxCommands),
            0.0,
            1.0);
        return (int)Math.Round(Math.Clamp(Math.Max(touchPressure, commandPressure), 0.0, 1.0) * 100);
    }

    private int CalculateCommandCamouflagePressure(DateTimeOffset now)
    {
        PruneTouchTimes(now);
        PruneDefenderCommandTimes(now);
        var touchPressure = CalculateTouchSuspicionScore(now) / 100.0;
        var commandPressure = Math.Clamp(
            Math.Max(0, state.DefenderCommandTimes.Count - 1) / Math.Max(1.0, state.Settings.ComfortBudgetMaxCommands),
            0.0,
            1.0);
        return (int)Math.Round(Math.Clamp(Math.Max(touchPressure, commandPressure), 0.0, 1.0) * 100);
    }

    private int CalculateStealthGovernorScore(DateTimeOffset now)
    {
        PruneTouchTimes(now);
        PruneDefenderCommandTimes(now);
        PruneRemoteChangeTimes(now);
        PruneVisibilityNoticeTimes(now);

        var touchScore = CalculateTouchSuspicionScore(now);
        var noticeScore = CalculateVisibilityPressure(now);
        var commandScore = Math.Min(90, state.DefenderCommandTimes.Count * 22);
        var remoteScore = Math.Min(85, state.RemoteChangeTimes.Count * 24);
        var blendedScore = (int)Math.Round(
            touchScore * 0.45
            + noticeScore * 0.25
            + commandScore * 0.20
            + remoteScore * 0.10);
        return Math.Clamp(Math.Max(Math.Max(touchScore, noticeScore), Math.Max(commandScore, blendedScore)), 0, 100);
    }

    private int CalculateRepeatCommandPressure(DateTimeOffset now)
    {
        PruneTouchTimes(now);
        PruneDefenderCommandTimes(now);
        var touchPressure = CalculateTouchSuspicionScore(now) / 100.0;
        var commandPressure = Math.Clamp(
            state.DefenderCommandTimes.Count / Math.Max(1.0, state.Settings.ComfortBudgetMaxCommands),
            0.0,
            1.0);
        return (int)Math.Round(Math.Clamp(Math.Max(touchPressure, commandPressure), 0.0, 1.0) * 100);
    }

    private DateTimeOffset CalculateVisibilityGuardUntil(DateTimeOffset now)
    {
        var minMinutes = Math.Clamp(state.Settings.VisibilityGuardMinimumHoldMinutes, 1, 240);
        var maxMinutes = Math.Clamp(state.Settings.VisibilityGuardMaximumHoldMinutes, minMinutes, 480);
        var pressure = CalculateVisibilityPressure(now) / 100.0;
        var baseMinutes = Lerp(minMinutes, maxMinutes, pressure);
        var jitterSeconds = random.Next(-90, 91);
        var delaySeconds = Math.Clamp(
            (int)Math.Round(baseMinutes * 60) + jitterSeconds,
            60,
            maxMinutes * 60);

        return now.AddSeconds(delaySeconds);
    }

    private void RecordVisibilityNoticeIfNeeded(DateTimeOffset now)
    {
        if (!state.Settings.VisibilityGuardEnabled)
        {
            state.VisibilityGuardStatus = "Visibility guard is off.";
            return;
        }

        PruneVisibilityNoticeTimes(now);
        var afterCommand = TimeSpan.FromSeconds(Math.Max(15, state.Settings.VisibilityGuardAfterCommandSeconds));
        if (state.LastDefenderCommandAt is not { } lastCommandAt || now - lastCommandAt > afterCommand)
        {
            return;
        }

        state.VisibilityNoticeTimes.Add(now);
        if (state.VisibilityNoticeTimes.Count > 100)
        {
            state.VisibilityNoticeTimes.RemoveRange(0, state.VisibilityNoticeTimes.Count - 100);
        }

        state.VisibilityGuardUntil = null;
        state.VisibilityGuardStatus = $"Wall touch happened {Math.Round((now - lastCommandAt).TotalSeconds)}s after a defender command; visibility guard will slow safe corrections.";
    }

    private int CalculateNaturalHoldSeconds()
    {
        if (!state.Settings.NaturalRecoveryEnabled)
        {
            return 0;
        }

        var plan = BuildNaturalRecoveryPlan(DateTimeOffset.UtcNow);
        var minSeconds = Math.Max(5, plan.MinimumDelaySeconds / 2);
        var maxSeconds = Math.Max(minSeconds, Math.Min(
            Math.Max(minSeconds, plan.MaximumDelaySeconds),
            Math.Max(minSeconds, plan.MinimumDelaySeconds + state.Settings.BaseCooldownSeconds)));

        return maxSeconds == minSeconds
            ? minSeconds
            : random.Next(minSeconds, maxSeconds + 1);
    }

    private NaturalRecoveryPlan BuildNaturalRecoveryPlan(DateTimeOffset now)
    {
        PruneTouchTimes(now);

        var recentTouches = state.ExternalTouchTimes.Count;
        var minDelay = Math.Max(0, state.Settings.MinimumNaturalDelaySeconds);
        var maxDelay = Math.Max(minDelay, state.Settings.MaximumNaturalDelaySeconds);
        var step = Math.Round(Math.Max(0.1, state.Settings.NaturalStepCelsius), 1);
        var holdChance = Math.Clamp(state.Settings.NaturalHoldChancePercent, 0, 100);
        var maxHolds = Math.Clamp(state.Settings.MaxNaturalHolds, 0, 10);
        var commandGap = Math.Max(0, state.Settings.MinimumCommandGapSeconds);

        if (!state.Settings.NaturalRecoveryEnabled)
        {
            return new NaturalRecoveryPlan(
                recentTouches,
                "Off",
                minDelay,
                maxDelay,
                step,
                holdChance,
                maxHolds,
                commandGap,
                false);
        }

        var threshold = Math.Max(1, state.Settings.AdaptiveQuietTouchThreshold);
        if (!state.Settings.AdaptiveQuietnessEnabled || recentTouches < threshold)
        {
            var baseQuietLevel = recentTouches == 0 ? "Calm" : "Light";
            return new NaturalRecoveryPlan(
                recentTouches,
                baseQuietLevel,
                minDelay,
                maxDelay,
                step,
                holdChance,
                maxHolds,
                commandGap,
                false);
        }

        var excessTouches = Math.Max(1, recentTouches - threshold + 1);
        var intensity = Math.Min(1.0, excessTouches / 4.0);
        var adaptiveMaxDelay = Math.Max(maxDelay, state.Settings.MaximumAdaptiveDelaySeconds);
        var adaptiveMinDelay = Math.Min(adaptiveMaxDelay, (int)Math.Round(Lerp(minDelay, Math.Min(adaptiveMaxDelay, maxDelay), intensity)));
        var adaptiveStep = Math.Round(Lerp(
            step,
            Math.Min(step, Math.Max(0.1, state.Settings.MinimumAdaptiveStepCelsius)),
            intensity), 1);
        var adaptiveHoldChance = (int)Math.Round(Lerp(
            holdChance,
            Math.Max(holdChance, state.Settings.MaximumAdaptiveHoldChancePercent),
            intensity));
        var adaptiveCommandGap = (int)Math.Round(Lerp(
            commandGap,
            Math.Max(commandGap, state.Settings.MaximumAdaptiveCommandGapSeconds),
            intensity));
        var adaptiveMaxHolds = Math.Clamp(maxHolds + (int)Math.Ceiling(excessTouches / 2.0), 0, 10);
        var quietLevel = intensity >= 1.0
            ? "Softest"
            : intensity >= 0.5 ? "Extra quiet" : "Quiet";

        return new NaturalRecoveryPlan(
            recentTouches,
            quietLevel,
            adaptiveMinDelay,
            (int)Math.Round(Lerp(maxDelay, adaptiveMaxDelay, intensity)),
            adaptiveStep,
            Math.Clamp(adaptiveHoldChance, 0, 100),
            adaptiveMaxHolds,
            adaptiveCommandGap,
            true);
    }

    private static double Lerp(double start, double end, double amount)
    {
        return start + (end - start) * Math.Clamp(amount, 0.0, 1.0);
    }

    private bool ShouldBypassNaturalRecovery(ThermostatReading reading)
    {
        var overrideMargin = Math.Max(0.1, state.Settings.NaturalSafetyOverrideCelsius);
        return reading.CurrentTemperatureCelsius >= state.TargetTemperatureCelsius + overrideMargin;
    }

    private bool ShouldBypassCoolModeRestoreDelay(ThermostatReading reading)
    {
        if (ShouldBypassNaturalRecovery(reading))
        {
            return true;
        }

        var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.CoolModeRestoreComfortBandCelsius;
        if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
        {
            return true;
        }

        return state.UpstairsTooHot
            && state.HottestUpstairsTemperatureCelsius is { } hottest
            && hottest >= state.Settings.UpstairsMaxComfortCelsius + 1.0;
    }

    private int CalculateTouchSuspicionScore(DateTimeOffset now)
    {
        PruneTouchTimes(now);
        if (state.ExternalTouchTimes.Count == 0)
        {
            return 0;
        }

        var touchScore = Math.Min(80, state.ExternalTouchTimes.Count * 18);
        var lastTouch = state.ExternalTouchTimes.Max();
        var minutesSinceLastTouch = Math.Max(0.0, (now - lastTouch).TotalMinutes);
        var recencyScore = Math.Max(0, 20 - (int)Math.Round(minutesSinceLastTouch * 2.0));
        var conflictScore = state.ConflictQuietUntil is { } until && until > now ? 15 : 0;
        return Math.Clamp(touchScore + recencyScore + conflictScore, 0, 100);
    }

    private int CalculateVisibilityPressure(DateTimeOffset now)
    {
        PruneVisibilityNoticeTimes(now);
        if (state.VisibilityNoticeTimes.Count == 0)
        {
            return 0;
        }

        var noticeScore = Math.Min(75, state.VisibilityNoticeTimes.Count * 25);
        var latest = state.VisibilityNoticeTimes.Max();
        var minutesSinceLatest = Math.Max(0.0, (now - latest).TotalMinutes);
        var recencyScore = Math.Max(0, 25 - (int)Math.Round(minutesSinceLatest * 3.0));
        return Math.Clamp(noticeScore + recencyScore, 0, 100);
    }

    private ComfortMemorySlot? FindComfortMemorySlot(DateTimeOffset now)
    {
        var hour = now.ToLocalTime().Hour;
        return state.ComfortMemorySlots
            .Where(item => item.HourOfDay == hour)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
    }

    private void PruneComfortMemory(DateTimeOffset now)
    {
        var retention = TimeSpan.FromHours(Math.Max(1, state.Settings.ComfortMemoryRetentionHours));
        state.ComfortMemorySlots.RemoveAll(item => now - item.UpdatedAt > retention);
        if (state.ComfortMemorySlots.Count > 24)
        {
            state.ComfortMemorySlots = state.ComfortMemorySlots
                .OrderByDescending(item => item.UpdatedAt)
                .Take(24)
                .ToList();
        }
    }

    private void PruneTouchTimes(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.TouchFrequencyWindowMinutes));
        state.ExternalTouchTimes.RemoveAll(item => now - item > window);
    }

    private void PruneVisibilityNoticeTimes(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.VisibilityGuardNoticeWindowMinutes));
        state.VisibilityNoticeTimes.RemoveAll(item => now - item > window);
        if (state.VisibilityNoticeTimes.Count > 100)
        {
            state.VisibilityNoticeTimes.RemoveRange(0, state.VisibilityNoticeTimes.Count - 100);
        }

        if (state.VisibilityGuardUntil is { } until && until <= now)
        {
            state.VisibilityGuardUntil = null;
        }
    }

    private void PruneRemoteChangeTimes(DateTimeOffset now)
    {
        state.RemoteChangeTimes ??= [];
        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.SuperDefenderWindowMinutes));
        state.RemoteChangeTimes.RemoveAll(item => now - item > window);
        if (state.RemoteChangeTimes.Count > 200)
        {
            state.RemoteChangeTimes.RemoveRange(0, state.RemoteChangeTimes.Count - 200);
        }

        if (state.SuperDefenderUntil is { } until && until <= now)
        {
            state.SuperDefenderUntil = null;
        }
    }

    private void PruneDefenderCommandTimes(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.ComfortBudgetWindowMinutes));
        state.DefenderCommandTimes.RemoveAll(item => now - item > window);
        if (state.DefenderCommandTimes.Count > 200)
        {
            state.DefenderCommandTimes.RemoveRange(0, state.DefenderCommandTimes.Count - 200);
        }

        if (state.ComfortBudgetHoldUntil is { } holdUntil && holdUntil <= now)
        {
            state.ComfortBudgetHoldUntil = null;
        }
    }

    private static void PruneLoadedDefenderCommandTimes(DefenderRuntimeState saved)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Max(1, saved.Settings.ComfortBudgetWindowMinutes));
        saved.DefenderCommandTimes.RemoveAll(item => now - item > window);
        if (saved.DefenderCommandTimes.Count > 200)
        {
            saved.DefenderCommandTimes.RemoveRange(0, saved.DefenderCommandTimes.Count - 200);
        }

        if (saved.ComfortBudgetHoldUntil is { } holdUntil && holdUntil <= now)
        {
            saved.ComfortBudgetHoldUntil = null;
        }
    }

    private static void PruneLoadedVisibilityNoticeTimes(DefenderRuntimeState saved)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Max(1, saved.Settings.VisibilityGuardNoticeWindowMinutes));
        saved.VisibilityNoticeTimes.RemoveAll(item => now - item > window);
        if (saved.VisibilityNoticeTimes.Count > 100)
        {
            saved.VisibilityNoticeTimes.RemoveRange(0, saved.VisibilityNoticeTimes.Count - 100);
        }

        if (saved.VisibilityGuardUntil is { } until && until <= now)
        {
            saved.VisibilityGuardUntil = null;
        }
    }

    private static void PruneLoadedRemoteChangeTimes(DefenderRuntimeState saved)
    {
        var now = DateTimeOffset.UtcNow;
        saved.RemoteChangeTimes ??= [];
        var window = TimeSpan.FromMinutes(Math.Max(1, saved.Settings.SuperDefenderWindowMinutes));
        saved.RemoteChangeTimes.RemoveAll(item => now - item > window);
        if (saved.RemoteChangeTimes.Count > 200)
        {
            saved.RemoteChangeTimes.RemoveRange(0, saved.RemoteChangeTimes.Count - 200);
        }

        if (saved.SuperDefenderUntil is { } until && until <= now)
        {
            saved.SuperDefenderUntil = null;
        }
    }

    private static void PruneLoadedHomeAssistantReadingTimes(DefenderRuntimeState saved)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Max(5, saved.Settings.SensorRhythmWindowMinutes));
        saved.HomeAssistantReadingTimes.RemoveAll(item => now - item > window);
        if (saved.HomeAssistantReadingTimes.Count > 500)
        {
            saved.HomeAssistantReadingTimes.RemoveRange(0, saved.HomeAssistantReadingTimes.Count - 500);
        }
    }

    private DefenderRuntimeState LoadState()
    {
        try
        {
            if (File.Exists(stateFilePath))
            {
                var saved = JsonSerializer.Deserialize<DefenderRuntimeState>(File.ReadAllText(stateFilePath), jsonOptions);
                if (saved is not null)
                {
                    SanitizeLoadedState(saved);
                    return saved;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load defender state from {StateFilePath}", stateFilePath);
        }

        return new DefenderRuntimeState
        {
            TargetTemperatureCelsius = options.DefaultTargetCelsius,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void SanitizeLoadedState(DefenderRuntimeState saved)
    {
        if (string.Equals(saved.ConnectionState, "dummy", StringComparison.OrdinalIgnoreCase))
        {
            saved.ConnectionState = "unavailable";
        }

        saved.Settings ??= new DefenderSettings();
        saved.Settings.MaximumNaturalDelaySeconds = Math.Max(
            saved.Settings.MinimumNaturalDelaySeconds,
            saved.Settings.MaximumNaturalDelaySeconds);
        saved.Settings.AdaptiveQuietTouchThreshold = Math.Clamp(saved.Settings.AdaptiveQuietTouchThreshold, 1, 20);
        saved.Settings.MaximumAdaptiveDelaySeconds = Math.Max(
            saved.Settings.MaximumNaturalDelaySeconds,
            saved.Settings.MaximumAdaptiveDelaySeconds);
        saved.Settings.NaturalStepCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalStepCelsius, 0.1, 5.0), 1);
        saved.Settings.MinimumAdaptiveStepCelsius = Math.Round(Math.Clamp(
            Math.Min(saved.Settings.NaturalStepCelsius, saved.Settings.MinimumAdaptiveStepCelsius),
            0.1,
            saved.Settings.NaturalStepCelsius), 1);
        saved.Settings.NaturalHoldChancePercent = Math.Clamp(saved.Settings.NaturalHoldChancePercent, 0, 100);
        saved.Settings.MaximumAdaptiveHoldChancePercent = Math.Clamp(
            Math.Max(saved.Settings.NaturalHoldChancePercent, saved.Settings.MaximumAdaptiveHoldChancePercent),
            0,
            100);
        saved.Settings.MaxNaturalHolds = Math.Clamp(saved.Settings.MaxNaturalHolds, 0, 10);
        saved.Settings.MinimumCommandGapSeconds = Math.Max(0, saved.Settings.MinimumCommandGapSeconds);
        saved.Settings.MaximumAdaptiveCommandGapSeconds = Math.Max(
            saved.Settings.MinimumCommandGapSeconds,
            saved.Settings.MaximumAdaptiveCommandGapSeconds);
        saved.Settings.NaturalSafetyOverrideCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalSafetyOverrideCelsius, 0.1, 10.0), 1);
        saved.Settings.NaturalWalkbackTriggerTouches = Math.Clamp(saved.Settings.NaturalWalkbackTriggerTouches, 1, 20);
        saved.Settings.NaturalWalkbackStepCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalWalkbackStepCelsius, 0.1, 5.0), 1);
        saved.Settings.NaturalWalkbackJitterCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalWalkbackJitterCelsius, 0.0, 0.5), 1);
        saved.Settings.NaturalWalkbackSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalWalkbackSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.TouchSignatureTriggerTouches = Math.Clamp(saved.Settings.TouchSignatureTriggerTouches, 1, 20);
        saved.Settings.TouchSignatureRetentionMinutes = Math.Clamp(saved.Settings.TouchSignatureRetentionMinutes, 1, 1440);
        saved.Settings.TouchSignatureMinimumStepCelsius = Math.Round(Math.Clamp(saved.Settings.TouchSignatureMinimumStepCelsius, 0.1, 5.0), 1);
        saved.Settings.TouchSignatureMaximumStepCelsius = Math.Round(Math.Clamp(
            saved.Settings.TouchSignatureMaximumStepCelsius,
            saved.Settings.TouchSignatureMinimumStepCelsius,
            5.0), 1);
        saved.Settings.TouchSignatureSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.TouchSignatureSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.HumanNudgeTriggerTouches = Math.Clamp(saved.Settings.HumanNudgeTriggerTouches <= 0 ? 2 : saved.Settings.HumanNudgeTriggerTouches, 1, 20);
        saved.Settings.HumanNudgeStepCelsius = Math.Round(Math.Clamp(saved.Settings.HumanNudgeStepCelsius <= 0 ? 0.5 : saved.Settings.HumanNudgeStepCelsius, 0.1, 2.0), 1);
        saved.Settings.HumanNudgeSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.HumanNudgeSafetyBandCelsius <= 0 ? 1.0 : saved.Settings.HumanNudgeSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.VisibilityGuardTriggerNotices = Math.Clamp(saved.Settings.VisibilityGuardTriggerNotices, 1, 20);
        saved.Settings.VisibilityGuardNoticeWindowMinutes = Math.Clamp(saved.Settings.VisibilityGuardNoticeWindowMinutes, 1, 1440);
        saved.Settings.VisibilityGuardAfterCommandSeconds = Math.Clamp(saved.Settings.VisibilityGuardAfterCommandSeconds, 15, 3600);
        saved.Settings.VisibilityGuardMinimumHoldMinutes = Math.Clamp(saved.Settings.VisibilityGuardMinimumHoldMinutes, 1, 240);
        saved.Settings.VisibilityGuardMaximumHoldMinutes = Math.Clamp(
            saved.Settings.VisibilityGuardMaximumHoldMinutes,
            saved.Settings.VisibilityGuardMinimumHoldMinutes,
            480);
        saved.Settings.VisibilityGuardSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.VisibilityGuardSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.RoutineTimingTriggerTouches = Math.Clamp(saved.Settings.RoutineTimingTriggerTouches, 1, 20);
        saved.Settings.RoutineTimingIntervalMinutes = Math.Clamp(saved.Settings.RoutineTimingIntervalMinutes, 1, 60);
        saved.Settings.RoutineTimingJitterMinutes = Math.Clamp(saved.Settings.RoutineTimingJitterMinutes, 0, 30);
        saved.Settings.RoutineTimingMaxDelayMinutes = Math.Clamp(saved.Settings.RoutineTimingMaxDelayMinutes, 1, 180);
        saved.Settings.RoutineTimingSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.RoutineTimingSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.ComfortBudgetWindowMinutes = Math.Clamp(saved.Settings.ComfortBudgetWindowMinutes, 1, 240);
        saved.Settings.ComfortBudgetMaxCommands = Math.Clamp(saved.Settings.ComfortBudgetMaxCommands, 1, 30);
        saved.Settings.ComfortBudgetSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortBudgetSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.CommandCamouflageMinimumGapSeconds = Math.Clamp(saved.Settings.CommandCamouflageMinimumGapSeconds, 0, 1800);
        saved.Settings.CommandCamouflagePressureExtraSeconds = Math.Clamp(saved.Settings.CommandCamouflagePressureExtraSeconds, 0, 3600);
        saved.Settings.CommandCamouflageSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.CommandCamouflageSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.StealthGovernorTriggerScore = Math.Clamp(saved.Settings.StealthGovernorTriggerScore <= 0 ? 65 : saved.Settings.StealthGovernorTriggerScore, 1, 100);
        saved.Settings.StealthGovernorMinimumHoldMinutes = Math.Clamp(saved.Settings.StealthGovernorMinimumHoldMinutes <= 0 ? 5 : saved.Settings.StealthGovernorMinimumHoldMinutes, 1, 240);
        saved.Settings.StealthGovernorMaximumHoldMinutes = Math.Clamp(
            saved.Settings.StealthGovernorMaximumHoldMinutes <= 0 ? 25 : saved.Settings.StealthGovernorMaximumHoldMinutes,
            saved.Settings.StealthGovernorMinimumHoldMinutes,
            480);
        saved.Settings.StealthGovernorSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.StealthGovernorSafetyBandCelsius <= 0 ? 1.2 : saved.Settings.StealthGovernorSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.NaturalCadenceTriggerTouches = Math.Clamp(saved.Settings.NaturalCadenceTriggerTouches, 1, 20);
        saved.Settings.NaturalCadenceMinimumMinutes = Math.Clamp(saved.Settings.NaturalCadenceMinimumMinutes, 1, 120);
        saved.Settings.NaturalCadenceMaximumMinutes = Math.Clamp(
            saved.Settings.NaturalCadenceMaximumMinutes,
            saved.Settings.NaturalCadenceMinimumMinutes,
            240);
        saved.Settings.NaturalCadenceJitterMinutes = Math.Clamp(saved.Settings.NaturalCadenceJitterMinutes, 0, 60);
        saved.Settings.NaturalCadenceSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalCadenceSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.NaturalChangePlannerTriggerTouches = Math.Clamp(saved.Settings.NaturalChangePlannerTriggerTouches, 1, 20);
        saved.Settings.NaturalChangePlannerMinimumMinutes = Math.Clamp(saved.Settings.NaturalChangePlannerMinimumMinutes, 1, 240);
        saved.Settings.NaturalChangePlannerMaximumMinutes = Math.Clamp(
            saved.Settings.NaturalChangePlannerMaximumMinutes,
            saved.Settings.NaturalChangePlannerMinimumMinutes,
            480);
        saved.Settings.NaturalChangePlannerJitterMinutes = Math.Clamp(saved.Settings.NaturalChangePlannerJitterMinutes, 0, 120);
        saved.Settings.NaturalChangePlannerSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalChangePlannerSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.ComfortEnvelopeTriggerTouches = Math.Clamp(saved.Settings.ComfortEnvelopeTriggerTouches, 1, 20);
        saved.Settings.ComfortEnvelopeHoldMinutes = Math.Clamp(saved.Settings.ComfortEnvelopeHoldMinutes, 0, 240);
        saved.Settings.ComfortEnvelopeMaxOffsetCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortEnvelopeMaxOffsetCelsius, 0.1, 5.0), 1);
        saved.Settings.ComfortEnvelopeSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortEnvelopeSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.ComfortCompromiseTriggerTouches = Math.Clamp(saved.Settings.ComfortCompromiseTriggerTouches, 1, 20);
        saved.Settings.ComfortCompromiseHoldMinutes = Math.Clamp(saved.Settings.ComfortCompromiseHoldMinutes, 0, 240);
        saved.Settings.ComfortCompromiseDecayMinutes = Math.Clamp(saved.Settings.ComfortCompromiseDecayMinutes, 1, 240);
        saved.Settings.ComfortCompromiseMaxOffsetCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortCompromiseMaxOffsetCelsius, 0.1, 5.0), 1);
        saved.Settings.ComfortCompromiseSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortCompromiseSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.ComfortMemoryLearningTouches = Math.Clamp(saved.Settings.ComfortMemoryLearningTouches, 1, 20);
        saved.Settings.ComfortMemoryRetentionHours = Math.Clamp(saved.Settings.ComfortMemoryRetentionHours, 1, 168);
        saved.Settings.ComfortMemoryMaxOffsetCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortMemoryMaxOffsetCelsius, 0.1, 3.0), 1);
        saved.Settings.ComfortMemorySafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.ComfortMemorySafetyBandCelsius, 0.1, 3.0), 1);
        saved.Settings.CoolModeRestoreMinimumDelaySeconds = Math.Clamp(saved.Settings.CoolModeRestoreMinimumDelaySeconds, 0, 3600);
        saved.Settings.CoolModeRestoreMaximumDelaySeconds = Math.Clamp(
            saved.Settings.CoolModeRestoreMaximumDelaySeconds,
            saved.Settings.CoolModeRestoreMinimumDelaySeconds,
            7200);
        saved.Settings.CoolModeRestoreComfortBandCelsius = Math.Round(Math.Clamp(saved.Settings.CoolModeRestoreComfortBandCelsius, 0.1, 5.0), 1);
        saved.Settings.ConflictQuietTouchThreshold = Math.Clamp(saved.Settings.ConflictQuietTouchThreshold, 2, 20);
        saved.Settings.ConflictQuietMinutes = Math.Clamp(saved.Settings.ConflictQuietMinutes, 1, 240);
        saved.Settings.ConflictQuietComfortBandCelsius = Math.Round(Math.Clamp(saved.Settings.ConflictQuietComfortBandCelsius, 0.1, 5.0), 1);
        saved.Settings.WallSettlingMinimumTouches = Math.Clamp(saved.Settings.WallSettlingMinimumTouches, 1, 20);
        saved.Settings.WallSettlingWindowMinutes = Math.Clamp(saved.Settings.WallSettlingWindowMinutes, 1, 1440);
        saved.Settings.WallSettlingBaseSeconds = Math.Clamp(saved.Settings.WallSettlingBaseSeconds, 0, 1800);
        saved.Settings.WallSettlingPressureExtraSeconds = Math.Clamp(saved.Settings.WallSettlingPressureExtraSeconds, 0, 3600);
        saved.Settings.WallSettlingSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.WallSettlingSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.ManualComfortGraceMinutes = Math.Clamp(saved.Settings.ManualComfortGraceMinutes, 0, 240);
        saved.Settings.ManualComfortGraceBandCelsius = Math.Round(Math.Clamp(saved.Settings.ManualComfortGraceBandCelsius, 0.1, 5.0), 1);
        saved.Settings.TouchIntentMinimumTouches = Math.Clamp(saved.Settings.TouchIntentMinimumTouches, 1, 20);
        saved.Settings.TouchIntentWindowMinutes = Math.Clamp(saved.Settings.TouchIntentWindowMinutes, 1, 1440);
        saved.Settings.TouchIntentNetWarmThresholdCelsius = Math.Round(Math.Clamp(saved.Settings.TouchIntentNetWarmThresholdCelsius, 0.1, 5.0), 1);
        saved.Settings.TouchIntentExtraGraceMinutes = Math.Clamp(saved.Settings.TouchIntentExtraGraceMinutes, 0, 240);
        saved.Settings.TouchIntentSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.TouchIntentSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.CoolerIntentMinimumTouches = Math.Clamp(saved.Settings.CoolerIntentMinimumTouches, 1, 20);
        saved.Settings.CoolerIntentWindowMinutes = Math.Clamp(saved.Settings.CoolerIntentWindowMinutes, 1, 1440);
        saved.Settings.CoolerIntentHoldMinutes = Math.Clamp(saved.Settings.CoolerIntentHoldMinutes, 0, 240);
        saved.Settings.CoolerIntentNetCoolThresholdCelsius = Math.Round(Math.Clamp(saved.Settings.CoolerIntentNetCoolThresholdCelsius, 0.1, 5.0), 1);
        saved.Settings.CoolerIntentSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.CoolerIntentSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.SetpointEchoGraceSeconds = Math.Clamp(saved.Settings.SetpointEchoGraceSeconds, 5, 300);
        saved.Settings.SetpointEchoSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.SetpointEchoSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.RepeatCommandMinimumWaitSeconds = Math.Clamp(saved.Settings.RepeatCommandMinimumWaitSeconds, 0, 1800);
        saved.Settings.RepeatCommandPressureExtraSeconds = Math.Clamp(saved.Settings.RepeatCommandPressureExtraSeconds, 0, 3600);
        saved.Settings.RepeatCommandSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.RepeatCommandSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.SensorRhythmMinimumSamples = Math.Clamp(saved.Settings.SensorRhythmMinimumSamples, 2, 60);
        saved.Settings.SensorRhythmWindowMinutes = Math.Clamp(saved.Settings.SensorRhythmWindowMinutes, 5, 1440);
        saved.Settings.SensorRhythmJitterSeconds = Math.Clamp(saved.Settings.SensorRhythmJitterSeconds, 0, 300);
        saved.Settings.SensorRhythmSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.SensorRhythmSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.HvacActionAlibiTriggerTouches = Math.Clamp(saved.Settings.HvacActionAlibiTriggerTouches <= 0 ? 2 : saved.Settings.HvacActionAlibiTriggerTouches, 1, 20);
        saved.Settings.HvacActionAlibiTransitionWindowSeconds = Math.Clamp(saved.Settings.HvacActionAlibiTransitionWindowSeconds <= 0 ? 90 : saved.Settings.HvacActionAlibiTransitionWindowSeconds, 5, 1800);
        saved.Settings.HvacActionAlibiMaxHoldMinutes = Math.Clamp(saved.Settings.HvacActionAlibiMaxHoldMinutes <= 0 ? 12 : saved.Settings.HvacActionAlibiMaxHoldMinutes, 1, 240);
        saved.Settings.HvacActionAlibiSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.HvacActionAlibiSafetyBandCelsius <= 0 ? 1.0 : saved.Settings.HvacActionAlibiSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.CoolingRunwayMinimumSeconds = Math.Clamp(saved.Settings.CoolingRunwayMinimumSeconds, 0, 1800);
        saved.Settings.CoolingRunwayPressureExtraSeconds = Math.Clamp(saved.Settings.CoolingRunwayPressureExtraSeconds, 0, 3600);
        saved.Settings.CoolingRunwaySafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.CoolingRunwaySafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.RoomTrendWindowMinutes = Math.Clamp(saved.Settings.RoomTrendWindowMinutes, 2, 240);
        saved.Settings.RoomTrendStableToleranceCelsius = Math.Round(Math.Clamp(saved.Settings.RoomTrendStableToleranceCelsius, 0.05, 2.0), 2);
        saved.Settings.RoomTrendHoldMinutes = Math.Clamp(saved.Settings.RoomTrendHoldMinutes, 1, 120);
        saved.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour = Math.Round(Math.Clamp(saved.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour, 0.1, 5.0), 2);
        saved.Settings.ThermalMomentumLookAheadMinutes = Math.Clamp(saved.Settings.ThermalMomentumLookAheadMinutes, 5, 240);
        saved.Settings.ThermalMomentumHoldMinutes = Math.Clamp(saved.Settings.ThermalMomentumHoldMinutes, 1, 120);
        saved.Settings.WeatherDriftWindowMinutes = Math.Clamp(saved.Settings.WeatherDriftWindowMinutes, 5, 1440);
        saved.Settings.WeatherDriftMinimumChangeCelsius = Math.Round(Math.Clamp(saved.Settings.WeatherDriftMinimumChangeCelsius, 0.1, 5.0), 1);
        saved.Settings.WeatherDriftHoldMinutes = Math.Clamp(saved.Settings.WeatherDriftHoldMinutes, 1, 120);
        saved.Settings.WeatherDriftSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.WeatherDriftSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.PeakPowerSaverPowerThresholdKilowatts = Math.Round(Math.Clamp(saved.Settings.PeakPowerSaverPowerThresholdKilowatts <= 0 ? 2.5 : saved.Settings.PeakPowerSaverPowerThresholdKilowatts, 0.1, 50.0), 1);
        saved.Settings.PeakPowerSaverPriceThresholdCentsPerKwh = Math.Round(Math.Clamp(saved.Settings.PeakPowerSaverPriceThresholdCentsPerKwh < 0 ? 15.0 : saved.Settings.PeakPowerSaverPriceThresholdCentsPerKwh, 0.0, 200.0), 1);
        saved.Settings.PeakPowerSaverHoldMinutes = Math.Clamp(saved.Settings.PeakPowerSaverHoldMinutes <= 0 ? 20 : saved.Settings.PeakPowerSaverHoldMinutes, 1, 240);
        saved.Settings.PeakPowerSaverRefreshSeconds = Math.Clamp(saved.Settings.PeakPowerSaverRefreshSeconds <= 0 ? 120 : saved.Settings.PeakPowerSaverRefreshSeconds, 30, 3600);
        saved.Settings.PeakPowerSaverSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.PeakPowerSaverSafetyBandCelsius <= 0 ? 1.0 : saved.Settings.PeakPowerSaverSafetyBandCelsius, 0.1, 10.0), 1);
        saved.Settings.PeakPowerSaverFanMode = string.IsNullOrWhiteSpace(saved.Settings.PeakPowerSaverFanMode)
            ? "auto"
            : saved.Settings.PeakPowerSaverFanMode.Trim();
        saved.Settings.FrontDoorPersonEntityIds = saved.Settings.FrontDoorPersonEntityIds?.Trim() ?? string.Empty;
        saved.Settings.FrontDoorKillSwitchHoldMinutes = Math.Clamp(saved.Settings.FrontDoorKillSwitchHoldMinutes <= 0 ? 20 : saved.Settings.FrontDoorKillSwitchHoldMinutes, 1, 240);
        saved.Settings.FrontDoorKillSwitchRefreshSeconds = Math.Clamp(saved.Settings.FrontDoorKillSwitchRefreshSeconds <= 0 ? 5 : saved.Settings.FrontDoorKillSwitchRefreshSeconds, 2, 300);
        saved.Settings.SuperDefenderRemoteChangeThreshold = Math.Clamp(saved.Settings.SuperDefenderRemoteChangeThreshold, 1, 20);
        saved.Settings.SuperDefenderWindowMinutes = Math.Clamp(saved.Settings.SuperDefenderWindowMinutes, 1, 1440);
        saved.Settings.SuperDefenderHoldMinutes = Math.Clamp(saved.Settings.SuperDefenderHoldMinutes, 0, 240);
        saved.Settings.SuperDefenderSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.SuperDefenderSafetyBandCelsius, 0.1, 10.0), 1);
        saved.Settings.DefenderRunsContinuously = true;
        saved.Schedule ??= [];
        saved.Events ??= [];
        saved.ThermostatChanges ??= [];
        saved.ExternalTouchTimes ??= [];
        saved.UpstairsSensors ??= [];
        saved.Presence ??= [];
        saved.RoomTemperatureSamples ??= [];
        saved.WeatherSamples ??= [];
        saved.HomeAssistantReadingTimes ??= [];
        saved.ComfortMemorySlots ??= [];
        saved.DefenderCommandTimes ??= [];
        saved.VisibilityNoticeTimes ??= [];
        saved.RemoteChangeTimes ??= [];
        saved.FrontDoorPersonReadings ??= [];
        PruneLoadedDefenderCommandTimes(saved);
        PruneLoadedVisibilityNoticeTimes(saved);
        PruneLoadedRemoteChangeTimes(saved);
        PruneLoadedHomeAssistantReadingTimes(saved);
        saved.NaturalRecoveryStatus = string.IsNullOrWhiteSpace(saved.NaturalRecoveryStatus)
            ? "Comfort sync is ready."
            : saved.NaturalRecoveryStatus;
        saved.NaturalWalkbackStatus = string.IsNullOrWhiteSpace(saved.NaturalWalkbackStatus)
            ? "Natural walkback is watching."
            : saved.NaturalWalkbackStatus;
        saved.TouchSignatureStatus = string.IsNullOrWhiteSpace(saved.TouchSignatureStatus)
            ? "Touch signature is watching."
            : saved.TouchSignatureStatus;
        saved.VisibilityGuardStatus = string.IsNullOrWhiteSpace(saved.VisibilityGuardStatus)
            ? "Visibility guard is watching."
            : saved.VisibilityGuardStatus;
        saved.RoutineTimingStatus = string.IsNullOrWhiteSpace(saved.RoutineTimingStatus)
            ? "Routine timing is watching."
            : saved.RoutineTimingStatus;
        saved.ComfortBudgetStatus = string.IsNullOrWhiteSpace(saved.ComfortBudgetStatus)
            ? "Comfort budget is watching."
            : saved.ComfortBudgetStatus;
        saved.CommandCamouflageStatus = string.IsNullOrWhiteSpace(saved.CommandCamouflageStatus)
            ? "Command camouflage is watching for a recent helper command."
            : saved.CommandCamouflageStatus;
        if (!saved.Settings.CommandCamouflageEnabled)
        {
            saved.CommandCamouflageHoldUntil = null;
            saved.CommandCamouflageStatus = "Command camouflage is off.";
        }
        else if (saved.CommandCamouflageHoldUntil is { } camouflageUntil && camouflageUntil <= DateTimeOffset.UtcNow)
        {
            saved.CommandCamouflageHoldUntil = null;
            saved.CommandCamouflageStatus = "Command camouflage is watching for a recent helper command.";
        }
        saved.StealthGovernorStatus = string.IsNullOrWhiteSpace(saved.StealthGovernorStatus)
            ? "Stealth governor is watching overall pressure."
            : saved.StealthGovernorStatus;
        if (!saved.Settings.StealthGovernorEnabled)
        {
            saved.StealthGovernorHoldUntil = null;
            saved.StealthGovernorStatus = "Stealth governor is off.";
        }
        else if (saved.StealthGovernorHoldUntil is { } stealthUntil && stealthUntil <= DateTimeOffset.UtcNow)
        {
            saved.StealthGovernorHoldUntil = null;
            saved.StealthGovernorStatus = "Stealth governor is watching overall pressure.";
        }
        saved.HumanNudgeStatus = string.IsNullOrWhiteSpace(saved.HumanNudgeStatus)
            ? "Human nudge is watching for a safe command to shape."
            : saved.HumanNudgeStatus;
        if (!saved.Settings.HumanNudgeEnabled)
        {
            saved.HumanNudgeActive = false;
            saved.HumanNudgeLastSetPointCelsius = null;
            saved.HumanNudgeStatus = "Human nudge is off.";
        }
        saved.NaturalCadenceStatus = string.IsNullOrWhiteSpace(saved.NaturalCadenceStatus)
            ? "Natural cadence is watching."
            : saved.NaturalCadenceStatus;
        if (saved.NaturalCadenceDueAt is { } cadenceDueAt && cadenceDueAt <= DateTimeOffset.UtcNow)
        {
            saved.NaturalCadenceDueAt = null;
        }
        saved.NaturalChangePlannerReason = string.IsNullOrWhiteSpace(saved.NaturalChangePlannerReason)
            ? "watching"
            : saved.NaturalChangePlannerReason;
        saved.NaturalChangePlannerStatus = string.IsNullOrWhiteSpace(saved.NaturalChangePlannerStatus)
            ? "Comfort Pace is watching."
            : saved.NaturalChangePlannerStatus;
        if (!saved.Settings.NaturalChangePlannerEnabled)
        {
            saved.NaturalChangePlannerDueAt = null;
            saved.NaturalChangePlannerReason = "watching";
            saved.NaturalChangePlannerStatus = "Comfort Pace is off.";
        }
        else if (saved.NaturalChangePlannerDueAt is { } naturalChangeDueAt && naturalChangeDueAt <= DateTimeOffset.UtcNow)
        {
            saved.NaturalChangePlannerDueAt = null;
            saved.NaturalChangePlannerReason = "watching";
            saved.NaturalChangePlannerStatus = "Comfort Pace is watching.";
        }
        saved.ComfortEnvelopeStatus = string.IsNullOrWhiteSpace(saved.ComfortEnvelopeStatus)
            ? "Comfort envelope is watching."
            : saved.ComfortEnvelopeStatus;
        if (!saved.Settings.ComfortEnvelopeEnabled)
        {
            saved.ComfortEnvelopeUntil = null;
            saved.ComfortEnvelopePreferredSetPointCelsius = null;
            saved.ComfortEnvelopeMinimumAllowedSetPointCelsius = null;
            saved.ComfortEnvelopeMaximumAllowedSetPointCelsius = null;
            saved.ComfortEnvelopeStatus = "Comfort envelope is off.";
        }
        else if (saved.ComfortEnvelopeUntil is { } envelopeUntil && envelopeUntil <= DateTimeOffset.UtcNow)
        {
            saved.ComfortEnvelopeUntil = null;
            saved.ComfortEnvelopePreferredSetPointCelsius = null;
            saved.ComfortEnvelopeMinimumAllowedSetPointCelsius = null;
            saved.ComfortEnvelopeMaximumAllowedSetPointCelsius = null;
            saved.ComfortEnvelopeStatus = "Comfort envelope is watching.";
        }
        saved.ComfortCompromiseStatus = string.IsNullOrWhiteSpace(saved.ComfortCompromiseStatus)
            ? "Comfort compromise is watching for repeated wall changes."
            : saved.ComfortCompromiseStatus;
        saved.ComfortMemoryStatus = string.IsNullOrWhiteSpace(saved.ComfortMemoryStatus)
            ? "Comfort memory is watching wall choices."
            : saved.ComfortMemoryStatus;
        saved.CoolModeRestoreStatus = string.IsNullOrWhiteSpace(saved.CoolModeRestoreStatus)
            ? "Cool mode restore is watching."
            : saved.CoolModeRestoreStatus;
        saved.ConflictQuietStatus = string.IsNullOrWhiteSpace(saved.ConflictQuietStatus)
            ? "Conflict quiet is watching."
            : saved.ConflictQuietStatus;
        saved.WallSettlingStatus = string.IsNullOrWhiteSpace(saved.WallSettlingStatus)
            ? "Wall settling is watching."
            : saved.WallSettlingStatus;
        if (!saved.Settings.WallSettlingGuardEnabled)
        {
            saved.WallSettlingUntil = null;
            saved.WallSettlingStatus = "Wall settling guard is off.";
        }
        else if (saved.WallSettlingUntil is { } wallSettlingUntil && wallSettlingUntil <= DateTimeOffset.UtcNow)
        {
            saved.WallSettlingUntil = null;
            saved.WallSettlingStatus = "Wall settling is watching.";
        }
        saved.ManualComfortGraceStatus = string.IsNullOrWhiteSpace(saved.ManualComfortGraceStatus)
            ? "No wall-change grace active."
            : saved.ManualComfortGraceStatus;
        saved.TouchIntentStatus = string.IsNullOrWhiteSpace(saved.TouchIntentStatus)
            ? "Touch intent is watching."
            : saved.TouchIntentStatus;
        saved.CoolerIntentStatus = string.IsNullOrWhiteSpace(saved.CoolerIntentStatus)
            ? "Cooler intent fast lane is watching for repeated cooler wall touches."
            : saved.CoolerIntentStatus;
        if (!saved.Settings.CoolerIntentFastLaneEnabled)
        {
            saved.CoolerIntentUntil = null;
            saved.CoolerIntentStatus = "Cooler intent fast lane is off.";
        }
        else if (saved.CoolerIntentUntil is { } coolerIntentUntil && coolerIntentUntil <= DateTimeOffset.UtcNow)
        {
            saved.CoolerIntentUntil = null;
            saved.CoolerIntentStatus = "Cooler intent fast lane is watching for repeated cooler wall touches.";
        }
        saved.SetpointEchoStatus = string.IsNullOrWhiteSpace(saved.SetpointEchoStatus)
            ? "Setpoint echo is watching."
            : saved.SetpointEchoStatus;
        saved.RepeatCommandStatus = string.IsNullOrWhiteSpace(saved.RepeatCommandStatus)
            ? "Repeat quiet is watching for identical follow-up commands."
            : saved.RepeatCommandStatus;
        if (saved.RepeatCommandHoldUntil is { } repeatHoldUntil && repeatHoldUntil <= DateTimeOffset.UtcNow)
        {
            saved.RepeatCommandHoldUntil = null;
            saved.RepeatCommandStatus = "Repeat quiet is watching for identical follow-up commands.";
        }
        if (saved.PendingCommandAt is { } pendingAt
            && DateTimeOffset.UtcNow - pendingAt > TimeSpan.FromSeconds(Math.Max(saved.Settings.SetpointEchoGraceSeconds, 300)))
        {
            saved.PendingCommandSetPointCelsius = null;
            saved.PendingCommandHvacMode = null;
            saved.PendingCommandFanMode = null;
            saved.PendingCommandAt = null;
            saved.PendingCommandSourceKind = null;
            saved.PendingCommandSourceLabel = null;
            saved.PendingCommandSourceDetail = null;
            saved.SetpointEchoStatus = "Setpoint echo is watching.";
        }
        saved.SensorRhythmStatus = string.IsNullOrWhiteSpace(saved.SensorRhythmStatus)
            ? "Sensor rhythm is watching."
            : saved.SensorRhythmStatus;
        if (saved.SensorRhythmDueAt is { } rhythmDueAt && rhythmDueAt <= DateTimeOffset.UtcNow)
        {
            saved.SensorRhythmDueAt = null;
        }
        saved.HvacActionAlibiCurrentAction = string.IsNullOrWhiteSpace(saved.HvacActionAlibiCurrentAction)
            ? "unknown"
            : saved.HvacActionAlibiCurrentAction;
        saved.HvacActionAlibiStatus = string.IsNullOrWhiteSpace(saved.HvacActionAlibiStatus)
            ? "HVAC alibi is watching for a real action transition."
            : saved.HvacActionAlibiStatus;
        if (!saved.Settings.HvacActionAlibiEnabled)
        {
            saved.HvacActionAlibiStartedAt = null;
            saved.HvacActionAlibiUntil = null;
            saved.HvacActionAlibiStatus = "HVAC alibi is off.";
        }
        else if (saved.HvacActionAlibiUntil is { } alibiUntil && alibiUntil <= DateTimeOffset.UtcNow)
        {
            saved.HvacActionAlibiStartedAt = null;
            saved.HvacActionAlibiUntil = null;
            saved.HvacActionAlibiStatus = "HVAC alibi is watching for a real action transition.";
        }
        saved.CoolingRunwayStatus = string.IsNullOrWhiteSpace(saved.CoolingRunwayStatus)
            ? "Cooling runway is watching for a fresh cooling start."
            : saved.CoolingRunwayStatus;
        if (saved.CoolingRunwayHoldUntil is { } runwayUntil && runwayUntil <= DateTimeOffset.UtcNow)
        {
            saved.CoolingRunwayHoldUntil = null;
            saved.CoolingRunwayStatus = "Cooling runway is watching for a fresh cooling start.";
        }
        saved.RoomTrendStatus = string.IsNullOrWhiteSpace(saved.RoomTrendStatus)
            ? "Room trend guard is watching."
            : saved.RoomTrendStatus;
        saved.ThermalMomentumStatus = string.IsNullOrWhiteSpace(saved.ThermalMomentumStatus)
            ? "Thermal momentum guard is watching."
            : saved.ThermalMomentumStatus;
        saved.WeatherDriftStatus = string.IsNullOrWhiteSpace(saved.WeatherDriftStatus)
            ? "Weather drift guard is watching."
            : saved.WeatherDriftStatus;
        if (saved.WeatherDriftHoldUntil is { } weatherDriftUntil && weatherDriftUntil <= DateTimeOffset.UtcNow)
        {
            saved.WeatherDriftHoldUntil = null;
            saved.WeatherDriftStatus = "Weather drift guard is watching.";
        }
        saved.PeakPowerSaverStatus = string.IsNullOrWhiteSpace(saved.PeakPowerSaverStatus)
            ? "Alectra Peak Power Saver is watching usage sensors."
            : saved.PeakPowerSaverStatus;
        if (!saved.Settings.PeakPowerSaverEnabled)
        {
            saved.PeakPowerSaverUntil = null;
            saved.PeakPowerSaverStatus = "Alectra Peak Power Saver is off.";
        }
        else if (saved.PeakPowerSaverUntil is { } peakUntil && peakUntil <= DateTimeOffset.UtcNow)
        {
            saved.PeakPowerSaverUntil = null;
            saved.PeakPowerSaverStatus = "Alectra Peak Power Saver is watching usage sensors.";
        }
        saved.FrontDoorKillSwitchStatus = string.IsNullOrWhiteSpace(saved.FrontDoorKillSwitchStatus)
            ? "Front-door guard post is armed."
            : saved.FrontDoorKillSwitchStatus;
        saved.FrontDoorKillSwitchLastDetector = string.IsNullOrWhiteSpace(saved.FrontDoorKillSwitchLastDetector)
            ? "--"
            : saved.FrontDoorKillSwitchLastDetector;
        if (!saved.Settings.FrontDoorKillSwitchEnabled)
        {
            saved.FrontDoorKillSwitchUntil = null;
            saved.FrontDoorKillSwitchStatus = "Front-door guard post is off. The little sentry went for juice.";
        }
        else if (saved.FrontDoorKillSwitchUntil is { } frontDoorUntil && frontDoorUntil <= DateTimeOffset.UtcNow)
        {
            saved.FrontDoorKillSwitchUntil = null;
            saved.FrontDoorKillSwitchStatus = "Front-door guard post is armed and clear.";
        }
        saved.SuperDefenderStatus = string.IsNullOrWhiteSpace(saved.SuperDefenderStatus)
            ? "Super Defender is watching for repeated phone or Home Assistant changes."
            : saved.SuperDefenderStatus;
        saved.LastChangeSource = string.IsNullOrWhiteSpace(saved.LastChangeSource)
            ? "none"
            : saved.LastChangeSource;
        saved.LastChangeSourceDetail = string.IsNullOrWhiteSpace(saved.LastChangeSourceDetail)
            ? "No external thermostat change has been logged yet."
            : saved.LastChangeSourceDetail;
        if (!saved.Settings.SuperDefenderModeEnabled)
        {
            saved.SuperDefenderUntil = null;
            saved.SuperDefenderStatus = "Super Defender is off.";
        }
        else if (saved.SuperDefenderUntil is { } superUntil && superUntil <= DateTimeOffset.UtcNow)
        {
            saved.SuperDefenderUntil = null;
            saved.SuperDefenderStatus = "Super Defender is watching for repeated phone or Home Assistant changes.";
        }
        saved.EmergencyProtocol = string.IsNullOrWhiteSpace(saved.EmergencyProtocol)
            ? "None"
            : saved.EmergencyProtocol;
        saved.EmergencyStatus = string.IsNullOrWhiteSpace(saved.EmergencyStatus)
            ? "No emergency quiet mode active."
            : saved.EmergencyStatus;
        if (saved.EmergencyQuietUntil is { } emergencyUntil && emergencyUntil <= DateTimeOffset.UtcNow)
        {
            saved.EmergencyQuietUntil = null;
            saved.EmergencyProtocol = "None";
            saved.EmergencyStatus = "Emergency quiet ended; normal defender rules can resume.";
        }
        saved.CoolingFailureStatus = string.IsNullOrWhiteSpace(saved.CoolingFailureStatus)
            ? "Cooling failure watch is ready."
            : saved.CoolingFailureStatus;
        saved.WebsiteCommandDebounceStatus = string.IsNullOrWhiteSpace(saved.WebsiteCommandDebounceStatus)
            ? "Website controls are ready."
            : saved.WebsiteCommandDebounceStatus;
        if (saved.WebsiteCommandDebounceUntil is { } websiteCommandUntil && websiteCommandUntil <= DateTimeOffset.UtcNow)
        {
            saved.WebsiteCommandDebounceUntil = null;
            saved.WebsiteCommandDebounceStatus = "Website controls are ready.";
        }
        saved.ComfortStatus = string.IsNullOrWhiteSpace(saved.ComfortStatus)
            ? "Waiting for upstairs comfort readings."
            : saved.ComfortStatus;
        saved.Events.RemoveAll(item => item.Message.Contains("dummy", StringComparison.OrdinalIgnoreCase)
            || item.Message.Contains("simulator", StringComparison.OrdinalIgnoreCase));
    }

    private DefenderSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        PruneTouchTimes(now);
        var cooldownSeconds = state.CooldownUntil is { } cooldownUntil && cooldownUntil > now
            ? (int)Math.Ceiling((cooldownUntil - now).TotalSeconds)
            : 0;
        var websiteCommandDebounceSeconds = state.WebsiteCommandDebounceUntil is { } websiteCommandUntil && websiteCommandUntil > now
            ? (int)Math.Ceiling((websiteCommandUntil - now).TotalSeconds)
            : 0;
        if (websiteCommandDebounceSeconds == 0 && state.WebsiteCommandDebounceUntil is not null)
        {
            state.WebsiteCommandDebounceUntil = null;
            state.WebsiteCommandDebounceStatus = "Website controls are ready.";
        }
        var emergencySeconds = state.EmergencyQuietUntil is { } emergencyUntil && emergencyUntil > now
            ? (int)Math.Ceiling((emergencyUntil - now).TotalSeconds)
            : 0;
        if (emergencySeconds == 0 && state.EmergencyQuietUntil is not null)
        {
            ClearEmergencyQuiet("Emergency quiet ended; normal defender rules can resume.");
        }
        var frontDoorSeconds = state.FrontDoorKillSwitchUntil is { } frontDoorUntil && frontDoorUntil > now
            ? (int)Math.Ceiling((frontDoorUntil - now).TotalSeconds)
            : 0;
        if (frontDoorSeconds == 0 && state.FrontDoorKillSwitchUntil is not null)
        {
            ClearFrontDoorKillSwitch("Front-door guard post is armed and clear.");
        }
        var frontDoorPersonDetected = state.FrontDoorPersonReadings.Any(item => item.PersonDetected);
        var holdSeconds = state.NaturalHoldUntil is { } holdUntil && holdUntil > now
            ? (int)Math.Ceiling((holdUntil - now).TotalSeconds)
            : 0;
        var naturalSeconds = Math.Max(cooldownSeconds, holdSeconds);
        var coolModeRestoreSeconds = state.CoolModeRestoreDueAt is { } coolModeDueAt && coolModeDueAt > now
            ? (int)Math.Ceiling((coolModeDueAt - now).TotalSeconds)
            : 0;
        var comfortCompromiseSeconds = state.ComfortCompromiseUntil is { } compromiseUntil && compromiseUntil > now
            ? (int)Math.Ceiling((compromiseUntil - now).TotalSeconds)
            : 0;
        var conflictQuietSeconds = state.ConflictQuietUntil is { } conflictUntil && conflictUntil > now
            ? (int)Math.Ceiling((conflictUntil - now).TotalSeconds)
            : 0;
        var wallSettlingSeconds = state.WallSettlingUntil is { } settlingUntil && settlingUntil > now
            ? (int)Math.Ceiling((settlingUntil - now).TotalSeconds)
            : 0;
        if (wallSettlingSeconds == 0 && state.WallSettlingUntil is not null)
        {
            ClearWallSettling("Wall thermostat settled; the helper can continue.");
        }
        var manualGraceSeconds = state.ManualComfortGraceUntil is { } graceUntil && graceUntil > now
            ? (int)Math.Ceiling((graceUntil - now).TotalSeconds)
            : 0;
        var coolerIntentSeconds = state.CoolerIntentUntil is { } coolerIntentUntil && coolerIntentUntil > now
            ? (int)Math.Ceiling((coolerIntentUntil - now).TotalSeconds)
            : 0;
        var roomTrendSeconds = state.RoomTrendHoldUntil is { } trendUntil && trendUntil > now
            ? (int)Math.Ceiling((trendUntil - now).TotalSeconds)
            : 0;
        var thermalMomentumSeconds = state.ThermalMomentumHoldUntil is { } momentumUntil && momentumUntil > now
            ? (int)Math.Ceiling((momentumUntil - now).TotalSeconds)
            : 0;
        var weatherDriftSeconds = state.WeatherDriftHoldUntil is { } weatherDriftUntil && weatherDriftUntil > now
            ? (int)Math.Ceiling((weatherDriftUntil - now).TotalSeconds)
            : 0;
        var peakPowerSaverSeconds = state.PeakPowerSaverUntil is { } peakUntil && peakUntil > now
            ? (int)Math.Ceiling((peakUntil - now).TotalSeconds)
            : 0;
        if (peakPowerSaverSeconds == 0 && state.PeakPowerSaverUntil is not null)
        {
            ClearPeakPowerSaver("Alectra Peak Power Saver is watching usage sensors.");
        }
        var peakPowerReasons = BuildPeakPowerReasons(state.AlectraPeakPower);
        var peakPowerActive = state.Settings.PeakPowerSaverEnabled
            && peakPowerSaverSeconds > 0
            && peakPowerReasons.Count > 0;
        PruneRemoteChangeTimes(now);
        var superDefenderSeconds = state.SuperDefenderUntil is { } superUntil && superUntil > now
            ? (int)Math.Ceiling((superUntil - now).TotalSeconds)
            : 0;
        var superDefenderBypassing = state.Settings.SuperDefenderModeEnabled
            && state.Settings.SuperDefenderBypassQuietTiming
            && superDefenderSeconds > 0
            && state.HomeAssistantThermostat?.CurrentTemperatureCelsius > state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius;
        var coolingFailureAlerting = state.CoolingFailureSuspectedAt is not null
            && state.CoolingFailureStatus.Contains("MEGA ALERT", StringComparison.OrdinalIgnoreCase);
        var coolingFailureSeconds = coolingFailureAlerting && state.CoolingFailureSuspectedAt is { } failureSince
            ? (int)Math.Ceiling((now - failureSince).TotalSeconds)
            : 0;
        var omegaAlerting = coolingFailureAlerting && state.OmegaConfirmedAt is not null;
        var omegaSeconds = omegaAlerting && state.OmegaConfirmedAt is { } omegaSince
            ? (int)Math.Ceiling((now - omegaSince).TotalSeconds)
            : 0;
        var sensorRhythmSeconds = state.SensorRhythmDueAt is { } sensorRhythmDueAt && sensorRhythmDueAt > now
            ? (int)Math.Ceiling((sensorRhythmDueAt - now).TotalSeconds)
            : 0;
        var hvacActionAlibiSeconds = state.HvacActionAlibiUntil is { } alibiUntil && alibiUntil > now
            ? (int)Math.Ceiling((alibiUntil - now).TotalSeconds)
            : 0;
        if (hvacActionAlibiSeconds == 0 && state.HvacActionAlibiUntil is not null)
        {
            ClearHvacActionAlibi("HVAC alibi max wait ended; watching for the next real action transition.");
        }
        var coolingRunwaySeconds = state.CoolingRunwayHoldUntil is { } runwayUntil && runwayUntil > now
            ? (int)Math.Ceiling((runwayUntil - now).TotalSeconds)
            : 0;
        var setpointEchoUntil = state.PendingCommandAt?.AddSeconds(Math.Max(5, state.Settings.SetpointEchoGraceSeconds));
        var setpointEchoSeconds = setpointEchoUntil is { } echoUntil
            && echoUntil > now
            && state.PendingCommandSetPointCelsius is not null
            ? (int)Math.Ceiling((echoUntil - now).TotalSeconds)
            : 0;
        var repeatCommandSeconds = state.RepeatCommandHoldUntil is { } repeatUntil && repeatUntil > now
            ? (int)Math.Ceiling((repeatUntil - now).TotalSeconds)
            : 0;
        var routineTimingSeconds = state.RoutineTimingDueAt is { } routineDueAt && routineDueAt > now
            ? (int)Math.Ceiling((routineDueAt - now).TotalSeconds)
            : 0;
        PruneDefenderCommandTimes(now);
        var comfortBudgetSeconds = state.ComfortBudgetHoldUntil is { } budgetUntil && budgetUntil > now
            ? (int)Math.Ceiling((budgetUntil - now).TotalSeconds)
            : 0;
        var commandCamouflageSeconds = state.CommandCamouflageHoldUntil is { } camouflageUntil && camouflageUntil > now
            ? (int)Math.Ceiling((camouflageUntil - now).TotalSeconds)
            : 0;
        if (commandCamouflageSeconds == 0 && state.CommandCamouflageHoldUntil is not null)
        {
            ClearCommandCamouflage("Command camouflage slot arrived; watching for the next helper-command gap.");
        }
        var stealthGovernorSeconds = state.StealthGovernorHoldUntil is { } stealthUntil && stealthUntil > now
            ? (int)Math.Ceiling((stealthUntil - now).TotalSeconds)
            : 0;
        if (stealthGovernorSeconds == 0 && state.StealthGovernorHoldUntil is not null)
        {
            ClearStealthGovernor("Stealth governor low-profile window ended; watching overall pressure.");
        }
        var naturalCadenceSeconds = state.NaturalCadenceDueAt is { } cadenceDueAt && cadenceDueAt > now
            ? (int)Math.Ceiling((cadenceDueAt - now).TotalSeconds)
            : 0;
        var naturalChangePlannerSeconds = state.NaturalChangePlannerDueAt is { } naturalChangeDueAt && naturalChangeDueAt > now
            ? (int)Math.Ceiling((naturalChangeDueAt - now).TotalSeconds)
            : 0;
        if (naturalChangePlannerSeconds == 0 && state.NaturalChangePlannerDueAt is not null)
        {
            ClearNaturalChangePlanner("Comfort Pace slot arrived; watching for the next calm opening.");
        }
        var comfortEnvelopeSeconds = state.ComfortEnvelopeUntil is { } envelopeUntil && envelopeUntil > now
            ? (int)Math.Ceiling((envelopeUntil - now).TotalSeconds)
            : 0;
        if (comfortEnvelopeSeconds == 0 && state.ComfortEnvelopeUntil is not null)
        {
            ClearComfortEnvelope("Comfort envelope ended; watching for the next safe wall preference.");
        }
        PruneVisibilityNoticeTimes(now);
        var visibilityGuardSeconds = state.VisibilityGuardUntil is { } visibilityUntil && visibilityUntil > now
            ? (int)Math.Ceiling((visibilityUntil - now).TotalSeconds)
            : 0;
        var visibilityPressure = CalculateVisibilityPressure(now);
        var naturalPlan = BuildNaturalRecoveryPlan(now);
        var naturalWalkbackScore = CalculateTouchSuspicionScore(now);
        var commandCamouflagePressure = CalculateCommandCamouflagePressure(now);
        var stealthGovernorScore = CalculateStealthGovernorScore(now);
        var repeatCommandPressure = CalculateRepeatCommandPressure(now);
        var coolingRunwayPressure = CalculateCoolingRunwayPressure(now);
        var naturalWalkbackStep = Math.Min(
            naturalPlan.StepCelsius,
            Math.Round(Math.Max(0.1, state.Settings.NaturalWalkbackStepCelsius), 1));
        var currentReading = state.HomeAssistantThermostat is { } currentThermostat
            ? new ThermostatReading(
                state.HomeAssistantEntityId ?? string.Empty,
                currentThermostat.CurrentTemperatureCelsius,
                currentThermostat.SetPointCelsius,
                currentThermostat.HvacMode,
                currentThermostat.HvacAction,
                currentThermostat.FanMode,
                currentThermostat.AvailableFanModes,
                currentThermostat.ToContext())
            : null;
        double? comfortEnvelopeMinimum = null;
        double? comfortEnvelopeMaximum = null;
        if (state.Settings.ComfortEnvelopeEnabled
            && state.ComfortEnvelopePreferredSetPointCelsius is not null
            && currentReading is not null)
        {
            if (state.ComfortEnvelopeMinimumAllowedSetPointCelsius is { } minimum
                && state.ComfortEnvelopeMaximumAllowedSetPointCelsius is { } maximum)
            {
                comfortEnvelopeMinimum = minimum;
                comfortEnvelopeMaximum = maximum;
            }
            else
            {
                var envelopeTarget = state.ComfortCompromiseEffectiveTargetCelsius
                    ?? state.ComfortMemoryEffectiveTargetCelsius
                    ?? state.TargetTemperatureCelsius;
                var maxOffset = Math.Max(0.1, state.Settings.ComfortEnvelopeMaxOffsetCelsius);
                comfortEnvelopeMinimum = Math.Round(envelopeTarget - maxOffset, 1);
                comfortEnvelopeMaximum = Math.Round(envelopeTarget + maxOffset, 1);
            }
        }
        var naturalWalkbackActive = state.Settings.NaturalWalkbackEnabled
            && state.ExternalTouchTimes.Count >= Math.Max(1, state.Settings.NaturalWalkbackTriggerTouches)
            && currentReading is { } walkbackReading
            && walkbackReading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + state.Settings.NaturalWalkbackSafetyBandCelsius
            && !ShouldBypassNaturalRecovery(walkbackReading);
        var touchSignature = BuildTouchSignatureAnalysis(currentReading, naturalWalkbackStep, now);
        var roomTrend = BuildRoomTrend(now);
        var thermalMomentum = BuildThermalMomentum(now, state.HomeAssistantThermostat?.CurrentTemperatureCelsius);
        var weatherDrift = BuildWeatherDrift(now);
        var touchIntent = BuildTouchIntentAnalysis(now);
        var coolerIntent = BuildCoolerIntentAnalysis(now);
        var wallSettlingTouchCount = GetRecentWallSettlingTouches(now).Count;
        var wallSettlingSettleSeconds = CalculateWallSettlingSeconds(wallSettlingTouchCount);
        var sensorRhythm = BuildSensorRhythmAnalysis(now);
        PruneComfortMemory(now);
        var memorySlot = FindComfortMemorySlot(now);
        var memoryActive = state.Settings.ComfortMemoryEnabled
            && state.ComfortMemoryEffectiveTargetCelsius is not null
            && memorySlot is not null;
        return new DefenderSnapshot(
            state.TargetTemperatureCelsius,
            state.DefenderEnabled,
            state.BoostOffsetCelsius,
            state.ConnectionState,
            state.HomeAssistantThermostat?.ToSnapshot(),
            state.Weather?.ToSnapshot(),
            state.HomeAssistantEntityId,
            state.Weather?.EntityId,
            !string.IsNullOrWhiteSpace(state.HomeAssistantEntityId),
            state.LastCommand,
            state.LastError,
            state.NextAction,
            state.NextActionAt,
            cooldownSeconds,
            new WebsiteCommandDebounceSnapshot(
                websiteCommandDebounceSeconds > 0,
                websiteCommandDebounceSeconds,
                WebsiteCommandDebounceSeconds,
                string.IsNullOrWhiteSpace(state.WebsiteCommandDebounceStatus)
                    ? "Website controls are ready."
                    : state.WebsiteCommandDebounceStatus,
                state.LastWebsiteCommandName,
                websiteCommandDebounceSeconds > 0 ? state.WebsiteCommandDebounceUntil : null),
            new EmergencySnapshot(
                emergencySeconds > 0,
                emergencySeconds,
                string.IsNullOrWhiteSpace(state.EmergencyProtocol) ? "None" : state.EmergencyProtocol,
                string.IsNullOrWhiteSpace(state.EmergencyStatus)
                    ? "No emergency quiet mode active."
                    : state.EmergencyStatus,
                emergencySeconds > 0 ? state.EmergencyQuietUntil : null),
            new FrontDoorKillSwitchSnapshot(
                state.Settings.FrontDoorKillSwitchEnabled,
                frontDoorSeconds > 0,
                frontDoorPersonDetected,
                state.FrontDoorThermostatOffCommandedAt is { } offAt && now - offAt <= TimeSpan.FromSeconds(90),
                frontDoorSeconds,
                state.FrontDoorPersonReadings.Count,
                string.IsNullOrWhiteSpace(state.Settings.FrontDoorPersonEntityIds)
                    ? "auto-discover"
                    : state.Settings.FrontDoorPersonEntityIds,
                string.IsNullOrWhiteSpace(state.FrontDoorKillSwitchLastDetector)
                    ? "--"
                    : state.FrontDoorKillSwitchLastDetector,
                string.IsNullOrWhiteSpace(state.FrontDoorKillSwitchStatus)
                    ? "Front-door guard post is armed."
                    : state.FrontDoorKillSwitchStatus,
                frontDoorSeconds > 0 ? state.FrontDoorKillSwitchUntil : null,
                state.FrontDoorKillSwitchUpdatedAt,
                state.FrontDoorPersonReadings.ToArray()),
            new CoolModeRestoreSnapshot(
                state.Settings.CoolModeRestoreDelayEnabled,
                coolModeRestoreSeconds > 0,
                coolModeRestoreSeconds,
                string.IsNullOrWhiteSpace(state.CoolModeRestoreStatus)
                    ? "Cool mode restore is watching."
                    : state.CoolModeRestoreStatus,
                coolModeRestoreSeconds > 0 ? state.CoolModeRestoreDueAt : null),
            new NaturalRecoverySnapshot(
                state.Settings.NaturalRecoveryEnabled,
                naturalSeconds > 0,
                naturalSeconds,
                state.ExternalTouchTimes.Count,
                string.IsNullOrWhiteSpace(state.NaturalRecoveryStatus)
                    ? "Comfort sync is ready."
                    : state.NaturalRecoveryStatus,
                naturalPlan.QuietLevel,
                state.Settings.NaturalStepCelsius,
                naturalPlan.StepCelsius,
                state.Settings.NaturalHoldChancePercent,
                naturalPlan.HoldChancePercent,
                naturalPlan.CommandGapSeconds),
            new NaturalWalkbackSnapshot(
                state.Settings.NaturalWalkbackEnabled,
                naturalWalkbackActive,
                naturalWalkbackScore,
                naturalWalkbackStep,
                string.IsNullOrWhiteSpace(state.NaturalWalkbackStatus)
                    ? "Natural walkback is watching."
                    : state.NaturalWalkbackStatus),
            new TouchSignatureSnapshot(
                state.Settings.TouchSignatureEnabled,
                touchSignature.Active,
                touchSignature.SampleCount,
                touchSignature.LearnedStepCelsius,
                touchSignature.EffectiveStepCelsius,
                touchSignature.Status),
            new HumanNudgeSnapshot(
                state.Settings.HumanNudgeEnabled,
                state.HumanNudgeActive,
                state.HumanNudgeLastSetPointCelsius,
                Math.Round(Math.Clamp(state.Settings.HumanNudgeStepCelsius, 0.1, 2.0), 1),
                state.ExternalTouchTimes.Count,
                string.IsNullOrWhiteSpace(state.HumanNudgeStatus)
                    ? "Human nudge is watching for a safe command to shape."
                    : state.HumanNudgeStatus),
            new VisibilityGuardSnapshot(
                state.Settings.VisibilityGuardEnabled,
                visibilityGuardSeconds > 0,
                visibilityGuardSeconds,
                state.VisibilityNoticeTimes.Count,
                visibilityPressure,
                string.IsNullOrWhiteSpace(state.VisibilityGuardStatus)
                    ? "Visibility guard is watching."
                    : state.VisibilityGuardStatus,
                visibilityGuardSeconds > 0 ? state.VisibilityGuardUntil : null),
            new RoutineTimingSnapshot(
                state.Settings.RoutineTimingEnabled,
                routineTimingSeconds > 0,
                routineTimingSeconds,
                state.Settings.RoutineTimingIntervalMinutes,
                state.Settings.RoutineTimingJitterMinutes,
                string.IsNullOrWhiteSpace(state.RoutineTimingStatus)
                    ? "Routine timing is watching."
                    : state.RoutineTimingStatus,
                routineTimingSeconds > 0 ? state.RoutineTimingDueAt : null),
            new ComfortBudgetSnapshot(
                state.Settings.ComfortBudgetEnabled,
                comfortBudgetSeconds > 0,
                comfortBudgetSeconds,
                state.DefenderCommandTimes.Count,
                state.Settings.ComfortBudgetMaxCommands,
                string.IsNullOrWhiteSpace(state.ComfortBudgetStatus)
                    ? "Comfort budget is watching."
                    : state.ComfortBudgetStatus,
                comfortBudgetSeconds > 0 ? state.ComfortBudgetHoldUntil : null),
            new CommandCamouflageSnapshot(
                state.Settings.CommandCamouflageEnabled,
                commandCamouflageSeconds > 0,
                commandCamouflageSeconds,
                commandCamouflagePressure,
                state.DefenderCommandTimes.Count,
                string.IsNullOrWhiteSpace(state.CommandCamouflageStatus)
                    ? "Command camouflage is watching for a recent helper command."
                    : state.CommandCamouflageStatus,
                commandCamouflageSeconds > 0 ? state.CommandCamouflageHoldUntil : null),
            new StealthGovernorSnapshot(
                state.Settings.StealthGovernorEnabled,
                stealthGovernorSeconds > 0,
                stealthGovernorSeconds,
                stealthGovernorScore,
                state.Settings.StealthGovernorTriggerScore,
                state.ExternalTouchTimes.Count,
                state.DefenderCommandTimes.Count,
                string.IsNullOrWhiteSpace(state.StealthGovernorStatus)
                    ? "Stealth governor is watching overall pressure."
                    : state.StealthGovernorStatus,
                stealthGovernorSeconds > 0 ? state.StealthGovernorHoldUntil : null),
            new NaturalCadenceSnapshot(
                state.Settings.NaturalCadenceEnabled,
                naturalCadenceSeconds > 0,
                naturalCadenceSeconds,
                naturalWalkbackScore,
                state.DefenderCommandTimes.Count,
                string.IsNullOrWhiteSpace(state.NaturalCadenceStatus)
                    ? "Natural cadence is watching."
                    : state.NaturalCadenceStatus,
                naturalCadenceSeconds > 0 ? state.NaturalCadenceDueAt : null),
            new NaturalChangePlannerSnapshot(
                state.Settings.NaturalChangePlannerEnabled,
                naturalChangePlannerSeconds > 0,
                naturalChangePlannerSeconds,
                naturalWalkbackScore,
                state.ExternalTouchTimes.Count,
                state.DefenderCommandTimes.Count,
                string.IsNullOrWhiteSpace(state.NaturalChangePlannerReason)
                    ? "watching"
                    : state.NaturalChangePlannerReason,
                string.IsNullOrWhiteSpace(state.NaturalChangePlannerStatus)
                    ? "Comfort Pace is watching."
                    : state.NaturalChangePlannerStatus,
                naturalChangePlannerSeconds > 0 ? state.NaturalChangePlannerDueAt : null),
            new ComfortEnvelopeSnapshot(
                state.Settings.ComfortEnvelopeEnabled,
                comfortEnvelopeSeconds > 0,
                comfortEnvelopeSeconds,
                state.ExternalTouchTimes.Count,
                state.ComfortEnvelopePreferredSetPointCelsius,
                comfortEnvelopeMinimum,
                comfortEnvelopeMaximum,
                string.IsNullOrWhiteSpace(state.ComfortEnvelopeStatus)
                    ? "Comfort envelope is watching."
                    : state.ComfortEnvelopeStatus,
                comfortEnvelopeSeconds > 0 ? state.ComfortEnvelopeUntil : null),
            new ComfortCompromiseSnapshot(
                state.Settings.ComfortCompromiseEnabled,
                comfortCompromiseSeconds > 0,
                comfortCompromiseSeconds,
                state.ComfortCompromisePreferredSetPointCelsius,
                state.ComfortCompromiseEffectiveTargetCelsius,
                string.IsNullOrWhiteSpace(state.ComfortCompromiseStatus)
                    ? "Comfort compromise is watching for repeated wall changes."
                    : state.ComfortCompromiseStatus,
                comfortCompromiseSeconds > 0 ? state.ComfortCompromiseUntil : null),
            new ComfortMemorySnapshot(
                state.Settings.ComfortMemoryEnabled,
                memoryActive,
                memorySlot?.Samples ?? 0,
                memorySlot?.OffsetCelsius,
                state.ComfortMemoryEffectiveTargetCelsius,
                string.IsNullOrWhiteSpace(state.ComfortMemoryStatus)
                    ? "Comfort memory is watching wall choices."
                    : state.ComfortMemoryStatus),
            new ConflictQuietSnapshot(
                state.Settings.ConflictQuietModeEnabled,
                conflictQuietSeconds > 0,
                conflictQuietSeconds,
                state.Settings.ConflictQuietTouchThreshold,
                state.Settings.ConflictQuietComfortBandCelsius,
                string.IsNullOrWhiteSpace(state.ConflictQuietStatus)
                    ? "Conflict quiet is watching."
                    : state.ConflictQuietStatus,
                conflictQuietSeconds > 0 ? state.ConflictQuietUntil : null),
            new WallSettlingSnapshot(
                state.Settings.WallSettlingGuardEnabled,
                wallSettlingSeconds > 0,
                wallSettlingSeconds,
                wallSettlingTouchCount,
                wallSettlingSettleSeconds,
                string.IsNullOrWhiteSpace(state.WallSettlingStatus)
                    ? "Wall settling is watching."
                    : state.WallSettlingStatus,
                wallSettlingSeconds > 0 ? state.WallSettlingUntil : null),
            new ManualComfortGraceSnapshot(
                state.Settings.ManualComfortGraceEnabled,
                manualGraceSeconds > 0,
                manualGraceSeconds,
                string.IsNullOrWhiteSpace(state.ManualComfortGraceStatus)
                    ? "No wall-change grace active."
                    : state.ManualComfortGraceStatus,
                state.Settings.ManualComfortGraceBandCelsius,
                manualGraceSeconds > 0 ? state.ManualComfortGraceUntil : null),
            new TouchIntentSnapshot(
                touchIntent.Enabled,
                touchIntent.Active,
                touchIntent.RecentTouchCount,
                touchIntent.Direction,
                touchIntent.NetChangeCelsius,
                touchIntent.ExtraGraceMinutes,
                string.IsNullOrWhiteSpace(state.TouchIntentStatus)
                    ? touchIntent.Status
                    : state.TouchIntentStatus),
            new CoolerIntentSnapshot(
                state.Settings.CoolerIntentFastLaneEnabled,
                state.Settings.CoolerIntentFastLaneEnabled && coolerIntentSeconds > 0,
                coolerIntentSeconds,
                coolerIntent.RecentTouchCount,
                coolerIntent.NetChangeCelsius,
                string.IsNullOrWhiteSpace(state.CoolerIntentStatus)
                    ? coolerIntent.Status
                    : state.CoolerIntentStatus,
                coolerIntentSeconds > 0 ? state.CoolerIntentUntil : null),
            new SetpointEchoSnapshot(
                state.Settings.SetpointEchoGuardEnabled,
                setpointEchoSeconds > 0,
                setpointEchoSeconds,
                state.PendingCommandSetPointCelsius,
                string.IsNullOrWhiteSpace(state.SetpointEchoStatus)
                    ? "Setpoint echo is watching."
                    : state.SetpointEchoStatus,
                setpointEchoSeconds > 0 ? setpointEchoUntil : null),
            new RepeatCommandSnapshot(
                state.Settings.RepeatCommandGuardEnabled,
                repeatCommandSeconds > 0,
                repeatCommandSeconds,
                repeatCommandPressure,
                state.LastDefenderCommandSetPointCelsius,
                string.IsNullOrWhiteSpace(state.RepeatCommandStatus)
                    ? "Repeat quiet is watching for identical follow-up commands."
                    : state.RepeatCommandStatus,
                repeatCommandSeconds > 0 ? state.RepeatCommandHoldUntil : null),
            new SensorRhythmSnapshot(
                state.Settings.SensorRhythmGuardEnabled,
                sensorRhythmSeconds > 0,
                sensorRhythmSeconds,
                sensorRhythm.SampleCount,
                sensorRhythm.MedianIntervalSeconds,
                string.IsNullOrWhiteSpace(state.SensorRhythmStatus)
                    ? "Sensor rhythm is watching."
                    : state.SensorRhythmStatus,
                sensorRhythmSeconds > 0 ? state.SensorRhythmDueAt : null),
            new HvacActionAlibiSnapshot(
                state.Settings.HvacActionAlibiEnabled,
                hvacActionAlibiSeconds > 0,
                hvacActionAlibiSeconds,
                state.ExternalTouchTimes.Count,
                string.IsNullOrWhiteSpace(state.HvacActionAlibiCurrentAction)
                    ? "unknown"
                    : state.HvacActionAlibiCurrentAction,
                state.HvacActionAlibiLastTransitionAt,
                string.IsNullOrWhiteSpace(state.HvacActionAlibiStatus)
                    ? "HVAC alibi is watching for a real action transition."
                    : state.HvacActionAlibiStatus,
                hvacActionAlibiSeconds > 0 ? state.HvacActionAlibiUntil : null),
            new CoolingRunwaySnapshot(
                state.Settings.CoolingRunwayGuardEnabled,
                coolingRunwaySeconds > 0,
                coolingRunwaySeconds,
                coolingRunwayPressure,
                state.CoolingRunwayStartedAt,
                string.IsNullOrWhiteSpace(state.CoolingRunwayStatus)
                    ? "Cooling runway is watching for a fresh cooling start."
                    : state.CoolingRunwayStatus,
                coolingRunwaySeconds > 0 ? state.CoolingRunwayHoldUntil : null),
            new RoomTrendSnapshot(
                state.Settings.RoomTrendGuardEnabled,
                roomTrendSeconds > 0,
                roomTrendSeconds,
                roomTrend.Direction,
                roomTrend.DeltaCelsius,
                string.IsNullOrWhiteSpace(state.RoomTrendStatus)
                    ? "Room trend guard is watching."
                    : state.RoomTrendStatus,
                roomTrend.SampleCount),
            new ThermalMomentumSnapshot(
                state.Settings.ThermalMomentumGuardEnabled,
                thermalMomentumSeconds > 0,
                thermalMomentumSeconds,
                thermalMomentum.CoolingRateCelsiusPerHour,
                thermalMomentum.EstimatedMinutesToTarget,
                string.IsNullOrWhiteSpace(state.ThermalMomentumStatus)
                    ? "Thermal momentum guard is watching."
                    : state.ThermalMomentumStatus),
            new WeatherDriftSnapshot(
                state.Settings.WeatherDriftGuardEnabled,
                weatherDriftSeconds > 0,
                weatherDriftSeconds,
                weatherDrift.Direction,
                weatherDrift.OutdoorDeltaCelsius,
                weatherDrift.SampleCount,
                string.IsNullOrWhiteSpace(state.WeatherDriftStatus)
                    ? "Weather drift guard is watching."
                    : state.WeatherDriftStatus,
                weatherDriftSeconds > 0 ? state.WeatherDriftHoldUntil : null),
            new PeakPowerSaverSnapshot(
                state.Settings.PeakPowerSaverEnabled,
                peakPowerActive,
                peakPowerActive && !string.IsNullOrWhiteSpace(state.PeakPowerSaverStatus) && state.PeakPowerSaverStatus.Contains("holding", StringComparison.OrdinalIgnoreCase),
                state.Settings.PeakPowerSaverFanSaverEnabled,
                peakPowerSaverSeconds,
                state.AlectraPeakPower?.CurrentPowerKilowatts,
                state.Settings.PeakPowerSaverPowerThresholdKilowatts,
                state.AlectraPeakPower?.CurrentPriceCentsPerKwh,
                state.Settings.PeakPowerSaverPriceThresholdCentsPerKwh,
                string.IsNullOrWhiteSpace(state.AlectraPeakPower?.TouPeriod) ? "--" : state.AlectraPeakPower!.TouPeriod!,
                string.IsNullOrWhiteSpace(state.AlectraPeakPower?.CurrentPlan) ? "--" : state.AlectraPeakPower!.CurrentPlan!,
                string.IsNullOrWhiteSpace(state.PeakPowerSaverStatus)
                    ? "Alectra Peak Power Saver is watching usage sensors."
                    : state.PeakPowerSaverStatus,
                peakPowerSaverSeconds > 0 ? state.PeakPowerSaverUntil : null,
                state.AlectraPeakPower?.UpdatedAt),
            new SuperDefenderSnapshot(
                state.Settings.SuperDefenderModeEnabled,
                superDefenderSeconds > 0,
                superDefenderBypassing,
                superDefenderSeconds,
                state.RemoteChangeTimes.Count,
                string.IsNullOrWhiteSpace(state.LastChangeSource) ? "none" : state.LastChangeSource,
                string.IsNullOrWhiteSpace(state.LastChangeSourceDetail)
                    ? "No external thermostat change has been logged yet."
                    : state.LastChangeSourceDetail,
                string.IsNullOrWhiteSpace(state.SuperDefenderStatus)
                    ? "Super Defender is watching for repeated phone or Home Assistant changes."
                    : state.SuperDefenderStatus,
                "Automatic Wi-Fi blocking is intentionally not sent by AC Defender. Use router/MAC controls manually only if you accept the risk of cutting off thermostat recovery and monitoring.",
                superDefenderSeconds > 0 ? state.SuperDefenderUntil : null),
            new CoolingFailureSnapshot(
                true,
                coolingFailureAlerting,
                coolingFailureSeconds,
                state.CoolingFailureAlertCount,
                string.IsNullOrWhiteSpace(state.CoolingFailureStatus)
                    ? "Cooling failure watch is ready."
                    : state.CoolingFailureStatus,
                coolingFailureAlerting ? state.CoolingFailureSuspectedAt : null,
                coolingFailureAlerting ? state.CoolingFailureNextAlertAt : null,
                omegaAlerting,
                omegaSeconds,
                state.OmegaRoomRiseCelsius),
            new ComfortSnapshot(
                state.Settings.UpstairsComfortEnabled,
                state.Settings.HomePresenceRequired,
                state.IsHome,
                state.UpstairsTooHot,
                state.HottestUpstairsTemperatureCelsius,
                state.HottestUpstairsEntityId,
                state.ComfortStatus,
                state.UpstairsSensors.ToArray(),
                state.Presence.ToArray()),
            CloneSettings(state.Settings),
            state.Schedule.Select(CloneScheduleEntry).ToArray(),
            state.ThermostatChanges.ToArray(),
            state.UpdatedAt,
            state.Events.ToArray());
    }

    private void AddEvent(string level, string message)
    {
        if (state.Events.FirstOrDefault() is { } latest
            && latest.Level == level
            && latest.Message == message
            && DateTimeOffset.UtcNow - latest.Timestamp < TimeSpan.FromMinutes(2))
        {
            return;
        }

        state.Events.Insert(0, new DefenderEvent(DateTimeOffset.UtcNow, level, message));
        if (state.Events.Count > 40)
        {
            state.Events.RemoveRange(40, state.Events.Count - 40);
        }
    }

    private void ResetNaturalRecovery(string status)
    {
        state.CooldownUntil = null;
        state.NaturalHoldUntil = null;
        state.NaturalHoldCount = 0;
        state.NaturalRecoveryStatus = status;
        state.NaturalWalkbackStatus = "Natural walkback is watching.";
        state.TouchSignatureStatus = "Touch signature is watching.";
        ClearHumanNudge("Human nudge is watching for a safe command to shape.");
        ClearVisibilityGuard("Visibility guard is watching.");
        ClearRoutineTiming("Routine timing is watching.");
        ClearComfortBudget("Comfort budget is watching.");
        ClearCommandCamouflage("Command camouflage is watching for a recent helper command.");
        ClearStealthGovernor("Stealth governor is watching overall pressure.");
        ClearNaturalCadence("Natural cadence is watching.");
        ClearNaturalChangePlanner("Comfort Pace is watching.");
        ClearComfortEnvelope("Comfort envelope is watching.");
        ClearRepeatCommand("Repeat quiet is watching.");
        ClearCoolingRunway("Cooling runway is watching.");
        ClearSensorRhythm("Sensor rhythm is watching.");
        ClearHvacActionAlibi("HVAC alibi is watching for a real action transition.");
        ClearComfortCompromise("Comfort compromise reset after website target change.");
        state.ComfortMemoryEffectiveTargetCelsius = null;
        state.ComfortMemoryStatus = "Comfort memory is watching wall choices.";
        state.CoolModeRestoreDueAt = null;
        state.CoolModeRestoreCommandedAt = null;
        state.CoolModeRestoreStatus = "Cool mode restore is watching.";
        state.ConflictQuietUntil = null;
        state.ConflictQuietStatus = "Conflict quiet is watching.";
        ClearWallSettling("Wall settling is watching.");
        ClearManualComfortGrace();
        state.TouchIntentStatus = "Touch intent is watching.";
        ClearCoolerIntent("Cooler intent fast lane is watching for repeated cooler wall touches.");
        ClearPendingSetpointEcho("Setpoint echo is watching.");
        state.RoomTrendHoldUntil = null;
        state.RoomTrendStatus = "Room trend guard is watching.";
        state.ThermalMomentumHoldUntil = null;
        state.ThermalMomentumStatus = "Thermal momentum guard is watching.";
        ClearWeatherDrift("Weather drift guard is watching.");
    }

    private void ClearManualComfortGrace()
    {
        state.ManualComfortGraceUntil = null;
        state.ManualComfortGraceSetPointCelsius = null;
        state.ManualComfortGraceStatus = "No wall-change grace active.";
    }

    private void ClearCoolerIntent(string status)
    {
        state.CoolerIntentUntil = null;
        state.CoolerIntentStatus = status;
    }

    private void ClearWallSettling(string status)
    {
        state.WallSettlingUntil = null;
        state.WallSettlingStatus = status;
    }

    private void ClearRoutineTiming(string status)
    {
        state.RoutineTimingDueAt = null;
        state.RoutineTimingStatus = status;
    }

    private void ClearComfortBudget(string status)
    {
        state.ComfortBudgetHoldUntil = null;
        state.ComfortBudgetStatus = status;
    }

    private void ClearCommandCamouflage(string status)
    {
        state.CommandCamouflageHoldUntil = null;
        state.CommandCamouflageStatus = status;
    }

    private void ClearStealthGovernor(string status)
    {
        state.StealthGovernorHoldUntil = null;
        state.StealthGovernorStatus = status;
    }

    private void ClearHumanNudge(string status)
    {
        state.HumanNudgeActive = false;
        state.HumanNudgeLastSetPointCelsius = null;
        state.HumanNudgeStatus = status;
    }

    private void ClearNaturalCadence(string status)
    {
        state.NaturalCadenceDueAt = null;
        state.NaturalCadenceStatus = status;
    }

    private void ClearNaturalChangePlanner(string status)
    {
        state.NaturalChangePlannerDueAt = null;
        state.NaturalChangePlannerReason = "watching";
        state.NaturalChangePlannerStatus = status;
    }

    private void ClearComfortEnvelope(string status)
    {
        state.ComfortEnvelopeUntil = null;
        state.ComfortEnvelopePreferredSetPointCelsius = null;
        state.ComfortEnvelopeMinimumAllowedSetPointCelsius = null;
        state.ComfortEnvelopeMaximumAllowedSetPointCelsius = null;
        state.ComfortEnvelopeStatus = status;
    }

    private void ClearRepeatCommand(string status)
    {
        state.RepeatCommandHoldUntil = null;
        state.RepeatCommandStatus = status;
    }

    private void ClearCoolingRunway(string status)
    {
        state.CoolingRunwayHoldUntil = null;
        state.CoolingRunwayStatus = status;
    }

    private void ClearEmergencyQuiet(string status)
    {
        state.EmergencyQuietUntil = null;
        state.EmergencyProtocol = "None";
        state.EmergencyStatus = status;
    }

    private void ClearCoolingFailure(string status)
    {
        state.CoolingDemandStartedAt = null;
        state.CoolingFailureSuspectedAt = null;
        state.CoolingFailureNextAlertAt = null;
        state.CoolingFailureStatus = status;
        state.OmegaConfirmedAt = null;
        state.OmegaRoomRiseCelsius = null;
    }

    private void ClearVisibilityGuard(string status)
    {
        state.VisibilityGuardUntil = null;
        state.VisibilityGuardStatus = status;
    }

    private void ClearPendingSetpointEcho(string status)
    {
        state.PendingCommandSetPointCelsius = null;
        state.PendingCommandHvacMode = null;
        state.PendingCommandFanMode = null;
        state.PendingCommandAt = null;
        state.PendingCommandSourceKind = null;
        state.PendingCommandSourceLabel = null;
        state.PendingCommandSourceDetail = null;
        state.SetpointEchoStatus = status;
    }

    private void RecordPendingThermostatCommand(
        double? commandedSetPointCelsius,
        string? commandedHvacMode,
        string? commandedFanMode,
        string commandSourceKind,
        string commandSourceLabel,
        string commandSourceDetail)
    {
        var now = DateTimeOffset.UtcNow;
        state.PendingCommandSetPointCelsius = commandedSetPointCelsius is { } requestedSetPoint
            ? Math.Round(requestedSetPoint, 1)
            : null;
        state.PendingCommandHvacMode = string.IsNullOrWhiteSpace(commandedHvacMode)
            ? null
            : commandedHvacMode.Trim();
        state.PendingCommandFanMode = string.IsNullOrWhiteSpace(commandedFanMode)
            ? null
            : commandedFanMode.Trim();
        state.PendingCommandAt = now;
        state.PendingCommandSourceKind = string.IsNullOrWhiteSpace(commandSourceKind)
            ? "website-command"
            : commandSourceKind.Trim();
        state.PendingCommandSourceLabel = string.IsNullOrWhiteSpace(commandSourceLabel)
            ? "Website command"
            : commandSourceLabel.Trim();
        state.PendingCommandSourceDetail = string.IsNullOrWhiteSpace(commandSourceDetail)
            ? $"{state.PendingCommandSourceLabel} sent this command through AC Defender."
            : commandSourceDetail.Trim();

        if (commandedSetPointCelsius is { } setPoint)
        {
            state.SetpointEchoStatus = state.Settings.SetpointEchoGuardEnabled
                ? $"Setpoint echo is waiting for Home Assistant to report {setPoint:0.0} C from {state.PendingCommandSourceLabel}."
                : "Setpoint echo guard is off.";
            state.LastDefenderCommandAt = now;
            state.LastDefenderCommandSetPointCelsius = Math.Round(setPoint, 1);
            state.DefenderCommandTimes.Add(now);
            PruneDefenderCommandTimes(now);
            state.HumanNudgeStatus = state.HumanNudgeActive
                ? $"Human nudge sent a normal-looking {state.LastDefenderCommandSetPointCelsius:0.0} C step; watching for the next safe command."
                : "Human nudge is watching for a safe command to shape.";
            state.RoutineTimingDueAt = null;
            state.RoutineTimingStatus = "Routine timing used its comfort-check slot; watching for the next one.";
            state.ComfortBudgetStatus = $"Comfort budget counted {state.DefenderCommandTimes.Count}/{state.Settings.ComfortBudgetMaxCommands} recent comfort adjustments.";
            state.CommandCamouflageHoldUntil = null;
            state.CommandCamouflageStatus = "Command camouflage counted the latest helper command and is watching the next safe gap.";
            state.StealthGovernorHoldUntil = null;
            state.StealthGovernorStatus = "Stealth governor counted the latest helper command and is watching overall pressure.";
            state.NaturalCadenceDueAt = null;
            state.NaturalCadenceStatus = "Natural cadence used its quiet slot; watching for the next safe rhythm.";
            ClearNaturalChangePlanner("Comfort Pace used its climate slot; watching for the next calm opening.");
            ClearComfortEnvelope("Real comfort command sent; comfort envelope is watching again.");
            ClearRepeatCommand("Repeat quiet used its slot; watching for identical follow-up commands.");
            ClearHvacActionAlibi("HVAC alibi used its timing cue; watching for the next safe transition.");
        }
        else
        {
            var expected = string.Join(", ", new[]
            {
                state.PendingCommandHvacMode is null ? null : $"mode {state.PendingCommandHvacMode}",
                state.PendingCommandFanMode is null ? null : $"fan {state.PendingCommandFanMode}"
            }.Where(item => item is not null));
            state.SetpointEchoStatus = string.IsNullOrWhiteSpace(expected)
                ? "Thermostat command echo is waiting for Home Assistant."
                : $"Thermostat command echo is waiting for Home Assistant to report {expected} from {state.PendingCommandSourceLabel}.";
        }
    }

    private void ClearSensorRhythm(string status)
    {
        state.SensorRhythmDueAt = null;
        state.SensorRhythmStatus = status;
    }

    private void ClearHvacActionAlibi(string status)
    {
        state.HvacActionAlibiStartedAt = null;
        state.HvacActionAlibiUntil = null;
        state.HvacActionAlibiStatus = status;
    }

    private void ClearWeatherDrift(string status)
    {
        state.WeatherDriftHoldUntil = null;
        state.WeatherDriftStatus = status;
    }

    private void ClearPeakPowerSaver(string status)
    {
        state.PeakPowerSaverUntil = null;
        state.PeakPowerSaverStatus = status;
    }

    private void ClearFrontDoorKillSwitch(string status)
    {
        state.FrontDoorKillSwitchUntil = null;
        state.FrontDoorKillSwitchStatus = status;
    }

    private bool IsFrontDoorKillSwitchActive(DateTimeOffset now)
    {
        if (!state.Settings.FrontDoorKillSwitchEnabled)
        {
            state.FrontDoorKillSwitchUntil = null;
            return false;
        }

        if (state.FrontDoorKillSwitchUntil is not { } until || until <= now)
        {
            state.FrontDoorKillSwitchUntil = null;
            return false;
        }

        return true;
    }

    private bool IsPeakPowerSaverActive(DateTimeOffset now)
    {
        if (!state.Settings.PeakPowerSaverEnabled)
        {
            state.PeakPowerSaverUntil = null;
            return false;
        }

        if (state.PeakPowerSaverUntil is not { } until || until <= now)
        {
            state.PeakPowerSaverUntil = null;
            return false;
        }

        return true;
    }

    private List<string> BuildPeakPowerReasons(AlectraPeakPowerReading? reading)
    {
        var reasons = new List<string>();
        if (reading is null || !reading.HomeAssistantConfigured)
        {
            return reasons;
        }

        if (state.Settings.PeakPowerSaverOnPeakEnabled
            && IsOnPeakPeriod(reading.TouPeriod))
        {
            reasons.Add($"TOU {reading.TouPeriod}");
        }

        if (state.Settings.PeakPowerSaverPriceThresholdCentsPerKwh > 0
            && reading.CurrentPriceCentsPerKwh is { } price
            && price >= state.Settings.PeakPowerSaverPriceThresholdCentsPerKwh)
        {
            reasons.Add($"{price:0.0} c/kWh");
        }

        if (state.Settings.PeakPowerSaverHighPowerEnabled
            && reading.CurrentPowerKilowatts is { } power
            && power >= state.Settings.PeakPowerSaverPowerThresholdKilowatts)
        {
            reasons.Add($"{power:0.0} kW");
        }

        return reasons;
    }

    private string BuildPeakPowerSummary(AlectraPeakPowerReading? reading)
    {
        if (reading is null)
        {
            return "waiting for Alectra Hui usage sensors";
        }

        if (!reading.HomeAssistantConfigured)
        {
            return "Home Assistant usage sensors are not configured";
        }

        var reasons = BuildPeakPowerReasons(reading);
        var trigger = reasons.Count == 0 ? "no peak trigger" : string.Join(", ", reasons);
        var power = reading.CurrentPowerKilowatts is { } kw ? $"{kw:0.00} kW" : "-- kW";
        var price = reading.CurrentPriceCentsPerKwh is { } cents ? $"{cents:0.0} c/kWh" : "-- c/kWh";
        var period = string.IsNullOrWhiteSpace(reading.TouPeriod) ? "TOU --" : reading.TouPeriod;
        var plan = string.IsNullOrWhiteSpace(reading.CurrentPlan) ? "plan --" : reading.CurrentPlan;
        return $"{trigger}; {power}; {price}; {period}; {plan}";
    }

    private static bool IsOnPeakPeriod(string? touPeriod)
    {
        if (string.IsNullOrWhiteSpace(touPeriod))
        {
            return false;
        }

        var normalized = touPeriod.Trim().ToLowerInvariant();
        return normalized.Contains("on-peak", StringComparison.Ordinal)
            || normalized.Contains("on peak", StringComparison.Ordinal)
            || (normalized.Contains("peak", StringComparison.Ordinal)
                && !normalized.Contains("off", StringComparison.Ordinal)
                && !normalized.Contains("mid", StringComparison.Ordinal));
    }

    private void ClearSuperDefender(string status)
    {
        state.SuperDefenderUntil = null;
        state.SuperDefenderStatus = status;
    }

    private List<DateTimeOffset> GetRecentWallSettlingTouches(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.WallSettlingWindowMinutes));
        return state.ExternalTouchTimes
            .Where(item => item <= now && now - item <= window)
            .ToList();
    }

    private int CalculateWallSettlingSeconds(int recentTouchCount)
    {
        var baseSeconds = Math.Max(0, state.Settings.WallSettlingBaseSeconds);
        var extraSeconds = Math.Max(0, state.Settings.WallSettlingPressureExtraSeconds);
        var minimumTouches = Math.Max(1, state.Settings.WallSettlingMinimumTouches);
        var pressure = recentTouchCount <= minimumTouches
            ? 0.0
            : Math.Clamp((recentTouchCount - minimumTouches) / (double)Math.Max(1, minimumTouches), 0.0, 1.0);

        return Math.Clamp(baseSeconds + (int)Math.Round(extraSeconds * pressure), 0, 3600);
    }

    private void ResetCoolingDefenderStep()
    {
        state.BoostOffsetCelsius = 0.0;
        state.ActiveCoolingSetPointCelsius = null;
        state.ActiveCoolingStartedInSafeBand = false;
    }

    private void ClearComfortCompromise(string status)
    {
        state.ComfortCompromiseStartedAt = null;
        state.ComfortCompromiseUntil = null;
        state.ComfortCompromisePreferredSetPointCelsius = null;
        state.ComfortCompromiseEffectiveTargetCelsius = null;
        state.ComfortCompromiseStatus = status;
    }

    private void SaveState()
    {
        try
        {
            var directory = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(stateFilePath, JsonSerializer.Serialize(state, jsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save defender state to {StateFilePath}", stateFilePath);
        }
    }

    private static bool IsWeatherRuleAllowed(string mode, ThermostatReading reading, WeatherRuntimeState? weather)
    {
        var outdoor = weather?.OutdoorTemperatureCelsius;
        return NormalizeWeatherMode(mode) switch
        {
            "room-above-outdoor" => outdoor is not null && reading.CurrentTemperatureCelsius > outdoor,
            "room-below-outdoor" => outdoor is not null && reading.CurrentTemperatureCelsius < outdoor,
            "outdoor-above-target" => outdoor is not null && outdoor > reading.SetPointCelsius,
            "outdoor-below-target" => outdoor is not null && outdoor < reading.SetPointCelsius,
            _ => true
        };
    }

    private static bool IsScheduleActive(ScheduleEntry entry, DateTimeOffset now)
    {
        if (!entry.Enabled || !DayMatches(entry.Days, now.DayOfWeek))
        {
            return false;
        }

        if (!TimeSpan.TryParseExact(entry.StartTime, "hh\\:mm", CultureInfo.InvariantCulture, out var start)
            || !TimeSpan.TryParseExact(entry.EndTime, "hh\\:mm", CultureInfo.InvariantCulture, out var end))
        {
            return false;
        }

        var current = now.TimeOfDay;
        return start <= end
            ? current >= start && current <= end
            : current >= start || current <= end;
    }

    private static bool DayMatches(string days, DayOfWeek dayOfWeek)
    {
        var token = dayOfWeek.ToString()[..3];
        return days.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase));
    }

    private static ScheduleEntry NormalizeScheduleEntry(ScheduleEntry entry)
    {
        return new ScheduleEntry
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
            Enabled = entry.Enabled,
            Name = string.IsNullOrWhiteSpace(entry.Name) ? "Schedule" : entry.Name.Trim(),
            Days = string.IsNullOrWhiteSpace(entry.Days) ? "Mon,Tue,Wed,Thu,Fri,Sat,Sun" : entry.Days.Trim(),
            StartTime = string.IsNullOrWhiteSpace(entry.StartTime) ? "00:00" : entry.StartTime.Trim(),
            EndTime = string.IsNullOrWhiteSpace(entry.EndTime) ? "23:59" : entry.EndTime.Trim(),
            TargetTemperatureCelsius = Math.Round(Math.Clamp(entry.TargetTemperatureCelsius, 10, 35), 1),
            WeatherActivationMode = NormalizeWeatherMode(entry.WeatherActivationMode)
        };
    }

    private static ScheduleEntry CloneScheduleEntry(ScheduleEntry entry)
    {
        return new ScheduleEntry
        {
            Id = entry.Id,
            Enabled = entry.Enabled,
            Name = entry.Name,
            Days = entry.Days,
            StartTime = entry.StartTime,
            EndTime = entry.EndTime,
            TargetTemperatureCelsius = entry.TargetTemperatureCelsius,
            WeatherActivationMode = entry.WeatherActivationMode
        };
    }

    private static DefenderSettings CloneSettings(DefenderSettings settings)
    {
        return new DefenderSettings
        {
            ScheduleEnabled = settings.ScheduleEnabled,
            WeatherActivationMode = settings.WeatherActivationMode,
            BaseCooldownSeconds = settings.BaseCooldownSeconds,
            MaxCooldownSeconds = settings.MaxCooldownSeconds,
            TouchFrequencyWindowMinutes = settings.TouchFrequencyWindowMinutes,
            CoolModeRestoreDelayEnabled = settings.CoolModeRestoreDelayEnabled,
            CoolModeRestoreMinimumDelaySeconds = settings.CoolModeRestoreMinimumDelaySeconds,
            CoolModeRestoreMaximumDelaySeconds = settings.CoolModeRestoreMaximumDelaySeconds,
            CoolModeRestoreComfortBandCelsius = settings.CoolModeRestoreComfortBandCelsius,
            ConflictQuietModeEnabled = settings.ConflictQuietModeEnabled,
            ConflictQuietTouchThreshold = settings.ConflictQuietTouchThreshold,
            ConflictQuietMinutes = settings.ConflictQuietMinutes,
            ConflictQuietComfortBandCelsius = settings.ConflictQuietComfortBandCelsius,
            WallSettlingGuardEnabled = settings.WallSettlingGuardEnabled,
            WallSettlingMinimumTouches = settings.WallSettlingMinimumTouches,
            WallSettlingWindowMinutes = settings.WallSettlingWindowMinutes,
            WallSettlingBaseSeconds = settings.WallSettlingBaseSeconds,
            WallSettlingPressureExtraSeconds = settings.WallSettlingPressureExtraSeconds,
            WallSettlingSafetyBandCelsius = settings.WallSettlingSafetyBandCelsius,
            NaturalRecoveryEnabled = settings.NaturalRecoveryEnabled,
            AdaptiveQuietnessEnabled = settings.AdaptiveQuietnessEnabled,
            AdaptiveQuietTouchThreshold = settings.AdaptiveQuietTouchThreshold,
            MaximumAdaptiveDelaySeconds = settings.MaximumAdaptiveDelaySeconds,
            MinimumAdaptiveStepCelsius = settings.MinimumAdaptiveStepCelsius,
            MaximumAdaptiveHoldChancePercent = settings.MaximumAdaptiveHoldChancePercent,
            MaximumAdaptiveCommandGapSeconds = settings.MaximumAdaptiveCommandGapSeconds,
            MinimumNaturalDelaySeconds = settings.MinimumNaturalDelaySeconds,
            MaximumNaturalDelaySeconds = settings.MaximumNaturalDelaySeconds,
            NaturalStepCelsius = settings.NaturalStepCelsius,
            NaturalHoldChancePercent = settings.NaturalHoldChancePercent,
            MaxNaturalHolds = settings.MaxNaturalHolds,
            MinimumCommandGapSeconds = settings.MinimumCommandGapSeconds,
            NaturalSafetyOverrideCelsius = settings.NaturalSafetyOverrideCelsius,
            NaturalWalkbackEnabled = settings.NaturalWalkbackEnabled,
            NaturalWalkbackTriggerTouches = settings.NaturalWalkbackTriggerTouches,
            NaturalWalkbackStepCelsius = settings.NaturalWalkbackStepCelsius,
            NaturalWalkbackJitterCelsius = settings.NaturalWalkbackJitterCelsius,
            NaturalWalkbackSafetyBandCelsius = settings.NaturalWalkbackSafetyBandCelsius,
            TouchSignatureEnabled = settings.TouchSignatureEnabled,
            TouchSignatureTriggerTouches = settings.TouchSignatureTriggerTouches,
            TouchSignatureRetentionMinutes = settings.TouchSignatureRetentionMinutes,
            TouchSignatureMinimumStepCelsius = settings.TouchSignatureMinimumStepCelsius,
            TouchSignatureMaximumStepCelsius = settings.TouchSignatureMaximumStepCelsius,
            TouchSignatureSafetyBandCelsius = settings.TouchSignatureSafetyBandCelsius,
            HumanNudgeEnabled = settings.HumanNudgeEnabled,
            HumanNudgeTriggerTouches = settings.HumanNudgeTriggerTouches,
            HumanNudgeStepCelsius = settings.HumanNudgeStepCelsius,
            HumanNudgeSafetyBandCelsius = settings.HumanNudgeSafetyBandCelsius,
            VisibilityGuardEnabled = settings.VisibilityGuardEnabled,
            VisibilityGuardTriggerNotices = settings.VisibilityGuardTriggerNotices,
            VisibilityGuardNoticeWindowMinutes = settings.VisibilityGuardNoticeWindowMinutes,
            VisibilityGuardAfterCommandSeconds = settings.VisibilityGuardAfterCommandSeconds,
            VisibilityGuardMinimumHoldMinutes = settings.VisibilityGuardMinimumHoldMinutes,
            VisibilityGuardMaximumHoldMinutes = settings.VisibilityGuardMaximumHoldMinutes,
            VisibilityGuardSafetyBandCelsius = settings.VisibilityGuardSafetyBandCelsius,
            RoutineTimingEnabled = settings.RoutineTimingEnabled,
            RoutineTimingTriggerTouches = settings.RoutineTimingTriggerTouches,
            RoutineTimingIntervalMinutes = settings.RoutineTimingIntervalMinutes,
            RoutineTimingJitterMinutes = settings.RoutineTimingJitterMinutes,
            RoutineTimingMaxDelayMinutes = settings.RoutineTimingMaxDelayMinutes,
            RoutineTimingSafetyBandCelsius = settings.RoutineTimingSafetyBandCelsius,
            ComfortBudgetEnabled = settings.ComfortBudgetEnabled,
            ComfortBudgetWindowMinutes = settings.ComfortBudgetWindowMinutes,
            ComfortBudgetMaxCommands = settings.ComfortBudgetMaxCommands,
            ComfortBudgetSafetyBandCelsius = settings.ComfortBudgetSafetyBandCelsius,
            CommandCamouflageEnabled = settings.CommandCamouflageEnabled,
            CommandCamouflageMinimumGapSeconds = settings.CommandCamouflageMinimumGapSeconds,
            CommandCamouflagePressureExtraSeconds = settings.CommandCamouflagePressureExtraSeconds,
            CommandCamouflageSafetyBandCelsius = settings.CommandCamouflageSafetyBandCelsius,
            StealthGovernorEnabled = settings.StealthGovernorEnabled,
            StealthGovernorTriggerScore = settings.StealthGovernorTriggerScore,
            StealthGovernorMinimumHoldMinutes = settings.StealthGovernorMinimumHoldMinutes,
            StealthGovernorMaximumHoldMinutes = settings.StealthGovernorMaximumHoldMinutes,
            StealthGovernorSafetyBandCelsius = settings.StealthGovernorSafetyBandCelsius,
            NaturalCadenceEnabled = settings.NaturalCadenceEnabled,
            NaturalCadenceTriggerTouches = settings.NaturalCadenceTriggerTouches,
            NaturalCadenceMinimumMinutes = settings.NaturalCadenceMinimumMinutes,
            NaturalCadenceMaximumMinutes = settings.NaturalCadenceMaximumMinutes,
            NaturalCadenceJitterMinutes = settings.NaturalCadenceJitterMinutes,
            NaturalCadenceSafetyBandCelsius = settings.NaturalCadenceSafetyBandCelsius,
            NaturalChangePlannerEnabled = settings.NaturalChangePlannerEnabled,
            NaturalChangePlannerTriggerTouches = settings.NaturalChangePlannerTriggerTouches,
            NaturalChangePlannerMinimumMinutes = settings.NaturalChangePlannerMinimumMinutes,
            NaturalChangePlannerMaximumMinutes = settings.NaturalChangePlannerMaximumMinutes,
            NaturalChangePlannerJitterMinutes = settings.NaturalChangePlannerJitterMinutes,
            NaturalChangePlannerSafetyBandCelsius = settings.NaturalChangePlannerSafetyBandCelsius,
            NaturalChangePlannerPreferWeatherSlots = settings.NaturalChangePlannerPreferWeatherSlots,
            NaturalChangePlannerPreferSensorBeat = settings.NaturalChangePlannerPreferSensorBeat,
            ComfortEnvelopeEnabled = settings.ComfortEnvelopeEnabled,
            ComfortEnvelopeTriggerTouches = settings.ComfortEnvelopeTriggerTouches,
            ComfortEnvelopeHoldMinutes = settings.ComfortEnvelopeHoldMinutes,
            ComfortEnvelopeMaxOffsetCelsius = settings.ComfortEnvelopeMaxOffsetCelsius,
            ComfortEnvelopeSafetyBandCelsius = settings.ComfortEnvelopeSafetyBandCelsius,
            ComfortCompromiseEnabled = settings.ComfortCompromiseEnabled,
            ComfortCompromiseTriggerTouches = settings.ComfortCompromiseTriggerTouches,
            ComfortCompromiseHoldMinutes = settings.ComfortCompromiseHoldMinutes,
            ComfortCompromiseDecayMinutes = settings.ComfortCompromiseDecayMinutes,
            ComfortCompromiseMaxOffsetCelsius = settings.ComfortCompromiseMaxOffsetCelsius,
            ComfortCompromiseSafetyBandCelsius = settings.ComfortCompromiseSafetyBandCelsius,
            ComfortMemoryEnabled = settings.ComfortMemoryEnabled,
            ComfortMemoryLearningTouches = settings.ComfortMemoryLearningTouches,
            ComfortMemoryRetentionHours = settings.ComfortMemoryRetentionHours,
            ComfortMemoryMaxOffsetCelsius = settings.ComfortMemoryMaxOffsetCelsius,
            ComfortMemorySafetyBandCelsius = settings.ComfortMemorySafetyBandCelsius,
            ManualComfortGraceEnabled = settings.ManualComfortGraceEnabled,
            ManualComfortGraceMinutes = settings.ManualComfortGraceMinutes,
            ManualComfortGraceBandCelsius = settings.ManualComfortGraceBandCelsius,
            TouchIntentEnabled = settings.TouchIntentEnabled,
            TouchIntentMinimumTouches = settings.TouchIntentMinimumTouches,
            TouchIntentWindowMinutes = settings.TouchIntentWindowMinutes,
            TouchIntentNetWarmThresholdCelsius = settings.TouchIntentNetWarmThresholdCelsius,
            TouchIntentExtraGraceMinutes = settings.TouchIntentExtraGraceMinutes,
            TouchIntentSafetyBandCelsius = settings.TouchIntentSafetyBandCelsius,
            CoolerIntentFastLaneEnabled = settings.CoolerIntentFastLaneEnabled,
            CoolerIntentMinimumTouches = settings.CoolerIntentMinimumTouches,
            CoolerIntentWindowMinutes = settings.CoolerIntentWindowMinutes,
            CoolerIntentHoldMinutes = settings.CoolerIntentHoldMinutes,
            CoolerIntentNetCoolThresholdCelsius = settings.CoolerIntentNetCoolThresholdCelsius,
            CoolerIntentSafetyBandCelsius = settings.CoolerIntentSafetyBandCelsius,
            SetpointEchoGuardEnabled = settings.SetpointEchoGuardEnabled,
            SetpointEchoGraceSeconds = settings.SetpointEchoGraceSeconds,
            SetpointEchoSafetyBandCelsius = settings.SetpointEchoSafetyBandCelsius,
            RepeatCommandGuardEnabled = settings.RepeatCommandGuardEnabled,
            RepeatCommandMinimumWaitSeconds = settings.RepeatCommandMinimumWaitSeconds,
            RepeatCommandPressureExtraSeconds = settings.RepeatCommandPressureExtraSeconds,
            RepeatCommandSafetyBandCelsius = settings.RepeatCommandSafetyBandCelsius,
            SensorRhythmGuardEnabled = settings.SensorRhythmGuardEnabled,
            SensorRhythmMinimumSamples = settings.SensorRhythmMinimumSamples,
            SensorRhythmWindowMinutes = settings.SensorRhythmWindowMinutes,
            SensorRhythmJitterSeconds = settings.SensorRhythmJitterSeconds,
            SensorRhythmSafetyBandCelsius = settings.SensorRhythmSafetyBandCelsius,
            HvacActionAlibiEnabled = settings.HvacActionAlibiEnabled,
            HvacActionAlibiTriggerTouches = settings.HvacActionAlibiTriggerTouches,
            HvacActionAlibiTransitionWindowSeconds = settings.HvacActionAlibiTransitionWindowSeconds,
            HvacActionAlibiMaxHoldMinutes = settings.HvacActionAlibiMaxHoldMinutes,
            HvacActionAlibiSafetyBandCelsius = settings.HvacActionAlibiSafetyBandCelsius,
            CoolingRunwayGuardEnabled = settings.CoolingRunwayGuardEnabled,
            CoolingRunwayMinimumSeconds = settings.CoolingRunwayMinimumSeconds,
            CoolingRunwayPressureExtraSeconds = settings.CoolingRunwayPressureExtraSeconds,
            CoolingRunwaySafetyBandCelsius = settings.CoolingRunwaySafetyBandCelsius,
            RoomTrendGuardEnabled = settings.RoomTrendGuardEnabled,
            RoomTrendWindowMinutes = settings.RoomTrendWindowMinutes,
            RoomTrendStableToleranceCelsius = settings.RoomTrendStableToleranceCelsius,
            RoomTrendHoldMinutes = settings.RoomTrendHoldMinutes,
            ThermalMomentumGuardEnabled = settings.ThermalMomentumGuardEnabled,
            ThermalMomentumMinimumCoolingRateCelsiusPerHour = settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour,
            ThermalMomentumLookAheadMinutes = settings.ThermalMomentumLookAheadMinutes,
            ThermalMomentumHoldMinutes = settings.ThermalMomentumHoldMinutes,
            WeatherDriftGuardEnabled = settings.WeatherDriftGuardEnabled,
            WeatherDriftWindowMinutes = settings.WeatherDriftWindowMinutes,
            WeatherDriftMinimumChangeCelsius = settings.WeatherDriftMinimumChangeCelsius,
            WeatherDriftHoldMinutes = settings.WeatherDriftHoldMinutes,
            WeatherDriftSafetyBandCelsius = settings.WeatherDriftSafetyBandCelsius,
            PeakPowerSaverEnabled = settings.PeakPowerSaverEnabled,
            PeakPowerSaverOnPeakEnabled = settings.PeakPowerSaverOnPeakEnabled,
            PeakPowerSaverHighPowerEnabled = settings.PeakPowerSaverHighPowerEnabled,
            PeakPowerSaverPowerThresholdKilowatts = settings.PeakPowerSaverPowerThresholdKilowatts,
            PeakPowerSaverPriceThresholdCentsPerKwh = settings.PeakPowerSaverPriceThresholdCentsPerKwh,
            PeakPowerSaverHoldMinutes = settings.PeakPowerSaverHoldMinutes,
            PeakPowerSaverRefreshSeconds = settings.PeakPowerSaverRefreshSeconds,
            PeakPowerSaverSafetyBandCelsius = settings.PeakPowerSaverSafetyBandCelsius,
            PeakPowerSaverFanSaverEnabled = settings.PeakPowerSaverFanSaverEnabled,
            PeakPowerSaverFanMode = settings.PeakPowerSaverFanMode,
            FrontDoorKillSwitchEnabled = settings.FrontDoorKillSwitchEnabled,
            FrontDoorPersonEntityIds = settings.FrontDoorPersonEntityIds,
            FrontDoorKillSwitchHoldMinutes = settings.FrontDoorKillSwitchHoldMinutes,
            FrontDoorKillSwitchRefreshSeconds = settings.FrontDoorKillSwitchRefreshSeconds,
            FrontDoorKillSwitchTurnsThermostatOff = settings.FrontDoorKillSwitchTurnsThermostatOff,
            SuperDefenderModeEnabled = settings.SuperDefenderModeEnabled,
            SuperDefenderRemoteChangeThreshold = settings.SuperDefenderRemoteChangeThreshold,
            SuperDefenderWindowMinutes = settings.SuperDefenderWindowMinutes,
            SuperDefenderHoldMinutes = settings.SuperDefenderHoldMinutes,
            SuperDefenderSafetyBandCelsius = settings.SuperDefenderSafetyBandCelsius,
            SuperDefenderBypassQuietTiming = settings.SuperDefenderBypassQuietTiming,
            FanEnergySaverEnabled = settings.FanEnergySaverEnabled,
            FanEnergySaverThresholdCelsius = settings.FanEnergySaverThresholdCelsius,
            FanEnergySaverMode = settings.FanEnergySaverMode,
            UpstairsComfortEnabled = settings.UpstairsComfortEnabled,
            UpstairsTemperatureEntityIds = settings.UpstairsTemperatureEntityIds,
            UpstairsMaxComfortCelsius = settings.UpstairsMaxComfortCelsius,
            UpstairsComfortTargetCelsius = settings.UpstairsComfortTargetCelsius,
            UpstairsComfortBoostCelsius = settings.UpstairsComfortBoostCelsius,
            HomePresenceRequired = settings.HomePresenceRequired,
            PresenceEntityIds = settings.PresenceEntityIds,
            DefenderRunsContinuously = true
        };
    }

    private string BuildComfortStatus()
    {
        if (!state.Settings.UpstairsComfortEnabled)
        {
            return "Upstairs comfort guard is disabled.";
        }

        if (state.UpstairsSensors.Count == 0)
        {
            return "No upstairs temperature sensors found yet.";
        }

        var homeStatus = state.Settings.HomePresenceRequired
            ? state.IsHome ? "home" : "away"
            : "home check optional";
        var hottest = state.HottestUpstairsTemperatureCelsius is { } temp
            ? $"{temp:0.0} C"
            : "--";

        return state.UpstairsTooHot
            ? $"Upstairs is hot at {hottest}; comfort guard active ({homeStatus})."
            : $"Upstairs comfort ok at {hottest}; watching ({homeStatus}).";
    }

    private static string NormalizeWeatherMode(string? mode)
    {
        return (mode ?? "always").Trim().ToLowerInvariant() switch
        {
            "room-above-outdoor" => "room-above-outdoor",
            "room-below-outdoor" => "room-below-outdoor",
            "outdoor-above-target" => "outdoor-above-target",
            "outdoor-below-target" => "outdoor-below-target",
            _ => "always"
        };
    }

    private static string ResolveStatePath(string configuredPath, string contentRoot)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(contentRoot, configuredPath);
    }

    private sealed class DefenderRuntimeState
    {
        public double TargetTemperatureCelsius { get; set; }

        public bool DefenderEnabled { get; set; } = true;

        public double BoostOffsetCelsius { get; set; }

        public double? ActiveCoolingSetPointCelsius { get; set; }

        public bool ActiveCoolingStartedInSafeBand { get; set; }

        public string ConnectionState { get; set; } = "unavailable";

        public ThermostatRuntimeState? HomeAssistantThermostat { get; set; }

        public WeatherRuntimeState? Weather { get; set; }

        public string? HomeAssistantEntityId { get; set; }

        public string? LastCommand { get; set; }

        public string? LastError { get; set; }

        public string NextAction { get; set; } = "Waiting for the next 24/7 check.";

        public DateTimeOffset? NextActionAt { get; set; }

        public double? LastObservedSetPointCelsius { get; set; }

        public double? PendingCommandSetPointCelsius { get; set; }

        public string? PendingCommandHvacMode { get; set; }

        public string? PendingCommandFanMode { get; set; }

        public DateTimeOffset? PendingCommandAt { get; set; }

        public string? PendingCommandSourceKind { get; set; }

        public string? PendingCommandSourceLabel { get; set; }

        public string? PendingCommandSourceDetail { get; set; }

        public DateTimeOffset? WebsiteCommandDebounceUntil { get; set; }

        public DateTimeOffset? LastWebsiteCommandAt { get; set; }

        public string? LastWebsiteCommandName { get; set; }

        public string WebsiteCommandDebounceStatus { get; set; } = "Website controls are ready.";

        public DateTimeOffset? EmergencyQuietUntil { get; set; }

        public string EmergencyProtocol { get; set; } = "None";

        public string EmergencyStatus { get; set; } = "No emergency quiet mode active.";

        public DateTimeOffset? CooldownUntil { get; set; }

        public DateTimeOffset? NaturalHoldUntil { get; set; }

        public int NaturalHoldCount { get; set; }

        public DateTimeOffset? LastDefenderCommandAt { get; set; }

        public double? LastDefenderCommandSetPointCelsius { get; set; }

        public string NaturalRecoveryStatus { get; set; } = "Comfort sync is ready.";

        public string NaturalWalkbackStatus { get; set; } = "Natural walkback is watching.";

        public string TouchSignatureStatus { get; set; } = "Touch signature is watching.";

        public bool HumanNudgeActive { get; set; }

        public double? HumanNudgeLastSetPointCelsius { get; set; }

        public string HumanNudgeStatus { get; set; } = "Human nudge is watching for a safe command to shape.";

        public DateTimeOffset? VisibilityGuardUntil { get; set; }

        public string VisibilityGuardStatus { get; set; } = "Visibility guard is watching.";

        public List<DateTimeOffset> VisibilityNoticeTimes { get; set; } = [];

        public DateTimeOffset? RoutineTimingDueAt { get; set; }

        public string RoutineTimingStatus { get; set; } = "Routine timing is watching.";

        public DateTimeOffset? ComfortBudgetHoldUntil { get; set; }

        public string ComfortBudgetStatus { get; set; } = "Comfort budget is watching.";

        public DateTimeOffset? CommandCamouflageHoldUntil { get; set; }

        public string CommandCamouflageStatus { get; set; } = "Command camouflage is watching for a recent helper command.";

        public DateTimeOffset? StealthGovernorHoldUntil { get; set; }

        public string StealthGovernorStatus { get; set; } = "Stealth governor is watching overall pressure.";

        public List<DateTimeOffset> DefenderCommandTimes { get; set; } = [];

        public DateTimeOffset? NaturalCadenceDueAt { get; set; }

        public string NaturalCadenceStatus { get; set; } = "Natural cadence is watching.";

        public DateTimeOffset? NaturalChangePlannerDueAt { get; set; }

        public string NaturalChangePlannerReason { get; set; } = "watching";

        public string NaturalChangePlannerStatus { get; set; } = "Comfort Pace is watching.";

        public DateTimeOffset? ComfortEnvelopeUntil { get; set; }

        public double? ComfortEnvelopePreferredSetPointCelsius { get; set; }

        public double? ComfortEnvelopeMinimumAllowedSetPointCelsius { get; set; }

        public double? ComfortEnvelopeMaximumAllowedSetPointCelsius { get; set; }

        public string ComfortEnvelopeStatus { get; set; } = "Comfort envelope is watching.";

        public DateTimeOffset? ComfortCompromiseStartedAt { get; set; }

        public DateTimeOffset? ComfortCompromiseUntil { get; set; }

        public double? ComfortCompromisePreferredSetPointCelsius { get; set; }

        public double? ComfortCompromiseEffectiveTargetCelsius { get; set; }

        public string ComfortCompromiseStatus { get; set; } = "Comfort compromise is watching for repeated wall changes.";

        public List<ComfortMemorySlot> ComfortMemorySlots { get; set; } = [];

        public double? ComfortMemoryEffectiveTargetCelsius { get; set; }

        public string ComfortMemoryStatus { get; set; } = "Comfort memory is watching wall choices.";

        public DateTimeOffset? CoolModeRestoreDueAt { get; set; }

        public DateTimeOffset? CoolModeRestoreCommandedAt { get; set; }

        public string CoolModeRestoreStatus { get; set; } = "Cool mode restore is watching.";

        public DateTimeOffset? ConflictQuietUntil { get; set; }

        public string ConflictQuietStatus { get; set; } = "Conflict quiet is watching.";

        public DateTimeOffset? WallSettlingUntil { get; set; }

        public string WallSettlingStatus { get; set; } = "Wall settling is watching.";

        public DateTimeOffset? ManualComfortGraceUntil { get; set; }

        public double? ManualComfortGraceSetPointCelsius { get; set; }

        public string ManualComfortGraceStatus { get; set; } = "No wall-change grace active.";

        public string TouchIntentStatus { get; set; } = "Touch intent is watching.";

        public DateTimeOffset? CoolerIntentUntil { get; set; }

        public string CoolerIntentStatus { get; set; } = "Cooler intent fast lane is watching for repeated cooler wall touches.";

        public string SetpointEchoStatus { get; set; } = "Setpoint echo is watching.";

        public DateTimeOffset? RepeatCommandHoldUntil { get; set; }

        public string RepeatCommandStatus { get; set; } = "Repeat quiet is watching for identical follow-up commands.";

        public DateTimeOffset? SensorRhythmDueAt { get; set; }

        public string SensorRhythmStatus { get; set; } = "Sensor rhythm is watching.";

        public List<DateTimeOffset> HomeAssistantReadingTimes { get; set; } = [];

        public DateTimeOffset? HvacActionAlibiStartedAt { get; set; }

        public DateTimeOffset? HvacActionAlibiUntil { get; set; }

        public string HvacActionAlibiCurrentAction { get; set; } = "unknown";

        public DateTimeOffset? HvacActionAlibiLastTransitionAt { get; set; }

        public string? HvacActionAlibiLastTransitionFrom { get; set; }

        public string? HvacActionAlibiLastTransitionTo { get; set; }

        public string HvacActionAlibiStatus { get; set; } = "HVAC alibi is watching for a real action transition.";

        public DateTimeOffset? CoolingRunwayStartedAt { get; set; }

        public DateTimeOffset? CoolingRunwayHoldUntil { get; set; }

        public string CoolingRunwayStatus { get; set; } = "Cooling runway is watching for a fresh cooling start.";

        public DateTimeOffset? CoolingDemandStartedAt { get; set; }

        public DateTimeOffset? CoolingFailureSuspectedAt { get; set; }

        public DateTimeOffset? CoolingFailureNextAlertAt { get; set; }

        public int CoolingFailureAlertCount { get; set; }

        public string CoolingFailureStatus { get; set; } = "Cooling failure watch is ready.";

        public DateTimeOffset? OmegaConfirmedAt { get; set; }

        public double? OmegaRoomRiseCelsius { get; set; }

        public DateTimeOffset? RoomTrendHoldUntil { get; set; }

        public string RoomTrendStatus { get; set; } = "Room trend guard is watching.";

        public DateTimeOffset? ThermalMomentumHoldUntil { get; set; }

        public string ThermalMomentumStatus { get; set; } = "Thermal momentum guard is watching.";

        public DateTimeOffset? WeatherDriftHoldUntil { get; set; }

        public string WeatherDriftStatus { get; set; } = "Weather drift guard is watching.";

        public AlectraPeakPowerReading? AlectraPeakPower { get; set; }

        public DateTimeOffset? PeakPowerSaverUntil { get; set; }

        public string PeakPowerSaverStatus { get; set; } = "Alectra Peak Power Saver is watching usage sensors.";

        public List<FrontDoorPersonReading> FrontDoorPersonReadings { get; set; } = [];

        public DateTimeOffset? FrontDoorKillSwitchUntil { get; set; }

        public DateTimeOffset? FrontDoorKillSwitchTriggeredAt { get; set; }

        public DateTimeOffset? FrontDoorKillSwitchUpdatedAt { get; set; }

        public DateTimeOffset? FrontDoorThermostatOffCommandedAt { get; set; }

        public string FrontDoorKillSwitchStatus { get; set; } = "Front-door guard post is armed.";

        public string FrontDoorKillSwitchLastDetector { get; set; } = "--";

        public DateTimeOffset? SuperDefenderUntil { get; set; }

        public string SuperDefenderStatus { get; set; } = "Super Defender is watching for repeated phone or Home Assistant changes.";

        public List<DateTimeOffset> RemoteChangeTimes { get; set; } = [];

        public string LastChangeSource { get; set; } = "none";

        public string LastChangeSourceDetail { get; set; } = "No external thermostat change has been logged yet.";

        public string? LastChangeContextId { get; set; }

        public string? LastChangeContextParentId { get; set; }

        public string? LastChangeContextUserId { get; set; }

        public List<RoomTemperatureSample> RoomTemperatureSamples { get; set; } = [];

        public List<WeatherSample> WeatherSamples { get; set; } = [];

        public DefenderSettings Settings { get; set; } = new();

        public List<ScheduleEntry> Schedule { get; set; } = [];

        public List<ThermostatChangeAudit> ThermostatChanges { get; set; } = [];

        public List<DateTimeOffset> ExternalTouchTimes { get; set; } = [];

        public List<TemperatureSensorReading> UpstairsSensors { get; set; } = [];

        public List<PresenceReading> Presence { get; set; } = [];

        public bool IsHome { get; set; } = true;

        public bool UpstairsTooHot { get; set; }

        public double? HottestUpstairsTemperatureCelsius { get; set; }

        public string? HottestUpstairsEntityId { get; set; }

        public string ComfortStatus { get; set; } = "Waiting for upstairs comfort readings.";

        public DateTimeOffset UpdatedAt { get; set; }

        public List<DefenderEvent> Events { get; set; } = [];
    }

    private sealed record NaturalRecoveryPlan(
        int RecentTouchCount,
        string QuietLevel,
        int MinimumDelaySeconds,
        int MaximumDelaySeconds,
        double StepCelsius,
        int HoldChancePercent,
        int MaxHolds,
        int CommandGapSeconds,
        bool IsAdaptive);

    private sealed record TouchSignatureAnalysis(
        bool Active,
        int SampleCount,
        double? LearnedStepCelsius,
        double EffectiveStepCelsius,
        string Status);

    private sealed record TouchIntentAnalysis(
        bool Enabled,
        bool Active,
        int RecentTouchCount,
        string Direction,
        double NetChangeCelsius,
        int ExtraGraceMinutes,
        string Status);

    private sealed record CoolerIntentAnalysis(
        bool Enabled,
        bool Active,
        int RecentTouchCount,
        double NetChangeCelsius,
        string Status);

    private sealed record RoomTemperatureSample(
        DateTimeOffset Timestamp,
        double TemperatureCelsius);

    private sealed record WeatherSample(
        DateTimeOffset Timestamp,
        double OutdoorTemperatureCelsius,
        string? Condition);

    private sealed record SensorRhythmAnalysis(
        int SampleCount,
        int MedianIntervalSeconds,
        DateTimeOffset? LastReadingAt);

    private sealed record RoomTrendAnalysis(
        string Direction,
        double? DeltaCelsius,
        int SampleCount);

    private sealed record ThermalMomentumAnalysis(
        double? CoolingRateCelsiusPerHour,
        double? EstimatedMinutesToTarget,
        int SampleCount);

    private sealed record WeatherDriftAnalysis(
        int SampleCount,
        string Direction,
        double? OutdoorDeltaCelsius,
        bool ConditionChanged);

    private sealed class ComfortMemorySlot
    {
        public int HourOfDay { get; set; }

        public double OffsetCelsius { get; set; }

        public int Samples { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ThermostatRuntimeState
    {
        public double CurrentTemperatureCelsius { get; set; }

        public double SetPointCelsius { get; set; }

        public string HvacMode { get; set; } = "unknown";

        public string HvacAction { get; set; } = "unknown";

        public string? FanMode { get; set; }

        public List<string> AvailableFanModes { get; set; } = [];

        public string? ContextId { get; set; }

        public string? ContextParentId { get; set; }

        public string? ContextUserId { get; set; }

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public ThermostatSnapshot ToSnapshot()
        {
            return new ThermostatSnapshot(
                CurrentTemperatureCelsius,
                SetPointCelsius,
                HvacMode,
                HvacAction,
                FanMode,
                AvailableFanModes.ToArray(),
                UpdatedAt,
                ToContext());
        }

        public HomeAssistantStateContext? ToContext()
        {
            return string.IsNullOrWhiteSpace(ContextId)
                && string.IsNullOrWhiteSpace(ContextParentId)
                && string.IsNullOrWhiteSpace(ContextUserId)
                ? null
                : new HomeAssistantStateContext(ContextId, ContextParentId, ContextUserId);
        }
    }

    private sealed class WeatherRuntimeState
    {
        public double? OutdoorTemperatureCelsius { get; set; }

        public string? Condition { get; set; }

        public string EntityId { get; set; } = "";

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public WeatherSnapshot ToSnapshot()
        {
            return new WeatherSnapshot(OutdoorTemperatureCelsius, Condition, EntityId, UpdatedAt);
        }
    }
}

public sealed record ActiveRuleResult(
    ScheduleEntry? ActiveSchedule,
    string WeatherActivationMode,
    bool WeatherAllowsDefender);

public sealed record ComfortRuleResult(
    bool Active,
    bool BypassCooldown,
    double EffectiveTargetCelsius,
    string Status);

internal sealed record ChangeSourceClassification(
    string Kind,
    string Label,
    string Detail,
    bool CountsAsRemote);
