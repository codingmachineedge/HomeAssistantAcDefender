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

            if (!string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
            {
                stateStore.SetNextAction("Thermostat mode changed away from cool; restoring cool mode.", DateTimeOffset.UtcNow);
                await homeAssistantClient.SetHvacModeAsync(reading.EntityId, "cool", cancellationToken);
                stateStore.RecordCommand($"Home Assistant {reading.EntityId} mode restored to cool.");
                return;
            }

            if (!snapshot.DefenderEnabled)
            {
                stateStore.SetNextAction("Defender paused; still checking thermostat 24/7.", nextCheck);
                return;
            }

            var rules = stateStore.ApplyScheduleAndWeatherRules(reading);
            var comfort = stateStore.ApplyComfortRules();
            if (!rules.WeatherAllowsDefender && !comfort.Active)
            {
                stateStore.SetNextAction($"Weather rule '{rules.WeatherActivationMode}' is not met; checking again.", nextCheck);
                return;
            }

            if (!comfort.BypassCooldown
                && stateStore.TryRespectConflictQuietMode(reading, false, DateTimeOffset.UtcNow, out var conflictUntil, out var conflictMessage))
            {
                stateStore.SetNextAction(conflictMessage, conflictUntil);
                return;
            }
            else if (comfort.BypassCooldown)
            {
                stateStore.TryRespectConflictQuietMode(reading, true, DateTimeOffset.UtcNow, out _, out _);
            }

            if (!comfort.BypassCooldown
                && stateStore.TryRespectManualComfortGrace(reading, false, DateTimeOffset.UtcNow, out var graceUntil, out var graceMessage))
            {
                stateStore.SetNextAction(graceMessage, graceUntil);
                return;
            }
            else if (comfort.BypassCooldown)
            {
                stateStore.TryRespectManualComfortGrace(reading, true, DateTimeOffset.UtcNow, out _, out _);
            }

            if (!comfort.BypassCooldown && stateStore.TryGetCooldown(DateTimeOffset.UtcNow, out var cooldownUntil))
            {
                stateStore.SetNextAction($"Cooldown active after manual thermostat change; next correction after {cooldownUntil:yyyy-MM-dd HH:mm:ss}.", cooldownUntil);
                return;
            }
            else if (comfort.BypassCooldown)
            {
                stateStore.SetNextAction("Severe upstairs heat detected; bypassing cooldown for comfort.", DateTimeOffset.UtcNow);
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
                if (stateStore.TryRespectRoomTrendGuard(reading, expectedSetPoint, comfort.BypassCooldown, now, out var trendUntil, out var trendMessage))
                {
                    stateStore.SetNextAction(trendMessage, trendUntil);
                    return;
                }

                if (stateStore.TryRespectThermalMomentumGuard(reading, expectedSetPoint, comfort.BypassCooldown, now, out var momentumUntil, out var momentumMessage))
                {
                    stateStore.SetNextAction(momentumMessage, momentumUntil);
                    return;
                }

                if (stateStore.TryDelayNaturalCorrection(reading, expectedSetPoint, comfort.BypassCooldown, now, out var waitUntil, out var waitMessage))
                {
                    stateStore.SetNextAction(waitMessage, waitUntil);
                    return;
                }

                var commandSetPoint = stateStore.CalculateNaturalCommandSetPoint(reading, expectedSetPoint, comfort.BypassCooldown);
                stateStore.SetNextAction($"Setting real thermostat to {commandSetPoint:0.0} C from the room-temperature defender target.", now);
                await homeAssistantClient.SetCoolingAsync(reading.EntityId, commandSetPoint, cancellationToken);
                stateStore.RecordCommand($"Home Assistant {reading.EntityId} set to {commandSetPoint:0.0} C from room-temperature target {expectedSetPoint:0.0} C.", commandSetPoint);
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
