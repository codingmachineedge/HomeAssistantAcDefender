using HomeAssistantAcDefender.Guards;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new DefenderSetPointRegressionTests();
tests.ManualTouchWhileWarmRestartsBelowRoomByApproach();
tests.ManualTouchAfterTargetRestartsBelowRoomByApproach();
tests.IdleWarmRoomWalksDownByApproachUntilWebsiteTarget();
tests.SetpointEchoWaitsOnlyForSafeFollowUpCommands();
tests.RepeatQuietWaitsOnlyForIdenticalSafeCommands();
tests.SetpointStillnessWaitsForStableReadingsButBypassesHotRoom();
tests.CommandCamouflageSpacesSafeFollowUpButBypassesHotRoom();
tests.StealthGovernorHoldsSafeHighPressureButBypassesHotRoom();
tests.HumanNudgeShapesOnlySafeCommandsAndBypassesHotRoom();
tests.WebsiteCommandDebounceIsOffByDefault();
tests.WebsiteCommandDebounceBlocksRapidControlsForTwoMinutes();
tests.WebsiteCommandDebounceCanBypassNonThermostatButtons();
tests.CoolingFailureAlertsWhenCoolingDemandStaysIdle();
tests.OmegaAlertEscalatesOnlyWhenRoomRisesDuringIdleCoolingFailure();
tests.CoolingFailureStaysQuietWhenRoomIsAtUserTarget();
tests.CoolingFailureStaysQuietWhileRoomStillCoolingDown();
tests.CoolingFailureStaysQuietWhenActionInconclusiveAndRoomNotRising();
tests.CoolingFailureMegaAndOmegaWhenBreakerOffAndRoomRising();
tests.CoolingFailureMegaWhenFarAboveTargetEvenIfRoomDrifsDown();
tests.CoolingFailureMegaWhenCoolingActionButRoomNotDropping();
tests.AngerButtonLearnsUpsetAndRaisesThisHourSensitivity();
tests.HistoryLearningBuildsHumanComfortProfileAndCadence();
tests.MachineLearningTrainerLearnsAngerAndComfortPatterns();
tests.AdjustmentStatisticsSplitByPresenceAndBedroomOccupancy();
tests.OutdoorPowerRuleSilencesWhenColdLiteWhenMildButYieldsToHotRoom();
tests.AccountSignupOwnerThenCodeGatedAndValidates();
tests.EmergencyQuietPausesCorrectionsButKeepsStatus();
tests.FrontDoorKillSwitchPausesDefenderAndTagsThermostatOffSource();
tests.WallSettlingWaitsWhileWallThermostatIsStillBeingTouched();
tests.TugOfWarTruceHoldsAlternatingSafeCorrectionsButBypassesHotRoom();
tests.CoolerIntentFastLaneBypassesQuietTimingForRepeatedCoolerTouches();
tests.WeatherDriftWaitsOnlyForSafeStableOutdoorConditions();
tests.CoolingRunwayWaitsOnlyAfterSafeCoolingStarts();
tests.SensorRhythmWaitsOnlyForSafeCorrections();
tests.HvacActionAlibiWaitsForRealActionTransitionButBypassesHotRoom();
tests.TelemetryAlibiWaitsForHouseSignalButBypassesHotRoom();
tests.ComfortPaceWaitsAfterFrequentWallTouchesButBypassesHotRoom();
tests.ComfortEnvelopeObservesSmallSafeWallPreferenceButBypassesHotRoom();
tests.PeakPowerSaverHoldsSafeCoolingDuringAlectraOnPeakButBypassesHotRoom();
tests.SuperDefenderClassifiesRemoteHomeAssistantChangesAndBypassesQuietTiming();
tests.RemoteSettlingHoldsSafeRemotePatternButBypassesHotRoom();
tests.EnforcerRestoresCoolWhenTurnedOffByAnotherPerson();
tests.EnforcerSnapsToExactTargetWhenSetpointRaisedByAnotherPerson();
tests.EnforcerRespectsOwnerOwnChange();
tests.EnforcerDebouncesBeforeEnforcing();
tests.EnforcerCooldownWaitsForHomeAssistantToConfirm();
tests.EnforcerBacksOffWhenDeviceRejectsCommands();
tests.EnforcerEscalatesAfterRepeatedOverrides();
tests.EnforcerRateLimitHoldsInsteadOfThrashing();
tests.EnforcerClampsAssertToDeviceMinMax();
tests.EnforcerStealthModeLetsNaturalPipelineHandleSetpointRaise();
tests.EnforcerDoesNotFightOwnWarmRoomCoolingSetpoint();
tests.EnforcerStealthWaitsLongerDuringHighAngerHours();
tests.EnforcerInactiveWhenDisabledLeavesStealthPipelineUnchanged();
tests.EnforcerLearningModelsTrainInterferenceAndCadenceFromOverrides();
tests.EnforcerConsumesTrainedInterferenceModel();
tests.AppliedTargetPersistsAcrossStoreReloads();
tests.InvalidLoadedTargetFallsBackToConfiguredDefault();
tests.UserTargetSurvivesUpstairsComfortOverride();
tests.NightShutdownTurnsAcOffOnceAndStandsDown();
tests.GentleSteppingPacesWalkDownAndPreemptsCompressorStop();
tests.StepperNeverSnapsTheWallAndWalksBothWaysTowardMyTemp();
tests.PeaceOfferingConcedesUpwardOnAppRaiseThenStandsDown();
tests.CoolingRestStopsAnUnreachableTargetFromRunningForever();
tests.BrotherMadProtocolStandsDownForTwoHours();
tests.AutoBrotherMadFiresOnRageWithoutAnyButton();
tests.StandDownParkingRaisesOnlyWhenAppropriate();
tests.GuardCatalogProjectsEveryLiveGuardForADefaultSnapshot();
Console.WriteLine("Defender setpoint regression checks passed.");

internal sealed class DefenderSetPointRegressionTests
{
    public void AppliedTargetPersistsAcrossStoreReloads()
    {
        var contentRoot = DefenderStoreFixture.CreateContentRoot();
        try
        {
            using (var fixture = DefenderStoreFixture.Create(contentRoot))
            {
                fixture.Store.SetTarget(20.5);
            }

            using (var reloaded = DefenderStoreFixture.Create(contentRoot))
            {
                AssertEqual(20.5, reloaded.Store.GetTargetTemperature(), "Applied target should persist exactly after reloading state.");
            }
        }
        finally
        {
            DefenderStoreFixture.DeleteContentRoot(contentRoot);
        }
    }

    public void UserTargetSurvivesUpstairsComfortOverride()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(24.0);

        // Upstairs turns hot (default hot threshold 24.0): the guard adds urgency but must NOT
        // rewrite the user's stored 24.0 target and must NEVER cool below it — the user's
        // "temp I want" is a hard floor for every commanded setpoint.
        store.RecordComfortReadings(
            [new TemperatureSensorReading("sensor.master_bedroom", "Master bedroom", 27.0, "27.0")],
            []);
        var comfort = store.ApplyComfortRules();
        if (!comfort.Active)
        {
            throw new InvalidOperationException("Upstairs comfort guard should be active while upstairs is hot.");
        }

        AssertEqual(24.0, comfort.EffectiveTargetCelsius, "The cooling goal is always the user's own target — never lower.");
        AssertEqual(24.0, store.GetTargetTemperature(), "The comfort guard must never rewrite the user's target.");

        // Warm room walk-down: every commanded setpoint must stay at or above the user's 24.0.
        for (var i = 0; i < 12; i++)
        {
            var setPoint = store.CalculateExpectedSetPoint(26.0, "idle");
            if (setPoint < 24.0)
            {
                throw new InvalidOperationException($"The defender commanded {setPoint:0.0} C, below the user's 24.0 C floor.");
            }
        }

        AssertEqual(24.0, store.CalculateExpectedSetPoint(23.0, "cooling"), "A room already below the user's target gets the target itself, no over-cooling.");

        // Upstairs cools back off: the override lifts on its own and nothing changed underneath.
        store.RecordComfortReadings(
            [new TemperatureSensorReading("sensor.master_bedroom", "Master bedroom", 23.0, "23.0")],
            []);
        store.ApplyComfortRules();
        AssertEqual(24.0, store.GetTargetTemperature(), "The user's target must be untouched after the override lifts.");
        AssertEqual(24.0, store.CalculateExpectedSetPoint(23.0, "cooling"), "A 23.0 C room is satisfied once the room is below the user's 24.0 C.");
    }

    public void InvalidLoadedTargetFallsBackToConfiguredDefault()
    {
        var contentRoot = DefenderStoreFixture.CreateContentRoot();
        try
        {
            File.WriteAllText(Path.Combine(contentRoot, "state.json"), """
                {
                  "targetTemperatureCelsius": 0,
                  "defenderEnabled": true,
                  "connectionState": "unavailable"
                }
                """);

            using var fixture = DefenderStoreFixture.Create(contentRoot, new DefenderOptions { DefaultTargetCelsius = 21.5 });
            AssertEqual(21.5, fixture.Store.GetTargetTemperature(), "A missing or invalid persisted target must fall back to the configured default.");
        }
        finally
        {
            DefenderStoreFixture.DeleteContentRoot(contentRoot);
        }
    }

    public void GuardCatalogProjectsEveryLiveGuardForADefaultSnapshot()
    {
        using var fixture = DefenderStoreFixture.Create();
        var snapshot = fixture.Store.GetSnapshot();

        var live = GuardCatalog.Live.ToList();
        if (live.Count < 33)
        {
            throw new InvalidOperationException($"Expected at least 33 live guard cards in the catalog, found {live.Count}.");
        }

        foreach (var guard in live)
        {
            var view = guard.Project!(snapshot);
            if (view is null)
            {
                throw new InvalidOperationException($"Guard '{guard.Name}' projected a null live view from a default snapshot.");
            }

            if (string.IsNullOrWhiteSpace(guard.Summary)
                || string.IsNullOrWhiteSpace(guard.Watches)
                || string.IsNullOrWhiteSpace(guard.Logic)
                || string.IsNullOrWhiteSpace(guard.Output))
            {
                throw new InvalidOperationException($"Guard '{guard.Name}' is missing detail drawer help text.");
            }

            foreach (var metric in view.Metrics)
            {
                if (string.IsNullOrWhiteSpace(metric.Label) || string.IsNullOrWhiteSpace(metric.Value))
                {
                    throw new InvalidOperationException($"Guard '{guard.Name}' has a blank live evidence metric.");
                }
            }
        }
    }

    public void ManualTouchWhileWarmRestartsBelowRoomByApproach()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        // Default WarmRoomApproachCelsius is 0.5, so the setpoint tracks just under the room (less noticeable).
        var initial = store.CalculateExpectedSetPoint(25.0, "cooling");
        AssertEqual(24.5, initial, "Initial warm-room command should start 0.5 C below room temperature.");
        store.RecordCommand("Seed defender command.", initial);
        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room",
            25.0,
            initial,
            "cool",
            "cooling",
            null,
            []));

        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room",
            25.0,
            26.0,
            "cool",
            "cooling",
            null,
            []));

        var afterManualTouch = store.CalculateExpectedSetPoint(25.0, "cooling");
        AssertEqual(24.5, afterManualTouch, "Manual wall touch while warm should restart 0.5 C below room temperature, not from the wall setpoint.");
    }

    public void ManualTouchAfterTargetRestartsBelowRoomByApproach()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "Warm-room defense should start 0.5 C below the current room temperature.");
        // Steps are temperature-driven: with the room stuck at 25.0 the defender holds and waits.
        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "With no room-temperature progress the defender holds instead of stepping again.");
        // The room dropping to (near) the setpoint is the progress signal for the next step.
        AssertEqual(24.0, store.CalculateExpectedSetPoint(24.8, "cooling"), "Once the room drops to the setpoint the next 0.5 C step lands.");
        AssertEqual(23.5, store.CalculateExpectedSetPoint(24.3, "cooling"), "Each further room drop earns the next 0.5 C step toward the website target.");

        store.RecordCommand("Seed website target command.", 22.0);
        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room",
            25.0,
            22.0,
            "cool",
            "idle",
            null,
            []));

        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room",
            25.0,
            26.0,
            "cool",
            "idle",
            null,
            []));

        var afterManualTouch = store.CalculateExpectedSetPoint(25.0, "idle");
        AssertEqual(24.5, afterManualTouch, "A new wall touch after reaching target should restart 0.5 C below the current room temperature.");
    }

    public void IdleWarmRoomWalksDownByApproachUntilWebsiteTarget()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        // approach defaults to 0.5 C; every step after the first is earned by the room actually
        // dropping to the previous setpoint (temperature-driven, not clock-driven).
        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "First idle warm-room command should be 0.5 C below current room temperature.");
        AssertEqual(24.0, store.CalculateExpectedSetPoint(24.8, "cooling"), "Should step down 0.5 C as the room reaches the setpoint.");
        AssertEqual(23.5, store.CalculateExpectedSetPoint(24.3, "cooling"), "Should step down 0.5 C.");
        AssertEqual(23.0, store.CalculateExpectedSetPoint(23.8, "cooling"), "Should step down 0.5 C.");
        AssertEqual(22.5, store.CalculateExpectedSetPoint(23.3, "cooling"), "Should step down 0.5 C.");
        AssertEqual(22.0, store.CalculateExpectedSetPoint(22.8, "cooling"), "Should reach the website target.");
        AssertEqual(22.0, store.CalculateExpectedSetPoint(22.3, "cooling"), "Warm-room step-down must not go colder than the website target.");
    }

    public void StepperNeverSnapsTheWallAndWalksBothWaysTowardMyTemp()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(23.0);

        // Room satisfied but the wall was left at 26: never snap to 23 — one 0.5 C step DOWN.
        AssertEqual(25.5, store.CalculateExpectedSetPoint(22.5, "idle", 26.0), "A wall left above the target is walked DOWN one 0.5 C step per command, never snapped.");

        // Wall left BELOW "my temp" (someone forced 21): walk UP one 0.5 C step per command.
        AssertEqual(21.5, store.CalculateExpectedSetPoint(22.5, "idle", 21.0), "A wall below the user's floor is walked UP one 0.5 C step per command.");

        // While actually defending (warm room, wall above the goal) the step direction is DOWN.
        var warmStep = store.CalculateExpectedSetPoint(25.0, "idle", 26.0);
        AssertEqual(25.5, warmStep, "Defending a warm room steps the wall DOWN toward the goal, never up.");
    }

    public void SensorRhythmWaitsOnlyForSafeCorrections()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var now = DateTimeOffset.UtcNow;
        SeedHomeAssistantReadingTimes(store, now.AddSeconds(-20), now.AddSeconds(-10), now);
        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            24.0,
            "cool",
            "idle",
            null,
            []);

        var waited = store.TryRespectSensorRhythm(
            safeReading,
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out _);

        if (!waited || waitUntil <= now)
        {
            throw new InvalidOperationException("Sensor Rhythm should wait after the learned beat for safe corrections.");
        }

        var hotReading = safeReading with { CurrentTemperatureCelsius = 24.2 };
        var bypassed = store.TryRespectSensorRhythm(
            hotReading,
            22.0,
            bypassForComfort: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (bypassed)
        {
            throw new InvalidOperationException("Sensor Rhythm must step aside when direct cooling is needed.");
        }

        var snapshot = store.GetSnapshot();
        if (snapshot.SensorRhythm.Waiting)
        {
            throw new InvalidOperationException("Sensor Rhythm should clear its wait after comfort bypass.");
        }
    }

    public void SetpointEchoWaitsOnlyForSafeFollowUpCommands()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var now = DateTimeOffset.UtcNow;
        store.RecordCommand("Seed defender setpoint.", 22.0);
        SeedPendingCommandAt(store, now);
        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            24.0,
            "cool",
            "idle",
            null,
            []);

        var waited = store.TryRespectSetpointEcho(
            safeReading,
            bypassForComfort: false,
            now.AddSeconds(1),
            out var waitUntil,
            out _);

        if (!waited || waitUntil <= now)
        {
            throw new InvalidOperationException("Setpoint Echo should wait for Home Assistant confirmation before another safe command.");
        }

        var hotReading = safeReading with { CurrentTemperatureCelsius = 24.2 };
        var bypassed = store.TryRespectSetpointEcho(
            hotReading,
            bypassForComfort: false,
            now.AddSeconds(2),
            out _,
            out _);

        if (bypassed)
        {
            throw new InvalidOperationException("Setpoint Echo must step aside when direct cooling is needed.");
        }

        var echoed = store.TryRespectSetpointEcho(
            safeReading with { SetPointCelsius = 22.0 },
            bypassForComfort: false,
            now.AddSeconds(3),
            out _,
            out _);

        if (echoed)
        {
            throw new InvalidOperationException("Setpoint Echo should clear when Home Assistant reports the pending setpoint.");
        }

        var snapshot = store.GetSnapshot();
        if (snapshot.SetpointEcho.PendingSetPointCelsius is not null || snapshot.SetpointEcho.Waiting)
        {
            throw new InvalidOperationException("Setpoint Echo should have no pending target after Home Assistant echoes it.");
        }
    }

    public void RepeatQuietWaitsOnlyForIdenticalSafeCommands()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        store.RecordCommand("Seed same defender setpoint.", 24.0);
        var now = DateTimeOffset.UtcNow;
        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            26.0,
            "cool",
            "idle",
            null,
            []);

        var waited = store.TryRespectRepeatCommandGuard(
            safeReading,
            24.0,
            bypassRepeatGuard: false,
            now,
            out var waitUntil,
            out _);

        if (!waited || waitUntil <= now)
        {
            throw new InvalidOperationException("Repeat Quiet should wait before sending the same safe setpoint again.");
        }

        var differentSetPoint = store.TryRespectRepeatCommandGuard(
            safeReading,
            23.0,
            bypassRepeatGuard: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (differentSetPoint)
        {
            throw new InvalidOperationException("Repeat Quiet must not hold a different step-down setpoint.");
        }

        var hotReading = safeReading with { CurrentTemperatureCelsius = 24.2 };
        var bypassed = store.TryRespectRepeatCommandGuard(
            hotReading,
            24.0,
            bypassRepeatGuard: false,
            now.AddSeconds(2),
            out _,
            out _);

        if (bypassed)
        {
            throw new InvalidOperationException("Repeat Quiet must step aside when direct cooling is needed.");
        }

        var snapshot = store.GetSnapshot();
        if (snapshot.RepeatCommand.Holding)
        {
            throw new InvalidOperationException("Repeat Quiet should not keep holding after a comfort bypass.");
        }
    }

    public void TugOfWarTruceHoldsAlternatingSafeCorrectionsButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.TugOfWarTruceEnabled = true;
        settings.TugOfWarTruceMinimumFlips = 2;
        settings.TugOfWarTruceWindowMinutes = 12;
        settings.TugOfWarTruceHoldMinutes = 20;
        settings.TugOfWarTruceSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            22.0,
            "cool",
            "idle",
            null,
            []);

        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 24.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 23.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 24.0 });

        var now = DateTimeOffset.UtcNow;
        var held = store.TryRespectTugOfWarTruce(
            safeReading with { SetPointCelsius = 24.0 },
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out var message);

        if (!held || waitUntil <= now || !message.Contains("Tug-of-War", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tug-of-War Truce should hold a safe answer-back after alternating wall changes.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.TugOfWarTruce.Holding
            || snapshot.TugOfWarTruce.FlipCount < 2
            || !snapshot.TugOfWarTruce.DirectionPattern.Contains("down", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tug-of-War Truce snapshot should show the active hold, flip count, and direction pattern.");
        }

        var hotRoom = store.TryRespectTugOfWarTruce(
            safeReading with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 24.0 },
            22.0,
            bypassForComfort: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Tug-of-War Truce must step aside when direct cooling is needed.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.TugOfWarTruce.Holding)
        {
            throw new InvalidOperationException("Tug-of-War Truce should clear its hold after comfort safety takes over.");
        }
    }

    public void TelemetryAlibiWaitsForHouseSignalButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.TelemetryAlibiEnabled = true;
        settings.TelemetryAlibiTriggerTouches = 2;
        settings.TelemetryAlibiMinimumHoldSeconds = 60;
        settings.TelemetryAlibiMaxHoldMinutes = 10;
        settings.TelemetryAlibiSafetyBandCelsius = 1.0;
        settings.TelemetryAlibiUseWeather = false;
        settings.TelemetryAlibiUseSensorBeat = true;
        settings.TelemetryAlibiUsePeakPower = false;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.4,
            25.0,
            "cool",
            "idle",
            null,
            []);

        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 22.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 24.0 });
        store.RecordHomeAssistantReading(safeReading);

        var startedAt = DateTimeOffset.UtcNow;
        var held = store.TryRespectTelemetryAlibi(
            safeReading,
            22.0,
            bypassForComfort: false,
            startedAt,
            out var waitUntil,
            out var message);

        if (!held || waitUntil <= startedAt || !message.Contains("Telemetry alibi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Telemetry Alibi should hold a safe correction after repeated wall touches.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.TelemetryAlibi.Waiting
            || snapshot.TelemetryAlibi.RecentTouchCount < 2
            || !snapshot.TelemetryAlibi.LastSignal.Contains("sensor", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Telemetry Alibi snapshot should show active wait, touch pressure, and latest signal.");
        }

        var hotRoom = store.TryRespectTelemetryAlibi(
            safeReading with { CurrentTemperatureCelsius = 24.2 },
            22.0,
            bypassForComfort: false,
            startedAt.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Telemetry Alibi must step aside when direct cooling is needed.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.TelemetryAlibi.Waiting)
        {
            throw new InvalidOperationException("Telemetry Alibi should clear its hold after comfort safety takes over.");
        }

        var secondStart = startedAt.AddSeconds(2);
        held = store.TryRespectTelemetryAlibi(
            safeReading,
            22.0,
            bypassForComfort: false,
            secondStart,
            out _,
            out _);

        if (!held)
        {
            throw new InvalidOperationException("Telemetry Alibi should start a second safe hold before a fresh signal arrives.");
        }

        SeedHomeAssistantReadingTimes(store, secondStart.AddSeconds(61));
        var released = store.TryRespectTelemetryAlibi(
            safeReading,
            22.0,
            bypassForComfort: false,
            secondStart.AddSeconds(61),
            out _,
            out _);

        if (released)
        {
            throw new InvalidOperationException("Telemetry Alibi should release once a fresh sensor beat arrives after the quiet hold.");
        }
    }

    public void SetpointStillnessWaitsForStableReadingsButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.SetpointStillnessGuardEnabled = true;
        settings.SetpointStillnessTriggerTouches = 2;
        settings.SetpointStillnessRequiredSamples = 3;
        settings.SetpointStillnessMaxHoldSeconds = 120;
        settings.SetpointStillnessToleranceCelsius = 0.05;
        settings.SetpointStillnessSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.4,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 25.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 26.0 });

        var now = DateTimeOffset.UtcNow;
        SeedSetpointStillnessSamples(
            store,
            (now.AddSeconds(-8), 24.0),
            (now.AddSeconds(-4), 25.0),
            (now, 26.0));

        var waited = store.TryRespectSetpointStillness(
            safeReading with { SetPointCelsius = 26.0 },
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out var message);

        if (!waited || waitUntil <= now || !message.Contains("stillness", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Setpoint Stillness should wait until repeated real readings show the wall setpoint settled.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.SetpointStillness.Holding
            || snapshot.SetpointStillness.StableSampleCount >= snapshot.SetpointStillness.RequiredStableSamples
            || snapshot.SetpointStillness.CurrentSetPointCelsius is not 26.0)
        {
            throw new InvalidOperationException("Setpoint Stillness snapshot should show the active hold and incomplete stable-read count.");
        }

        var settledAt = DateTimeOffset.UtcNow;
        SeedSetpointStillnessSamples(
            store,
            (settledAt.AddSeconds(-8), 26.0),
            (settledAt.AddSeconds(-4), 26.0),
            (settledAt, 26.0));
        var released = store.TryRespectSetpointStillness(
            safeReading with { SetPointCelsius = 26.0 },
            22.0,
            bypassForComfort: false,
            settledAt,
            out _,
            out _);

        if (released)
        {
            throw new InvalidOperationException("Setpoint Stillness should release once enough real readings match the same wall setpoint.");
        }

        var hotRoom = store.TryRespectSetpointStillness(
            safeReading with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 26.0 },
            22.0,
            bypassForComfort: false,
            DateTimeOffset.UtcNow,
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Setpoint Stillness must step aside when direct cooling is needed.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.SetpointStillness.Holding)
        {
            throw new InvalidOperationException("Setpoint Stillness should clear its hold after stable reads or comfort bypass.");
        }
    }

    public void CommandCamouflageSpacesSafeFollowUpButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.CommandCamouflageEnabled = true;
        settings.CommandCamouflageMinimumGapSeconds = 120;
        settings.CommandCamouflagePressureExtraSeconds = 0;
        settings.CommandCamouflageSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        store.RecordCommand("Seed helper command.", 23.5);
        var now = DateTimeOffset.UtcNow;
        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            25.0,
            "cool",
            "idle",
            null,
            []);

        var waited = store.TryRespectCommandCamouflage(
            safeReading,
            22.0,
            bypassCommandCamouflage: false,
            now,
            out var waitUntil,
            out var message);

        if (!waited || waitUntil <= now || !message.Contains("camouflage", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Command Camouflage should space a safe follow-up after a recent helper command.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.CommandCamouflage.Holding
            || snapshot.CommandCamouflage.SecondsRemaining <= 0
            || snapshot.CommandCamouflage.RecentCommandCount < 1)
        {
            throw new InvalidOperationException("Command Camouflage snapshot should show the active hold and recent command count.");
        }

        var hotRoom = store.TryRespectCommandCamouflage(
            safeReading with { CurrentTemperatureCelsius = 24.2 },
            22.0,
            bypassCommandCamouflage: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Command Camouflage must step aside when the room is too warm.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.CommandCamouflage.Holding)
        {
            throw new InvalidOperationException("Command Camouflage should clear its hold after a comfort bypass.");
        }
    }

    public void StealthGovernorHoldsSafeHighPressureButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.StealthGovernorEnabled = true;
        settings.StealthGovernorTriggerScore = 20;
        settings.StealthGovernorMinimumHoldMinutes = 4;
        settings.StealthGovernorMaximumHoldMinutes = 4;
        settings.StealthGovernorSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.1,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 25.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 26.0 });

        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectStealthGovernor(
            safeReading with { SetPointCelsius = 26.0 },
            22.0,
            bypassStealthGovernor: false,
            now,
            out var waitUntil,
            out var message);

        if (!waited || waitUntil <= now || !message.Contains("Stealth", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Stealth Governor should hold a safe correction when overall pressure is high.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.StealthGovernor.Holding
            || snapshot.StealthGovernor.Score < settings.StealthGovernorTriggerScore
            || snapshot.StealthGovernor.RecentTouchCount < 2)
        {
            throw new InvalidOperationException("Stealth Governor snapshot should show the active hold, score, and recent touches.");
        }

        var hotRoom = store.TryRespectStealthGovernor(
            safeReading with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 26.0 },
            22.0,
            bypassStealthGovernor: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Stealth Governor must step aside when the room is too warm.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.StealthGovernor.Holding)
        {
            throw new InvalidOperationException("Stealth Governor should clear its hold after comfort safety takes over.");
        }
    }

    public void HumanNudgeShapesOnlySafeCommandsAndBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.HumanNudgeEnabled = true;
        settings.HumanNudgeTriggerTouches = 2;
        settings.HumanNudgeStepCelsius = 0.5;
        settings.HumanNudgeSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.1,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 25.0 });
        store.RecordHomeAssistantReading(safeReading);

        var shaped = store.CalculateHumanNudgeCommandSetPoint(
            safeReading,
            expectedSetPointCelsius: 22.0,
            candidateSetPointCelsius: 22.8,
            bypassHumanNudge: false);

        AssertEqual(23.5, shaped, "Human Nudge should turn a safe odd command into one normal thermostat step.");

        var snapshot = store.GetSnapshot();
        if (!snapshot.HumanNudge.Active
            || snapshot.HumanNudge.LastSetPointCelsius is not 23.5
            || snapshot.HumanNudge.RecentTouchCount < 2)
        {
            throw new InvalidOperationException("Human Nudge snapshot should show the shaped setpoint and touch evidence.");
        }

        var hotRoom = store.CalculateHumanNudgeCommandSetPoint(
            safeReading with { CurrentTemperatureCelsius = 24.2 },
            expectedSetPointCelsius: 22.0,
            candidateSetPointCelsius: 22.8,
            bypassHumanNudge: false);

        AssertEqual(22.8, hotRoom, "Human Nudge must leave direct warm-room cooling commands alone.");

        snapshot = store.GetSnapshot();
        if (snapshot.HumanNudge.Active)
        {
            throw new InvalidOperationException("Human Nudge should clear after a comfort bypass.");
        }
    }

    public void WebsiteCommandDebounceIsOffByDefault()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;

        var first = store.TryBeginWebsiteCommand("force cooling");
        var second = store.TryBeginWebsiteCommand("force exact target");
        if (!first.Accepted || !second.Accepted)
        {
            throw new InvalidOperationException("With debounce off (the default), every website command must be accepted immediately.");
        }

        if (second.Snapshot.WebsiteCommandDebounce.Active)
        {
            throw new InvalidOperationException("With debounce off, no debounce window may be armed.");
        }
    }

    public void GentleSteppingPacesWalkDownAndPreemptsCompressorStop()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var s = store.GetSettings();
        s.CoolingStepMinimumGapSeconds = 180;
        SetRuntimeProperty(store, "Settings", s);

        // First correction lands normally: 0.5 C below the room.
        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "First warm-room step starts 0.5 C below the room.");

        // Immediately asking again must NOT step further — the pacing gap holds it steady.
        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "A second idle step within the pacing gap must hold, not fire another command.");
        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "Still holding inside the pacing gap.");

        // Pre-empt: the AC is cooling and the room is about to reach the setpoint (within 0.4 C).
        // With the anti-flap satisfied, nudge one 0.5 C step lower BEFORE the unit stops.
        SetRuntimeProperty(store, "LastWalkStepAt", DateTimeOffset.UtcNow.AddSeconds(-200));
        AssertEqual(24.0, store.CalculateExpectedSetPoint(24.7, "cooling"), "While cooling, a room within 0.4 C of the setpoint gets the next 0.5 C step before the compressor stops.");

        // But never a burst: right after that step the pre-empt is paced too.
        AssertEqual(24.0, store.CalculateExpectedSetPoint(24.2, "cooling"), "No immediate second pre-empt step.");

        // And the floor is absolute: with the gap satisfied, walking continues but stops at 22.0.
        for (var i = 0; i < 8; i++)
        {
            SetRuntimeProperty(store, "LastWalkStepAt", DateTimeOffset.UtcNow.AddSeconds(-200));
            var setPoint = store.CalculateExpectedSetPoint(24.7, "cooling");
            if (setPoint < 22.0)
            {
                throw new InvalidOperationException($"Walk-down went to {setPoint:0.0} C, below the user's 22.0 C.");
            }
        }
    }

    public void PeaceOfferingConcedesUpwardOnAppRaiseThenStandsDown()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        // Baseline reading, then an app user (user_id attached) raises the setpoint 23.0 -> 24.5.
        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room", 23.5, 23.0, "cool", "idle", null, []));
        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room", 23.5, 24.5, "cool", "idle", null, [],
            new HomeAssistantStateContext("ctx-1", null, "app-user-1")));

        var reading = new ThermostatReading("climate.dining_room", 23.5, 24.5, "cool", "idle", null, []);
        if (!store.TryBeginPeaceOffering(reading, DateTimeOffset.UtcNow, out var gift, out _, out _) || gift is null)
        {
            throw new InvalidOperationException("An app-sourced raise must arm a one-shot peace offering.");
        }

        AssertEqual(25.0, gift.Value, "The gift goes one step ABOVE what they asked for (24.5 + 0.5).");

        // The gesture holds: no second command, corrections stand down.
        if (!store.TryBeginPeaceOffering(reading, DateTimeOffset.UtcNow, out var second, out _, out _) || second is not null)
        {
            throw new InvalidOperationException("After the gift, the defender must hold without sending more commands.");
        }

        // A genuinely hot room cancels the hold — comfort still wins.
        var hotReading = new ThermostatReading("climate.dining_room", 25.5, 25.0, "cool", "idle", null, []);
        if (store.TryBeginPeaceOffering(hotReading, DateTimeOffset.UtcNow, out _, out _, out _))
        {
            throw new InvalidOperationException("A room above target + safety override must cancel the peace-offering hold.");
        }

        // A LOWERING app change must not arm anything (they want colder; going up would be war).
        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room", 23.5, 23.0, "cool", "idle", null, [],
            new HomeAssistantStateContext("ctx-2", null, "app-user-1")));
        if (store.TryBeginPeaceOffering(reading, DateTimeOffset.UtcNow, out _, out _, out _))
        {
            throw new InvalidOperationException("Lowering the setpoint from the app must not trigger a peace offering.");
        }

        // A wall/device raise (no user_id) DOES arm the offering too — household changes arrive as
        // "thermostat-device" in the live logs, and they deserve the same concession.
        store.RecordHomeAssistantReading(new ThermostatReading(
            "climate.dining_room", 23.5, 24.0, "cool", "idle", null, []));
        if (!store.TryBeginPeaceOffering(reading, DateTimeOffset.UtcNow, out var wallGift, out _, out _) || wallGift is null)
        {
            throw new InvalidOperationException("A wall/device raise must also trigger the peace offering.");
        }

        AssertEqual(24.5, wallGift.Value, "The wall raise 23.0 -> 24.0 earns a 24.5 C gift.");
    }

    public void CoolingRestStopsAnUnreachableTargetFromRunningForever()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(23.0);

        // The AC has been cooling for 4 hours straight and the room is still hot: rest kicks in.
        SetRuntimeProperty(store, "CoolingRunStartedAt", DateTimeOffset.UtcNow.AddHours(-4));
        var hotReading = new ThermostatReading("climate.dining_room", 26.5, 26.0, "cool", "cooling", null, []);
        if (!store.TryBeginCoolingRest(hotReading, DateTimeOffset.UtcNow, out _, out var until, out _) || until is null)
        {
            throw new InvalidOperationException("Four hours of continuous cooling without reaching the target must trigger a rest.");
        }

        // During the rest the setpoint is eased gently ABOVE the room (0.5 C steps) so the unit stops.
        if (!store.TryBeginCoolingRest(hotReading, DateTimeOffset.UtcNow, out var easeUp, out _, out _) || easeUp is not { } step)
        {
            throw new InvalidOperationException("The rest must ease the setpoint upward so the AC actually stops.");
        }

        AssertEqual(26.5, step, "The ease-up moves 0.5 C at a time toward just above the room.");

        // After the rest window, normal duty resumes with a fresh run clock.
        SetRuntimeProperty(store, "CoolingRestUntil", DateTimeOffset.UtcNow.AddSeconds(-1));
        if (store.TryBeginCoolingRest(hotReading, DateTimeOffset.UtcNow, out _, out _, out _))
        {
            throw new InvalidOperationException("An expired rest must release the worker back to normal duty.");
        }

        // A satisfied room never triggers a rest — the AC stops on its own.
        SetRuntimeProperty(store, "CoolingRunStartedAt", DateTimeOffset.UtcNow.AddHours(-9));
        var satisfiedReading = new ThermostatReading("climate.dining_room", 22.8, 23.0, "cool", "cooling", null, []);
        if (store.TryBeginCoolingRest(satisfiedReading, DateTimeOffset.UtcNow, out _, out _, out _))
        {
            throw new InvalidOperationException("A room at target must not trigger a rest; the AC stops naturally.");
        }
    }

    public void AutoBrotherMadFiresOnRageWithoutAnyButton()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(23.0);

        // One big angry raise (>= 2.0 C) triggers the auto-apology on its own.
        store.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 23.0, "cool", "idle", null, []));
        store.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 26.5, "cool", "idle", null, []));
        var snapshot = store.GetSnapshot();
        if (!snapshot.Emergency.Active || !snapshot.Emergency.Protocol.Contains("auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A single big raise must trigger the automatic brother-mad apology.");
        }

        if (!snapshot.DefenderEnabled)
        {
            throw new InvalidOperationException("The auto-apology stands down corrections but must not fully pause the defender.");
        }

        // Touch-burst variant: three external changes inside the window on a fresh store.
        using var fixture2 = DefenderStoreFixture.Create();
        var store2 = fixture2.Store;
        store2.SetTarget(23.0);
        store2.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 23.0, "cool", "idle", null, []));
        store2.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 23.5, "cool", "idle", null, []));
        store2.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 24.0, "cool", "idle", null, []));
        store2.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 24.5, "cool", "idle", null, []));
        var burst = store2.GetSnapshot();
        if (!burst.Emergency.Active || !burst.Emergency.Protocol.Contains("auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Three quick external touches must trigger the automatic brother-mad apology.");
        }
    }

    public void StandDownParkingRaisesOnlyWhenAppropriate()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(23.0);

        // Cool mode with a low setpoint: park at 28.
        var cool = new ThermostatReading("climate.dining_room", 24.0, 23.0, "cool", "idle", null, []);
        if (!store.TryGetStandDownPark(cool, out var park))
        {
            throw new InvalidOperationException("Standing down in cool mode with a low setpoint must park the thermostat.");
        }

        AssertEqual(28.0, park, "The default park setpoint is 28.0 C.");

        // Already above the park value: leave it alone (never lower).
        var warm = new ThermostatReading("climate.dining_room", 24.0, 29.0, "cool", "idle", null, []);
        if (store.TryGetStandDownPark(warm, out _))
        {
            throw new InvalidOperationException("Parking must never lower a setpoint that is already above the park value.");
        }

        // Mode off: nothing to park (never turn the unit on).
        var off = new ThermostatReading("climate.dining_room", 24.0, 23.0, "off", "off", null, []);
        if (store.TryGetStandDownPark(off, out _))
        {
            throw new InvalidOperationException("Parking must not touch a unit that is off.");
        }
    }

    public void BrotherMadProtocolStandsDownForTwoHours()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(23.0);

        store.ActivateEmergencyQuiet(
            "Brother upset",
            TimeSpan.FromHours(2),
            "Brother-mad apology active.",
            pauseDefender: false);

        var snapshot = store.GetSnapshot();
        if (!snapshot.Emergency.Active || snapshot.Emergency.SecondsRemaining < 7000)
        {
            throw new InvalidOperationException("The brother-mad apology must hold an emergency quiet window of about 2 hours.");
        }

        if (!snapshot.DefenderEnabled)
        {
            throw new InvalidOperationException("Brother-mad stands down corrections but must not fully pause the defender.");
        }

        if (!store.TryRespectEmergencyQuiet(DateTimeOffset.UtcNow, out var quietUntil, out _) || quietUntil <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The worker must hold the whole cycle while the brother-mad window is active.");
        }
    }

    public void NightShutdownTurnsAcOffOnceAndStandsDown()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(23.0);

        var s = store.GetSettings();
        s.NightShutdownEnabled = true;
        s.NightShutdownStartTime = "01:00";
        s.NightShutdownEndTime = "08:00";
        s.NightShutdownOutdoorBelowCelsius = 24.0;
        SetRuntimeProperty(store, "Settings", s);

        store.RecordWeatherReading(new WeatherReading("weather.home", 20.0, "clear"));

        var night = new DateTimeOffset(DateTime.Now.Date.AddHours(2)); // 02:00 local
        var coolReading = new ThermostatReading("climate.dining_room", 24.0, 23.0, "cool", "cooling", null, []);

        if (!store.TryBeginNightShutdown(coolReading, night, out _, out _, out var turnOff) || !turnOff)
        {
            throw new InvalidOperationException("Entering the night window with a cool outdoor reading must trigger a single AC-off command.");
        }

        // Someone turns the AC back on mid-window: respected — no second off command, but still standing down.
        if (!store.TryBeginNightShutdown(coolReading, night.AddMinutes(10), out _, out _, out var secondOff) || secondOff)
        {
            throw new InvalidOperationException("The off command must be sent only once per window; manual re-enables are respected.");
        }

        // Hot night: no shutdown, but PASSIVE WATCH — the defender holds (no off command) and
        // sends no corrections, because history shows night-time fights are the angriest.
        store.RecordWeatherReading(new WeatherReading("weather.home", 27.0, "hot"));
        if (!store.TryBeginNightShutdown(coolReading, night.AddMinutes(20), out _, out _, out var hotNightOff) || hotNightOff)
        {
            throw new InvalidOperationException("A hot night must enter passive watch (hold, no off command) instead of normal fighting.");
        }

        // But a genuinely overheating room breaks passive watch so comfort can win.
        var overheating = new ThermostatReading("climate.dining_room", 26.5, 23.0, "cool", "cooling", null, []);
        if (store.TryBeginNightShutdown(overheating, night.AddMinutes(25), out _, out _, out _))
        {
            throw new InvalidOperationException("A room far above target must break night passive watch.");
        }

        // Outside the window: normal duty.
        store.RecordWeatherReading(new WeatherReading("weather.home", 20.0, "clear"));
        var morning = new DateTimeOffset(DateTime.Now.Date.AddHours(9)); // 09:00 local
        if (store.TryBeginNightShutdown(coolReading, morning, out _, out _, out _))
        {
            throw new InvalidOperationException("Night shutdown must not hold outside its window.");
        }
    }

    private static void EnableWebsiteDebounce(DefenderStateStore store)
    {
        var s = store.GetSettings();
        s.WebsiteCommandDebounceEnabled = true;
        SetRuntimeProperty(store, "Settings", s);
    }

    public void WebsiteCommandDebounceBlocksRapidControlsForTwoMinutes()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        EnableWebsiteDebounce(store);

        var first = store.TryBeginWebsiteCommand("force cooling");
        if (!first.Accepted)
        {
            throw new InvalidOperationException("First website command should be accepted.");
        }

        if (!first.Snapshot.WebsiteCommandDebounce.Active
            || first.Snapshot.WebsiteCommandDebounce.SecondsRemaining < 110
            || first.Snapshot.WebsiteCommandDebounce.DebounceSeconds != 120)
        {
            throw new InvalidOperationException("First website command should start a two-minute debounce countdown.");
        }

        var second = store.TryBeginWebsiteCommand("force exact target");
        if (second.Accepted)
        {
            throw new InvalidOperationException("Second rapid website command should be blocked.");
        }

        if (!second.Snapshot.WebsiteCommandDebounce.Active
            || !second.Message.Contains("wait", StringComparison.OrdinalIgnoreCase)
            || second.Snapshot.WebsiteCommandDebounce.LastCommand != "force cooling")
        {
            throw new InvalidOperationException("Blocked website command should report the active debounce and previous command.");
        }

        SetRuntimeProperty(store, "WebsiteCommandDebounceUntil", DateTimeOffset.UtcNow.AddSeconds(-1));
        var afterExpiry = store.TryBeginWebsiteCommand("force exact target");
        if (!afterExpiry.Accepted)
        {
            throw new InvalidOperationException("Website debounce should reopen after its expiry time.");
        }
    }

    public void WebsiteCommandDebounceCanBypassNonThermostatButtons()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        EnableWebsiteDebounce(store);

        var first = store.TryBeginWebsiteCommand("force cooling");
        if (!first.Accepted)
        {
            throw new InvalidOperationException("First thermostat-affecting command should be accepted.");
        }

        var settingsSave = store.TryBeginWebsiteCommand("save settings", bypassDebounce: true);
        if (!settingsSave.Accepted)
        {
            throw new InvalidOperationException("Settings save should bypass the thermostat debounce.");
        }

        if (!settingsSave.Snapshot.WebsiteCommandDebounce.Active
            || settingsSave.Snapshot.WebsiteCommandDebounce.LastCommand != "force cooling")
        {
            throw new InvalidOperationException("Bypassed commands should not overwrite the active thermostat debounce owner.");
        }

        var defenderToggle = store.TryBeginWebsiteCommand("pause defender", bypassDebounce: true);
        if (!defenderToggle.Accepted)
        {
            throw new InvalidOperationException("Defender toggle should bypass the thermostat debounce.");
        }

        // Target adjustments are website-only state changes; the adjust-then-save flow must never
        // be blocked by an armed debounce window.
        var adjustTarget = store.TryBeginWebsiteCommand("set target", bypassDebounce: true);
        if (!adjustTarget.Accepted)
        {
            throw new InvalidOperationException("Target adjustments should bypass the thermostat debounce.");
        }

        var nextThermostatCommand = store.TryBeginWebsiteCommand("force exact target");
        if (nextThermostatCommand.Accepted)
        {
            throw new InvalidOperationException("A second thermostat-affecting command should still be blocked.");
        }
    }

    public void CoolingFailureAlertsWhenCoolingDemandStaysIdle()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        var reading = new ThermostatReading(
            "climate.dining_room",
            24.0,
            22.0,
            "cool",
            "idle",
            null,
            []);

        store.RecordHomeAssistantReading(reading);
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));
        var snapshot = store.RecordHomeAssistantReading(reading);
        if (!snapshot.CoolingFailure.Alerting || !snapshot.CoolingFailure.Status.Contains("MEGA ALERT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cooling failure should mega-alert when cooling demand stays idle.");
        }

        snapshot = store.RecordHomeAssistantReading(reading with { HvacAction = "cooling", CurrentTemperatureCelsius = 23.6 });
        if (snapshot.CoolingFailure.Alerting)
        {
            throw new InvalidOperationException("Cooling failure should clear after Home Assistant reports cooling and room temperature improves.");
        }
    }

    public void OmegaAlertEscalatesOnlyWhenRoomRisesDuringIdleCoolingFailure()
    {
        var idleWarm = new ThermostatReading("climate.dining_room", 24.0, 22.0, "cool", "idle", null, []);

        // Rising room (+0.6 C over the window) during an armed idle cooling failure -> OMEGA confirmed.
        using (var fixture = DefenderStoreFixture.Create())
        {
            var store = fixture.Store;
            store.SetTarget(22.0);
            SeedRoomSample(store, DateTimeOffset.UtcNow.AddMinutes(-6), 23.4);
            SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));

            var snapshot = store.RecordHomeAssistantReading(idleWarm);
            if (!snapshot.CoolingFailure.Alerting)
            {
                throw new InvalidOperationException("Idle cooling demand should mega-alert.");
            }

            if (!snapshot.CoolingFailure.OmegaAlerting
                || !snapshot.CoolingFailure.Status.Contains("OMEGA ALERT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A sustained room rise during an idle cooling failure should escalate to OMEGA.");
            }
        }

        // Flat room (no rise) under the identical mega-alert conditions must NOT escalate (false-positive guard).
        using (var fixture = DefenderStoreFixture.Create())
        {
            var store = fixture.Store;
            store.SetTarget(22.0);
            SeedRoomSample(store, DateTimeOffset.UtcNow.AddMinutes(-6), 24.0);
            SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));

            var snapshot = store.RecordHomeAssistantReading(idleWarm);
            if (!snapshot.CoolingFailure.Alerting)
            {
                throw new InvalidOperationException("Flat idle cooling demand should still mega-alert.");
            }

            if (snapshot.CoolingFailure.OmegaAlerting)
            {
                throw new InvalidOperationException("A flat room (no sustained rise) must NOT escalate to OMEGA.");
            }
        }
    }

    public void CoolingFailureStaysQuietWhenRoomIsAtUserTarget()
    {
        // False positive root cause: the defender pins the wall setpoint to ~room-1 C, so the old
        // setpoint-only demand check was perpetually true. Here the room is essentially AT the user's
        // target, so the target-aware demand must read FALSE and no mega alert may fire even with a
        // stale armed timer.
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        // current-setPoint = 1.3 (old demand TRUE) but current-target = 0.3 < 0.6 (new demand FALSE).
        var reading = new ThermostatReading("climate.dining_room", 22.3, 21.0, "cool", "idle", null, []);
        store.RecordHomeAssistantReading(reading);
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));
        var snapshot = store.RecordHomeAssistantReading(reading);

        if (snapshot.CoolingFailure.Alerting || snapshot.CoolingFailure.Status.Contains("MEGA ALERT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No mega alert may fire while the room is at the user's comfort target.");
        }
    }

    public void CoolingFailureStaysQuietWhileRoomStillCoolingDown()
    {
        // Normal compressor cycling: the unit cut out (idle) but the room is still coasting down from
        // residual cooling. While the room is improving and not far above target, no mega alert.
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        SeedRoomSample(store, DateTimeOffset.UtcNow.AddMinutes(-6), 24.0);

        var reading = new ThermostatReading("climate.dining_room", 23.0, 22.0, "cool", "idle", null, []);
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));
        var snapshot = store.RecordHomeAssistantReading(reading); // rise = 23.0-24.0 = -1.0 (improving), 23.0 < 22+2.0

        if (snapshot.CoolingFailure.Alerting)
        {
            throw new InvalidOperationException("No mega alert while the room is still cooling down within the safe band.");
        }
    }

    public void CoolingFailureStaysQuietWhenActionInconclusiveAndRoomNotRising()
    {
        // Flaky integration: hvac_action is 'unknown'. With a flat room inside the safe band that is
        // inconclusive (not proof of failure), so no alert.
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        SeedRoomSample(store, DateTimeOffset.UtcNow.AddMinutes(-6), 23.0);

        var reading = new ThermostatReading("climate.dining_room", 23.0, 22.0, "cool", "unknown", null, []);
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));
        var snapshot = store.RecordHomeAssistantReading(reading); // rise = 0.0, action inconclusive, 23.0 < 24.0

        if (snapshot.CoolingFailure.Alerting)
        {
            throw new InvalidOperationException("Inconclusive action with a non-rising room inside the safe band must not mega-alert.");
        }
    }

    public void CoolingFailureMegaAndOmegaWhenBreakerOffAndRoomRising()
    {
        // Real failure (dead breaker): idle while demanded AND the room is rising above target -> OMEGA.
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        SeedRoomSample(store, DateTimeOffset.UtcNow.AddMinutes(-6), 24.0);

        var reading = new ThermostatReading("climate.dining_room", 24.8, 23.8, "cool", "idle", null, []);
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));
        var snapshot = store.RecordHomeAssistantReading(reading); // rise = +0.8 >= 0.4

        if (!snapshot.CoolingFailure.Alerting || !snapshot.CoolingFailure.Status.Contains("MEGA ALERT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A dead breaker with a rising room must still mega-alert.");
        }
        if (!snapshot.CoolingFailure.OmegaAlerting || !snapshot.CoolingFailure.Status.Contains("OMEGA ALERT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A sustained rise above target while idle must escalate to OMEGA.");
        }
    }

    public void CoolingFailureMegaWhenFarAboveTargetEvenIfRoomDrifsDown()
    {
        // Adversarial regression: a dead dining-room unit (idle) whose room is being slowly cooled by a
        // DIFFERENT source (another zone / ambient drift). The room is drifting DOWN, but it is far above
        // the user's real target, so the "room improving" suppression must NOT mask the dead unit.
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        SeedRoomSample(store, DateTimeOffset.UtcNow.AddMinutes(-6), 25.8);

        var reading = new ThermostatReading("climate.dining_room", 25.5, 24.5, "cool", "idle", null, []);
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-12));
        var snapshot = store.RecordHomeAssistantReading(reading); // rise = -0.3 (improving) BUT 25.5 >= 22+2.0

        if (!snapshot.CoolingFailure.Alerting || !snapshot.CoolingFailure.Status.Contains("MEGA ALERT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A dead unit holding the room far above target must mega-alert even while it slowly drifts down.");
        }
        if (snapshot.CoolingFailure.OmegaAlerting)
        {
            throw new InvalidOperationException("A drifting-down room must not escalate to OMEGA (no rise).");
        }
    }

    public void CoolingFailureMegaWhenCoolingActionButRoomNotDropping()
    {
        // Frozen coil / stuck compressor: action reports 'cooling' but the room barely moves over 20 min
        // while above target. This branch is unchanged by the fix and must still mega-alert.
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        SeedRoomSample(store, DateTimeOffset.UtcNow.AddSeconds(-1230), 25.0);

        var reading = new ThermostatReading("climate.dining_room", 24.95, 23.95, "cool", "cooling", null, []);
        var snapshot = store.RecordHomeAssistantReading(reading); // drop = 25.0-24.95 = 0.05 < 0.2 over 20 min

        if (!snapshot.CoolingFailure.Alerting || !snapshot.CoolingFailure.Status.Contains("MEGA ALERT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cooling action with no real room drop over 20 min must still mega-alert (frozen coil).");
        }
    }

    public void AngerButtonLearnsUpsetAndRaisesThisHourSensitivity()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        store.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 22.5, 21.5, "cool", "idle", null, []));

        if (store.GetSnapshot().AngerLearning.EventCount != 0)
        {
            throw new InvalidOperationException("No anger events should exist before any upset press.");
        }

        // Press the someone-upset button.
        store.ActivateEmergencyQuiet("Someone upset", TimeSpan.FromMinutes(45), "Someone-upset quiet mode active.", pauseDefender: false);

        var after = store.GetSnapshot().AngerLearning;
        if (after.EventCount != 1)
        {
            throw new InvalidOperationException("Pressing someone-upset should record exactly one anger event.");
        }
        if (after.CurrentHourSensitivity <= 0)
        {
            throw new InvalidOperationException("This hour's learned sensitivity should rise after an upset press.");
        }
        if (after.ExtraGraceMinutes <= 0)
        {
            throw new InvalidOperationException("A sensitive hour should add hands-off grace minutes.");
        }

        // A non-anger emergency (suspicion) must NOT record an anger event.
        store.ActivateEmergencyQuiet("Suspicion quiet", TimeSpan.FromMinutes(90), "Suspicion quiet.", pauseDefender: false);
        if (store.GetSnapshot().AngerLearning.EventCount != 1)
        {
            throw new InvalidOperationException("Only the someone-upset button should record anger events.");
        }
    }

    public void HistoryLearningBuildsHumanComfortProfileAndCadence()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var anchor = DateTimeOffset.UtcNow.AddDays(-2);
        var hourStart = new DateTimeOffset(anchor.Year, anchor.Month, anchor.Day, anchor.Hour, 0, 0, TimeSpan.Zero);
        var samples = new List<ClimateHistorySample>
        {
            // Human wall choice (20.0 is NOT room-1 of 24.0) — counts for the profile.
            new(hourStart, 20.0, 24.0, "cool", "idle", "user-abc"),
            new(hourStart.AddMinutes(2), 20.0, 24.0, "cool", "idle", "user-abc"),
            // Defender-style room-minus-approach command (23.5 == 24.0-0.5) — must be filtered OUT of the profile.
            new(hourStart.AddMinutes(5), 23.5, 24.0, "cool", "cooling", null),
            // Another human wall choice.
            new(hourStart.AddMinutes(9), 20.5, 24.5, "cool", "idle", "user-abc"),
        };

        var result = store.LearnFromThermostatHistory(samples, DateTimeOffset.UtcNow);

        if (result.LearnedHourCount < 1)
        {
            throw new InvalidOperationException("History learning should learn at least one hour's human comfort profile.");
        }
        if (result.MedianTouchIntervalMinutes is not { } cadence || cadence <= 0)
        {
            throw new InvalidOperationException("History learning should compute a human touch cadence.");
        }

        // The learned profile must come from the HUMAN setpoints (~20 C), not the defender's 23.5 command.
        if (result.CurrentHourPreferredSetPointCelsius is not { } preferred || preferred > 21.0)
        {
            throw new InvalidOperationException($"Learned preferred setpoint should reflect the human ~20 C choices, got {result.CurrentHourPreferredSetPointCelsius}.");
        }
    }

    public void MachineLearningTrainerLearnsAngerAndComfortPatterns()
    {
        var trainer = new LearningTrainer();

        // Anger classifier: late-evening + hard defender push = upset; daytime + gentle = benign.
        var angerSamples = new List<(double[] Features, int Label)>();
        for (var i = 0; i < 8; i++)
        {
            angerSamples.Add((LearningTrainer.AngerFeatures(23, 2.5, 4, 1.5), 1)); // 23:00, pushing hard
            angerSamples.Add((LearningTrainer.AngerFeatures(13, 0.4, 0, 0.2), 0)); // 13:00, gentle
        }

        // Comfort regressor: people like it cooler at night (20 C) and warmer midday (23 C).
        var comfortSamples = new List<(double[] Features, double Target)>();
        for (var i = 0; i < 6; i++)
        {
            comfortSamples.Add((LearningTrainer.ComfortFeatures(2), 20.0));
            comfortSamples.Add((LearningTrainer.ComfortFeatures(14), 23.0));
        }

        var model = trainer.Train(angerSamples, comfortSamples, null);

        var upset = trainer.PredictAngerProbability(model, LearningTrainer.AngerFeatures(23, 2.5, 4, 1.5));
        var calm = trainer.PredictAngerProbability(model, LearningTrainer.AngerFeatures(13, 0.4, 0, 0.2));
        if (upset <= 0.5 || calm >= 0.5 || upset <= calm)
        {
            throw new InvalidOperationException($"Anger model should separate upset ({upset:0.00}) from benign ({calm:0.00}) contexts.");
        }

        var night = trainer.PredictPreferredSetPoint(model, LearningTrainer.ComfortFeatures(2));
        var midday = trainer.PredictPreferredSetPoint(model, LearningTrainer.ComfortFeatures(14));
        if (night is not { } n || midday is not { } m)
        {
            throw new InvalidOperationException("Comfort model should predict a setpoint once trained.");
        }
        if (Math.Abs(n - 20.0) > 1.0 || Math.Abs(m - 23.0) > 1.0 || n >= m)
        {
            throw new InvalidOperationException($"Comfort model should fit ~20 C at night and ~23 C midday, got {n:0.0}/{m:0.0}.");
        }
    }

    public void AdjustmentStatisticsSplitByPresenceAndBedroomOccupancy()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        store.RecordWeatherReading(new WeatherReading("weather.home", 30.0, "sunny"));
        store.RecordHomeAssistantReading(new ThermostatReading("climate.dining_room", 24.0, 24.0, "cool", "idle", null, []));

        // Bedroom occupied + tracked person home → two hard cooling commands (20 C).
        store.RecordTrackedContext(new TrackedContextReading("Taylor Swift", true, true, true, true));
        store.RecordCommand("seed", 20.0);
        store.RecordCommand("seed", 20.0);

        // Bedroom empty + person away → one gentler command (23 C).
        store.RecordTrackedContext(new TrackedContextReading("Taylor Swift", true, false, true, false));
        store.RecordCommand("seed", 23.0);

        var stats = store.GetSnapshot().AdjustmentStatistics;
        if (stats.TrackedPersonLabel != "Taylor Swift")
        {
            throw new InvalidOperationException("Statistics should carry the tracked-person label.");
        }
        if (stats.TotalAdjustments != 3 || stats.BedroomOccupied.Count != 2 || stats.BedroomEmpty.Count != 1
            || stats.PersonHome.Count != 2 || stats.PersonAway.Count != 1)
        {
            throw new InvalidOperationException($"Adjustment splits are wrong: total={stats.TotalAdjustments}, occ={stats.BedroomOccupied.Count}, empty={stats.BedroomEmpty.Count}, home={stats.PersonHome.Count}, away={stats.PersonAway.Count}.");
        }
        if (stats.BedroomOccupied.AverageSetPointCelsius is not { } occ || Math.Abs(occ - 20.0) > 0.01
            || stats.BedroomEmpty.AverageSetPointCelsius is not { } empty || Math.Abs(empty - 23.0) > 0.01)
        {
            throw new InvalidOperationException("Average setpoints per split are wrong.");
        }
        if (stats.AverageOutdoorTemperatureCelsius is not { } outdoor || Math.Abs(outdoor - 30.0) > 0.01)
        {
            throw new InvalidOperationException("Outdoor temperature should be captured per adjustment.");
        }
        if (!stats.Insight.Contains("cooler", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Insight should note cooler-when-occupied, got: {stats.Insight}");
        }
    }

    public void OutdoorPowerRuleSilencesWhenColdLiteWhenMildButYieldsToHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var now = DateTimeOffset.UtcNow;
        var comfyReading = new ThermostatReading("climate.dining_room", 22.3, 22.0, "cool", "idle", null, []);

        // Below 20 C outside -> silenced.
        store.RecordWeatherReading(new WeatherReading("weather.home", 18.0, "cloudy"));
        if (!store.TryRespectOutdoorPowerRule(comfyReading, bypassForComfort: false, now, out _, out var coldMsg)
            || !coldMsg.Contains("Silenced", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Defender should be silenced when it is below 20 C outside.");
        }

        // 20-22 C outside, room near target -> lite mode holds.
        store.RecordWeatherReading(new WeatherReading("weather.home", 21.0, "cloudy"));
        if (!store.TryRespectOutdoorPowerRule(comfyReading, bypassForComfort: false, now, out _, out var liteMsg)
            || !liteMsg.Contains("Lite mode", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Defender should run in lite mode between 20 and 22 C outside.");
        }

        // 20-22 C outside, room above the lite band (but below the comfort-override) -> lets it through.
        var warmReading = comfyReading with { CurrentTemperatureCelsius = 23.5 };
        store.RecordWeatherReading(new WeatherReading("weather.home", 21.0, "cloudy"));
        if (store.TryRespectOutdoorPowerRule(warmReading, bypassForComfort: false, now, out _, out _))
        {
            throw new InvalidOperationException("Lite mode must still cool a room more than 1 C above target.");
        }

        // Below 20 C outside but the room is dangerously hot -> comfort safety wins, not silenced.
        var hotReading = comfyReading with { CurrentTemperatureCelsius = 24.5 };
        store.RecordWeatherReading(new WeatherReading("weather.home", 18.0, "cloudy"));
        if (store.TryRespectOutdoorPowerRule(hotReading, bypassForComfort: false, now, out _, out _))
        {
            throw new InvalidOperationException("A genuinely hot room must override the cold-outside silence.");
        }

        // Warm outside (>=22) -> rule does nothing.
        store.RecordWeatherReading(new WeatherReading("weather.home", 26.0, "sunny"));
        if (store.TryRespectOutdoorPowerRule(comfyReading, bypassForComfort: false, now, out _, out _))
        {
            throw new InvalidOperationException("The outdoor power rule must not engage at or above 22 C outside.");
        }
    }

    public void AccountSignupOwnerThenCodeGatedAndValidates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "acd_acct_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var env = new TestWebHostEnvironment(dir);
            var auth = new TwoFactorAuth(new ConfigurationBuilder().Build(), env, NullLogger<TwoFactorAuth>.Instance);

            if (auth.HasAnyAccount)
            {
                throw new InvalidOperationException("No accounts should exist initially.");
            }

            // First account becomes the owner with no code required.
            if (!auth.TryCreateAccount("owner", "secret123", null, out var e1))
            {
                throw new InvalidOperationException($"Owner signup should succeed: {e1}");
            }
            if (!auth.HasAnyAccount)
            {
                throw new InvalidOperationException("Owner account should now exist.");
            }

            // Second account blocked until the owner sets a registration code.
            if (auth.TryCreateAccount("guest", "secret123", null, out _))
            {
                throw new InvalidOperationException("Additional signup must be blocked without a registration code.");
            }

            auth.SetRegistrationCode("letmein");
            if (auth.TryCreateAccount("guest", "secret123", "wrong", out _))
            {
                throw new InvalidOperationException("Wrong registration code must be rejected.");
            }
            if (!auth.TryCreateAccount("guest", "secret123", "letmein", out var e3))
            {
                throw new InvalidOperationException($"Correct registration code should allow signup: {e3}");
            }

            // Duplicate username (case-insensitive) and short password are rejected.
            if (auth.TryCreateAccount("OWNER", "secret123", "letmein", out _))
            {
                throw new InvalidOperationException("Duplicate username must be rejected.");
            }
            if (auth.TryCreateAccount("newbie", "abc", "letmein", out _))
            {
                throw new InvalidOperationException("Short password must be rejected.");
            }

            // Login validation.
            if (!auth.ValidateAccount("owner", "secret123") || auth.ValidateAccount("owner", "wrong") || auth.ValidateAccount("ghost", "secret123"))
            {
                throw new InvalidOperationException("Account validation is wrong.");
            }

            // Accounts persist and reload from disk.
            var reloaded = new TwoFactorAuth(new ConfigurationBuilder().Build(), env, NullLogger<TwoFactorAuth>.Instance);
            if (reloaded.AccountCount != 2 || !reloaded.ValidateAccount("guest", "secret123"))
            {
                throw new InvalidOperationException("Accounts should persist and reload from disk.");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public void EmergencyQuietPausesCorrectionsButKeepsStatus()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        var snapshot = store.ActivateEmergencyQuiet(
            "Suspicion quiet",
            TimeSpan.FromMinutes(15),
            "Suspicion quiet mode active; observing only.",
            pauseDefender: false);

        if (!snapshot.Emergency.Active || snapshot.Emergency.SecondsRemaining <= 0)
        {
            throw new InvalidOperationException("Emergency quiet should be visible in the snapshot.");
        }

        var respected = store.TryRespectEmergencyQuiet(DateTimeOffset.UtcNow, out var waitUntil, out var message);
        if (!respected || waitUntil <= DateTimeOffset.UtcNow || !message.Contains("Suspicion", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Emergency quiet should ask the worker to stand down while it is active.");
        }
    }

    public void WallSettlingWaitsWhileWallThermostatIsStillBeingTouched()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var reading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(reading);
        store.RecordHomeAssistantReading(reading with { SetPointCelsius = 25.0 });
        store.RecordHomeAssistantReading(reading with { SetPointCelsius = 26.0 });

        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectWallSettlingGuard(
            reading with { SetPointCelsius = 26.0 },
            bypassForComfort: false,
            now,
            out var waitUntil,
            out _);

        if (!waited || waitUntil <= now)
        {
            throw new InvalidOperationException("Wall Settling should wait after repeated recent wall touches.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.WallSettling.Holding || snapshot.WallSettling.RecentTouchCount < 2)
        {
            throw new InvalidOperationException("Wall Settling snapshot should show an active hold and recent touch count.");
        }

        var hotRoom = store.TryRespectWallSettlingGuard(
            reading with { CurrentTemperatureCelsius = 23.5, SetPointCelsius = 26.0 },
            bypassForComfort: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Wall Settling must step aside when the room moves above its safety band.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.WallSettling.Holding)
        {
            throw new InvalidOperationException("Wall Settling should clear its hold after comfort safety takes over.");
        }

        using var singleTouchFixture = DefenderStoreFixture.Create();
        var singleTouchStore = singleTouchFixture.Store;
        singleTouchStore.SetTarget(22.0);
        singleTouchStore.RecordHomeAssistantReading(reading);
        singleTouchStore.RecordHomeAssistantReading(reading with { SetPointCelsius = 25.0 });
        var singleTouchWaited = singleTouchStore.TryRespectWallSettlingGuard(
            reading with { SetPointCelsius = 25.0 },
            bypassForComfort: false,
            DateTimeOffset.UtcNow,
            out _,
            out _);

        if (singleTouchWaited)
        {
            throw new InvalidOperationException("Wall Settling should keep watching until the configured touch threshold is reached.");
        }
    }

    public void CoolerIntentFastLaneBypassesQuietTimingForRepeatedCoolerTouches()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var reading = new ThermostatReading(
            "climate.dining_room",
            23.0,
            25.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(reading);
        store.RecordHomeAssistantReading(reading with { SetPointCelsius = 24.0 });
        store.RecordHomeAssistantReading(reading with { SetPointCelsius = 23.0 });

        var snapshot = store.GetSnapshot();
        if (!snapshot.CoolerIntent.Active)
        {
            throw new InvalidOperationException("Cooler Intent Fast Lane should activate after repeated cooler wall touches.");
        }

        if (snapshot.ManualComfortGrace.Active)
        {
            throw new InvalidOperationException("Cooler Intent Fast Lane should clear wall-change grace so cooling can catch up.");
        }

        var now = DateTimeOffset.UtcNow;
        var bypassed = store.ShouldBypassQuietTimingForCoolerIntent(
            reading with { SetPointCelsius = 23.0 },
            now);
        if (!bypassed)
        {
            throw new InvalidOperationException("Cooler Intent Fast Lane should bypass quiet timing while the room is above target.");
        }

        var reachedTarget = store.ShouldBypassQuietTimingForCoolerIntent(
            reading with { CurrentTemperatureCelsius = 22.0, SetPointCelsius = 23.0 },
            now.AddSeconds(1));
        if (reachedTarget)
        {
            throw new InvalidOperationException("Cooler Intent Fast Lane should stop bypassing once the room reaches the website target.");
        }
    }

    public void WeatherDriftWaitsOnlyForSafeStableOutdoorConditions()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 25.0 });
        store.RecordWeatherReading(new WeatherReading("weather.home", 20.0, "sunny"));
        store.RecordWeatherReading(new WeatherReading("weather.home", 20.1, "sunny"));

        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectWeatherDriftGuard(
            safeReading with { SetPointCelsius = 25.0 },
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out _);

        if (!waited || waitUntil <= now)
        {
            throw new InvalidOperationException("Weather Drift should hold safe corrections while outdoor weather is stable.");
        }

        store.RecordWeatherReading(new WeatherReading("weather.home", 20.6, "sunny"));
        var warming = store.TryRespectWeatherDriftGuard(
            safeReading with { SetPointCelsius = 25.0 },
            22.0,
            bypassForComfort: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (warming)
        {
            throw new InvalidOperationException("Weather Drift should let a safe correction continue after real outdoor warming.");
        }

        store.RecordWeatherReading(new WeatherReading("weather.home", 20.7, "sunny"));
        var hotRoom = store.TryRespectWeatherDriftGuard(
            safeReading with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 25.0 },
            22.0,
            bypassForComfort: false,
            now.AddSeconds(2),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Weather Drift must step aside when direct cooling is needed.");
        }
    }

    public void CoolingRunwayWaitsOnlyAfterSafeCoolingStarts()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var idleReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(idleReading);

        var coolingReading = idleReading with { HvacAction = "cooling" };
        store.RecordHomeAssistantReading(coolingReading);
        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectCoolingRunway(
            coolingReading,
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out _);

        if (!waited || waitUntil <= now)
        {
            throw new InvalidOperationException("Cooling Runway should wait after a fresh safe cooling start.");
        }

        var idleAgain = coolingReading with { HvacAction = "idle" };
        var notCooling = store.TryRespectCoolingRunway(
            idleAgain,
            22.0,
            bypassForComfort: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (notCooling)
        {
            throw new InvalidOperationException("Cooling Runway must not hold when Home Assistant is not cooling.");
        }

        store.RecordHomeAssistantReading(idleReading);
        store.RecordHomeAssistantReading(coolingReading);
        var hotReading = coolingReading with { CurrentTemperatureCelsius = 24.2 };
        var bypassed = store.TryRespectCoolingRunway(
            hotReading,
            22.0,
            bypassForComfort: false,
            now.AddSeconds(2),
            out _,
            out _);

        if (bypassed)
        {
            throw new InvalidOperationException("Cooling Runway must step aside when direct cooling is needed.");
        }

        var snapshot = store.GetSnapshot();
        if (snapshot.CoolingRunway.Holding)
        {
            throw new InvalidOperationException("Cooling Runway should not keep holding after a comfort bypass.");
        }
    }

    public void HvacActionAlibiWaitsForRealActionTransitionButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.HvacActionAlibiEnabled = true;
        settings.HvacActionAlibiTriggerTouches = 2;
        settings.HvacActionAlibiTransitionWindowSeconds = 90;
        settings.HvacActionAlibiMaxHoldMinutes = 6;
        settings.HvacActionAlibiSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.3,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 25.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 26.0 });

        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectHvacActionAlibi(
            safeReading with { SetPointCelsius = 26.0 },
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out var message);

        if (!waited || waitUntil <= now || !message.Contains("HVAC alibi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HVAC Alibi should wait for a real action transition after repeated safe wall touches.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.HvacActionAlibi.Waiting
            || snapshot.HvacActionAlibi.CurrentAction != "idle"
            || snapshot.HvacActionAlibi.RecentTouchCount < 2)
        {
            throw new InvalidOperationException("HVAC Alibi snapshot should show the wait, current action, and recent touch count.");
        }

        var coolingReading = safeReading with { SetPointCelsius = 26.0, HvacAction = "cooling" };
        store.RecordHomeAssistantReading(coolingReading);
        var afterTransition = store.TryRespectHvacActionAlibi(
            coolingReading,
            22.0,
            bypassForComfort: false,
            DateTimeOffset.UtcNow,
            out _,
            out _);

        if (afterTransition)
        {
            throw new InvalidOperationException("HVAC Alibi should release when a real hvac_action transition occurs.");
        }

        var hotRoom = store.TryRespectHvacActionAlibi(
            safeReading with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 26.0 },
            22.0,
            bypassForComfort: false,
            DateTimeOffset.UtcNow,
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("HVAC Alibi must step aside when direct cooling is needed.");
        }
    }

    public void ComfortPaceWaitsAfterFrequentWallTouchesButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.NaturalChangePlannerEnabled = true;
        settings.NaturalChangePlannerTriggerTouches = 2;
        settings.NaturalChangePlannerMinimumMinutes = 8;
        settings.NaturalChangePlannerMaximumMinutes = 8;
        settings.NaturalChangePlannerJitterMinutes = 0;
        settings.NaturalChangePlannerSafetyBandCelsius = 1.1;
        settings.NaturalChangePlannerPreferWeatherSlots = false;
        settings.NaturalChangePlannerPreferSensorBeat = false;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.5,
            24.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 25.0 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 26.0 });

        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectNaturalChangePlanner(
            safeReading with { SetPointCelsius = 26.0 },
            22.0,
            bypassNaturalChange: false,
            now,
            out var waitUntil,
            out var message);

        if (!waited || waitUntil <= now || !message.Contains("Comfort Pace", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Comfort Pace should wait for a calm climate slot after frequent wall touches.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.NaturalChangePlanner.Waiting
            || snapshot.NaturalChangePlanner.RecentTouchCount < 2
            || snapshot.NaturalChangePlanner.PlannedReason != "routine comfort-check")
        {
            throw new InvalidOperationException("Comfort Pace snapshot should show the active wait, touch count, and planned reason.");
        }

        var hotRoom = store.TryRespectNaturalChangePlanner(
            safeReading with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 26.0 },
            22.0,
            bypassNaturalChange: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Comfort Pace must step aside when the room is too warm.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.NaturalChangePlanner.Waiting)
        {
            throw new InvalidOperationException("Comfort Pace should clear its wait after comfort safety takes over.");
        }
    }

    public void ComfortEnvelopeObservesSmallSafeWallPreferenceButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.ComfortEnvelopeEnabled = true;
        settings.ComfortEnvelopeTriggerTouches = 2;
        settings.ComfortEnvelopeHoldMinutes = 10;
        settings.ComfortEnvelopeMaxOffsetCelsius = 0.8;
        settings.ComfortEnvelopeSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.4,
            22.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 22.5 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 22.6 });

        var now = DateTimeOffset.UtcNow;
        var held = store.TryRespectComfortEnvelope(
            safeReading with { SetPointCelsius = 22.6 },
            22.0,
            bypassEnvelope: false,
            now,
            out var waitUntil,
            out var message);

        if (!held || waitUntil <= now || !message.Contains("Comfort envelope", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Comfort Envelope should observe a small safe wall preference after repeated touches.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.ComfortEnvelope.Active
            || snapshot.ComfortEnvelope.PreferredSetPointCelsius is not 22.6
            || snapshot.ComfortEnvelope.MinimumAllowedSetPointCelsius is not 21.2
            || snapshot.ComfortEnvelope.MaximumAllowedSetPointCelsius is not 22.8)
        {
            throw new InvalidOperationException("Comfort Envelope snapshot should show the active safe range and preferred wall setpoint.");
        }

        var outsideEnvelope = store.TryRespectComfortEnvelope(
            safeReading with { SetPointCelsius = 23.2 },
            22.0,
            bypassEnvelope: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (outsideEnvelope)
        {
            throw new InvalidOperationException("Comfort Envelope should not hold wall preferences outside its configured range.");
        }

        store.RecordHomeAssistantReading(safeReading);
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 22.5 });
        store.RecordHomeAssistantReading(safeReading with { SetPointCelsius = 22.6 });
        var hotRoom = store.TryRespectComfortEnvelope(
            safeReading with { CurrentTemperatureCelsius = 24.1, SetPointCelsius = 22.6 },
            22.0,
            bypassEnvelope: false,
            now.AddSeconds(2),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Comfort Envelope must step aside when the room is too warm.");
        }
    }

    public void FrontDoorKillSwitchPausesDefenderAndTagsThermostatOffSource()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        var settings = store.GetSettings();
        settings.FrontDoorKillSwitchEnabled = true;
        settings.FrontDoorKillSwitchHoldMinutes = 20;
        settings.FrontDoorKillSwitchRefreshSeconds = 5;
        settings.FrontDoorKillSwitchTurnsThermostatOff = true;
        SetRuntimeProperty(store, "Settings", settings);

        var baseline = new ThermostatReading(
            "climate.dining_room",
            22.0,
            22.0,
            "cool",
            "idle",
            null,
            []);
        store.RecordHomeAssistantReading(baseline);

        var snapshot = store.RecordFrontDoorPersonReadings(
        [
            new FrontDoorPersonReading("binary_sensor.front_door_person", "Front Door Person", "on", true, DateTimeOffset.UtcNow)
        ]);

        if (snapshot.DefenderEnabled
            || !snapshot.FrontDoorKillSwitch.Active
            || !snapshot.FrontDoorKillSwitch.PersonDetected)
        {
            throw new InvalidOperationException("Front-door kill switch should pause the defender and show an active person detection.");
        }

        var respected = store.TryRespectFrontDoorKillSwitch(
            baseline,
            DateTimeOffset.UtcNow,
            out var shouldTurnOff,
            out var waitUntil,
            out var message);
        if (!respected || !shouldTurnOff || waitUntil is null || !message.Contains("Front-door", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Front-door kill switch should ask the worker to turn the thermostat off.");
        }

        store.RecordFrontDoorThermostatOffCommand("climate.dining_room");
        snapshot = store.RecordHomeAssistantReading(baseline with
        {
            SetPointCelsius = 23.0,
            HvacMode = "off",
            HvacAction = "off"
        });

        if (snapshot.ThermostatChanges.Count != 0)
        {
            throw new InvalidOperationException("A thermostat state echo from the front-door kill switch must not be logged as a wall touch.");
        }

        if (!snapshot.Events.Any(item => item.Message.Contains("Front-door kill switch turned", StringComparison.OrdinalIgnoreCase)
            || item.Message.Contains("Known thermostat command echoed", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Front-door thermostat-off source should be visible in activity events.");
        }
    }

    public void SuperDefenderClassifiesRemoteHomeAssistantChangesAndBypassesQuietTiming()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.SuperDefenderModeEnabled = true;
        settings.SuperDefenderRemoteChangeThreshold = 2;
        settings.SuperDefenderWindowMinutes = 30;
        settings.SuperDefenderHoldMinutes = 20;
        settings.SuperDefenderSafetyBandCelsius = 2.0;
        settings.SuperDefenderBypassQuietTiming = true;
        SetRuntimeProperty(store, "Settings", settings);

        var baseline = new ThermostatReading(
            "climate.dining_room",
            23.0,
            22.0,
            "cool",
            "idle",
            null,
            [],
            new HomeAssistantStateContext("ctx-base", null, null));
        store.RecordHomeAssistantReading(baseline);

        var firstRemote = baseline with
        {
            SetPointCelsius = 24.0,
            Context = new HomeAssistantStateContext("ctx-user-1", null, "ha-user")
        };
        store.RecordHomeAssistantReading(firstRemote);
        var snapshot = store.GetSnapshot();
        var latest = snapshot.ThermostatChanges.First();
        if (latest.ChangeSource != "home-assistant-user"
            || latest.ContextUserId != "ha-user"
            || !latest.SourceDetail.Contains("user_id", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Home Assistant user context should classify a thermostat change as phone/Home Assistant user source.");
        }

        var secondRemote = baseline with
        {
            SetPointCelsius = 25.0,
            Context = new HomeAssistantStateContext("ctx-user-2", null, "ha-user")
        };
        store.RecordHomeAssistantReading(secondRemote);
        snapshot = store.GetSnapshot();
        if (!snapshot.SuperDefender.Active
            || snapshot.SuperDefender.RecentRemoteChangeCount < 2
            || snapshot.SuperDefender.LastChangeSource != "home-assistant-user"
            || !snapshot.SuperDefender.NetworkLockdownStatus.Contains("not", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Repeated Home Assistant user changes should arm Super Defender and expose the no-automatic-Wi-Fi-block status.");
        }

        var bypass = store.ShouldBypassQuietTimingForSuperDefender(secondRemote, DateTimeOffset.UtcNow);
        if (!bypass)
        {
            throw new InvalidOperationException("Super Defender should bypass quiet timing while active and room temperature remains above target.");
        }

        var nearTargetBypass = store.ShouldBypassQuietTimingForSuperDefender(
            secondRemote with { CurrentTemperatureCelsius = 22.0 },
            DateTimeOffset.UtcNow);
        if (nearTargetBypass)
        {
            throw new InvalidOperationException("Super Defender should not bypass quiet timing when the room is already at target.");
        }
    }

    public void RemoteSettlingHoldsSafeRemotePatternButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.SuperDefenderModeEnabled = false;
        settings.RemoteSettlingGuardEnabled = true;
        settings.RemoteSettlingTriggerChanges = 2;
        settings.RemoteSettlingWindowMinutes = 30;
        settings.RemoteSettlingHoldMinutes = 12;
        settings.RemoteSettlingSafetyBandCelsius = 1.0;
        SetRuntimeProperty(store, "Settings", settings);

        var baseline = new ThermostatReading(
            "climate.dining_room",
            22.4,
            22.0,
            "cool",
            "idle",
            null,
            [],
            new HomeAssistantStateContext("ctx-base", null, null));
        store.RecordHomeAssistantReading(baseline);
        store.RecordHomeAssistantReading(baseline with
        {
            SetPointCelsius = 24.0,
            Context = new HomeAssistantStateContext("ctx-user-1", null, "ha-user")
        });
        store.RecordHomeAssistantReading(baseline with
        {
            SetPointCelsius = 25.0,
            Context = new HomeAssistantStateContext("ctx-user-2", null, "ha-user")
        });

        var now = DateTimeOffset.UtcNow;
        var waited = store.TryRespectRemoteSettlingGuard(
            baseline with { SetPointCelsius = 25.0 },
            22.0,
            bypassForComfort: false,
            now,
            out var waitUntil,
            out var message);

        if (!waited || waitUntil <= now || !message.Contains("Remote settling", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Remote Settling should hold a safe correction after repeated Home Assistant user changes.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.RemoteSettling.Holding
            || snapshot.RemoteSettling.RecentRemoteChangeCount < 2
            || snapshot.RemoteSettling.LastChangeSource != "home-assistant-user")
        {
            throw new InvalidOperationException("Remote Settling snapshot should show the active hold, source, and recent remote change count.");
        }

        var hotRoom = store.TryRespectRemoteSettlingGuard(
            baseline with { CurrentTemperatureCelsius = 24.2, SetPointCelsius = 25.0 },
            22.0,
            bypassForComfort: false,
            now.AddSeconds(1),
            out _,
            out _);

        if (hotRoom)
        {
            throw new InvalidOperationException("Remote Settling must step aside when direct cooling is needed.");
        }

        snapshot = store.GetSnapshot();
        if (snapshot.RemoteSettling.Holding)
        {
            throw new InvalidOperationException("Remote Settling should clear its hold after comfort safety takes over.");
        }
    }

    // ===================== Desired-State Enforcer =====================

    private static DefenderSettings EnforcerSettings(DefenderStateStore store, Action<DefenderSettings> configure)
    {
        var s = store.GetSettings();
        s.EnforcerModeEnabled = true;
        s.EnforcerEnforceMode = true;
        s.EnforcerEnforceSetpoint = true;
        s.EnforcerStealthShaping = false;
        s.EnforcerRespectOwner = true;
        s.EnforcerOwnerUserIds = "";
        s.EnforcerDebounceSeconds = 8;
        s.EnforcerCooldownSeconds = 30;
        s.EnforcerRateWindowMinutes = 15;
        s.EnforcerMaxAssertsPerWindow = 6;
        s.EnforcerEscalateAfterOverrides = 3;
        s.EnforcerBackoffBaseSeconds = 20;
        s.EnforcerBackoffMaxSeconds = 300;
        s.EnforcerNotifyEnabled = false;
        s.EnforcerUseLearning = false;
        s.EnforcerScheduleEnabled = false;
        s.EnforcerRequirePresence = false;
        configure(s);
        SetRuntimeProperty(store, "Settings", s);
        return s;
    }

    private static ThermostatReading EnforcerReading(string hvacMode, double setPoint, double room, string? userId, double? min = null, double? max = null)
        => new(
            "climate.dining_room",
            room,
            setPoint,
            hvacMode,
            string.Equals(hvacMode, "cool", StringComparison.OrdinalIgnoreCase) ? "idle" : "off",
            null,
            [],
            new HomeAssistantStateContext("ctx", null, userId),
            min,
            max);

    public void EnforcerRestoresCoolWhenTurnedOffByAnotherPerson()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        EnforcerSettings(store, _ => { });

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        var gate = store.EvaluateEnforcer(EnforcerReading("off", 22.0, 24.0, "intruder"), now);

        if (gate.Decision != EnforcerDecision.EnforceMode)
        {
            throw new InvalidOperationException($"Enforcer should restore cool mode when someone else turns the AC off, got {gate.Decision}.");
        }

        AssertEqual(22.0, gate.AssertSetPoint, "Restoring after an off should command the desired target.");

        var snapshot = store.GetSnapshot();
        if (snapshot.Enforcer.LastChangeUser != "intruder")
        {
            throw new InvalidOperationException("Enforcer snapshot should attribute the off to the external user.");
        }
    }

    public void EnforcerSnapsToExactTargetWhenSetpointRaisedByAnotherPerson()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s => s.EnforcerStealthShaping = false);

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);

        if (gate.Decision != EnforcerDecision.EnforceSetpoint)
        {
            throw new InvalidOperationException($"Enforcer should restore the setpoint after an external raise, got {gate.Decision}.");
        }

        AssertEqual(21.0, gate.AssertSetPoint, "Hard mode should snap to the owner's exact target.");
    }

    public void EnforcerRespectsOwnerOwnChange()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s =>
        {
            s.EnforcerRespectOwner = true;
            s.EnforcerOwnerUserIds = "owner-123";
        });

        var now = DateTimeOffset.UtcNow;
        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "owner-123"), now);

        if (gate.Decision != EnforcerDecision.RespectOwner)
        {
            throw new InvalidOperationException($"A change attributed to the owner must be respected, got {gate.Decision}.");
        }

        var snapshot = store.GetSnapshot();
        if (snapshot.Enforcer.RecentAssertCount != 0)
        {
            throw new InvalidOperationException("Respecting the owner must not count an assert.");
        }
    }

    public void EnforcerDebouncesBeforeEnforcing()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s => s.EnforcerDebounceSeconds = 30);

        var now = DateTimeOffset.UtcNow;
        var first = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);
        if (first.Decision != EnforcerDecision.Cooldown || !first.Message.Contains("debounc", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"A fresh deviation should debounce first, got {first.Decision}.");
        }

        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        var afterDebounce = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now.AddSeconds(1));
        if (afterDebounce.Decision != EnforcerDecision.EnforceSetpoint)
        {
            throw new InvalidOperationException($"After the debounce window the enforcer should assert, got {afterDebounce.Decision}.");
        }
    }

    public void EnforcerCooldownWaitsForHomeAssistantToConfirm()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s => s.EnforcerCooldownSeconds = 30);

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        SetRuntimeProperty(store, "EnforcerLastAssertAt", now.AddSeconds(-5));
        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);

        if (gate.Decision != EnforcerDecision.Cooldown || !gate.Message.Contains("confirm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Inside the echo/cooldown window the enforcer must wait, got {gate.Decision}.");
        }
    }

    public void EnforcerBacksOffWhenDeviceRejectsCommands()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s =>
        {
            s.EnforcerCooldownSeconds = 30;
            s.EnforcerBackoffBaseSeconds = 20;
        });

        var now = DateTimeOffset.UtcNow;
        // Cooldown is max(EnforcerCooldownSeconds, max(15, CommandGraceSeconds=120)) = 120s. Put the last
        // assert just past the cooldown so the device-reject backoff (20*2^1 = 40s) is the active gate.
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-200));
        SetRuntimeProperty(store, "EnforcerLastAssertAt", now.AddSeconds(-130));
        SetRuntimeProperty(store, "EnforcerConsecutiveRejects", 2);
        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);

        if (gate.Decision != EnforcerDecision.Backoff || !gate.Message.Contains("backing off", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"A non-confirming device should trigger exponential backoff, got {gate.Decision}.");
        }

        var snapshot = store.GetSnapshot();
        if (snapshot.Enforcer.RecentAssertCount != 0)
        {
            throw new InvalidOperationException("Backoff must not send a new assert.");
        }
    }

    public void EnforcerEscalatesAfterRepeatedOverrides()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s =>
        {
            s.EnforcerStealthShaping = true;
            s.EnforcerEscalateAfterOverrides = 3;
            s.EnforcerNotifyEnabled = true;
        });

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerOverrideTimes", new List<DateTimeOffset>
        {
            now.AddMinutes(-3),
            now.AddMinutes(-2),
            now.AddMinutes(-1)
        });

        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);

        var snapshot = store.GetSnapshot();
        if (!snapshot.Enforcer.Escalated)
        {
            throw new InvalidOperationException("Repeated external overrides should escalate the enforcer.");
        }

        if (!gate.Notify || !gate.NotifyMessage.Contains("interference", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Escalation with notify enabled should raise an interference notification.");
        }
    }

    public void EnforcerRateLimitHoldsInsteadOfThrashing()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s => s.EnforcerMaxAssertsPerWindow = 2);

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        SetRuntimeProperty(store, "EnforcerAssertTimes", new List<DateTimeOffset>
        {
            now.AddMinutes(-2),
            now.AddMinutes(-1)
        });

        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);

        if (gate.Decision != EnforcerDecision.Backoff || !gate.Message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Hitting the rate limit should hold instead of thrashing, got {gate.Decision}.");
        }
    }

    public void EnforcerClampsAssertToDeviceMinMax()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        EnforcerSettings(store, s => s.EnforcerTargetTemperatureCelsius = 10.0);

        // Feed a reading that carries the device's real min/max so the store captures them.
        store.RecordHomeAssistantReading(EnforcerReading("cool", 24.0, 24.0, "intruder", min: 16.0, max: 26.0));

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 24.0, "intruder", min: 16.0, max: 26.0), now);

        if (gate.Decision != EnforcerDecision.EnforceSetpoint)
        {
            throw new InvalidOperationException($"Enforcer should assert when the setpoint is above the (clamped) target, got {gate.Decision}.");
        }

        AssertEqual(16.0, gate.AssertSetPoint, "A below-min desired target must be clamped up to the device minimum.");
    }

    public void EnforcerStealthModeLetsNaturalPipelineHandleSetpointRaise()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s =>
        {
            s.EnforcerStealthShaping = true;
            s.EnforcerEscalateAfterOverrides = 10;
        });

        var now = DateTimeOffset.UtcNow;
        SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-60));
        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);

        if (gate.Decision != EnforcerDecision.Inactive || !gate.Message.Contains("Stealth", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Stealth mode should hand a non-escalated setpoint raise to the natural pipeline, got {gate.Decision}.");
        }
    }

    public void EnforcerInactiveWhenDisabledLeavesStealthPipelineUnchanged()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(21.0);
        EnforcerSettings(store, s => s.EnforcerModeEnabled = false);

        var gate = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), DateTimeOffset.UtcNow);
        if (gate.Decision != EnforcerDecision.Inactive)
        {
            throw new InvalidOperationException($"A disabled enforcer must stay inactive so the stealth pipeline runs, got {gate.Decision}.");
        }

        // The existing pipeline must still be reachable/working when the enforcer is off.
        AssertEqual(24.5, store.CalculateExpectedSetPoint(25.0, "idle"), "The existing warm-room path must remain intact when the enforcer is off.");
    }

    public void EnforcerDoesNotFightOwnWarmRoomCoolingSetpoint()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        EnforcerSettings(store, s => s.EnforcerStealthShaping = true);

        var now = DateTimeOffset.UtcNow;

        // The defender's own warm-room command parks the setpoint at room - 0.5 (= 25.5 for a 26 C room)
        // while it cools toward the 22 C target. That is ABOVE the target but is normal cooling, not an
        // override: the enforcer must not flag it, count it, or escalate.
        var cooling = store.EvaluateEnforcer(EnforcerReading("cool", 25.5, 26.0, "intruder"), now);
        if (cooling.Decision != EnforcerDecision.Inactive)
        {
            throw new InvalidOperationException($"The defender's own warm-room cooling setpoint must not be treated as an override, got {cooling.Decision}.");
        }

        if (store.GetSnapshot().Enforcer.RecentOverrideCount != 0)
        {
            throw new InvalidOperationException("A normal cooling setpoint must not be counted as an override (false-positive guard).");
        }

        // A genuine raise above the cooling plan (27 C while the room is 26 C — high enough to stop the AC
        // cooling) IS an override and must still be caught.
        var raised = store.EvaluateEnforcer(EnforcerReading("cool", 27.0, 26.0, "intruder"), now.AddSeconds(1));
        if (raised.Decision == EnforcerDecision.Inactive)
        {
            throw new InvalidOperationException("A real setpoint raise above the cooling plan must still be caught.");
        }

        if (store.GetSnapshot().Enforcer.RecentOverrideCount != 1)
        {
            throw new InvalidOperationException("A real raise should count exactly one override.");
        }
    }

    public void EnforcerStealthWaitsLongerDuringHighAngerHours()
    {
        var localHour = DateTimeOffset.UtcNow.ToLocalTime().Hour;

        // Baseline: stealth mode, learning on, no anger history. A deviation pending for 10s clears the 8s
        // debounce and is handed to the natural pipeline (Inactive).
        using (var fixture = DefenderStoreFixture.Create())
        {
            var store = fixture.Store;
            store.SetTarget(21.0);
            EnforcerSettings(store, s =>
            {
                s.EnforcerStealthShaping = true;
                s.EnforcerUseLearning = true;
                s.EnforcerDebounceSeconds = 8;
                s.EnforcerEscalateAfterOverrides = 50;
            });

            var now = DateTimeOffset.UtcNow;
            SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-10));
            var baseline = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);
            if (baseline.Decision != EnforcerDecision.Inactive)
            {
                throw new InvalidOperationException($"Without anger history a 10s-old deviation should clear the 8s debounce, got {baseline.Decision}.");
            }
        }

        // High-upset hour: the same 10s-old deviation must still be debouncing because the trained-anger
        // sensitivity stretches the stealth debounce (so the fix is less likely to make anyone mad).
        using (var fixture = DefenderStoreFixture.Create())
        {
            var store = fixture.Store;
            store.SetTarget(21.0);
            EnforcerSettings(store, s =>
            {
                s.EnforcerStealthShaping = true;
                s.EnforcerUseLearning = true;
                s.EnforcerDebounceSeconds = 8;
                s.EnforcerEscalateAfterOverrides = 50;
            });

            var now = DateTimeOffset.UtcNow;
            SeedAngerMemorySlot(store, localHour, 0.9, now);
            SetRuntimeProperty(store, "EnforcerPendingSince", now.AddSeconds(-10));
            var cautious = store.EvaluateEnforcer(EnforcerReading("cool", 24.0, 21.5, "intruder"), now);
            if (cautious.Decision != EnforcerDecision.Cooldown)
            {
                throw new InvalidOperationException($"A high-upset hour should stretch the stealth debounce so the deviation is still waiting, got {cautious.Decision}.");
            }
        }
    }

    public void EnforcerLearningModelsTrainInterferenceAndCadenceFromOverrides()
    {
        var trainer = new LearningTrainer();

        var interferenceSamples = new List<(double[] Features, int Label)>();
        var cadenceSamples = new List<(double[] Features, double Target)>();
        for (var i = 0; i < 8; i++)
        {
            // Evening, owner home, bedroom occupied, room hot -> interference.
            interferenceSamples.Add((LearningTrainer.InterferenceFeatures(21, true, true, false, 3.0, 3), 1));
            // Pre-dawn, away, cool room -> benign.
            interferenceSamples.Add((LearningTrainer.InterferenceFeatures(4, false, false, false, -1.0, 0), 0));
            // People fight roughly every 8 min in the evening, ~45 min midday.
            cadenceSamples.Add((LearningTrainer.OverrideCadenceFeatures(21), 8.0));
            cadenceSamples.Add((LearningTrainer.OverrideCadenceFeatures(13), 45.0));
        }

        var model = trainer.Train([], [], null, interferenceSamples, cadenceSamples);

        var hot = trainer.PredictInterferenceProbability(model, LearningTrainer.InterferenceFeatures(21, true, true, false, 3.0, 3));
        var calm = trainer.PredictInterferenceProbability(model, LearningTrainer.InterferenceFeatures(4, false, false, false, -1.0, 0));
        if (hot <= 0.5 || calm >= 0.5 || hot <= calm)
        {
            throw new InvalidOperationException($"Interference model should separate likely ({hot:0.00}) from unlikely ({calm:0.00}) contexts.");
        }

        var evening = trainer.PredictOverrideCadenceMinutes(model, LearningTrainer.OverrideCadenceFeatures(21));
        var midday = trainer.PredictOverrideCadenceMinutes(model, LearningTrainer.OverrideCadenceFeatures(13));
        if (evening is not { } e || midday is not { } m || e >= m)
        {
            throw new InvalidOperationException($"Cadence model should learn faster evening fighting than midday, got {evening:0.0}/{midday:0.0}.");
        }
    }

    public void EnforcerConsumesTrainedInterferenceModel()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        EnforcerSettings(store, s => s.EnforcerUseLearning = true);

        var now = DateTimeOffset.UtcNow;
        var samples = new List<EnforcerOverrideSample>();
        for (var i = 0; i < 6; i++)
        {
            samples.Add(new EnforcerOverrideSample { At = now.AddMinutes(-i * 7), HourOfDay = 21, OwnerHome = true, BedroomOccupied = true, PeakPower = false, RoomAboveTargetCelsius = 3.0, RecentOverrideCount = 3, Label = 1 });
            samples.Add(new EnforcerOverrideSample { At = now.AddHours(-i - 1), HourOfDay = 4, OwnerHome = false, BedroomOccupied = false, PeakPower = false, RoomAboveTargetCelsius = -1.0, RecentOverrideCount = 0, Label = 0 });
        }

        SetRuntimeProperty(store, "EnforcerOverrideSamples", samples);
        store.TrainLearningModels(now);

        var trainedSnapshot = store.GetSnapshot();
        if (!trainedSnapshot.LearningModel.InterferenceModelTrained || trainedSnapshot.LearningModel.InterferencePositiveSamples <= 0)
        {
            throw new InvalidOperationException("The enforcer override log should train the interference model.");
        }

        // With the model trained and learning enabled, a live evaluation should report the model as active.
        store.RecordHomeAssistantReading(EnforcerReading("cool", 22.0, 22.0, "intruder"));
        store.EvaluateEnforcer(EnforcerReading("cool", 22.0, 22.0, "intruder"), now);
        var snapshot = store.GetSnapshot();
        if (!snapshot.Enforcer.LearningActive)
        {
            throw new InvalidOperationException("With trained data and learning enabled, the enforcer should be using the trained model.");
        }
    }

    public void PeakPowerSaverHoldsSafeCoolingDuringAlectraOnPeakButBypassesHotRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);
        var settings = store.GetSettings();
        settings.PeakPowerSaverEnabled = true;
        settings.PeakPowerSaverOnPeakEnabled = true;
        settings.PeakPowerSaverHighPowerEnabled = true;
        settings.PeakPowerSaverPowerThresholdKilowatts = 2.5;
        settings.PeakPowerSaverPriceThresholdCentsPerKwh = 15.0;
        settings.PeakPowerSaverHoldMinutes = 20;
        settings.PeakPowerSaverRefreshSeconds = 120;
        settings.PeakPowerSaverSafetyBandCelsius = 1.0;
        settings.PeakPowerSaverFanSaverEnabled = true;
        settings.PeakPowerSaverFanMode = "auto";
        SetRuntimeProperty(store, "Settings", settings);

        store.RecordAlectraPeakPowerReading(new AlectraPeakPowerReading(
            true,
            1.8,
            15.8,
            "On-peak",
            "TIERED",
            DateTimeOffset.UtcNow));

        var safeReading = new ThermostatReading(
            "climate.dining_room",
            22.6,
            24.0,
            "cool",
            "idle",
            "on",
            ["auto", "on"]);

        if (!store.ShouldUsePeakPowerFanSaver(safeReading))
        {
            throw new InvalidOperationException("Peak Power Saver should prefer the saver fan mode while the room is inside the safety band.");
        }

        var holdsSafeCooling = store.TryRespectPeakPowerSaver(
            safeReading,
            22.0,
            false,
            DateTimeOffset.UtcNow,
            out var holdUntil,
            out var holdMessage);
        if (!holdsSafeCooling || holdUntil is null || !holdMessage.Contains("Alectra Peak Power Saver", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Peak Power Saver should hold a safe cooling command during On-peak/high-price usage.");
        }

        var hotRoomBypass = store.TryRespectPeakPowerSaver(
            safeReading with { CurrentTemperatureCelsius = 23.4 },
            22.0,
            false,
            DateTimeOffset.UtcNow,
            out _,
            out _);
        if (hotRoomBypass)
        {
            throw new InvalidOperationException("Peak Power Saver must step aside when the room exceeds the configured safety band.");
        }

        var energySavingCommandBypass = store.TryRespectPeakPowerSaver(
            safeReading,
            25.0,
            false,
            DateTimeOffset.UtcNow,
            out _,
            out _);
        if (energySavingCommandBypass)
        {
            throw new InvalidOperationException("Peak Power Saver should allow a command that does not demand more cooling.");
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.PeakPowerSaver.Active
            || snapshot.PeakPowerSaver.CurrentPriceCentsPerKwh != 15.8
            || snapshot.PeakPowerSaver.TouPeriod != "On-peak")
        {
            throw new InvalidOperationException("Peak Power Saver status should expose the live Alectra price and TOU period.");
        }
    }

    private static void AssertEqual(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 0.05)
        {
            throw new InvalidOperationException($"{message} Expected {expected:0.0} C, got {actual:0.0} C.");
        }
    }

    private static void SeedHomeAssistantReadingTimes(DefenderStateStore store, params DateTimeOffset[] readingTimes)
    {
        var state = GetRuntimeState(store);
        var readingTimesProperty = state.GetType().GetProperty("HomeAssistantReadingTimes")
            ?? throw new InvalidOperationException("Could not find HomeAssistantReadingTimes state property.");
        var list = (List<DateTimeOffset>?)readingTimesProperty.GetValue(state)
            ?? throw new InvalidOperationException("Could not read HomeAssistantReadingTimes.");

        list.Clear();
        list.AddRange(readingTimes);
    }

    private static void SeedSetpointStillnessSamples(DefenderStateStore store, params (DateTimeOffset Timestamp, double SetPointCelsius)[] samples)
    {
        var state = GetRuntimeState(store);
        var listProperty = state.GetType().GetProperty("SetpointStillnessSamples")
            ?? throw new InvalidOperationException("Could not find SetpointStillnessSamples state property.");
        var list = (System.Collections.IList?)listProperty.GetValue(state)
            ?? throw new InvalidOperationException("Could not read SetpointStillnessSamples.");
        var elementType = listProperty.PropertyType.GetGenericArguments()[0];
        var constructor = elementType.GetConstructors()[0];

        list.Clear();
        foreach (var sample in samples)
        {
            list.Add(constructor.Invoke([sample.Timestamp, sample.SetPointCelsius]));
        }
    }

    private static void SeedPendingCommandAt(DefenderStateStore store, DateTimeOffset pendingAt)
    {
        SetRuntimeProperty(store, "PendingCommandAt", pendingAt);
    }

    private static void SetRuntimeProperty(DefenderStateStore store, string propertyName, object? value)
    {
        var state = GetRuntimeState(store);
        var property = state.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Could not find {propertyName} state property.");
        property.SetValue(state, value);
    }

    private static void SeedRoomSample(DefenderStateStore store, DateTimeOffset timestamp, double temperatureCelsius)
    {
        var state = GetRuntimeState(store);
        var listProperty = state.GetType().GetProperty("RoomTemperatureSamples")
            ?? throw new InvalidOperationException("Could not find RoomTemperatureSamples state property.");
        var list = (System.Collections.IList?)listProperty.GetValue(state)
            ?? throw new InvalidOperationException("Could not read RoomTemperatureSamples.");
        var elementType = listProperty.PropertyType.GetGenericArguments()[0];
        var sample = elementType.GetConstructors()[0].Invoke([timestamp, temperatureCelsius]);
        list.Add(sample);
    }

    private static void SeedAngerMemorySlot(DefenderStateStore store, int hourOfDay, double angerScore, DateTimeOffset updatedAt)
    {
        var state = GetRuntimeState(store);
        var listProperty = state.GetType().GetProperty("AngerMemorySlots")
            ?? throw new InvalidOperationException("Could not find AngerMemorySlots state property.");
        var list = (System.Collections.IList?)listProperty.GetValue(state)
            ?? throw new InvalidOperationException("Could not read AngerMemorySlots.");
        var elementType = listProperty.PropertyType.GetGenericArguments()[0];
        var slot = Activator.CreateInstance(elementType)
            ?? throw new InvalidOperationException("Could not create an AngerMemorySlot.");
        elementType.GetProperty("HourOfDay")!.SetValue(slot, hourOfDay);
        elementType.GetProperty("AngerScore")!.SetValue(slot, angerScore);
        elementType.GetProperty("UpdatedAt")!.SetValue(slot, updatedAt);
        list.Add(slot);
    }

    private static object GetRuntimeState(DefenderStateStore store)
    {
        var stateField = typeof(DefenderStateStore).GetField("state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find DefenderStateStore state field.");
        return stateField.GetValue(store)
            ?? throw new InvalidOperationException("Could not read DefenderStateStore state.");
    }
}

internal sealed class DefenderStoreFixture : IDisposable
{
    private readonly bool ownsContentRoot;

    private DefenderStoreFixture(string contentRoot, DefenderOptions? options = null, bool ownsContentRoot = true)
    {
        ContentRoot = contentRoot;
        this.ownsContentRoot = ownsContentRoot;
        options ??= new DefenderOptions();
        options.StateFilePath = Path.Combine(contentRoot, "state.json");
        options.MinimumCoolingSetPointCelsius = 16.0;
        options.MaximumBoostOffsetCelsius = 5.0;
        options.TemperatureToleranceCelsius = 0.1;
        Store = new DefenderStateStore(
            Options.Create(options),
            new TestWebHostEnvironment(contentRoot),
            NullLogger<DefenderStateStore>.Instance);

        // Tests drive the walk-down state machine call-by-call, so disable the wall-clock pacing
        // gap by default; the dedicated gentle-stepping test re-enables it explicitly.
        var stateField = typeof(DefenderStateStore).GetField("state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var runtimeState = stateField.GetValue(Store)!;
        var settingsProperty = runtimeState.GetType().GetProperty("Settings")!;
        var settings = (DefenderSettings)settingsProperty.GetValue(runtimeState)!;
        settings.CoolingStepMinimumGapSeconds = 0;
    }

    public string ContentRoot { get; }

    public DefenderStateStore Store { get; }

    public static DefenderStoreFixture Create()
    {
        var contentRoot = CreateContentRoot();
        return new DefenderStoreFixture(contentRoot);
    }

    public static DefenderStoreFixture Create(string contentRoot, DefenderOptions? options = null)
    {
        Directory.CreateDirectory(contentRoot);
        return new DefenderStoreFixture(contentRoot, options, ownsContentRoot: false);
    }

    public static string CreateContentRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "ha-ac-defender-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        return contentRoot;
    }

    public static void DeleteContentRoot(string contentRoot)
    {
        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    public void Dispose()
    {
        if (ownsContentRoot)
        {
            DeleteContentRoot(ContentRoot);
        }
    }
}

internal sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "HomeAssistantAcDefender.Tests";

    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);

    public string ContentRootPath { get; set; } = contentRootPath;

    public string EnvironmentName { get; set; } = Environments.Development;

    public string WebRootPath { get; set; } = contentRootPath;

    public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
}
