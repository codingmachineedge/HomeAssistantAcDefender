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
            state.BoostOffsetCelsius = 1.0;
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
            state.BoostOffsetCelsius = 1.0;
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

    public DefenderSnapshot UpdateDummyThermostat(DummyThermostatRequest request)
    {
        lock (gate)
        {
            if (request.CurrentTemperatureCelsius is { } current)
            {
                state.DummyThermostat.CurrentTemperatureCelsius = Math.Round(current, 1);
            }

            if (request.SetPointCelsius is { } setPoint)
            {
                state.DummyThermostat.SetPointCelsius = Math.Round(setPoint, 1);
                AddEvent("warning", $"Dummy thermostat changed to {state.DummyThermostat.SetPointCelsius:0.0} C.");
            }

            if (!string.IsNullOrWhiteSpace(request.HvacMode))
            {
                state.DummyThermostat.HvacMode = request.HvacMode.Trim().ToLowerInvariant();
            }

            state.DummyThermostat.UpdatedAt = DateTimeOffset.UtcNow;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordHomeAssistantReading(ThermostatReading reading)
    {
        lock (gate)
        {
            state.ActiveSource = "home-assistant";
            state.HomeAssistantEntityId = reading.EntityId;
            state.HomeAssistantThermostat = new ThermostatRuntimeState
            {
                CurrentTemperatureCelsius = reading.CurrentTemperatureCelsius,
                SetPointCelsius = reading.SetPointCelsius,
                HvacMode = reading.HvacMode,
                HvacAction = reading.HvacAction,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            state.LastError = null;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordHomeAssistantUnavailable(string message)
    {
        lock (gate)
        {
            state.ActiveSource = "dummy";
            state.LastError = message;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("warning", message);
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot RecordCommand(string message)
    {
        lock (gate)
        {
            state.LastCommand = message;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("info", message);
            SaveState();
            return CreateSnapshot();
        }
    }

    public DefenderSnapshot ApplyDummyDefenderCycle()
    {
        lock (gate)
        {
            var dummy = state.DummyThermostat;
            var cooling = dummy.HvacMode == "cool" && dummy.SetPointCelsius < dummy.CurrentTemperatureCelsius - 0.05;
            dummy.HvacAction = cooling ? "cooling" : "idle";
            dummy.CurrentTemperatureCelsius = Math.Round(cooling
                ? Math.Max(dummy.SetPointCelsius, dummy.CurrentTemperatureCelsius - 0.2)
                : Math.Min(28.0, dummy.CurrentTemperatureCelsius + 0.05), 1);

            var expectedSetPoint = CalculateExpectedSetPoint(dummy.CurrentTemperatureCelsius, dummy.HvacAction);
            if (state.DefenderEnabled && Math.Abs(dummy.SetPointCelsius - expectedSetPoint) > 0.05)
            {
                dummy.SetPointCelsius = expectedSetPoint;
                dummy.HvacMode = "cool";
                state.LastCommand = $"Dummy thermostat forced to {expectedSetPoint:0.0} C.";
                AddEvent("info", state.LastCommand);
            }

            dummy.UpdatedAt = DateTimeOffset.UtcNow;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveState();
            return CreateSnapshot();
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
            if (!isCooling)
            {
                state.BoostOffsetCelsius = Math.Min(
                    Math.Max(1.0, state.BoostOffsetCelsius + 1.0),
                    options.MaximumBoostOffsetCelsius);
            }
            else if (state.BoostOffsetCelsius < 1.0)
            {
                state.BoostOffsetCelsius = 1.0;
            }

            return Math.Round(Math.Max(
                options.MinimumCoolingSetPointCelsius,
                target - state.BoostOffsetCelsius), 1);
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
            DummyThermostat = new ThermostatRuntimeState
            {
                CurrentTemperatureCelsius = 25.0,
                SetPointCelsius = options.DefaultTargetCelsius + 2.0,
                HvacMode = "cool",
                HvacAction = "idle",
                UpdatedAt = DateTimeOffset.UtcNow
            },
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private DefenderSnapshot CreateSnapshot()
    {
        return new DefenderSnapshot(
            state.TargetTemperatureCelsius,
            state.DefenderEnabled,
            state.BoostOffsetCelsius,
            state.ActiveSource,
            state.DummyThermostat.ToSnapshot(),
            state.HomeAssistantThermostat?.ToSnapshot(),
            state.HomeAssistantEntityId,
            !string.IsNullOrWhiteSpace(state.HomeAssistantEntityId),
            state.LastCommand,
            state.LastError,
            state.UpdatedAt,
            state.Events.ToArray());
    }

    private void AddEvent(string level, string message)
    {
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

        public string ActiveSource { get; set; } = "dummy";

        public ThermostatRuntimeState DummyThermostat { get; set; } = new();

        public ThermostatRuntimeState? HomeAssistantThermostat { get; set; }

        public string? HomeAssistantEntityId { get; set; }

        public string? LastCommand { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public List<DefenderEvent> Events { get; set; } = [];
    }

    private sealed class ThermostatRuntimeState
    {
        public double CurrentTemperatureCelsius { get; set; }

        public double SetPointCelsius { get; set; }

        public string HvacMode { get; set; } = "cool";

        public string HvacAction { get; set; } = "idle";

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public ThermostatSnapshot ToSnapshot()
        {
            return new ThermostatSnapshot(
                CurrentTemperatureCelsius,
                SetPointCelsius,
                HvacMode,
                HvacAction,
                UpdatedAt);
        }
    }
}
