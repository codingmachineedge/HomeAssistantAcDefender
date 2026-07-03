using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class AcDefenderService
{
    private readonly DefenderStateStore stateStore;
    private readonly HomeAssistantClient homeAssistantClient;
    private readonly IOptionsMonitor<DefenderOptions> options;
    private readonly IOptionsMonitor<HomeAssistantOptions> homeAssistantOptions;
    private readonly ILogger<AcDefenderService> logger;

    public AcDefenderService(
        DefenderStateStore stateStore,
        HomeAssistantClient homeAssistantClient,
        IOptionsMonitor<DefenderOptions> options,
        IOptionsMonitor<HomeAssistantOptions> homeAssistantOptions,
        ILogger<AcDefenderService> logger)
    {
        this.stateStore = stateStore;
        this.homeAssistantClient = homeAssistantClient;
        this.options = options;
        this.homeAssistantOptions = homeAssistantOptions;
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
            var nextCheck = DateTimeOffset.UtcNow.AddSeconds(Math.Max(3, options.CurrentValue.PollIntervalSeconds));

            var reading = await RefreshReadingAsync(cancellationToken);
            if (reading is null)
            {
                return;
            }
            await AccumulateElectricityCostAsync(cancellationToken);
            await RefreshPeakPowerStatusIfDueAsync(cancellationToken);
            await RefreshFrontDoorKillSwitchIfDueAsync(cancellationToken);
            await RefreshLearningIfDueAsync(cancellationToken);

            await TryBackfillRuntimeFromHistoryAsync(reading, cancellationToken);

            // Rival Schedule Watch: announce AC-app schedule block boundaries (observation only).
            stateStore.ObserveRivalSchedule(DateTimeOffset.UtcNow);

            var frontDoorNow = DateTimeOffset.UtcNow;
            if (stateStore.TryRespectFrontDoorKillSwitch(reading, frontDoorNow, out var shouldTurnOff, out var frontDoorUntil, out var frontDoorMessage))
            {
                stateStore.SetNextAction(frontDoorMessage, frontDoorUntil);
                if (shouldTurnOff)
                {
                    await homeAssistantClient.SetHvacModeAsync(reading.EntityId, "off", cancellationToken);
                    stateStore.RecordFrontDoorThermostatOffCommand(reading.EntityId);
                }

                return;
            }

            // Peace offering runs BEFORE emergency quiet on purpose: when the rage detector fires
            // an auto-apology, the friendly "goes up a bit" gift from the same raise must still
            // land. The gift only ever raises the setpoint and only in cool mode.
            var peaceSnapshot = stateStore.GetSnapshot();
            if (peaceSnapshot.DefenderEnabled
                && stateStore.TryBeginPeaceOffering(reading, DateTimeOffset.UtcNow, out var peaceSetPoint, out var peaceUntil, out var peaceMessage))
            {
                if (peaceSetPoint is { } gift && string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
                {
                    await homeAssistantClient.SetCoolingAsync(reading.EntityId, gift, cancellationToken);
                }

                stateStore.SetNextAction(peaceMessage, peaceUntil);
                return;
            }

            var emergencyNow = DateTimeOffset.UtcNow;
            if (stateStore.TryRespectEmergencyQuiet(emergencyNow, out var emergencyUntil, out var emergencyMessage))
            {
                stateStore.SetNextAction(emergencyMessage, emergencyUntil);
                return;
            }

            var snapshot = stateStore.GetSnapshot();
            if (!snapshot.DefenderEnabled)
            {
                stateStore.SetNextAction("Defender paused; still reading the real thermostat 24/7.", nextCheck);
                return;
            }

            // On-forever protection runs BEFORE the night gates: the run clock must keep counting
            // through night passive watch, otherwise a warm night could hide 7+ hours of
            // continuous cooling from the limit. During a rest, ease up and wait.
            if (stateStore.TryBeginCoolingRest(reading, DateTimeOffset.UtcNow, out var restSetPoint, out var restUntil, out var restMessage))
            {
                if (restSetPoint is { } easeUp)
                {
                    await homeAssistantClient.SetCoolingAsync(reading.EntityId, easeUp, cancellationToken);
                }

                stateStore.SetNextAction(restMessage, restUntil);
                return;
            }

            // Night shutdown / night passive watch: cheap cool night = AC off; warm night =
            // observe only. Runs before the enforcer and cool-mode restore so nothing turns the
            // unit back on during the window.
            if (stateStore.TryBeginNightShutdown(reading, DateTimeOffset.UtcNow, out var nightUntil, out var nightMessage, out var nightTurnOff, out var nightEaseUp))
            {
                if (nightTurnOff)
                {
                    await homeAssistantClient.SetHvacModeAsync(reading.EntityId, "off", cancellationToken);
                }
                else if (nightEaseUp is { } budgetEase)
                {
                    // Night cooling budget spent: ease the setpoint up (leave the mode on cool) so
                    // the compressor stops without a jarring mode change.
                    await homeAssistantClient.SetCoolingAsync(reading.EntityId, budgetEase, cancellationToken);
                }

                stateStore.SetNextAction(nightMessage, nightUntil);
                return;
            }

            // Desired-State Enforcer: the assertive layer that makes the owner's chosen state win. When it
            // acts (or holds), it short-circuits the cycle; when Inactive it falls through to the stealth
            // pipeline unchanged.
            var enforcerNow = DateTimeOffset.UtcNow;
            var enforcerGate = stateStore.EvaluateEnforcer(reading, enforcerNow);
            switch (enforcerGate.Decision)
            {
                case EnforcerDecision.EnforceMode:
                case EnforcerDecision.EnforceSetpoint:
                    await homeAssistantClient.SetCoolingAsync(reading.EntityId, enforcerGate.AssertSetPoint, cancellationToken);
                    stateStore.RecordCommand(
                        enforcerGate.Message,
                        enforcerGate.AssertSetPoint,
                        commandedHvacMode: "cool",
                        commandSourceKind: "desired-state-enforcer",
                        commandSourceLabel: "Desired-State Enforcer",
                        commandSourceDetail: "The Desired-State Enforcer restored the owner's chosen AC state.");
                    await TrySendEnforcerNotificationAsync(enforcerGate, cancellationToken);
                    stateStore.SetNextAction(enforcerGate.Message, enforcerGate.Until);
                    return;
                case EnforcerDecision.Cooldown:
                case EnforcerDecision.Backoff:
                case EnforcerDecision.RespectOwner:
                    await TrySendEnforcerNotificationAsync(enforcerGate, cancellationToken);
                    stateStore.SetNextAction(enforcerGate.Message, enforcerGate.Until);
                    return;
                case EnforcerDecision.Inactive:
                default:
                    break;
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
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} mode restored to cool.",
                    commandedHvacMode: "cool",
                    commandSourceDetail: "AC Defender restored cool mode because the thermostat mode was changed away from cool.");
                return;
            }

            var rules = stateStore.ApplyScheduleAndWeatherRules(reading);
            var comfort = stateStore.ApplyComfortRules(reading);
            if (!rules.WeatherAllowsDefender && !comfort.Active)
            {
                stateStore.SetNextAction($"Weather rule '{rules.WeatherActivationMode}' is not met; checking again.", nextCheck);
                return;
            }

            var quietBypassNow = DateTimeOffset.UtcNow;
            var coolerIntentBypass = stateStore.ShouldBypassQuietTimingForCoolerIntent(reading, quietBypassNow);
            var superDefenderBypass = stateStore.ShouldBypassQuietTimingForSuperDefender(reading, quietBypassNow);
            var rivalScheduleBypass = stateStore.ShouldBypassQuietTimingForRivalSchedule(reading, quietBypassNow);
            var bypassQuietTiming = comfort.BypassCooldown || coolerIntentBypass || superDefenderBypass || rivalScheduleBypass;

            // Outdoor power rule: silence when it is cold outside (<20 C), lite mode between 20-22 C.
            if (stateStore.TryRespectOutdoorPowerRule(reading, bypassQuietTiming, quietBypassNow, out var outdoorUntil, out var outdoorMessage))
            {
                stateStore.SetNextAction(outdoorMessage, outdoorUntil);
                return;
            }

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
            else if (superDefenderBypass)
            {
                stateStore.SetNextAction("Super Defender detected repeated phone/Home Assistant changes; bypassing quiet waits while cooling is needed.", quietBypassNow);
            }
            else if (rivalScheduleBypass)
            {
                stateStore.SetNextAction("AC app schedule is holding the wall above my temp; bypassing quiet waits so the walk-back to my temp happens promptly.", quietBypassNow);
            }

            if (stateStore.ShouldUsePeakPowerFanSaver(reading))
            {
                var fanMode = stateStore.GetPeakPowerFanSaverMode();
                await homeAssistantClient.SetFanModeAsync(reading.EntityId, fanMode, cancellationToken);
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} fan set to {fanMode} for Alectra Peak Power Saver.",
                    commandedFanMode: fanMode,
                    commandSourceDetail: "AC Defender adjusted fan mode for Alectra Peak Power Saver.");
            }
            else if (stateStore.ShouldUseFanSaver(reading))
            {
                var fanMode = stateStore.GetFanSaverMode();
                await homeAssistantClient.SetFanModeAsync(reading.EntityId, fanMode, cancellationToken);
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} fan set to {fanMode} for energy saver.",
                    commandedFanMode: fanMode,
                    commandSourceDetail: "AC Defender adjusted fan mode for energy saver.");
            }

            var expectedSetPoint = stateStore.CalculateExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction, reading.SetPointCelsius);
            var changed = Math.Abs(reading.SetPointCelsius - expectedSetPoint) > 0.05;

            if (changed)
            {
                var now = DateTimeOffset.UtcNow;
                if (stateStore.TryRespectPeakPowerSaver(reading, expectedSetPoint, bypassQuietTiming, now, out var peakUntil, out var peakMessage))
                {
                    stateStore.SetNextAction(peakMessage, peakUntil);
                    return;
                }

                if (stateStore.TryRespectComfortEnvelope(reading, expectedSetPoint, bypassQuietTiming, now, out var envelopeUntil, out var envelopeMessage))
                {
                    stateStore.SetNextAction(envelopeMessage, envelopeUntil);
                    return;
                }

                if (stateStore.TryRespectTugOfWarTruce(reading, expectedSetPoint, bypassQuietTiming, now, out var truceUntil, out var truceMessage))
                {
                    stateStore.SetNextAction(truceMessage, truceUntil);
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

                if (stateStore.TryRespectSetpointStillness(reading, expectedSetPoint, bypassQuietTiming, now, out var stillnessUntil, out var stillnessMessage))
                {
                    stateStore.SetNextAction(stillnessMessage, stillnessUntil);
                    return;
                }

                if (stateStore.TryRespectRemoteSettlingGuard(reading, expectedSetPoint, bypassQuietTiming, now, out var remoteSettlingUntil, out var remoteSettlingMessage))
                {
                    stateStore.SetNextAction(remoteSettlingMessage, remoteSettlingUntil);
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

                if (stateStore.TryRespectHvacActionAlibi(reading, expectedSetPoint, bypassQuietTiming, now, out var alibiUntil, out var alibiMessage))
                {
                    stateStore.SetNextAction(alibiMessage, alibiUntil);
                    return;
                }

                if (stateStore.TryRespectTelemetryAlibi(reading, expectedSetPoint, bypassQuietTiming, now, out var telemetryUntil, out var telemetryMessage))
                {
                    stateStore.SetNextAction(telemetryMessage, telemetryUntil);
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

                if (stateStore.TryRespectCommandCamouflage(reading, expectedSetPoint, bypassQuietTiming, now, out var camouflageUntil, out var camouflageMessage))
                {
                    stateStore.SetNextAction(camouflageMessage, camouflageUntil);
                    return;
                }

                if (stateStore.TryRespectStealthGovernor(reading, expectedSetPoint, bypassQuietTiming, now, out var stealthUntil, out var stealthMessage))
                {
                    stateStore.SetNextAction(stealthMessage, stealthUntil);
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
                commandSetPoint = stateStore.CalculateHumanNudgeCommandSetPoint(reading, expectedSetPoint, commandSetPoint, bypassQuietTiming);
                if (stateStore.TryRespectRepeatCommandGuard(reading, commandSetPoint, bypassQuietTiming, now, out var repeatUntil, out var repeatMessage))
                {
                    stateStore.SetNextAction(repeatMessage, repeatUntil);
                    return;
                }

                stateStore.SetNextAction($"Setting real thermostat to {commandSetPoint:0.0} C from the current-room-minus-1 C defender target.", now);
                await homeAssistantClient.SetCoolingAsync(reading.EntityId, commandSetPoint, cancellationToken);
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} set to {commandSetPoint:0.0} C from current-room-minus-1 C target {expectedSetPoint:0.0} C.",
                    commandSetPoint,
                    commandedHvacMode: "cool",
                    commandSourceDetail: "AC Defender background service sent the current-room-minus-1 C correction.");
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

    private async Task TrySendEnforcerNotificationAsync(EnforcerGate gate, CancellationToken cancellationToken)
    {
        if (!gate.Notify || string.IsNullOrWhiteSpace(gate.NotifyMessage))
        {
            return;
        }

        try
        {
            await homeAssistantClient.SendNotificationAsync(
                homeAssistantOptions.CurrentValue.NotifyService,
                "AC Defender",
                gate.NotifyMessage,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Desired-State Enforcer notification failed");
        }
    }

    private async Task RefreshLearningIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (stateStore.ShouldRunHistoryLearning(now))
        {
            stateStore.ScheduleNextHistoryLearning(now);
            try
            {
                await LearnFromHistoryAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Automatic thermostat-history learning failed");
            }
        }
        else if (stateStore.ShouldTrainModels(now))
        {
            // Retrain the ML models from already-accumulated data without a fresh history fetch, so the
            // trained models stay current (and populate promptly after a restart) between history runs.
            stateStore.TrainLearningModels(now);
        }
    }

    // Sample the Alectra power sensor every cycle and accrue electricity cost at the current TOU rate.
    // Independent of the Peak Power Saver guard so cost keeps tracking even when the guard is off.
    private async Task AccumulateElectricityCostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var powerKilowatts = await homeAssistantClient.GetUsagePowerKilowattsAsync(cancellationToken);
            stateStore.RecordElectricityCostSample(powerKilowatts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Electricity cost sample failed");
        }
    }

    private async Task RefreshPeakPowerStatusIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!stateStore.ShouldRefreshPeakPowerSaver(now))
        {
            return;
        }

        try
        {
            stateStore.RecordAlectraPeakPowerReading(await homeAssistantClient.GetAlectraPeakPowerAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Alectra peak power status refresh failed");
            stateStore.RecordAlectraPeakPowerUnavailable(ex.Message);
        }
    }

    private async Task RefreshFrontDoorKillSwitchIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!stateStore.ShouldRefreshFrontDoorKillSwitch(now))
        {
            return;
        }

        try
        {
            var settings = stateStore.GetSettings();
            var readings = await homeAssistantClient.GetFrontDoorPersonDetectorsAsync(settings.FrontDoorPersonEntityIds, cancellationToken);
            stateStore.RecordFrontDoorPersonReadings(readings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Front-door kill switch refresh failed");
            stateStore.RecordFrontDoorKillSwitchUnavailable(ex.Message);
        }
    }

    public async Task ForceTargetAsync(CancellationToken cancellationToken)
    {
        // Even the explicit "force my temp" button never snaps the wall: it sends the stepper's
        // next 0.5 C move toward the target; repeated cycles finish the walk.
        var reading = await RequireReadingAsync(cancellationToken);
        var target = stateStore.GetTargetTemperature();
        var stepSetPoint = stateStore.CalculateExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction, reading.SetPointCelsius);
        await homeAssistantClient.SetCoolingAsync(reading.EntityId, stepSetPoint, cancellationToken);
        stateStore.RecordCommand(
            $"Home Assistant {reading.EntityId} stepped to {stepSetPoint:0.0} C toward the target {target:0.0} C.",
            stepSetPoint,
            commandedHvacMode: "cool",
            commandSourceKind: "website-command",
            commandSourceLabel: "Website control",
            commandSourceDetail: "Website Force target button sent one stepper move toward the target.");
        stateStore.SetNextAction("Step toward the target sent; the walk continues with the live readings.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public async Task ForceCoolingBoostAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        var expectedSetPoint = stateStore.CalculateExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction, reading.SetPointCelsius);
        await homeAssistantClient.SetCoolingAsync(reading.EntityId, expectedSetPoint, cancellationToken);
        stateStore.RecordCommand(
            $"Home Assistant {reading.EntityId} cooling boost set to {expectedSetPoint:0.0} C.",
            expectedSetPoint,
            commandedHvacMode: "cool",
            commandSourceKind: "website-command",
            commandSourceLabel: "Website control",
            commandSourceDetail: "Website Force cooling button sent this Home Assistant command.");
        stateStore.SetNextAction("Cooling boost command sent; waiting for the next live reading.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public async Task ForceFanModeAsync(string fanMode, CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        await homeAssistantClient.SetFanModeAsync(reading.EntityId, fanMode, cancellationToken);
        stateStore.RecordCommand(
            $"Home Assistant {reading.EntityId} fan set to {fanMode}.",
            commandedFanMode: fanMode,
            commandSourceKind: "website-command",
            commandSourceLabel: "Website control",
            commandSourceDetail: "Website fan-mode button sent this Home Assistant command.");
    }

    public async Task TurnThermostatOffAsync(
        CancellationToken cancellationToken,
        string commandSourceKind = "website-command",
        string commandSourceLabel = "Website control",
        string commandSourceDetail = "Website thermostat-off button sent this Home Assistant command.")
    {
        var reading = await RequireReadingAsync(cancellationToken);
        await homeAssistantClient.SetHvacModeAsync(reading.EntityId, "off", cancellationToken);
        stateStore.RecordCommand(
            $"Home Assistant {reading.EntityId} thermostat turned off while defender is paused.",
            commandedHvacMode: "off",
            commandSourceKind: commandSourceKind,
            commandSourceLabel: commandSourceLabel,
            commandSourceDetail: commandSourceDetail);
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
            case "brother-mad":
                // The apology protocol. "Brother upset" contains "upset" so the anger learning
                // model records this hour as a sensitive one and grows more hands-off over time.
                stateStore.ActivateEmergencyQuiet(
                    "Brother upset",
                    TimeSpan.FromHours(2),
                    "🙇 BROTHER-MAD APOLOGY ACTIVE: the defenders are bowing deeply, all corrections are stopped for 2 hours, and the AC was eased up as a peace gesture.",
                    pauseDefender: false);
                var apologyReading = await RequireReadingAsync(cancellationToken);
                if (string.Equals(apologyReading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
                {
                    var relief = Math.Round(Math.Min(apologyReading.SetPointCelsius + 1.0, 30.0), 1);
                    if (relief > apologyReading.SetPointCelsius + 0.05)
                    {
                        await homeAssistantClient.SetCoolingAsync(apologyReading.EntityId, relief, cancellationToken);
                        stateStore.RecordCommand(
                            $"Brother-mad apology: setpoint eased up to {relief:0.0} C as an immediate peace gesture.",
                            relief,
                            commandedHvacMode: "cool",
                            commandSourceKind: "brother-mad-apology",
                            commandSourceLabel: "Brother-mad apology",
                            commandSourceDetail: "The BROTHER MAD emergency button eased the AC up 1.0 C so the apology is felt immediately.");
                    }
                }

                break;
            default:
                throw new InvalidOperationException("Pick an emergency protocol first.");
        }
    }

    public async Task RefreshRealThermostatAsync(CancellationToken cancellationToken)
    {
        await RequireReadingAsync(cancellationToken);
    }

    /// <summary>
    /// Pulls the real Home Assistant thermostat history and learns a human comfort profile + touch
    /// cadence from it (see <see cref="DefenderStateStore.LearnFromThermostatHistory"/>).
    /// </summary>
    /// <summary>
    /// Called right after the defender is turned OFF: parks the thermostat at the configured
    /// stand-down setpoint (default 28 C) when appropriate, so the unguarded AC barely runs.
    /// Returns a human message when a park command was sent, null when nothing was appropriate.
    /// </summary>
    public async Task<string?> ParkThermostatForStandDownAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        if (!stateStore.TryGetStandDownPark(reading, out var parkSetPoint))
        {
            return null;
        }

        await homeAssistantClient.SetCoolingAsync(reading.EntityId, parkSetPoint, cancellationToken);
        stateStore.RecordCommand(
            $"Defender stood down; thermostat parked at {parkSetPoint:0.0} C so the AC barely runs unguarded.",
            parkSetPoint,
            commandedHvacMode: "cool",
            commandSourceKind: "stand-down-park",
            commandSourceLabel: "Stand-down parking",
            commandSourceDetail: "The defender was turned off; the setpoint was raised to the stand-down park value to save power while unguarded.");
        return $"Thermostat parked at {parkSetPoint:0.0} °C while the defender is off.";
    }

    private DateTimeOffset _lastRuntimeBackfillAttemptAt = DateTimeOffset.MinValue;

    // One-time: seed the runtime counters from the recorder archive (past logs), retried at most
    // every 10 minutes if Home Assistant is unreachable.
    private async Task TryBackfillRuntimeFromHistoryAsync(ThermostatReading reading, CancellationToken cancellationToken)
    {
        if (!stateStore.NeedsAcRuntimeBackfill
            || DateTimeOffset.UtcNow - _lastRuntimeBackfillAttemptAt < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastRuntimeBackfillAttemptAt = DateTimeOffset.UtcNow;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var samples = await homeAssistantClient.GetClimateHistoryAsync(reading.EntityId, to.AddDays(-31), to, cancellationToken);
            stateStore.BackfillAcRuntimeFromHistory(samples, to);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Runtime history backfill failed; will retry.");
        }
    }

    public async Task<HistoryLearningSnapshot> LearnFromHistoryAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        var settings = stateStore.GetSettings();
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-Math.Max(1, settings.HistoryLearningDays));
        var samples = await homeAssistantClient.GetClimateHistoryAsync(reading.EntityId, from, to, cancellationToken);
        return stateStore.LearnFromThermostatHistory(samples, DateTimeOffset.UtcNow);
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

        // Adjustment-statistics context: is the tracked person home, is the master bedroom occupied.
        var trackedContext = await homeAssistantClient.GetTrackedContextAsync(cancellationToken);
        stateStore.RecordTrackedContext(trackedContext);

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
