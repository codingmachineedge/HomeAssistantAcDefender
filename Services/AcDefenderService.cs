using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class AcDefenderService
{
    private readonly DefenderStateStore stateStore;
    private readonly HomeAssistantClient homeAssistantClient;
    private readonly IOptionsMonitor<DefenderOptions> options;
    private readonly IOptionsMonitor<HomeAssistantOptions> homeAssistantOptions;
    private readonly ILogger<AcDefenderService> logger;
    private readonly OpenMeteoWeatherClient? openMeteoWeatherClient;
    private readonly IHostApplicationLifetime? applicationLifetime;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object backgroundActuatorCancellationGate = new();
    private readonly object explicitOperationCancellationGate = new();
    private readonly AsyncLocal<ActuatorOperationTicket?> currentExplicitOperationTicket = new();
    private CancellationTokenSource? activeBackgroundActuatorCancellation;
    private ThermostatOperationPriority activeBackgroundActuatorPriority;
    private CancellationTokenSource? activeExplicitOperationCancellation;
    private ThermostatOperationPriority activeExplicitOperationPriority;
    private long explicitOperationGeneration;
    private long nextActuatorOperationId;
    private long latestNormalActuatorOperationId;
    private long latestStandDownActuatorOperationId;
    private long latestStandDownReversalOperationId;
    private long latestSafetyOffOperationId;
    private DateTimeOffset nextOpenMeteoCoordinateWarningAt;
    private DateTimeOffset nextOpenMeteoIncompleteCoordinateWarningAt;

    public AcDefenderService(
        DefenderStateStore stateStore,
        HomeAssistantClient homeAssistantClient,
        IOptionsMonitor<DefenderOptions> options,
        IOptionsMonitor<HomeAssistantOptions> homeAssistantOptions,
        ILogger<AcDefenderService> logger,
        OpenMeteoWeatherClient? openMeteoWeatherClient = null,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        this.stateStore = stateStore;
        this.homeAssistantClient = homeAssistantClient;
        this.options = options;
        this.homeAssistantOptions = homeAssistantOptions;
        this.logger = logger;
        this.openMeteoWeatherClient = openMeteoWeatherClient;
        this.applicationLifetime = applicationLifetime;
    }

    /// <summary>
    /// The 24/7 decision cycle. Reads real Home Assistant data, then walks the defender guards in order
    /// (emergency → deliberate shutdowns → schedule/weather → upstairs/outdoor comfort →
    /// cool-mode restore → wall-touch holds →
    /// expected setpoint → sensor-timing holds → command shaping → send). The first guard that wants to
    /// wait stops the cycle and records the next action. See <c>Guards/GuardCatalog.cs</c> and
    /// <c>docs/wiki/Defender-Logic.md</c> for the full per-algorithm reference.
    /// </summary>
    public Task RunCycleAsync(CancellationToken cancellationToken) =>
        RunCycleCoreAsync(cancellationToken);

    private async Task RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nextCheck = DateTimeOffset.UtcNow.AddSeconds(Math.Max(3, options.CurrentValue.PollIntervalSeconds));
            var observedExplicitGeneration = Volatile.Read(ref explicitOperationGeneration);

            ThermostatReading? reading;
            await operationGate.WaitAsync(cancellationToken);
            try
            {
                if (observedExplicitGeneration != Volatile.Read(ref explicitOperationGeneration))
                {
                    stateStore.SetNextAction("A direct control operation arrived; re-reading Home Assistant before any background work.", nextCheck);
                    return;
                }

                reading = null;
                await RunBackgroundActuatorAsync(
                    observedExplicitGeneration,
                    ThermostatOperationPriority.NormalActuator,
                    async token => reading = await RefreshClimateReadingAsync(token),
                    cancellationToken);
            }
            finally
            {
                operationGate.Release();
            }

            if (reading is null)
            {
                return;
            }
            await RefreshAncillaryReadingsAsync(cancellationToken);
            await AccumulateElectricityCostAsync(cancellationToken);
            await RefreshForecastIfDueAsync(cancellationToken);
            await RefreshPeakPowerStatusIfDueAsync(cancellationToken);
            await RefreshFrontDoorKillSwitchIfDueAsync(cancellationToken);
            await RefreshLearningIfDueAsync(cancellationToken);

            await TryBackfillRuntimeFromHistoryAsync(reading, cancellationToken);

            // Observation and history reads happen outside the actuator gate so an emergency OFF
            // never queues behind weather, telemetry, or recorder work. If an explicit control did
            // run while this cycle was observing, discard this now-stale reading and refresh on the
            // next poll instead of visibly answering the human command.
            await operationGate.WaitAsync(cancellationToken);
            try
            {
                if (observedExplicitGeneration != Volatile.Read(ref explicitOperationGeneration))
                {
                    stateStore.SetNextAction("A direct control operation completed; re-reading Home Assistant before any background command.", nextCheck);
                    return;
                }

                Task ActuateAsync(
                    Func<CancellationToken, Task> operation,
                    ThermostatOperationPriority priority = ThermostatOperationPriority.NormalActuator) =>
                    RunBackgroundActuatorAsync(observedExplicitGeneration, priority, operation, cancellationToken);

            // Rival Schedule Watch: announce AC-app schedule block boundaries (observation only).
            stateStore.ObserveRivalSchedule(DateTimeOffset.UtcNow);

            var hasUnconfirmedIntent = stateStore.TryRespectInFlightThermostatCommand(
                DateTimeOffset.UtcNow,
                out var intentUntil,
                out var intentMessage);
            var directRoomCoolingNeeded = stateStore.NeedsDirectCooling(reading);

            var frontDoorNow = DateTimeOffset.UtcNow;
            if (stateStore.TryRespectFrontDoorKillSwitch(reading, frontDoorNow, out var shouldTurnOff, out var frontDoorUntil, out var frontDoorMessage))
            {
                stateStore.SetNextAction(frontDoorMessage, frontDoorUntil);
                if (shouldTurnOff)
                {
                    await ActuateAsync(
                        token => SetHvacModeWithIntentAsync(
                            reading.EntityId,
                            "off",
                            token,
                            bypassRejectedCommandBackoff: true),
                        ThermostatOperationPriority.SafetyOff);
                    stateStore.RecordFrontDoorThermostatOffCommand(reading.EntityId);
                }

                return;
            }

            // Peace offering runs BEFORE emergency quiet on purpose: when the rage detector fires
            // an auto-apology, the friendly "goes up a bit" gift from the same raise must still
            // land. The gift only ever raises the setpoint and only in cool mode.
            var peaceSnapshot = stateStore.GetSnapshot();
            if (!hasUnconfirmedIntent
                && peaceSnapshot.DefenderEnabled
                && stateStore.TryBeginPeaceOffering(reading, DateTimeOffset.UtcNow, out var peaceSetPoint, out var peaceUntil, out var peaceMessage))
            {
                if (peaceSetPoint is { } gift && string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
                {
                    await ActuateAsync(token => SetTemperatureWithIntentAsync(reading.EntityId, gift, token));
                    peaceMessage = stateStore.RecordPeaceOfferingCommand(
                        gift,
                        peaceUntil ?? DateTimeOffset.UtcNow);
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

            // Siesta (mess hall): a nap of an ENABLED defender. Front-door kill, peace offering,
            // and emergency quiet outrank it (they run earlier); it short-circuits the cycle
            // itself instead of toggling the master switch, so "keep reading HA 24/7" stays
            // trivially true and there is no auto-resume flag to owe. Wake-on-hot-room is
            // checked inside every cycle — comfort and safety always win.
            if (stateStore.TryRespectSiesta(reading, DateTimeOffset.UtcNow, out var siestaUntil, out var siestaMessage))
            {
                stateStore.SetNextAction(siestaMessage, siestaUntil ?? nextCheck);
                return;
            }

            // On-forever protection runs BEFORE the night gates: the run clock must keep counting
            // through night passive watch, otherwise a warm night could hide 7+ hours of
            // continuous cooling from the limit. During a rest, ease up and wait.
            if (!hasUnconfirmedIntent
                && stateStore.TryBeginCoolingRest(reading, DateTimeOffset.UtcNow, out var restSetPoint, out var restUntil, out var restMessage))
            {
                if (restSetPoint is { } easeUp)
                {
                    await ActuateAsync(token => SetTemperatureWithIntentAsync(reading.EntityId, easeUp, token));
                    stateStore.RecordCoolingRestSetPointCommand(easeUp);
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
                    await ActuateAsync(
                        token => SetHvacModeWithIntentAsync(reading.EntityId, "off", token),
                        ThermostatOperationPriority.SafetyOff);
                    stateStore.RecordNightShutdownOffCommand(reading.EntityId, nightUntil ?? DateTimeOffset.UtcNow);
                }
                else if (nightEaseUp is { } budgetEase)
                {
                    // Night cooling budget spent: ease the setpoint up (leave the mode on cool) so
                    // the compressor stops without a jarring mode change.
                    await ActuateAsync(token => SetTemperatureWithIntentAsync(reading.EntityId, budgetEase, token));
                    stateStore.RecordNightBudgetEaseCommand(reading.EntityId, budgetEase);
                }

                stateStore.SetNextAction(nightMessage, nightUntil);
                return;
            }

            // Cool-Outdoor Shutdown: genuinely cool outside + forecast staying cool = AC fully
            // off; auto-restores by weather or room comfort. Runs after night shutdown (which
            // owns the night window — no double-off) and before the enforcer and the cool-mode
            // restore so nothing turns the unit back on while this hold is deliberate.
            if (stateStore.TryBeginCoolOutdoorShutdown(reading, DateTimeOffset.UtcNow, out var coolOutUntil, out var coolOutMessage, out var coolOutOff, out var coolOutRestore, out var coolOutSetPoint))
            {
                if (coolOutOff)
                {
                    await ActuateAsync(
                        token => SetHvacModeWithIntentAsync(reading.EntityId, "off", token),
                        ThermostatOperationPriority.SafetyOff);
                    stateStore.RecordCoolOutdoorOffCommand(reading.EntityId);
                }

                stateStore.SetNextAction(coolOutMessage, coolOutUntil);
                return;
            }
            else if (coolOutRestore)
            {
                if (coolOutSetPoint is { } restoreTo)
                {
                    await ActuateAsync(token => SetCoolingWithTrackedPreparationAsync(
                        reading.EntityId,
                        restoreTo,
                        "cool-outdoor-restore",
                        "Cool-Outdoor Shutdown",
                        "Preparing the safe comfort setpoint before restoring cool after a cool-outdoor shutdown.",
                        token,
                        bypassRejectedCommandBackoff: true));
                }
                else
                {
                    await ActuateAsync(token => SetHvacModeWithIntentAsync(reading.EntityId, "cool", token));
                }

                var coolOutdoorRestoreMessage = coolOutSetPoint is { } restoredSetPoint
                    ? $"Home Assistant {reading.EntityId} cooling restored at {restoredSetPoint:0.0} C after the cool-outdoor shutdown."
                    : $"Home Assistant {reading.EntityId} cool mode restored after the cool-outdoor shutdown.";
                if (coolOutSetPoint is { } completedSetPoint)
                {
                    stateStore.RecordCoolingTransitionCompleted(
                        coolOutdoorRestoreMessage,
                        completedSetPoint,
                        "cool-outdoor-restore",
                        "Cool-Outdoor Shutdown",
                        "Outdoor warmth or direct room comfort ended the cool-outdoor shutdown; AC Defender restored cooling.");
                }
                else
                {
                    stateStore.RecordCommand(
                        coolOutdoorRestoreMessage,
                        commandedHvacMode: "cool",
                        commandSourceKind: "cool-outdoor-restore",
                        commandSourceLabel: "Cool-Outdoor Shutdown",
                        commandSourceDetail: "Outdoor warmth or direct room comfort ended the cool-outdoor shutdown; AC Defender restored cooling.");
                }
                stateStore.RecordCoolOutdoorRestoreCommand(reading.EntityId);
                stateStore.RecordCoolModeRestoreCommand(reading.HvacMode);
                stateStore.SetNextAction(coolOutMessage, coolOutUntil);
                return;
            }

            // Cooling-Failure Shutdown: while a MEGA/OMEGA cooling-failure alert is up, turn the AC
            // fully off (a failing unit is not cooling anyway) and hold it off until the room warms by
            // the release margin (0.5 C), then restore cool. Runs before the enforcer and cool-mode
            // restore so nothing turns the unit back on while the hold is deliberate.
            if (stateStore.TryRespectCoolingFailureShutdown(reading, DateTimeOffset.UtcNow, out var coolFailUntil, out var coolFailMessage, out var coolFailOff, out var coolFailRestore, out var coolFailSetPoint))
            {
                if (coolFailOff)
                {
                    await ActuateAsync(
                        token => SetHvacModeWithIntentAsync(
                            reading.EntityId,
                            "off",
                            token,
                            bypassRejectedCommandBackoff: true),
                        ThermostatOperationPriority.SafetyOff);
                    stateStore.RecordCoolingFailureShutdownOffCommand(reading.EntityId);
                }

                stateStore.SetNextAction(coolFailMessage, coolFailUntil);
                return;
            }
            else if (coolFailRestore)
            {
                if (coolFailSetPoint is { } restoreTo)
                {
                    await ActuateAsync(token => SetCoolingWithTrackedPreparationAsync(
                        reading.EntityId,
                        restoreTo,
                        "cooling-failure-restore",
                        "Cooling-Failure Shutdown",
                        "Preparing the setpoint before restoring cool after the confirmed cooling-failure release.",
                        token));
                }
                else
                {
                    await ActuateAsync(token => SetHvacModeWithIntentAsync(reading.EntityId, "cool", token));
                }

                var coolingFailureRestoreMessage = coolFailSetPoint is { } restoredSetPoint
                    ? $"Home Assistant {reading.EntityId} cooling restored at {restoredSetPoint:0.0} C after the cooling-failure shutdown."
                    : $"Home Assistant {reading.EntityId} cool mode restored after the cooling-failure shutdown.";
                if (coolFailSetPoint is { } completedSetPoint)
                {
                    stateStore.RecordCoolingTransitionCompleted(
                        coolingFailureRestoreMessage,
                        completedSetPoint,
                        "cooling-failure-restore",
                        "Cooling-Failure Shutdown",
                        "The room warmed by the confirmed release margin after a cooling-failure shutdown; AC Defender restored cooling.");
                }
                else
                {
                    stateStore.RecordCommand(
                        coolingFailureRestoreMessage,
                        commandedHvacMode: "cool",
                        commandSourceKind: "cooling-failure-restore",
                        commandSourceLabel: "Cooling-Failure Shutdown",
                        commandSourceDetail: "The room warmed by the confirmed release margin after a cooling-failure shutdown; AC Defender restored cooling.");
                }
                stateStore.RecordCoolingFailureRestoreCommand(reading.EntityId);
                stateStore.RecordCoolModeRestoreCommand(reading.HvacMode);
                stateStore.SetNextAction(coolFailMessage, coolFailUntil);
                return;
            }

            if (hasUnconfirmedIntent && !directRoomCoolingNeeded)
            {
                stateStore.SetNextAction(intentMessage, intentUntil);
                return;
            }

            // Resolve target/comfort context and the outdoor power rule BEFORE either the
            // Desired-State Enforcer or ordinary cool-mode restore can send a command. When the
            // thermostat is already off and it is cold enough outside to stand down, checking the
            // rule later would restore COOL now and only silence the defender on the next five-second
            // poll. That creates a real, brief AC activation even though this cycle already has the
            // outdoor reading needed to avoid it.
            var rules = stateStore.ApplyScheduleAndWeatherRules(reading);
            var comfort = stateStore.ApplyComfortRules(reading);
            var directCoolingNeeded = directRoomCoolingNeeded || comfort.BypassCooldown;
            var quietBypassNow = DateTimeOffset.UtcNow;
            var coolerIntentBypass = stateStore.ShouldBypassQuietTimingForCoolerIntent(reading, quietBypassNow);
            var superDefenderBypass = stateStore.ShouldBypassQuietTimingForSuperDefender(reading, quietBypassNow);
            var rivalScheduleBypass = stateStore.ShouldBypassQuietTimingForRivalSchedule(reading, quietBypassNow);
            var bypassQuietTiming = comfort.BypassCooldown || coolerIntentBypass || superDefenderBypass || rivalScheduleBypass;

            // All-day outdoor power rule: stand down below 23 C; use lite mode from 23-25 C.
            if (stateStore.TryRespectOutdoorPowerRule(reading, bypassQuietTiming, quietBypassNow, out var outdoorUntil, out var outdoorMessage))
            {
                stateStore.SetNextAction(outdoorMessage, outdoorUntil);
                return;
            }

            // Every automatic path that can restore COOL, including Desired-State Enforcer,
            // shares the compressor anti-short-cycle dwell. Direct room-comfort safety remains
            // the only automatic bypass and is decided inside this store guard.
            if (!string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase)
                && stateStore.TryDelayCoolModeRestore(reading, quietBypassNow, out var restoreAt, out var restoreMessage))
            {
                stateStore.SetNextAction(restoreMessage, restoreAt);
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
                    await ActuateAsync(token => SetCoolingWithTrackedPreparationAsync(
                        reading.EntityId,
                        enforcerGate.AssertSetPoint,
                        "desired-state-enforcer",
                        "Desired-State Enforcer",
                        "Preparing the owner's chosen setpoint before restoring cool mode.",
                        token,
                        bypassRejectedCommandBackoff: directCoolingNeeded));
                    stateStore.RecordCoolingTransitionCompleted(
                        enforcerGate.Message,
                        enforcerGate.AssertSetPoint,
                        "desired-state-enforcer",
                        "Desired-State Enforcer",
                        "The Desired-State Enforcer restored the owner's chosen AC state.");
                    stateStore.RecordEnforcerCommandAccepted();
                    _ = TrySendEnforcerNotificationAsync(enforcerGate, cancellationToken);
                    stateStore.SetNextAction(enforcerGate.Message, enforcerGate.Until);
                    return;
                case EnforcerDecision.EnforceSetpoint:
                    await ActuateAsync(token => SetTemperatureWithIntentAsync(reading.EntityId, enforcerGate.AssertSetPoint, token));
                    stateStore.RecordCommand(
                        enforcerGate.Message,
                        enforcerGate.AssertSetPoint,
                        commandSourceKind: "desired-state-enforcer",
                        commandSourceLabel: "Desired-State Enforcer",
                        commandSourceDetail: "The Desired-State Enforcer restored the owner's chosen AC state.");
                    stateStore.RecordEnforcerCommandAccepted();
                    _ = TrySendEnforcerNotificationAsync(enforcerGate, cancellationToken);
                    stateStore.SetNextAction(enforcerGate.Message, enforcerGate.Until);
                    return;
                case EnforcerDecision.Cooldown:
                case EnforcerDecision.Backoff:
                case EnforcerDecision.RespectOwner:
                    _ = TrySendEnforcerNotificationAsync(enforcerGate, cancellationToken);
                    stateStore.SetNextAction(enforcerGate.Message, enforcerGate.Until);
                    return;
                case EnforcerDecision.Inactive:
                default:
                    break;
            }

            if (!string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTimeOffset.UtcNow;
                stateStore.SetNextAction("Cool mode restore delay finished; restoring cool mode now.", now);
                await ActuateAsync(token => SetHvacModeWithIntentAsync(
                    reading.EntityId,
                    "cool",
                    token,
                    bypassRejectedCommandBackoff: directCoolingNeeded));
                stateStore.RecordCoolModeRestoreCommand(reading.HvacMode);
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} mode restored to cool.",
                    commandedHvacMode: "cool",
                    commandSourceDetail: "AC Defender restored cool mode because the thermostat mode was changed away from cool.");
                return;
            }

            if (!rules.WeatherAllowsDefender && !comfort.Active)
            {
                stateStore.SetNextAction($"Weather rule '{rules.WeatherActivationMode}' is not met; checking again.", nextCheck);
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
                await ActuateAsync(token => SetFanModeWithIntentAsync(reading.EntityId, fanMode, token));
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} fan set to {fanMode} for Alectra Peak Power Saver.",
                    commandedFanMode: fanMode,
                    commandSourceDetail: "AC Defender adjusted fan mode for Alectra Peak Power Saver.");
                stateStore.SetNextAction("Peak-power fan command sent; waiting for the next real thermostat reading before any other actuator change.", nextCheck);
                return;
            }
            else if (stateStore.ShouldUseFanSaver(reading))
            {
                var fanMode = stateStore.GetFanSaverMode();
                await ActuateAsync(token => SetFanModeWithIntentAsync(reading.EntityId, fanMode, token));
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} fan set to {fanMode} for energy saver.",
                    commandedFanMode: fanMode,
                    commandSourceDetail: "AC Defender adjusted fan mode for energy saver.");
                stateStore.SetNextAction("Fan-saver command sent; waiting for the next real thermostat reading before any other actuator change.", nextCheck);
                return;
            }

            var coolingPlan = stateStore.PlanExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction, reading.SetPointCelsius);
            var expectedSetPoint = coolingPlan.ExpectedSetPointCelsius;
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
                await ActuateAsync(token => SetTemperatureWithIntentAsync(
                    reading.EntityId,
                    commandSetPoint,
                    token,
                    bypassRejectedCommandBackoff: directCoolingNeeded));
                stateStore.RecordCommand(
                    $"Home Assistant {reading.EntityId} set to {commandSetPoint:0.0} C from current-room-minus-1 C target {expectedSetPoint:0.0} C.",
                    commandSetPoint,
                    commandSourceDetail: "AC Defender background service sent the current-room-minus-1 C correction.");
                stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: true);
                return;
            }

            stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: false);
            stateStore.RecordNaturalRecoverySettled();
            stateStore.SetNextAction($"No correction needed; next 24/7 check at {nextCheck:HH:mm:ss}.", nextCheck);
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (BackgroundCommandSupersededException)
        {
            stateStore.SetNextAction(
                "A newer direct control superseded the background actuator request; re-reading Home Assistant before any retry.",
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(3, options.CurrentValue.PollIntervalSeconds)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ThermostatCommandRetryDeferredException ex)
        {
            stateStore.SetNextAction(ex.Message, ex.Until);
        }
        catch (ThermostatCommandRejectedException ex)
        {
            logger.LogWarning(ex, "Home Assistant rejected a defender thermostat command");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            logger.LogWarning(ex, "Home Assistant rejected a defender climate read");
            stateStore.RecordHomeAssistantRequestRejected(
                $"Home Assistant rejected the climate read ({(int)ex.StatusCode.Value}): {ex.Message}");
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
            // Notifications are deliberately detached from the actuator gate so a slow notify
            // integration cannot delay an emergency OFF after the thermostat command is complete.
            await Task.Yield();
            await homeAssistantClient.SendNotificationAsync(
                homeAssistantOptions.CurrentValue.NotifyService,
                "AC Defender",
                gate.NotifyMessage,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Desired-State Enforcer notification timed out");
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
                await LearnFromHistoryCoreAsync(cancellationToken);
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

    // Weather forecast refresh: throttled to ForecastRefreshMinutes (5-min backoff on failure).
    // Runs AFTER RefreshReadingAsync so Weather.EntityId exists on first boot. A sensor-only
    // outdoor source has no forecast service — recorded as unavailable, never treated as an error.
    private async Task RefreshForecastIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!stateStore.ShouldRefreshForecast(now))
        {
            return;
        }

        var entityId = stateStore.GetSnapshot().Weather?.EntityId;
        try
        {
            if (!string.IsNullOrWhiteSpace(entityId)
                && entityId.StartsWith("weather.", StringComparison.OrdinalIgnoreCase))
            {
                var homeAssistantForecast = await homeAssistantClient.GetWeatherForecastAsync(entityId, cancellationToken);
                if (homeAssistantForecast is { Entries.Count: > 0 })
                {
                    stateStore.RecordForecastReading(homeAssistantForecast, now);
                    return;
                }
            }

            var openMeteo = await TryGetOpenMeteoBackupAsync(cancellationToken);
            var openMeteoRefreshMinutes = Math.Clamp(
                homeAssistantOptions.CurrentValue.OpenMeteoRefreshMinutes,
                10,
                24 * 60);
            stateStore.RecordForecastReading(
                openMeteo is { Forecast.Entries.Count: > 0 } ? openMeteo.Forecast : null,
                openMeteo?.FetchedAt ?? now,
                openMeteo?.FetchedAt.AddMinutes(openMeteoRefreshMinutes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Weather forecast refresh failed");
            stateStore.RecordForecastReading(null, now);
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

    public Task<DefenderSnapshot> SetTargetAsync(double temperatureCelsius, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.SetTarget(temperatureCelsius)),
            cancellationToken,
            cancelBackgroundActuator: true);

    public Task<DefenderSnapshot> GenerateTargetAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.GenerateTarget()),
            cancellationToken,
            cancelBackgroundActuator: true);

    public Task<DefenderSnapshot> SetDefenderEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.SetDefenderEnabled(enabled)),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: enabled
                ? ThermostatOperationPriority.StandDownReversal
                : ThermostatOperationPriority.StandDown);

    public Task<DefenderSnapshot> UpdateSettingsAsync(SettingsRequest request, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.UpdateSettings(request)),
            cancellationToken,
            cancelBackgroundActuator: true);

    public Task<SettingsRepositoryActionResult> UndoLastSettingsRepositoryCommitAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.UndoLastSettingsRepositoryCommit()),
            cancellationToken,
            cancelBackgroundActuator: true);

    public Task<SettingsRepositoryActionResult> RestoreSettingsRepositoryCommitAsync(
        string hash,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.RestoreSettingsRepositoryCommit(hash)),
            cancellationToken,
            cancelBackgroundActuator: true);

    public Task ForceTargetAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            ForceTargetCoreAsync,
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.NormalActuator);

    private async Task ForceTargetCoreAsync(CancellationToken cancellationToken)
    {
        if (!stateStore.GetSnapshot().DefenderEnabled)
        {
            stateStore.SetNextAction(
                "Force target ignored while the defender is paused; enable the defender before requesting cooling.",
                DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
            return;
        }

        // Even the explicit "force my temp" button never snaps the wall: it sends the stepper's
        // next 0.5 C move toward the target; repeated cycles finish the walk.
        var reading = await RequireReadingAsync(cancellationToken);
        var target = stateStore.GetTargetTemperature();
        var coolingPlan = stateStore.PlanExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction, reading.SetPointCelsius);
        var stepSetPoint = coolingPlan.ExpectedSetPointCelsius;
        if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(stepSetPoint - reading.SetPointCelsius) <= 0.05)
            {
                stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: false);
                stateStore.SetNextAction(
                    $"Real thermostat is already at the next safe target step ({stepSetPoint:0.0} C); no duplicate command sent.",
                    DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
                return;
            }

            await SetTemperatureWithIntentAsync(reading.EntityId, stepSetPoint, cancellationToken);
        }
        else
        {
            if (stateStore.TryDelayCoolModeRestore(reading, DateTimeOffset.UtcNow, out var restoreAt, out var restoreMessage))
            {
                stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: false);
                stateStore.SetNextAction(restoreMessage, restoreAt);
                return;
            }

            var coolingEnabled = await SetCoolingWithTrackedPreparationAsync(
                reading.EntityId,
                stepSetPoint,
                "website-command",
                "Website control",
                "Preparing the requested Force target setpoint before enabling cool mode.",
                cancellationToken,
                onSetPointAccepted: () => stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: true));
            if (!coolingEnabled)
            {
                return;
            }
        }
        var message = $"Home Assistant {reading.EntityId} stepped to {stepSetPoint:0.0} C toward the target {target:0.0} C.";
        if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
        {
            stateStore.RecordCommand(
                message,
                stepSetPoint,
                commandSourceKind: "website-command",
                commandSourceLabel: "Website control",
                commandSourceDetail: "Website Force target button sent one stepper move toward the target.");
        }
        else
        {
            stateStore.RecordCoolingTransitionCompleted(
                message,
                stepSetPoint,
                "website-command",
                "Website control",
                "Website Force target button sent one stepper move toward the target.");
        }
        if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
        {
            stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: true);
        }
        stateStore.SetNextAction("Step toward the target sent; the walk continues with the live readings.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public Task ForceCoolingBoostAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            ForceCoolingBoostCoreAsync,
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.NormalActuator);

    private async Task ForceCoolingBoostCoreAsync(CancellationToken cancellationToken)
    {
        if (!stateStore.GetSnapshot().DefenderEnabled)
        {
            stateStore.SetNextAction(
                "Cooling boost ignored while the defender is paused; enable the defender before requesting cooling.",
                DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
            return;
        }

        var reading = await RequireReadingAsync(cancellationToken);
        var coolingPlan = stateStore.PlanExpectedSetPoint(reading.CurrentTemperatureCelsius, reading.HvacAction, reading.SetPointCelsius);
        var expectedSetPoint = coolingPlan.ExpectedSetPointCelsius;
        if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(expectedSetPoint - reading.SetPointCelsius) <= 0.05)
            {
                stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: false);
                stateStore.SetNextAction(
                    $"Real thermostat is already at the requested cooling step ({expectedSetPoint:0.0} C); no duplicate command sent.",
                    DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
                return;
            }

            await SetTemperatureWithIntentAsync(reading.EntityId, expectedSetPoint, cancellationToken);
        }
        else
        {
            if (stateStore.TryDelayCoolModeRestore(reading, DateTimeOffset.UtcNow, out var restoreAt, out var restoreMessage))
            {
                stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: false);
                stateStore.SetNextAction(restoreMessage, restoreAt);
                return;
            }

            var coolingEnabled = await SetCoolingWithTrackedPreparationAsync(
                reading.EntityId,
                expectedSetPoint,
                "website-command",
                "Website control",
                "Preparing the requested cooling-boost setpoint before enabling cool mode.",
                cancellationToken,
                onSetPointAccepted: () => stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: true));
            if (!coolingEnabled)
            {
                return;
            }
        }
        var message = $"Home Assistant {reading.EntityId} cooling boost set to {expectedSetPoint:0.0} C.";
        if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
        {
            stateStore.RecordCommand(
                message,
                expectedSetPoint,
                commandSourceKind: "website-command",
                commandSourceLabel: "Website control",
                commandSourceDetail: "Website Force cooling button sent this Home Assistant command.");
        }
        else
        {
            stateStore.RecordCoolingTransitionCompleted(
                message,
                expectedSetPoint,
                "website-command",
                "Website control",
                "Website Force cooling button sent this Home Assistant command.");
        }
        if (string.Equals(reading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
        {
            stateStore.CommitCoolingSetPointPlan(coolingPlan, commandAccepted: true);
        }
        stateStore.SetNextAction("Cooling boost command sent; waiting for the next live reading.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public Task ForceFanModeAsync(string fanMode, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            token => ForceFanModeCoreAsync(fanMode, token),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.NormalActuator);

    private async Task ForceFanModeCoreAsync(string fanMode, CancellationToken cancellationToken)
    {
        if (!stateStore.GetSnapshot().DefenderEnabled)
        {
            stateStore.SetNextAction(
                "Fan command ignored while the defender is paused.",
                DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
            return;
        }

        var reading = await RequireReadingAsync(cancellationToken);
        if (string.Equals(reading.FanMode, fanMode, StringComparison.OrdinalIgnoreCase))
        {
            stateStore.SetNextAction(
                $"Real thermostat fan is already {fanMode}; no duplicate command sent.",
                DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
            return;
        }

        await SetFanModeWithIntentAsync(reading.EntityId, fanMode, cancellationToken);
        stateStore.RecordCommand(
            $"Home Assistant {reading.EntityId} fan set to {fanMode}.",
            commandedFanMode: fanMode,
            commandSourceKind: "website-command",
            commandSourceLabel: "Website control",
            commandSourceDetail: "Website fan-mode button sent this Home Assistant command.");
    }

    public Task TurnThermostatOffAsync(
        CancellationToken cancellationToken,
        string commandSourceKind = "website-command",
        string commandSourceLabel = "Website control",
        string commandSourceDetail = "Website thermostat-off button sent this Home Assistant command.")
    {
        // The SafetyOff signal cancels stale background cooling before this reaches the gate. The
        // persistent pause is committed inside the gate, then the OFF finishes even if the HTTP
        // caller disconnects; otherwise a canceled request could leave COOL running while paused.
        return RunExclusiveAsync(
            token => TurnThermostatOffCoreAsync(token, commandSourceKind, commandSourceLabel, commandSourceDetail),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.SafetyOff,
            completeAfterCallerCancellation: true);
    }

    private async Task TurnThermostatOffCoreAsync(
        CancellationToken cancellationToken,
        string commandSourceKind,
        string commandSourceLabel,
        string commandSourceDetail)
    {
        // OFF is a stand-down intent on every surface. Persist it while holding the same operation
        // gate as the worker, so no already-running cycle can answer the OFF with a stale COOL write.
        stateStore.SetDefenderEnabled(false);
        var reading = await homeAssistantClient.GetDiningRoomClimateAsync(cancellationToken)
            ?? throw new InvalidOperationException("The real Home Assistant climate entity is unavailable; defender remains paused.");
        stateStore.RecordHomeAssistantReading(reading);
        if (!string.Equals(reading.HvacMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            await SetHvacModeWithIntentAsync(
                reading.EntityId,
                "off",
                cancellationToken,
                bypassRejectedCommandBackoff: true);
            stateStore.RecordCommand(
                $"Home Assistant {reading.EntityId} thermostat turned off while defender is paused.",
                commandedHvacMode: "off",
                commandSourceKind: commandSourceKind,
                commandSourceLabel: commandSourceLabel,
                commandSourceDetail: commandSourceDetail);
            stateStore.SetNextAction("Thermostat off command sent; defender is paused and will keep reading status.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
            return;
        }

        stateStore.SetNextAction("Thermostat is already off; defender is paused and will keep reading status.", DateTimeOffset.UtcNow.AddSeconds(options.CurrentValue.PollIntervalSeconds));
    }

    public Task ApplyEmergencyProtocolAsync(string protocol, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            token => ApplyEmergencyProtocolCoreAsync(protocol, token),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: string.Equals(protocol?.Trim(), "too-cold", StringComparison.OrdinalIgnoreCase)
                ? ThermostatOperationPriority.SafetyOff
                : ThermostatOperationPriority.StandDown,
            completeAfterCallerCancellation: string.Equals(protocol?.Trim(), "too-cold", StringComparison.OrdinalIgnoreCase));

    private async Task ApplyEmergencyProtocolCoreAsync(string protocol, CancellationToken cancellationToken)
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
                await TurnThermostatOffCoreAsync(
                    cancellationToken,
                    "emergency-too-cold",
                    "Too-cold emergency",
                    "The too-cold emergency paused the defender and turned the real thermostat off.");
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
                    "🙇 BROTHER-MAD APOLOGY ACTIVE: the defenders are bowing deeply, all corrections are stopped for 2 hours, and a safe setpoint ease-up will be attempted as a peace gesture.",
                    pauseDefender: false);
                var apologyReading = await RequireReadingAsync(cancellationToken);
                if (string.Equals(apologyReading.HvacMode, "cool", StringComparison.OrdinalIgnoreCase))
                {
                    var relief = Math.Round(Math.Min(apologyReading.SetPointCelsius + 1.0, 30.0), 1);
                    if (relief > apologyReading.SetPointCelsius + 0.05)
                    {
                        await SetTemperatureWithIntentAsync(apologyReading.EntityId, relief, cancellationToken);
                        stateStore.RecordCommand(
                            $"Brother-mad apology: setpoint eased up to {relief:0.0} C as an immediate peace gesture.",
                            relief,
                            commandSourceKind: "brother-mad-apology",
                            commandSourceLabel: "Brother-mad apology",
                            commandSourceDetail: "The BROTHER MAD emergency button eased the AC up 1.0 C so the apology is felt immediately.");
                        stateStore.RecordEmergencyStatus($"🙇 BROTHER-MAD APOLOGY ACTIVE: corrections are stopped for 2 hours, and Home Assistant accepted the {relief:0.0} C peace-gesture setpoint.");
                    }
                    else
                    {
                        stateStore.RecordEmergencyStatus("🙇 BROTHER-MAD APOLOGY ACTIVE: corrections are stopped for 2 hours; the thermostat was already at its safe upper setpoint, so no extra command was needed.");
                    }
                }
                else
                {
                    stateStore.RecordEmergencyStatus("🙇 BROTHER-MAD APOLOGY ACTIVE: corrections are stopped for 2 hours; the thermostat was not in cool mode, so no setpoint command was sent.");
                }

                break;
            default:
                throw new InvalidOperationException("Pick an emergency protocol first.");
        }
    }

    public Task RefreshRealThermostatAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(RefreshRealThermostatCoreAsync, cancellationToken);

    private async Task RefreshRealThermostatCoreAsync(CancellationToken cancellationToken)
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
    public Task<string?> ParkThermostatForStandDownAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            ParkThermostatForStandDownCoreAsync,
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.StandDown,
            completeAfterCallerCancellation: true);

    private async Task<string?> ParkThermostatForStandDownCoreAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        if (!stateStore.TryGetStandDownPark(reading, out var parkSetPoint))
        {
            return null;
        }

        // The store only allows parking while the real unit is already in cool mode. A temperature-
        // only call avoids a redundant, conspicuous COOL mode write.
        await SetTemperatureWithIntentAsync(reading.EntityId, parkSetPoint, cancellationToken);
        stateStore.RecordCommand(
            $"Defender stood down; thermostat parked at {parkSetPoint:0.0} C so the AC barely runs unguarded.",
            parkSetPoint,
            commandSourceKind: "stand-down-park",
            commandSourceLabel: "Stand-down parking",
            commandSourceDetail: "The defender was turned off; the setpoint was raised to the stand-down park value to save power while unguarded.");
        return $"Thermostat parked at {parkSetPoint:0.0} °C while the defender is off.";
    }

    /// <summary>
    /// The one-shot thermostat action for a freshly started siesta: park (raise-only, cool-mode
    /// only) or off, per the SiestaThermostatAction setting. The store records the tagged
    /// command; this just sends it. Returns a human sentence, or null when nothing was needed.
    /// </summary>
    public Task<(bool Started, string Message)> StartSiestaAsync(
        int minutes,
        string reason,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            token => StartSiestaCoreAsync(minutes, reason, token),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.StandDown,
            completeAfterCallerCancellation: true);

    private async Task<(bool Started, string Message)> StartSiestaCoreAsync(
        int minutes,
        string reason,
        CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        if (!stateStore.TryStartSiesta(minutes, reason, out var message, reading: reading))
        {
            return (false, message);
        }

        try
        {
            await ApplySiestaThermostatActionCoreAsync(cancellationToken, reading);
        }
        catch (OperationCanceledException)
        {
            stateStore.RecordSiestaThermostatActionUnconfirmed("the request ended before Home Assistant confirmed its response");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Siesta thermostat action failed after the siesta started");
            stateStore.RecordSiestaThermostatActionUnconfirmed(ex.Message);
            return (
                true,
                $"Siesta is active, but Home Assistant's one-shot thermostat action could not be confirmed: {ex.Message}");
        }

        return (true, message);
    }

    public Task<(bool Cancelled, string Message)> CancelSiestaAsync(
        string source,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            _ => Task.FromResult(stateStore.TryCancelSiesta(out var message, source) ? (true, message) : (false, message)),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.StandDownReversal);

    public Task<string?> ApplySiestaThermostatActionAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            token => ApplySiestaThermostatActionCoreAsync(token),
            cancellationToken,
            cancelBackgroundActuator: true,
            actuatorPriority: ThermostatOperationPriority.StandDown,
            completeAfterCallerCancellation: true);

    private async Task<string?> ApplySiestaThermostatActionCoreAsync(
        CancellationToken cancellationToken,
        ThermostatReading? existingReading = null)
    {
        var reading = existingReading ?? await RequireReadingAsync(cancellationToken);
        if (!stateStore.TryGetSiestaThermostatAction(reading, out var turnOff, out var parkSetPoint))
        {
            return null;
        }

        if (turnOff)
        {
            await SetHvacModeWithIntentAsync(reading.EntityId, "off", cancellationToken);
            stateStore.RecordCommand(
                $"Home Assistant {reading.EntityId} thermostat turned off for the siesta.",
                commandedHvacMode: "off",
                commandSourceKind: "siesta",
                commandSourceLabel: "Siesta (mess hall)",
                commandSourceDetail: "A siesta started with the 'off' action; AC Defender turned the AC off while the guards nap.");
            return "AC turned off while the guards nap.";
        }

        if (parkSetPoint is { } park)
        {
            await SetTemperatureWithIntentAsync(reading.EntityId, park, cancellationToken);
            stateStore.RecordCommand(
                $"Home Assistant {reading.EntityId} parked at {park:0.0} C for the siesta.",
                park,
                commandSourceKind: "siesta",
                commandSourceLabel: "Siesta (mess hall)",
                commandSourceDetail: "A siesta started with the 'park' action; AC Defender raised the setpoint so the unit barely runs while the guards nap.");
            return $"Thermostat parked at {park:0.0} °C while the guards nap.";
        }

        return null;
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

    public Task<HistoryLearningSnapshot> LearnFromHistoryAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(LearnFromHistoryCoreAsync, cancellationToken);

    private async Task<HistoryLearningSnapshot> LearnFromHistoryCoreAsync(CancellationToken cancellationToken)
    {
        var reading = await RequireReadingAsync(cancellationToken);
        var settings = stateStore.GetSettings();
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-Math.Max(1, settings.HistoryLearningDays));
        var samples = await homeAssistantClient.GetClimateHistoryAsync(reading.EntityId, from, to, cancellationToken);
        return stateStore.LearnFromThermostatHistory(samples, DateTimeOffset.UtcNow);
    }

    private async Task<ThermostatReading?> RefreshClimateReadingAsync(CancellationToken cancellationToken)
    {
        if (!homeAssistantClient.IsConfigured)
        {
            stateStore.RecordHomeAssistantUnavailable("Home Assistant token is not configured.");
            return null;
        }

        // Climate is the safety-critical 24/7 reading. Fetch and persist it before optional weather,
        // presence, and model context so a slow auxiliary integration cannot starve thermostat state.
        var reading = await homeAssistantClient.GetDiningRoomClimateAsync(cancellationToken);
        if (reading is null)
        {
            stateStore.RecordHomeAssistantUnavailable("Dining room climate entity was not found.");
            return null;
        }
        stateStore.RecordHomeAssistantReading(reading);
        return reading;
    }

    private async Task RefreshAncillaryReadingsAsync(CancellationToken cancellationToken)
    {

        WeatherReading? weather = null;
        try
        {
            weather = await homeAssistantClient.GetWeatherAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Home Assistant outdoor weather refresh timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Home Assistant outdoor weather refresh failed.");
        }

        if (weather?.OutdoorTemperatureCelsius is not null)
        {
            stateStore.RecordWeatherReading(weather);
        }
        else
        {
            var openMeteo = await TryGetOpenMeteoBackupAsync(cancellationToken);
            if (openMeteo is not null)
            {
                var currentWeather = stateStore.GetSnapshot().Weather;
                if (!string.Equals(currentWeather?.EntityId, OpenMeteoWeatherClient.SourceId, StringComparison.OrdinalIgnoreCase)
                    || currentWeather?.UpdatedAt != openMeteo.ObservedAt)
                {
                    // Preserve the real API observation time. Returning the client's cached object
                    // on five-second cycles must not create pretend fresh weather samples.
                    stateStore.RecordWeatherReading(openMeteo.Current, openMeteo.ObservedAt);
                }
            }
            else
            {
                var currentWeather = stateStore.GetSnapshot().Weather;
                if (string.Equals(currentWeather?.EntityId, OpenMeteoWeatherClient.SourceId, StringComparison.OrdinalIgnoreCase)
                    && currentWeather?.OutdoorTemperatureCelsius is not null)
                {
                    // Do not let an expired backup value keep making weather decisions after the
                    // source becomes unreachable. Keep its original timestamp and clear its value.
                    stateStore.RecordWeatherReading(
                        new WeatherReading(OpenMeteoWeatherClient.SourceId, null, "unavailable"),
                        currentWeather.UpdatedAt);
                }
                else
                {
                    stateStore.RecordWeatherReading(null);
                }
            }
        }

        try
        {
            var settings = stateStore.GetSettings();
            var upstairsSensors = await homeAssistantClient.GetUpstairsTemperatureSensorsAsync(settings.UpstairsTemperatureEntityIds, cancellationToken);
            var presence = await homeAssistantClient.GetPresenceAsync(settings.PresenceEntityIds, cancellationToken);
            stateStore.RecordComfortReadings(upstairsSensors, presence);

            // Adjustment-statistics context: is the tracked person home, is the master bedroom occupied.
            var trackedContext = await homeAssistantClient.GetTrackedContextAsync(cancellationToken);
            stateStore.RecordTrackedContext(trackedContext);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Home Assistant comfort-context refresh timed out; retaining the last real context.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Home Assistant comfort-context refresh failed; retaining the last real context.");
        }

    }

    private async Task<OpenMeteoWeatherBundle?> TryGetOpenMeteoBackupAsync(CancellationToken cancellationToken)
    {
        var configured = homeAssistantOptions.CurrentValue;
        if (!configured.OpenMeteoBackupEnabled || openMeteoWeatherClient is null)
        {
            return null;
        }

        var latitude = configured.OpenMeteoLatitude;
        var longitude = configured.OpenMeteoLongitude;
        if ((latitude is null) != (longitude is null))
        {
            if (DateTimeOffset.UtcNow >= nextOpenMeteoIncompleteCoordinateWarningAt)
            {
                logger.LogWarning(
                    "Open-Meteo coordinate override is incomplete; ignoring it and using both Home Assistant installation coordinates.");
                nextOpenMeteoIncompleteCoordinateWarningAt = DateTimeOffset.UtcNow.AddMinutes(30);
            }

            latitude = null;
            longitude = null;
        }

        if (latitude is null || longitude is null)
        {
            var installation = await homeAssistantClient.GetInstallationCoordinatesAsync(cancellationToken);
            latitude = installation?.Latitude;
            longitude = installation?.Longitude;
        }

        if (latitude is not { } lat || longitude is not { } lon)
        {
            if (DateTimeOffset.UtcNow >= nextOpenMeteoCoordinateWarningAt)
            {
                logger.LogWarning(
                    "Open-Meteo backup is enabled but neither configuration nor Home Assistant supplied complete coordinates.");
                nextOpenMeteoCoordinateWarningAt = DateTimeOffset.UtcNow.AddMinutes(30);
            }

            return null;
        }

        nextOpenMeteoCoordinateWarningAt = default;
        return await openMeteoWeatherClient.GetWeatherAsync(lat, lon, cancellationToken);
    }

    private async Task<bool> SetCoolingWithTrackedPreparationAsync(
        string entityId,
        double setPointCelsius,
        string commandSourceKind,
        string commandSourceLabel,
        string commandSourceDetail,
        CancellationToken cancellationToken,
        bool bypassRejectedCommandBackoff = false,
        Action? onSetPointAccepted = null)
    {
        var directTicket = currentExplicitOperationTicket.Value;
        // This transition deliberately has two real HA calls. Record the accepted temperature
        // stage before enabling COOL so a mode-call failure cannot make our setpoint echo look like
        // a human wall touch or leave an untruthful all-or-nothing command record.
        await SetTemperatureWithIntentAsync(
            entityId,
            setPointCelsius,
            cancellationToken,
            commandSourceKind,
            commandSourceLabel,
            commandSourceDetail,
            bypassRejectedCommandBackoff);
        stateStore.RecordCommand(
            $"Home Assistant {entityId} prepared at {setPointCelsius:0.0} C before enabling cool mode.",
            setPointCelsius,
            commandSourceKind: commandSourceKind,
            commandSourceLabel: commandSourceLabel,
            commandSourceDetail: commandSourceDetail);
        onSetPointAccepted?.Invoke();
        if (directTicket is { } ticket && IsActuatorOperationSuperseded(ticket))
        {
            stateStore.SetNextAction("A newer direct control superseded the pending COOL transition after its safe setpoint preparation.", DateTimeOffset.UtcNow);
            return false;
        }

        await SetHvacModeWithIntentAsync(
            entityId,
            "cool",
            cancellationToken,
            commandSourceKind,
            commandSourceLabel,
            commandSourceDetail,
            bypassRejectedCommandBackoff);
        return true;
    }

    private async Task SetTemperatureWithIntentAsync(
        string entityId,
        double setPointCelsius,
        CancellationToken cancellationToken,
        string commandSourceKind = "defender-service",
        string commandSourceLabel = "AC Defender",
        string commandSourceDetail = "AC Defender requested this real thermostat setpoint.",
        bool bypassRejectedCommandBackoff = false)
    {
        ThrowIfExplicitOperationSuperseded();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfMatchingThermostatCommandDeferred(
            setPointCelsius,
            hvacMode: null,
            fanMode: null,
            bypassRejectedCommandBackoff);
        stateStore.BeginThermostatCommandIntent(
            setPointCelsius,
            commandSourceKind: commandSourceKind,
            commandSourceLabel: commandSourceLabel,
            commandSourceDetail: commandSourceDetail);
        try
        {
            await homeAssistantClient.SetTemperatureAsync(entityId, setPointCelsius, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            stateStore.ClearThermostatCommandIntent();
            stateStore.RecordThermostatCommandRejected(
                $"Home Assistant rejected the thermostat setpoint command ({(int)ex.StatusCode.Value}): {ex.Message}",
                setPointCelsius: setPointCelsius,
                urgent: bypassRejectedCommandBackoff);
            throw new ThermostatCommandRejectedException(ex.Message, ex.StatusCode.Value, ex);
        }
    }

    private async Task SetHvacModeWithIntentAsync(
        string entityId,
        string hvacMode,
        CancellationToken cancellationToken,
        string commandSourceKind = "defender-service",
        string commandSourceLabel = "AC Defender",
        string commandSourceDetail = "AC Defender requested this real thermostat mode.",
        bool bypassRejectedCommandBackoff = false)
    {
        ThrowIfExplicitOperationSuperseded();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfMatchingThermostatCommandDeferred(
            setPointCelsius: null,
            hvacMode,
            fanMode: null,
            bypassRejectedCommandBackoff);
        stateStore.BeginThermostatCommandIntent(
            hvacMode: hvacMode,
            commandSourceKind: commandSourceKind,
            commandSourceLabel: commandSourceLabel,
            commandSourceDetail: commandSourceDetail);
        try
        {
            await homeAssistantClient.SetHvacModeAsync(entityId, hvacMode, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            stateStore.ClearThermostatCommandIntent();
            stateStore.RecordThermostatCommandRejected(
                $"Home Assistant rejected the thermostat mode command ({(int)ex.StatusCode.Value}): {ex.Message}",
                hvacMode: hvacMode,
                urgent: bypassRejectedCommandBackoff);
            throw new ThermostatCommandRejectedException(ex.Message, ex.StatusCode.Value, ex);
        }
    }

    private async Task SetFanModeWithIntentAsync(
        string entityId,
        string fanMode,
        CancellationToken cancellationToken,
        string commandSourceKind = "defender-service",
        string commandSourceLabel = "AC Defender",
        string commandSourceDetail = "AC Defender requested this real thermostat fan mode.")
    {
        ThrowIfExplicitOperationSuperseded();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfMatchingThermostatCommandDeferred(setPointCelsius: null, hvacMode: null, fanMode);
        stateStore.BeginThermostatCommandIntent(
            fanMode: fanMode,
            commandSourceKind: commandSourceKind,
            commandSourceLabel: commandSourceLabel,
            commandSourceDetail: commandSourceDetail);
        try
        {
            await homeAssistantClient.SetFanModeAsync(entityId, fanMode, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            stateStore.ClearThermostatCommandIntent();
            stateStore.RecordThermostatCommandRejected(
                $"Home Assistant rejected the thermostat fan command ({(int)ex.StatusCode.Value}): {ex.Message}",
                fanMode: fanMode);
            throw new ThermostatCommandRejectedException(ex.Message, ex.StatusCode.Value, ex);
        }
    }

    private void ThrowIfMatchingThermostatCommandDeferred(
        double? setPointCelsius,
        string? hvacMode,
        string? fanMode,
        bool bypassRejectedCommandBackoff = false)
    {
        if (stateStore.TryRespectMatchingThermostatCommand(
            DateTimeOffset.UtcNow,
            setPointCelsius,
            hvacMode,
            fanMode,
            bypassRejectedCommandBackoff,
            out var until,
            out var message))
        {
            throw new ThermostatCommandRetryDeferredException(message, until);
        }
    }

    private async Task RunBackgroundActuatorAsync(
        long observedExplicitGeneration,
        ThermostatOperationPriority priority,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (priority != ThermostatOperationPriority.SafetyOff
            && observedExplicitGeneration != Volatile.Read(ref explicitOperationGeneration))
        {
            throw new BackgroundCommandSupersededException();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (backgroundActuatorCancellationGate)
        {
            activeBackgroundActuatorCancellation = linkedCancellation;
            activeBackgroundActuatorPriority = priority;
        }

        try
        {
            // A stand-down command that has already won the decision gate is safety-priority:
            // a later target/settings/fan request may wait, but cannot cancel the pending OFF.
            if (priority != ThermostatOperationPriority.SafetyOff
                && observedExplicitGeneration != Volatile.Read(ref explicitOperationGeneration))
            {
                throw new BackgroundCommandSupersededException();
            }

            await operation(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && linkedCancellation.IsCancellationRequested)
        {
            throw new BackgroundCommandSupersededException();
        }
        finally
        {
            lock (backgroundActuatorCancellationGate)
            {
                if (ReferenceEquals(activeBackgroundActuatorCancellation, linkedCancellation))
                {
                    activeBackgroundActuatorCancellation = null;
                    activeBackgroundActuatorPriority = ThermostatOperationPriority.None;
                }
            }
        }
    }

    private async Task<ThermostatReading> RequireReadingAsync(CancellationToken cancellationToken)
    {
        var reading = await RefreshClimateReadingAsync(cancellationToken);
        if (reading is null)
        {
            throw new InvalidOperationException(stateStore.GetSnapshot().LastError ?? "Home Assistant is unavailable.");
        }

        return reading;
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        bool cancelBackgroundActuator = false,
        ThermostatOperationPriority actuatorPriority = ThermostatOperationPriority.None,
        bool completeAfterCallerCancellation = false)
    {
        var ticket = SignalExplicitOperation(cancelBackgroundActuator, actuatorPriority);
        using var linkedCancellation = CreateOperationCancellationSource(cancellationToken, completeAfterCallerCancellation);
        await operationGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (ticket is { } pendingTicket && IsActuatorOperationSuperseded(pendingTicket))
            {
                throw new ThermostatOperationSupersededException();
            }

            if (ticket is { } activeTicket)
            {
                lock (explicitOperationCancellationGate)
                {
                    activeExplicitOperationCancellation = linkedCancellation;
                    activeExplicitOperationPriority = activeTicket.Priority;
                }
            }
            currentExplicitOperationTicket.Value = ticket;
            try
            {
                if (ticket is { } startedTicket && IsActuatorOperationSuperseded(startedTicket))
                {
                    throw new ThermostatOperationSupersededException();
                }

                await operation(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && applicationLifetime?.ApplicationStopping.IsCancellationRequested != true
                && linkedCancellation.IsCancellationRequested)
            {
                throw new ThermostatOperationSupersededException();
            }
        }
        finally
        {
            currentExplicitOperationTicket.Value = null;
            if (ticket is not null)
            {
                lock (explicitOperationCancellationGate)
                {
                    if (ReferenceEquals(activeExplicitOperationCancellation, linkedCancellation))
                    {
                        activeExplicitOperationCancellation = null;
                        activeExplicitOperationPriority = ThermostatOperationPriority.None;
                    }
                }
            }
            operationGate.Release();
        }
    }

    private async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool cancelBackgroundActuator = false,
        ThermostatOperationPriority actuatorPriority = ThermostatOperationPriority.None,
        bool completeAfterCallerCancellation = false)
    {
        var ticket = SignalExplicitOperation(cancelBackgroundActuator, actuatorPriority);
        using var linkedCancellation = CreateOperationCancellationSource(cancellationToken, completeAfterCallerCancellation);
        await operationGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (ticket is { } pendingTicket && IsActuatorOperationSuperseded(pendingTicket))
            {
                throw new ThermostatOperationSupersededException();
            }

            if (ticket is { } activeTicket)
            {
                lock (explicitOperationCancellationGate)
                {
                    activeExplicitOperationCancellation = linkedCancellation;
                    activeExplicitOperationPriority = activeTicket.Priority;
                }
            }
            currentExplicitOperationTicket.Value = ticket;
            try
            {
                if (ticket is { } startedTicket && IsActuatorOperationSuperseded(startedTicket))
                {
                    throw new ThermostatOperationSupersededException();
                }

                return await operation(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && applicationLifetime?.ApplicationStopping.IsCancellationRequested != true
                && linkedCancellation.IsCancellationRequested)
            {
                throw new ThermostatOperationSupersededException();
            }
        }
        finally
        {
            currentExplicitOperationTicket.Value = null;
            if (ticket is not null)
            {
                lock (explicitOperationCancellationGate)
                {
                    if (ReferenceEquals(activeExplicitOperationCancellation, linkedCancellation))
                    {
                        activeExplicitOperationCancellation = null;
                        activeExplicitOperationPriority = ThermostatOperationPriority.None;
                    }
                }
            }
            operationGate.Release();
        }
    }

    private CancellationTokenSource CreateOperationCancellationSource(
        CancellationToken callerCancellation,
        bool completeAfterCallerCancellation)
    {
        var applicationStopping = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
        if (completeAfterCallerCancellation)
        {
            return applicationStopping.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(applicationStopping)
                : new CancellationTokenSource();
        }

        return applicationStopping.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, applicationStopping)
            : CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
    }

    private ActuatorOperationTicket? SignalExplicitOperation(
        bool cancelBackgroundActuator,
        ThermostatOperationPriority actuatorPriority)
    {
        Interlocked.Increment(ref explicitOperationGeneration);

        ActuatorOperationTicket? ticket = null;
        if (actuatorPriority != ThermostatOperationPriority.None)
        {
            var operationId = Interlocked.Increment(ref nextActuatorOperationId);
            if (actuatorPriority == ThermostatOperationPriority.SafetyOff)
            {
                PublishMaximum(ref latestSafetyOffOperationId, operationId);
            }
            else if (actuatorPriority == ThermostatOperationPriority.StandDown)
            {
                PublishMaximum(ref latestStandDownActuatorOperationId, operationId);
            }
            else if (actuatorPriority == ThermostatOperationPriority.StandDownReversal)
            {
                PublishMaximum(ref latestStandDownReversalOperationId, operationId);
            }
            else
            {
                PublishMaximum(ref latestNormalActuatorOperationId, operationId);
            }

            ticket = new ActuatorOperationTicket(operationId, actuatorPriority);
        }

        if (cancelBackgroundActuator)
        {
            var cancellationPriority = actuatorPriority == ThermostatOperationPriority.None
                ? ThermostatOperationPriority.NormalActuator
                : actuatorPriority;
            lock (backgroundActuatorCancellationGate)
            {
                if (activeBackgroundActuatorCancellation is not null
                    && ShouldCancelActiveOperation(activeBackgroundActuatorPriority, cancellationPriority))
                {
                    activeBackgroundActuatorCancellation.Cancel();
                }
            }
        }

        if (ticket is { } actuatorTicket)
        {
            lock (explicitOperationCancellationGate)
            {
                if (activeExplicitOperationCancellation is not null
                    && ShouldCancelActiveOperation(activeExplicitOperationPriority, actuatorTicket.Priority))
                {
                    activeExplicitOperationCancellation.Cancel();
                }
            }
        }

        return ticket;
    }

    private bool IsActuatorOperationSuperseded(ActuatorOperationTicket ticket) =>
        ticket.Priority switch
        {
            ThermostatOperationPriority.NormalActuator =>
                ticket.Id != Volatile.Read(ref latestNormalActuatorOperationId)
                || Volatile.Read(ref latestStandDownActuatorOperationId) > ticket.Id
                || Volatile.Read(ref latestStandDownReversalOperationId) > ticket.Id
                || Volatile.Read(ref latestSafetyOffOperationId) > ticket.Id,
            ThermostatOperationPriority.StandDown =>
                Volatile.Read(ref latestStandDownReversalOperationId) > ticket.Id
                || Volatile.Read(ref latestSafetyOffOperationId) > ticket.Id,
            ThermostatOperationPriority.StandDownReversal =>
                ticket.Id != Volatile.Read(ref latestStandDownReversalOperationId)
                || Volatile.Read(ref latestStandDownActuatorOperationId) > ticket.Id
                || Volatile.Read(ref latestSafetyOffOperationId) > ticket.Id,
            // Multiple OFF/stand-down safety requests are idempotent and serialize. A later
            // peer must never cancel an earlier OFF after Home Assistant may have accepted it.
            ThermostatOperationPriority.SafetyOff => false,
            _ => false
        };

    private static bool ShouldCancelActiveOperation(
        ThermostatOperationPriority activePriority,
        ThermostatOperationPriority incomingPriority) =>
        incomingPriority > activePriority
        || (activePriority == ThermostatOperationPriority.StandDownReversal
            && incomingPriority == ThermostatOperationPriority.StandDown)
        || (incomingPriority == ThermostatOperationPriority.NormalActuator
            && activePriority == ThermostatOperationPriority.NormalActuator);

    private static void PublishMaximum(ref long location, long value)
    {
        var observed = Volatile.Read(ref location);
        while (observed < value)
        {
            var exchanged = Interlocked.CompareExchange(ref location, value, observed);
            if (exchanged == observed)
            {
                return;
            }

            observed = exchanged;
        }
    }

    private void ThrowIfExplicitOperationSuperseded()
    {
        if (currentExplicitOperationTicket.Value is { } ticket && IsActuatorOperationSuperseded(ticket))
        {
            throw new ThermostatOperationSupersededException();
        }
    }

    private sealed class BackgroundCommandSupersededException : Exception
    {
    }

    private enum ThermostatOperationPriority
    {
        None = 0,
        NormalActuator = 1,
        StandDown = 2,
        StandDownReversal = 3,
        SafetyOff = 4
    }

    private readonly record struct ActuatorOperationTicket(
        long Id,
        ThermostatOperationPriority Priority);

}

public sealed class ThermostatOperationSupersededException : OperationCanceledException
{
    public ThermostatOperationSupersededException()
        : base("A newer direct thermostat operation superseded this request.")
    {
    }
}

public sealed class ThermostatCommandRejectedException : Exception
{
    public ThermostatCommandRejectedException(
        string message,
        System.Net.HttpStatusCode statusCode,
        Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
}

public sealed class ThermostatCommandRetryDeferredException : Exception
{
    public ThermostatCommandRetryDeferredException(string message, DateTimeOffset until)
        : base(message)
    {
        Until = until;
    }

    public DateTimeOffset Until { get; }
}
