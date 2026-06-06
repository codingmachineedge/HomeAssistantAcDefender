using HomeAssistantAcDefender.Guards;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new DefenderSetPointRegressionTests();
tests.ManualTouchWhileWarmRestartsAtOneDegreeBelowRoom();
tests.ManualTouchAfterTargetRestartsAtOneDegreeBelowRoom();
tests.IdleWarmRoomWalksDownOneDegreeUntilWebsiteTarget();
tests.SetpointEchoWaitsOnlyForSafeFollowUpCommands();
tests.RepeatQuietWaitsOnlyForIdenticalSafeCommands();
tests.SetpointStillnessWaitsForStableReadingsButBypassesHotRoom();
tests.CommandCamouflageSpacesSafeFollowUpButBypassesHotRoom();
tests.StealthGovernorHoldsSafeHighPressureButBypassesHotRoom();
tests.HumanNudgeShapesOnlySafeCommandsAndBypassesHotRoom();
tests.WebsiteCommandDebounceBlocksRapidControlsForTwoMinutes();
tests.WebsiteCommandDebounceCanBypassNonThermostatButtons();
tests.CoolingFailureAlertsWhenCoolingDemandStaysIdle();
tests.OmegaAlertEscalatesOnlyWhenRoomRisesDuringIdleCoolingFailure();
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
tests.GuardCatalogProjectsEveryLiveGuardForADefaultSnapshot();
Console.WriteLine("Defender setpoint regression checks passed.");

internal sealed class DefenderSetPointRegressionTests
{
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

    public void ManualTouchWhileWarmRestartsAtOneDegreeBelowRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var initial = store.CalculateExpectedSetPoint(25.0, "cooling");
        AssertEqual(24.0, initial, "Initial warm-room command should start 1 C below room temperature.");
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
        AssertEqual(24.0, afterManualTouch, "Manual wall touch while warm should restart from room temperature minus 1 C, not the wall setpoint.");
    }

    public void ManualTouchAfterTargetRestartsAtOneDegreeBelowRoom()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        AssertEqual(24.0, store.CalculateExpectedSetPoint(25.0, "idle"), "Warm-room defense should start one degree below the current room temperature.");
        AssertEqual(23.0, store.CalculateExpectedSetPoint(25.0, "idle"), "Warm-room defense should keep stepping down while cooling is still off.");
        AssertEqual(22.0, store.CalculateExpectedSetPoint(25.0, "idle"), "Warm-room defense should stop walking down at the website target.");

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
        AssertEqual(24.0, afterManualTouch, "A new wall touch after reaching target should restart at current room temperature minus 1 C.");
    }

    public void IdleWarmRoomWalksDownOneDegreeUntilWebsiteTarget()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;
        store.SetTarget(22.0);

        var first = store.CalculateExpectedSetPoint(25.0, "idle");
        AssertEqual(24.0, first, "First idle warm-room command should be 1 C below current room temperature.");

        var second = store.CalculateExpectedSetPoint(25.0, "idle");
        AssertEqual(23.0, second, "Second idle warm-room command should step down one more degree.");

        var third = store.CalculateExpectedSetPoint(25.0, "idle");
        AssertEqual(22.0, third, "Warm-room step-down should stop at the website target.");

        var fourth = store.CalculateExpectedSetPoint(25.0, "idle");
        AssertEqual(22.0, fourth, "Warm-room step-down must not go colder than the website target.");
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

    public void WebsiteCommandDebounceBlocksRapidControlsForTwoMinutes()
    {
        using var fixture = DefenderStoreFixture.Create();
        var store = fixture.Store;

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
        SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-7));
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
            SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-7));

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
            SetRuntimeProperty(store, "CoolingFailureSuspectedAt", DateTimeOffset.UtcNow.AddMinutes(-7));

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
    private DefenderStoreFixture(string contentRoot)
    {
        ContentRoot = contentRoot;
        Store = new DefenderStateStore(
            Options.Create(new DefenderOptions
            {
                StateFilePath = Path.Combine(contentRoot, "state.json"),
                MinimumCoolingSetPointCelsius = 16.0,
                MaximumBoostOffsetCelsius = 5.0,
                TemperatureToleranceCelsius = 0.1
            }),
            new TestWebHostEnvironment(contentRoot),
            NullLogger<DefenderStateStore>.Instance);
    }

    public string ContentRoot { get; }

    public DefenderStateStore Store { get; }

    public static DefenderStoreFixture Create()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "ha-ac-defender-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        return new DefenderStoreFixture(contentRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(ContentRoot))
        {
            Directory.Delete(ContentRoot, recursive: true);
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
