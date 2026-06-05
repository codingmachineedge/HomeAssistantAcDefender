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
tests.WebsiteCommandDebounceBlocksRapidControlsForTwoMinutes();
tests.CoolingFailureAlertsWhenCoolingDemandStaysIdle();
tests.EmergencyQuietPausesCorrectionsButKeepsStatus();
tests.WallSettlingWaitsWhileWallThermostatIsStillBeingTouched();
tests.CoolerIntentFastLaneBypassesQuietTimingForRepeatedCoolerTouches();
tests.WeatherDriftWaitsOnlyForSafeStableOutdoorConditions();
tests.CoolingRunwayWaitsOnlyAfterSafeCoolingStarts();
tests.SensorRhythmWaitsOnlyForSafeCorrections();
tests.ComfortPaceWaitsAfterFrequentWallTouchesButBypassesHotRoom();
Console.WriteLine("Defender setpoint regression checks passed.");

internal sealed class DefenderSetPointRegressionTests
{
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
        var afterExpiry = store.TryBeginWebsiteCommand("refresh thermostat");
        if (!afterExpiry.Accepted)
        {
            throw new InvalidOperationException("Website debounce should reopen after its expiry time.");
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
