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

            return Math.Round(Math.Max(
                options.MinimumCoolingSetPointCelsius,
                target - state.BoostOffsetCelsius), 1);
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
        var cooldownSeconds = CalculateDynamicCooldownSeconds(now);
        if (cooldownSeconds > 0)
        {
            state.CooldownUntil = now.AddSeconds(cooldownSeconds);
        }

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
            saved.Settings.DefenderRunsContinuously = true;
        saved.Schedule ??= [];
        saved.Events ??= [];
        saved.ThermostatChanges ??= [];
        saved.ExternalTouchTimes ??= [];
        saved.UpstairsSensors ??= [];
        saved.Presence ??= [];
        saved.ComfortStatus = string.IsNullOrWhiteSpace(saved.ComfortStatus)
            ? "Waiting for upstairs comfort readings."
            : saved.ComfortStatus;
        saved.Events.RemoveAll(item => item.Message.Contains("dummy", StringComparison.OrdinalIgnoreCase)
            || item.Message.Contains("simulator", StringComparison.OrdinalIgnoreCase));
    }

    private DefenderSnapshot CreateSnapshot()
    {
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
            state.CooldownUntil is { } until && until > DateTimeOffset.UtcNow
                ? (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)
                : 0,
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
