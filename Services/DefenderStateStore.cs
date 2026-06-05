using System.Globalization;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class DefenderStateStore
{
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
            state.Settings.NaturalCadenceEnabled = request.NaturalCadenceEnabled;
            state.Settings.NaturalCadenceTriggerTouches = Math.Clamp(request.NaturalCadenceTriggerTouches, 1, 20);
            state.Settings.NaturalCadenceMinimumMinutes = Math.Clamp(request.NaturalCadenceMinimumMinutes, 1, 120);
            state.Settings.NaturalCadenceMaximumMinutes = Math.Clamp(
                request.NaturalCadenceMaximumMinutes,
                state.Settings.NaturalCadenceMinimumMinutes,
                240);
            state.Settings.NaturalCadenceJitterMinutes = Math.Clamp(request.NaturalCadenceJitterMinutes, 0, 60);
            state.Settings.NaturalCadenceSafetyBandCelsius = Math.Round(Math.Clamp(request.NaturalCadenceSafetyBandCelsius, 0.1, 5.0), 1);
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
            state.Settings.RoomTrendGuardEnabled = request.RoomTrendGuardEnabled;
            state.Settings.RoomTrendWindowMinutes = Math.Clamp(request.RoomTrendWindowMinutes, 2, 240);
            state.Settings.RoomTrendStableToleranceCelsius = Math.Round(Math.Clamp(request.RoomTrendStableToleranceCelsius, 0.05, 2.0), 2);
            state.Settings.RoomTrendHoldMinutes = Math.Clamp(request.RoomTrendHoldMinutes, 1, 120);
            state.Settings.ThermalMomentumGuardEnabled = request.ThermalMomentumGuardEnabled;
            state.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour = Math.Round(Math.Clamp(request.ThermalMomentumMinimumCoolingRateCelsiusPerHour, 0.1, 5.0), 2);
            state.Settings.ThermalMomentumLookAheadMinutes = Math.Clamp(request.ThermalMomentumLookAheadMinutes, 5, 240);
            state.Settings.ThermalMomentumHoldMinutes = Math.Clamp(request.ThermalMomentumHoldMinutes, 1, 120);
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
            }

            state.UpdatedAt = DateTimeOffset.UtcNow;
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
            DetectExternalSetPointChange(reading, now);
            RecordRoomTemperatureSample(reading, now);

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
                UpdatedAt = now
            };
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

    public DefenderSnapshot RecordCommand(string message, double? commandedSetPointCelsius = null)
    {
        lock (gate)
        {
            state.LastCommand = message;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            if (commandedSetPointCelsius is { } setPoint)
            {
                state.PendingCommandSetPointCelsius = setPoint;
                state.PendingCommandAt = state.UpdatedAt;
                state.LastDefenderCommandAt = state.UpdatedAt;
                state.DefenderCommandTimes.Add(state.UpdatedAt);
                PruneDefenderCommandTimes(state.UpdatedAt);
                state.RoutineTimingDueAt = null;
                state.RoutineTimingStatus = "Routine timing used its comfort-check slot; watching for the next one.";
                state.ComfortBudgetStatus = $"Comfort budget counted {state.DefenderCommandTimes.Count}/{state.Settings.ComfortBudgetMaxCommands} recent comfort adjustments.";
                state.NaturalCadenceDueAt = null;
                state.NaturalCadenceStatus = "Natural cadence used its quiet slot; watching for the next safe rhythm.";
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

    public bool TryGetCooldown(DateTimeOffset now, out DateTimeOffset cooldownUntil)
    {
        lock (gate)
        {
            cooldownUntil = state.CooldownUntil ?? DateTimeOffset.MinValue;
            return state.CooldownUntil is { } until && until > now;
        }
    }

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
                && state.NaturalCadenceDueAt is null
                && state.VisibilityGuardUntil is null
                && state.ConflictQuietUntil is null
                && state.RoomTrendHoldUntil is null
                && state.ThermalMomentumHoldUntil is null)
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
            state.RoomTrendHoldUntil = null;
            state.RoomTrendStatus = state.Settings.RoomTrendGuardEnabled
                ? "Room trend is lined up; no hold needed."
                : "Room trend guard is off.";
            state.ThermalMomentumHoldUntil = null;
            state.ThermalMomentumStatus = state.Settings.ThermalMomentumGuardEnabled
                ? "Thermal momentum is lined up; no hold needed."
                : "Thermal momentum guard is off.";
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
            ClearNaturalCadence(state.Settings.NaturalCadenceEnabled
                ? "Natural cadence is lined up; no quiet slot needed."
                : "Natural cadence is off.");
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

    public string GetFanSaverMode()
    {
        lock (gate)
        {
            return state.Settings.FanEnergySaverMode;
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

        var commandGrace = TimeSpan.FromSeconds(Math.Max(15, options.CommandGraceSeconds));
        var matchesPendingCommand = state.PendingCommandSetPointCelsius is { } pending
            && state.PendingCommandAt is { } pendingAt
            && now - pendingAt <= commandGrace
            && Math.Abs(pending - reading.SetPointCelsius) <= 0.15;

        if (matchesPendingCommand)
        {
            state.PendingCommandSetPointCelsius = null;
            state.PendingCommandAt = null;
            return;
        }

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
            ? $"Manual thermostat touch noticed; comfort sync is in {plan.QuietLevel.ToLowerInvariant()} mode and will wait {cooldownSeconds}s before a quiet nudge."
            : "Manual thermostat touch noticed; quiet recovery is off.";
        var touchScore = CalculateTouchSuspicionScore(now);
        state.NaturalWalkbackStatus = state.Settings.NaturalWalkbackEnabled
            ? $"Manual touch score is {touchScore}/100; natural walkback will use small safe-band nudges if correction is needed."
            : "Natural walkback is off.";
        state.NaturalCadenceDueAt = null;
        state.NaturalCadenceStatus = state.Settings.NaturalCadenceEnabled
            ? $"Manual touch pressure is {touchScore}/100; natural cadence will pick a quiet slot if a safe correction is needed."
            : "Natural cadence is off.";
        state.TouchSignatureStatus = state.Settings.TouchSignatureEnabled
            ? "Manual wall step logged; touch signature will shape safe nudges after enough samples."
            : "Touch signature is off.";
        state.VisibilityGuardStatus = state.Settings.VisibilityGuardEnabled
            ? $"Visibility guard has {state.VisibilityNoticeTimes.Count} noticed correction signal(s)."
            : "Visibility guard is off.";

        var audit = new ThermostatChangeAudit(
            now,
            reading.EntityId,
            Math.Round(previous, 1),
            Math.Round(reading.SetPointCelsius, 1),
            reading.CurrentTemperatureCelsius,
            state.Weather?.OutdoorTemperatureCelsius,
            state.Weather?.Condition);

        state.ThermostatChanges.Insert(0, audit);
        if (state.ThermostatChanges.Count > 100)
        {
            state.ThermostatChanges.RemoveRange(100, state.ThermostatChanges.Count - 100);
        }

        ApplyTouchIntentGrace(reading, now);
        TryUpdateComfortMemory(reading, now);
        TryStartComfortCompromise(reading, now);

        AddEvent("warning",
            $"External thermostat change: {previous:0.0} C to {reading.SetPointCelsius:0.0} C at {now:yyyy-MM-dd HH:mm:ss}.");
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

    private void PruneRoomTemperatureSamples(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(2, state.Settings.RoomTrendWindowMinutes));
        state.RoomTemperatureSamples.RemoveAll(item => now - item.Timestamp > window);
        if (state.RoomTemperatureSamples.Count > 300)
        {
            state.RoomTemperatureSamples.RemoveRange(0, state.RoomTemperatureSamples.Count - 300);
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
        saved.Settings.NaturalCadenceTriggerTouches = Math.Clamp(saved.Settings.NaturalCadenceTriggerTouches, 1, 20);
        saved.Settings.NaturalCadenceMinimumMinutes = Math.Clamp(saved.Settings.NaturalCadenceMinimumMinutes, 1, 120);
        saved.Settings.NaturalCadenceMaximumMinutes = Math.Clamp(
            saved.Settings.NaturalCadenceMaximumMinutes,
            saved.Settings.NaturalCadenceMinimumMinutes,
            240);
        saved.Settings.NaturalCadenceJitterMinutes = Math.Clamp(saved.Settings.NaturalCadenceJitterMinutes, 0, 60);
        saved.Settings.NaturalCadenceSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.NaturalCadenceSafetyBandCelsius, 0.1, 5.0), 1);
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
        saved.Settings.ManualComfortGraceMinutes = Math.Clamp(saved.Settings.ManualComfortGraceMinutes, 0, 240);
        saved.Settings.ManualComfortGraceBandCelsius = Math.Round(Math.Clamp(saved.Settings.ManualComfortGraceBandCelsius, 0.1, 5.0), 1);
        saved.Settings.TouchIntentMinimumTouches = Math.Clamp(saved.Settings.TouchIntentMinimumTouches, 1, 20);
        saved.Settings.TouchIntentWindowMinutes = Math.Clamp(saved.Settings.TouchIntentWindowMinutes, 1, 1440);
        saved.Settings.TouchIntentNetWarmThresholdCelsius = Math.Round(Math.Clamp(saved.Settings.TouchIntentNetWarmThresholdCelsius, 0.1, 5.0), 1);
        saved.Settings.TouchIntentExtraGraceMinutes = Math.Clamp(saved.Settings.TouchIntentExtraGraceMinutes, 0, 240);
        saved.Settings.TouchIntentSafetyBandCelsius = Math.Round(Math.Clamp(saved.Settings.TouchIntentSafetyBandCelsius, 0.1, 5.0), 1);
        saved.Settings.RoomTrendWindowMinutes = Math.Clamp(saved.Settings.RoomTrendWindowMinutes, 2, 240);
        saved.Settings.RoomTrendStableToleranceCelsius = Math.Round(Math.Clamp(saved.Settings.RoomTrendStableToleranceCelsius, 0.05, 2.0), 2);
        saved.Settings.RoomTrendHoldMinutes = Math.Clamp(saved.Settings.RoomTrendHoldMinutes, 1, 120);
        saved.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour = Math.Round(Math.Clamp(saved.Settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour, 0.1, 5.0), 2);
        saved.Settings.ThermalMomentumLookAheadMinutes = Math.Clamp(saved.Settings.ThermalMomentumLookAheadMinutes, 5, 240);
        saved.Settings.ThermalMomentumHoldMinutes = Math.Clamp(saved.Settings.ThermalMomentumHoldMinutes, 1, 120);
        saved.Settings.DefenderRunsContinuously = true;
        saved.Schedule ??= [];
        saved.Events ??= [];
        saved.ThermostatChanges ??= [];
        saved.ExternalTouchTimes ??= [];
        saved.UpstairsSensors ??= [];
        saved.Presence ??= [];
        saved.RoomTemperatureSamples ??= [];
        saved.ComfortMemorySlots ??= [];
        saved.DefenderCommandTimes ??= [];
        saved.VisibilityNoticeTimes ??= [];
        PruneLoadedDefenderCommandTimes(saved);
        PruneLoadedVisibilityNoticeTimes(saved);
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
        saved.NaturalCadenceStatus = string.IsNullOrWhiteSpace(saved.NaturalCadenceStatus)
            ? "Natural cadence is watching."
            : saved.NaturalCadenceStatus;
        if (saved.NaturalCadenceDueAt is { } cadenceDueAt && cadenceDueAt <= DateTimeOffset.UtcNow)
        {
            saved.NaturalCadenceDueAt = null;
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
        saved.ManualComfortGraceStatus = string.IsNullOrWhiteSpace(saved.ManualComfortGraceStatus)
            ? "No wall-change grace active."
            : saved.ManualComfortGraceStatus;
        saved.TouchIntentStatus = string.IsNullOrWhiteSpace(saved.TouchIntentStatus)
            ? "Touch intent is watching."
            : saved.TouchIntentStatus;
        saved.RoomTrendStatus = string.IsNullOrWhiteSpace(saved.RoomTrendStatus)
            ? "Room trend guard is watching."
            : saved.RoomTrendStatus;
        saved.ThermalMomentumStatus = string.IsNullOrWhiteSpace(saved.ThermalMomentumStatus)
            ? "Thermal momentum guard is watching."
            : saved.ThermalMomentumStatus;
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
        var manualGraceSeconds = state.ManualComfortGraceUntil is { } graceUntil && graceUntil > now
            ? (int)Math.Ceiling((graceUntil - now).TotalSeconds)
            : 0;
        var roomTrendSeconds = state.RoomTrendHoldUntil is { } trendUntil && trendUntil > now
            ? (int)Math.Ceiling((trendUntil - now).TotalSeconds)
            : 0;
        var thermalMomentumSeconds = state.ThermalMomentumHoldUntil is { } momentumUntil && momentumUntil > now
            ? (int)Math.Ceiling((momentumUntil - now).TotalSeconds)
            : 0;
        var routineTimingSeconds = state.RoutineTimingDueAt is { } routineDueAt && routineDueAt > now
            ? (int)Math.Ceiling((routineDueAt - now).TotalSeconds)
            : 0;
        PruneDefenderCommandTimes(now);
        var comfortBudgetSeconds = state.ComfortBudgetHoldUntil is { } budgetUntil && budgetUntil > now
            ? (int)Math.Ceiling((budgetUntil - now).TotalSeconds)
            : 0;
        var naturalCadenceSeconds = state.NaturalCadenceDueAt is { } cadenceDueAt && cadenceDueAt > now
            ? (int)Math.Ceiling((cadenceDueAt - now).TotalSeconds)
            : 0;
        PruneVisibilityNoticeTimes(now);
        var visibilityGuardSeconds = state.VisibilityGuardUntil is { } visibilityUntil && visibilityUntil > now
            ? (int)Math.Ceiling((visibilityUntil - now).TotalSeconds)
            : 0;
        var visibilityPressure = CalculateVisibilityPressure(now);
        var naturalPlan = BuildNaturalRecoveryPlan(now);
        var naturalWalkbackScore = CalculateTouchSuspicionScore(now);
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
                currentThermostat.AvailableFanModes)
            : null;
        var naturalWalkbackActive = state.Settings.NaturalWalkbackEnabled
            && state.ExternalTouchTimes.Count >= Math.Max(1, state.Settings.NaturalWalkbackTriggerTouches)
            && currentReading is { } walkbackReading
            && walkbackReading.CurrentTemperatureCelsius <= state.TargetTemperatureCelsius + state.Settings.NaturalWalkbackSafetyBandCelsius
            && !ShouldBypassNaturalRecovery(walkbackReading);
        var touchSignature = BuildTouchSignatureAnalysis(currentReading, naturalWalkbackStep, now);
        var roomTrend = BuildRoomTrend(now);
        var thermalMomentum = BuildThermalMomentum(now, state.HomeAssistantThermostat?.CurrentTemperatureCelsius);
        var touchIntent = BuildTouchIntentAnalysis(now);
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
        ClearVisibilityGuard("Visibility guard is watching.");
        ClearRoutineTiming("Routine timing is watching.");
        ClearComfortBudget("Comfort budget is watching.");
        ClearNaturalCadence("Natural cadence is watching.");
        ClearComfortCompromise("Comfort compromise reset after website target change.");
        state.ComfortMemoryEffectiveTargetCelsius = null;
        state.ComfortMemoryStatus = "Comfort memory is watching wall choices.";
        state.CoolModeRestoreDueAt = null;
        state.CoolModeRestoreCommandedAt = null;
        state.CoolModeRestoreStatus = "Cool mode restore is watching.";
        state.ConflictQuietUntil = null;
        state.ConflictQuietStatus = "Conflict quiet is watching.";
        ClearManualComfortGrace();
        state.TouchIntentStatus = "Touch intent is watching.";
        state.RoomTrendHoldUntil = null;
        state.RoomTrendStatus = "Room trend guard is watching.";
        state.ThermalMomentumHoldUntil = null;
        state.ThermalMomentumStatus = "Thermal momentum guard is watching.";
    }

    private void ClearManualComfortGrace()
    {
        state.ManualComfortGraceUntil = null;
        state.ManualComfortGraceSetPointCelsius = null;
        state.ManualComfortGraceStatus = "No wall-change grace active.";
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

    private void ClearNaturalCadence(string status)
    {
        state.NaturalCadenceDueAt = null;
        state.NaturalCadenceStatus = status;
    }

    private void ClearVisibilityGuard(string status)
    {
        state.VisibilityGuardUntil = null;
        state.VisibilityGuardStatus = status;
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
            NaturalCadenceEnabled = settings.NaturalCadenceEnabled,
            NaturalCadenceTriggerTouches = settings.NaturalCadenceTriggerTouches,
            NaturalCadenceMinimumMinutes = settings.NaturalCadenceMinimumMinutes,
            NaturalCadenceMaximumMinutes = settings.NaturalCadenceMaximumMinutes,
            NaturalCadenceJitterMinutes = settings.NaturalCadenceJitterMinutes,
            NaturalCadenceSafetyBandCelsius = settings.NaturalCadenceSafetyBandCelsius,
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
            RoomTrendGuardEnabled = settings.RoomTrendGuardEnabled,
            RoomTrendWindowMinutes = settings.RoomTrendWindowMinutes,
            RoomTrendStableToleranceCelsius = settings.RoomTrendStableToleranceCelsius,
            RoomTrendHoldMinutes = settings.RoomTrendHoldMinutes,
            ThermalMomentumGuardEnabled = settings.ThermalMomentumGuardEnabled,
            ThermalMomentumMinimumCoolingRateCelsiusPerHour = settings.ThermalMomentumMinimumCoolingRateCelsiusPerHour,
            ThermalMomentumLookAheadMinutes = settings.ThermalMomentumLookAheadMinutes,
            ThermalMomentumHoldMinutes = settings.ThermalMomentumHoldMinutes,
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

        public DateTimeOffset? PendingCommandAt { get; set; }

        public DateTimeOffset? CooldownUntil { get; set; }

        public DateTimeOffset? NaturalHoldUntil { get; set; }

        public int NaturalHoldCount { get; set; }

        public DateTimeOffset? LastDefenderCommandAt { get; set; }

        public string NaturalRecoveryStatus { get; set; } = "Comfort sync is ready.";

        public string NaturalWalkbackStatus { get; set; } = "Natural walkback is watching.";

        public string TouchSignatureStatus { get; set; } = "Touch signature is watching.";

        public DateTimeOffset? VisibilityGuardUntil { get; set; }

        public string VisibilityGuardStatus { get; set; } = "Visibility guard is watching.";

        public List<DateTimeOffset> VisibilityNoticeTimes { get; set; } = [];

        public DateTimeOffset? RoutineTimingDueAt { get; set; }

        public string RoutineTimingStatus { get; set; } = "Routine timing is watching.";

        public DateTimeOffset? ComfortBudgetHoldUntil { get; set; }

        public string ComfortBudgetStatus { get; set; } = "Comfort budget is watching.";

        public List<DateTimeOffset> DefenderCommandTimes { get; set; } = [];

        public DateTimeOffset? NaturalCadenceDueAt { get; set; }

        public string NaturalCadenceStatus { get; set; } = "Natural cadence is watching.";

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

        public DateTimeOffset? ManualComfortGraceUntil { get; set; }

        public double? ManualComfortGraceSetPointCelsius { get; set; }

        public string ManualComfortGraceStatus { get; set; } = "No wall-change grace active.";

        public string TouchIntentStatus { get; set; } = "Touch intent is watching.";

        public DateTimeOffset? RoomTrendHoldUntil { get; set; }

        public string RoomTrendStatus { get; set; } = "Room trend guard is watching.";

        public DateTimeOffset? ThermalMomentumHoldUntil { get; set; }

        public string ThermalMomentumStatus { get; set; } = "Thermal momentum guard is watching.";

        public List<RoomTemperatureSample> RoomTemperatureSamples { get; set; } = [];

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

    private sealed record RoomTemperatureSample(
        DateTimeOffset Timestamp,
        double TemperatureCelsius);

    private sealed record RoomTrendAnalysis(
        string Direction,
        double? DeltaCelsius,
        int SampleCount);

    private sealed record ThermalMomentumAnalysis(
        double? CoolingRateCelsiusPerHour,
        double? EstimatedMinutesToTarget,
        int SampleCount);

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
                UpdatedAt);
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
