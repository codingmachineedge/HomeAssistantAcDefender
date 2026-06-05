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
            state.BoostOffsetCelsius = 0.0;
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
            state.BoostOffsetCelsius = 0.0;
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
            state.Settings.ManualComfortGraceEnabled = request.ManualComfortGraceEnabled;
            state.Settings.ManualComfortGraceMinutes = Math.Clamp(request.ManualComfortGraceMinutes, 0, 240);
            state.Settings.ManualComfortGraceBandCelsius = Math.Round(Math.Clamp(request.ManualComfortGraceBandCelsius, 0.1, 5.0), 1);
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

            var allowedRoomTemperature = state.TargetTemperatureCelsius + state.Settings.ManualComfortGraceBandCelsius;
            if (reading.CurrentTemperatureCelsius > allowedRoomTemperature)
            {
                state.ManualComfortGraceStatus = $"Room rose above {allowedRoomTemperature:0.0} C, so wall-change grace ended.";
                state.ManualComfortGraceUntil = null;
                SaveState();
                return false;
            }

            waitUntil = graceUntil;
            message = $"Room is still comfortable after wall change; holding until {graceUntil.ToLocalTime():HH:mm:ss} unless room rises above {allowedRoomTemperature:0.0} C.";
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

    public double CalculateNaturalCommandSetPoint(
        ThermostatReading reading,
        double expectedSetPointCelsius,
        bool bypassNaturalStep)
    {
        lock (gate)
        {
            if (!state.Settings.NaturalRecoveryEnabled
                || bypassNaturalStep
                || ShouldBypassNaturalRecovery(reading))
            {
                return Math.Round(expectedSetPointCelsius, 1);
            }

            var plan = BuildNaturalRecoveryPlan(DateTimeOffset.UtcNow);
            var step = Math.Max(0.1, plan.StepCelsius);
            if (reading.CurrentTemperatureCelsius > state.TargetTemperatureCelsius + options.TemperatureToleranceCelsius
                && reading.SetPointCelsius > expectedSetPointCelsius + 0.05)
            {
                // CalculateExpectedSetPoint anchors warm-room defense at room temp minus the active boost.
                return Math.Round(expectedSetPointCelsius, 1);
            }

            var delta = expectedSetPointCelsius - reading.SetPointCelsius;
            if (Math.Abs(delta) <= step)
            {
                return Math.Round(expectedSetPointCelsius, 1);
            }

            return Math.Round(reading.SetPointCelsius + Math.Sign(delta) * step, 1);
        }
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
                    state.BoostOffsetCelsius = 0.0;
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
            var target = state.TargetTemperatureCelsius;
            if (currentTemperatureCelsius <= target + options.TemperatureToleranceCelsius)
            {
                state.BoostOffsetCelsius = 0.0;
                return Math.Round(target, 1);
            }

            var action = (hvacAction ?? string.Empty).Trim().ToLowerInvariant();
            var isCooling = action is "cooling" or "cool";
            if (state.BoostOffsetCelsius < 1.0)
            {
                state.BoostOffsetCelsius = 1.0;
            }
            else if (!isCooling)
            {
                state.BoostOffsetCelsius = Math.Min(
                    state.BoostOffsetCelsius + 1.0,
                    options.MaximumBoostOffsetCelsius);
            }

            var lowestNormalSetPoint = Math.Max(options.MinimumCoolingSetPointCelsius, target);
            return Math.Round(Math.Max(
                lowestNormalSetPoint,
                currentTemperatureCelsius - state.BoostOffsetCelsius), 1);
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
        state.BoostOffsetCelsius = 0.0;
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

        AddEvent("warning",
            $"External thermostat change: {previous:0.0} C to {reading.SetPointCelsius:0.0} C at {now:yyyy-MM-dd HH:mm:ss}.");
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

    private void PruneTouchTimes(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, state.Settings.TouchFrequencyWindowMinutes));
        state.ExternalTouchTimes.RemoveAll(item => now - item > window);
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
        saved.NaturalRecoveryStatus = string.IsNullOrWhiteSpace(saved.NaturalRecoveryStatus)
            ? "Comfort sync is ready."
            : saved.NaturalRecoveryStatus;
        saved.CoolModeRestoreStatus = string.IsNullOrWhiteSpace(saved.CoolModeRestoreStatus)
            ? "Cool mode restore is watching."
            : saved.CoolModeRestoreStatus;
        saved.ConflictQuietStatus = string.IsNullOrWhiteSpace(saved.ConflictQuietStatus)
            ? "Conflict quiet is watching."
            : saved.ConflictQuietStatus;
        saved.ManualComfortGraceStatus = string.IsNullOrWhiteSpace(saved.ManualComfortGraceStatus)
            ? "No wall-change grace active."
            : saved.ManualComfortGraceStatus;
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
        var naturalPlan = BuildNaturalRecoveryPlan(now);
        var roomTrend = BuildRoomTrend(now);
        var thermalMomentum = BuildThermalMomentum(now, state.HomeAssistantThermostat?.CurrentTemperatureCelsius);
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
        state.CoolModeRestoreDueAt = null;
        state.CoolModeRestoreCommandedAt = null;
        state.CoolModeRestoreStatus = "Cool mode restore is watching.";
        state.ConflictQuietUntil = null;
        state.ConflictQuietStatus = "Conflict quiet is watching.";
        ClearManualComfortGrace();
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
            ManualComfortGraceEnabled = settings.ManualComfortGraceEnabled,
            ManualComfortGraceMinutes = settings.ManualComfortGraceMinutes,
            ManualComfortGraceBandCelsius = settings.ManualComfortGraceBandCelsius,
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

        public double BoostOffsetCelsius { get; set; } = 1.0;

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

        public DateTimeOffset? CoolModeRestoreDueAt { get; set; }

        public DateTimeOffset? CoolModeRestoreCommandedAt { get; set; }

        public string CoolModeRestoreStatus { get; set; } = "Cool mode restore is watching.";

        public DateTimeOffset? ConflictQuietUntil { get; set; }

        public string ConflictQuietStatus { get; set; } = "Conflict quiet is watching.";

        public DateTimeOffset? ManualComfortGraceUntil { get; set; }

        public double? ManualComfortGraceSetPointCelsius { get; set; }

        public string ManualComfortGraceStatus { get; set; } = "No wall-change grace active.";

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
