using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class AcDefenderService
{
    private readonly DefenderStateStore stateStore;
    private readonly HomeAssistantClient homeAssistantClient;
    private readonly IOptionsMonitor<DefenderOptions> options;
    private readonly ILogger<AcDefenderService> logger;

    public AcDefenderService(
        DefenderStateStore stateStore,
        HomeAssistantClient homeAssistantClient,
        IOptionsMonitor<DefenderOptions> options,
        ILogger<AcDefenderService> logger)
    {
        this.stateStore = stateStore;
        this.homeAssistantClient = homeAssistantClient;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>
    /// The 24/7 decision cycle. Reads real Home Assistant data, then walks the defender guards in order
    /// (emergency → cool-mode restore → schedule/weather → upstairs comfort → wall-touch holds →
    /// expected setpoint → sensor-timing holds → command shaping → send). The first guard that wants to
    /// wait stops the cycle and records the next action. See <c>Guards/GuardCatalog.cs</c> and
    /// <c>docs/wiki/Defender-Logic.md</c> for the full per-algorithm reference.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = stateStore.GetSnapshot();
            var nextCheck = DateTimeOffset.UtcNow.AddSeconds(Math.Max(3, options.CurrentValue.PollIntervalSeconds));

            var reading = await RefreshReadingAsync(cancellationToken);
            if (reading is null)
            {
                return;
            }

            var emergencyNow = DateTimeOffset.UtcNow;
            if (stateStore.TryRespectEmergencyQuiet(emergencyNow, out var emergencyUntil, out var emergencyMessage))
            {
                stateStore.SetNextAction(emergencyMessage, emergencyUntil);
                return;
            }

            if (!snapshot.DefenderEnabled)
            {
                stateStore.SetNextAction("Defender paused; still reading the real thermostat 24/7.", nextCheck);
                return;
            }

            if (!string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTimeOffset.UtcNow;
                if (stateStore.TryDelayCoolModeRestore(reading, now, out var restoreAt, out var restoreMessage))
                {
                    stateStore.SetNextAction(restoreMessage, restoreAt);
                    return;
                }

                stateStore.SetNextAction("Cool mode restore delay finished; restoring cool mode now.", now);
                await homeAssistantClient.SetHvacModeAsync(reading.EntityId, "cool", cancellationToken);
                stateStore.RecordCoolModeRestoreCommand(reading.HvacMode);
                stateStore.RecordCommand($"Home Assistant {reading.EntityId} mode restored to cool.");
                return;
            }

            var rules = stateStore.ApplyScheduleAndWeatherRules(reading);
            var comfort = stateStore.ApplyComfortRules();
            if (!rules.WeatherAllowsDefender && !comfort.Active)
            {
                stateStore.SetNextAction($"Weather rule '{rules.WeatherActivationMode}' is not met; checking again.", nextCheck);
                return;
            }

            var quietBypassNow = DateTimeOffset.UtcNow;
            var coolerIntentBypass = stateStore.ShouldBypassQuietTimingForCoolerIntent(reading, quietBypassNow);
            var bypassQuietTiming = comfort.BypassCooldown || coolerIntentBypass;

            if (!bypassQuietTiming
                && stateStore.TryRespectWallSettlingGuard(reading, false, quietBypassNow, out var settlingUntil, out var settlingMessage))
            {
                stateStore.SetNextAction(settlingMessage, settlingUntil);
                return;
            }
            else if (bypassQuietTiming)
            {
                stateStore.TryRespectWallSettlingGuard(reading, true, quietBypassNow, out _, out _);
            }

            if (!bypassQuietTiming
                && stateStore.TryRespectConflictQuietMode(reading, false, quietBypassNow, out var conflictUntil, out var conflictMessage))
            {
                stateStore.SetNextAction(conflictMessage, conflictUntil);
                return;
            }
            else if (bypassQuietTiming)
            {
                stateStore.TryRespectConflictQuietMode(reading, true, quietBypassNow, out _, out _);
            }

            if (!bypassQuietTiming
                && stateStore.TryRespectManualComfortGrace(reading, false, quietBypassNow, out var graceUntil, out var graceMessage))
            {
                stateStore.SetNextAction(graceMessage, graceUntil);
                return;
            }
            else if (bypassQuietTiming)
            {
                stateStore.TryRespectManualComfortGrace(reading, true, quietBypassNow, out _, out _);
            }

            if (!bypassQuietTiming && stateStore.TryGetCooldown(quietBypassNow, out var cooldownUntil))
            {
                stateStore.SetNextAction($"Cooldown active after manual thermostat change; next correction after {cooldownUntil:yyyy-MM-dd HH:mm:ss}.", cooldownUntil);
                return;
            }
            else if (comfort.BypassCooldown)
            {
                stateStore.SetNextAction("Severe upstairs heat detected; bypassing cooldown for comfort.", quietBypassNow);
            }
            else if (coolerIntentBypass)
            {
                stateStore.SetNextAction("Cooler wall intent detected; bypassing quiet waits so comfort can catch up.", quietBypassNow);
            }

            if (stateStore.ShouldUseFanSaver(reading))
            {
                var fanMode = stateStore.GetFanSaverMode();
                await homeAssistantClient.SetFanModeAsync(reading.EntityId, fanMode, cancellationToken);
                stateStore.RecordCommand($"Home Assistant {reading.EntityId} fan set to {fanMode} for energy saver.");
            }

            var expectedSetPoint = stateStore.CalculateExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction);
            var changed = Math.Abs(reading.SetPointCelsius - expectedSetPoint) > 0.05;

            if (changed)
            {
                var now = DateTimeOffset.UtcNow;
                if (stateStore.TryRespectComfortEnvelope(reading, expectedSetPoint, bypassQuietTiming, now, out var envelopeUntil, out var envelopeMessage))
                {
                    stateStore.SetNextAction(envelopeMessage, envelopeUntil);
                    return;
                }

                if (stateStore.TryRespectRoomTrendGuard(reading, expectedSetPoint, bypassQuietTiming, now, out var trendUntil, out var trendMessage))
                {
                    stateStore.SetNextAction(trendMessage, trendUntil);
                    return;
                }

                if (stateStore.TryRespectThermalMomentumGuard(reading, expectedSetPoint, bypassQuietTiming, now, out var momentumUntil, out var momentumMessage))
                {
                    stateStore.SetNextAction(momentumMessage, momentumUntil);
                    return;
                }

                if (stateStore.TryRespectWeatherDriftGuard(reading, expectedSetPoint, bypassQuietTiming, now, out var weatherDriftUntil, out var weatherDriftMessage))
                {
                    stateStore.SetNextAction(weatherDriftMessage, weatherDriftUntil);
                    return;
                }

                if (stateStore.TryRespectSetpointEcho(reading, bypassQuietTiming, now, out var echoUntil, out var echoMessage))
                {
                    stateStore.SetNextAction(echoMessage, echoUntil);
                    return;
                }

                if (stateStore.TryRespectCoolingRunway(reading, expectedSetPoint, bypassQuietTiming, now, out var runwayUntil, out var runwayMessage))
                {
                    stateStore.SetNextAction(runwayMessage, runwayUntil);
                    return;
                }

                if (stateStore.TryRespectSensorRhythm(reading, expectedSetPoint, bypassQuietTiming, now, out var rhythmUntil, out var rhythmMessage))
                {
                    stateStore.SetNextAction(rhythmMessage, rhythmUntil);
                    return;
                }

                if (stateStore.TryDelayNaturalCorrection(reading, expectedSetPoint, bypassQuietTiming, now, out var waitUntil, out var waitMessage))
                {
                    stateStore.SetNextAction(waitMessage, waitUntil);
                    return;
                }

                if (stateStore.TryRespectNaturalChangePlanner(reading, expectedSetPoint, bypassQuietTiming, now, out var naturalChangeUntil, out var naturalChangeMessage))
                {
                    stateStore.SetNextAction(naturalChangeMessage, naturalChangeUntil);
                    return;
                }

                if (stateStore.TryRespectRoutineTiming(reading, expectedSetPoint, bypassQuietTiming, now, out var routineUntil, out var routineMessage))
                {
                    stateStore.SetNextAction(routineMessage, routineUntil);
                    return;
                }

                if (stateStore.TryRespectComfortBudget(reading, bypassQuietTiming, now, out var budgetUntil, out var budgetMessage))
                {
                    stateStore.SetNextAction(budgetMessage, budgetUntil);
                    return;
                }

                if (stateStore.TryRespectVisibilityGuard(reading, expectedSetPoint, bypassQuietTiming, now, out var visibilityUntil, out var visibilityMessage))
                {
                    stateStore.SetNextAction(visibilityMessage, visibilityUntil);
                    return;
                }

                if (stateStore.TryRespectNaturalCadence(reading, expectedSetPoint, bypassQuietTiming, now, out var cadenceUntil, out var cadenceMessage))
                {
                    stateStore.SetNextAction(cadenceMessage, cadenceUntil);
                    return;
                }

                var commandSetPoint = stateStore.CalculateNaturalCommandSetPoint(reading, expectedSetPoint, bypassQuietTiming);
                if (stateStore.TryRespectRepeatCommandGuard(reading, commandSetPoint, bypassQuietTiming, now, out var repeatUntil, out var repeatMessage))
                {
                    stateStore.SetNextAction(repeatMessage, repeatUntil);
                    return;
                }

                stateStore.SetNextAction($"Setting real thermostat to {commandSetPoint:0.0} C from the current-room-minus-1 C defender target.", now);
                await homeAssistantClient.SetCoolingAsync(reading.EntityId, commandSetPoint, cancellationToken);
                stateStore.RecordCommand($"Home Assistant {reading.EntityId} set to {commandSetPoint:0.0} C from current-room-minus-1 C target {expectedSetPoint:0.0} C.", commandSetPoint);
                return;
            }

            stateStore.RecordNaturalRecoverySettled();
            stateStore.SetNextAction($"No correction needed; next 24/7 check at {nextCheck:HH:mm:ss}.", nextCheck);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Defender cycle failed");
            stateStore.RecordHomeAssistantUnavailable($"Home Assistant error: {ex.Message}");
        }
    }

    public async Task ForceTargetAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        var target = stateStore.GetTargetTemperature();
        await homeAssistantClient.SetCoolingAsync(reading.EntityId, target, cancellationToken);
        stateStore.RecordCommand($"Home Assistant {reading.EntityId} set to exact target {target:0.0} C.", target);
        stateStore.SetNextAction("Exact target command sent; waiting for the next live reading.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public async Task ForceCoolingBoostAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        var expectedSetPoint = stateStore.CalculateExpectedSetPoint(reading.CurrentTemperatureCelsius, "idle");
        await homeAssistantClient.SetCoolingAsync(reading.EntityId, expectedSetPoint, cancellationToken);
        stateStore.RecordCommand($"Home Assistant {reading.EntityId} cooling boost set to {expectedSetPoint:0.0} C.", expectedSetPoint);
        stateStore.SetNextAction("Cooling boost command sent; waiting for the next live reading.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public async Task ForceFanModeAsync(string fanMode, CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        await homeAssistantClient.SetFanModeAsync(reading.EntityId, fanMode, cancellationToken);
        stateStore.RecordCommand($"Home Assistant {reading.EntityId} fan set to {fanMode}.");
    }

    public async Task TurnThermostatOffAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        await homeAssistantClient.SetHvacModeAsync(reading.EntityId, "off", cancellationToken);
        stateStore.RecordCommand($"Home Assistant {reading.EntityId} thermostat turned off while defender is paused.");
        stateStore.SetNextAction("Thermostat off command sent; defender is paused and will keep reading status.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public async Task ApplyEmergencyProtocolAsync(string protocol, CancellationToken cancellationToken)
    {
        var normalized = (protocol ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "too-cold":
                stateStore.ActivateEmergencyQuiet(
                    "Too cold",
                    TimeSpan.FromMinutes(30),
                    "Too cold emergency active; defender is paused and the real thermostat is being turned off.",
                    pauseDefender: true);
                await TurnThermostatOffAsync(cancellationToken);
                break;
            case "someone-upset":
                stateStore.ActivateEmergencyQuiet(
                    "Someone upset",
                    TimeSpan.FromMinutes(45),
                    "Someone-upset quiet mode active; defender is observing only and will not fight wall changes.",
                    pauseDefender: false);
                break;
            case "suspicion":
                stateStore.ActivateEmergencyQuiet(
                    "Suspicion quiet",
                    TimeSpan.FromMinutes(90),
                    "Suspicion quiet mode active; defender is standing down corrections and only reading the real thermostat.",
                    pauseDefender: false);
                break;
            default:
                throw new InvalidOperationException("Pick an emergency protocol first.");
        }
    }

    public async Task RefreshRealThermostatAsync(CancellationToken cancellationToken)
    {
        await RequireReadingAsync(cancellationToken);
    }

    private async Task<ThermostatReading?> RefreshReadingAsync(CancellationToken cancellationToken)
    {
        if (!homeAssistantClient.IsConfigured)
        {
            stateStore.RecordHomeAssistantUnavailable("Home Assistant token is not configured.");
            return null;
        }

        var weather = await homeAssistantClient.GetWeatherAsync(cancellationToken);
        stateStore.RecordWeatherReading(weather);

        var settings = stateStore.GetSettings();
        var upstairsSensors = await homeAssistantClient.GetUpstairsTemperatureSensorsAsync(settings.UpstairsTemperatureEntityIds, cancellationToken);
        var presence = await homeAssistantClient.GetPresenceAsync(settings.PresenceEntityIds, cancellationToken);
        stateStore.RecordComfortReadings(upstairsSensors, presence);

        var reading = await homeAssistantClient.GetDiningRoomClimateAsync(cancellationToken);
        if (reading is null)
        {
            stateStore.RecordHomeAssistantUnavailable("Dining room climate entity was not found.");
            return null;
        }

        stateStore.RecordHomeAssistantReading(reading);
        return reading;
    }

    private async Task<ThermostatReading> RequireReadingAsync(CancellationToken cancellationToken)
    {
        var reading = await RefreshReadingAsync(cancellationToken);
        if (reading is null)
        {
            throw new InvalidOperationException(stateStore.GetSnapshot().LastError ?? "Home Assistant is unavailable.");
        }

        return reading;
    }
}
