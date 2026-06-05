# Defender Logic

This page describes every algorithm the AC Defender runs. The same descriptions appear in the app:
each guard on the **Defense** page has a "How this works" explainer, and the **Guide** page lists the
whole reference. The source of truth for the live cards and this document is `Guards/GuardCatalog.cs`,
projected from the real implementation in `Services/DefenderStateStore.cs` and orchestrated by
`Services/AcDefenderService.RunCycleAsync`.

All timing/comfort guards read **real Home Assistant data only**. There is no simulator.

## The decision cycle

Every few seconds (`PollIntervalSeconds`, minimum 3) the worker reads Home Assistant and walks this
sequence. The first guard that wants to wait stops the cycle and reports its next action.

1. Pull weather and outdoor temperature.
2. Pull the real dining-room climate entity.
3. **Emergency protocols** — stand down if a too-cold, someone-upset, or suspicion window is active.
4. If the defender is **paused**, keep reading 24/7 but send nothing.
5. **Cool Mode Restore** — bring the HVAC mode back to `cool` (after a short safe delay).
6. **Schedule & weather rules** — choose the target and decide whether corrective action is allowed.
7. **Upstairs Comfort Guard** — lower the target and add boost when upstairs is hot.
8. Decide whether **severe upstairs heat** or **Cooler Intent Fast Lane** should bypass quiet timing.
9. **Wall Settling**, **Conflict Quiet**, **Manual Comfort Grace**, and **Dynamic Cooldown** may each hold.
10. **Fan Energy Saver** — move the fan to a saver mode when near target.
11. Compute the **expected setpoint** (1 °C below room when the room is warm — see below).
12. If the setpoint needs to change, walk the timing guards in order: **Comfort Envelope → Room Trend →
    Thermal Momentum → Weather Drift → Setpoint Echo → Cooling Runway → Sensor Rhythm → Comfort Sync →
    Comfort Pace → Routine Timing → Comfort Budget → Visibility Guard → Natural Cadence**.
13. Shape the command size with **Natural Walkback** and **Touch Signature**, then **Repeat Quiet**.
14. Send the corrected setpoint to Home Assistant.
15. **Cooling Failure Watch** runs alongside and raises a mega-alert if cooling is demanded but not real.

## Warm-room cooling — the "1 °C below room" rule

When the room is above target, a fresh correction commands a setpoint exactly **1 °C below the current
room temperature** to force cooling — not 1 °C below the wall setting. If Home Assistant reports cooling
is idle/off while the room is still above target, it lowers the setpoint one more degree each cycle. It
never goes below the website target, and once the room reaches target the setpoint returns to the exact
target.

> Example: room `25.0 °C`, website target `22.0 °C`, wall moved to `26.0 °C` → the first command is
> `24.0 °C` (room − 1), then `23.0 °C`, then `22.0 °C` if cooling keeps stalling.

## Quiet levels

Adaptive quietness ramps up with repeated wall touches and is shown on the dashboard:

| Level | Meaning |
|-------|---------|
| **Calm** | No recent wall touches. |
| **Light** | A few touches; base quiet settings. |
| **Quiet** | Repeated touches; waits and spacing grow. |
| **Extra quiet** | More touches; smaller nudges, higher hold chance. |
| **Softest** | Maximum quietness before comfort safety overrides. |

---

## Core cooling

### Comfort Sync (quiet recovery)
Spaces out and softens corrections so a fixed thermostat does not look like an instant robot.
- **Watches:** recent wall-touch count, time since the last defender command, how far the room is above target.
- **Logic:** after a manual change it waits a random delay, may hold one or two extra short beats, enforces a minimum gap between commands, and shrinks the nudge size. Repeated touches raise the quiet level (Calm → Softest), lengthening waits and shrinking steps. A room over the safety override skips all of it.
- **Settings:** `NaturalRecoveryEnabled`, `AdaptiveQuietnessEnabled`, `MinimumNaturalDelaySeconds`, `MaximumNaturalDelaySeconds`, `NaturalStepCelsius`, `NaturalHoldChancePercent`, `MinimumCommandGapSeconds`, `NaturalSafetyOverrideCelsius`.

### Cool Mode Restore
Puts the thermostat back into cool mode whenever someone switches it to heat/off/auto.
- **Watches:** the Home Assistant HVAC mode, plus how far the room is above target.
- **Logic:** if the mode is not `cool` it normally waits a short random delay (between `Minimum` and `Maximum` seconds) while the room stays within `target + comfort band`. A warmer room, severe upstairs heat, or a crossed safety override restores `cool` immediately.
- **Settings:** `CoolModeRestoreDelayEnabled`, `CoolModeRestoreMinimumDelaySeconds`, `CoolModeRestoreMaximumDelaySeconds`, `CoolModeRestoreComfortBandCelsius`.

---

## Wall-touch response

### Natural Walkback
Walks a safe-band correction toward target in small, slightly random steps instead of one obvious jump.
- **Watches:** recent wall-touch pressure (a 0–100 suspicion score) and the distance from the defender target.
- **Logic:** once touches reach the trigger and the room is inside the walkback safety band, each command moves only about the walkback step (plus a tiny jitter). A warm room that needs direct cooling skips walkback and still commands 1 °C below room temperature.
- **Settings:** `NaturalWalkbackEnabled`, `NaturalWalkbackTriggerTouches`, `NaturalWalkbackStepCelsius`, `NaturalWalkbackJitterCelsius`, `NaturalWalkbackSafetyBandCelsius`.

### Touch Signature
Matches safe nudges to the size of steps people actually use on the wall thermostat.
- **Watches:** recent real wall steps (their median size) inside the retention window.
- **Logic:** with enough recent steps and a room inside the signature safety band, it learns the median wall-step size, clamps it between the min and max signature step, and caps safe nudges to that size.
- **Settings:** `TouchSignatureEnabled`, `TouchSignatureTriggerTouches`, `TouchSignatureRetentionMinutes`, `TouchSignatureMinimumStepCelsius`, `TouchSignatureMaximumStepCelsius`, `TouchSignatureSafetyBandCelsius`.

### Visibility Guard
Slows the next safe nudge when a wall touch lands right after a defender command (someone likely noticed).
- **Watches:** wall touches within the after-command window, counted as "notices" over the notice window.
- **Logic:** each notice adds pressure (0–100). When notices reach the trigger, the next safe correction waits a variable hold between the min and max hold minutes, scaled by pressure. A room over the safety band clears the hold.
- **Settings:** `VisibilityGuardEnabled`, `VisibilityGuardTriggerNotices`, `VisibilityGuardNoticeWindowMinutes`, `VisibilityGuardAfterCommandSeconds`, `VisibilityGuardMinimumHoldMinutes`, `VisibilityGuardMaximumHoldMinutes`, `VisibilityGuardSafetyBandCelsius`.

### Routine Timing
Lines safe corrections up with a normal-looking comfort-check rhythm instead of firing instantly.
- **Watches:** recent wall touches and the wall-clock minute.
- **Logic:** after repeated touches and while the room is safe, the next correction waits until the next interval boundary plus a little random wiggle, capped at the max routine delay.
- **Settings:** `RoutineTimingEnabled`, `RoutineTimingTriggerTouches`, `RoutineTimingIntervalMinutes`, `RoutineTimingJitterMinutes`, `RoutineTimingMaxDelayMinutes`, `RoutineTimingSafetyBandCelsius`.

### Comfort Budget
Caps how many safe corrections happen inside a rolling window.
- **Watches:** the count of recent automatic setpoint commands in the budget window.
- **Logic:** if the count reaches the max, it rests until the oldest command ages out of the window. A room over the safety band clears the budget.
- **Settings:** `ComfortBudgetEnabled`, `ComfortBudgetWindowMinutes`, `ComfortBudgetMaxCommands`, `ComfortBudgetSafetyBandCelsius`.

### Natural Cadence
Picks a variable future slot for safe nudges so they never land at identical, robotic times.
- **Watches:** recent wall-touch pressure and recent command pressure.
- **Logic:** after repeated touches it chooses a wait between the min and max cadence minutes (later as pressure rises) plus a small jitter.
- **Settings:** `NaturalCadenceEnabled`, `NaturalCadenceTriggerTouches`, `NaturalCadenceMinimumMinutes`, `NaturalCadenceMaximumMinutes`, `NaturalCadenceJitterMinutes`, `NaturalCadenceSafetyBandCelsius`.

### Comfort Pace
The high-frequency planner: under heavy wall fighting it waits for a calm weather, sensor, or clock-aligned slot.
- **Watches:** touch pressure, command pressure, real outdoor-weather movement, the learned Home Assistant sensor beat, and 5/10-minute clock boundaries.
- **Logic:** when touches reach the trigger and the room is inside the safety band, it computes a base delay between the min and max pace minutes (scaling with pressure) and snaps it to the nearest calm slot — a weather update, the sensor beat, or a clock boundary — recording why.
- **Settings:** `NaturalChangePlannerEnabled`, `NaturalChangePlannerTriggerTouches`, `NaturalChangePlannerMinimumMinutes`, `NaturalChangePlannerMaximumMinutes`, `NaturalChangePlannerJitterMinutes`, `NaturalChangePlannerPreferWeatherSlots`, `NaturalChangePlannerPreferSensorBeat`.

### Comfort Envelope
Lets a tiny safe wall preference rest for a while instead of being corrected the instant it appears.
- **Watches:** the wall setpoint relative to the defender target and how far the room is above target.
- **Logic:** after repeated touches, if the wall setpoint stays within `target ± max offset` and the room is under the safety band, it observes for the hold minutes.
- **Settings:** `ComfortEnvelopeEnabled`, `ComfortEnvelopeTriggerTouches`, `ComfortEnvelopeHoldMinutes`, `ComfortEnvelopeMaxOffsetCelsius`, `ComfortEnvelopeSafetyBandCelsius`.

### Comfort Compromise
Blends a repeated wall choice into a temporary target, then fades it back to the website target.
- **Watches:** the latest wall setpoint, the website target, and the room temperature.
- **Logic:** if touches repeat and the room is inside the safety band, the wall setpoint pulls the effective target up to the max offset for the hold minutes, then eases back over the decay minutes. Effective target = `target + (preference − target) × decayFactor`.
- **Settings:** `ComfortCompromiseEnabled`, `ComfortCompromiseTriggerTouches`, `ComfortCompromiseHoldMinutes`, `ComfortCompromiseDecayMinutes`, `ComfortCompromiseMaxOffsetCelsius`, `ComfortCompromiseSafetyBandCelsius`.

### Comfort Memory
Learns a small time-of-day target bias from repeated safe wall choices and re-applies it later that hour.
- **Watches:** the current hour and the offsets learned for it; the room temperature.
- **Logic:** repeated safe touches teach a bounded offset (`± max offset`) for the current hour slot, applied on later checks in the same window. Memory expires after the retention hours and is skipped when warm or when upstairs needs cooling.
- **Settings:** `ComfortMemoryEnabled`, `ComfortMemoryLearningTouches`, `ComfortMemoryRetentionHours`, `ComfortMemoryMaxOffsetCelsius`, `ComfortMemorySafetyBandCelsius`.

### Conflict Quiet
Stands the defender down during an obvious tug-of-war over the thermostat.
- **Watches:** recent wall touches within the touch window and how far the room is above target.
- **Logic:** when touches reach the threshold, it stops sending visible corrections for the stand-down minutes while the room stays within `target + comfort band`.
- **Settings:** `ConflictQuietModeEnabled`, `ConflictQuietTouchThreshold`, `ConflictQuietMinutes`, `ConflictQuietComfortBandCelsius`.

### Wall Settling
Waits for someone who is still tapping the wall thermostat to stop before correcting.
- **Watches:** recent touches inside the settling window and the room temperature.
- **Logic:** with enough recent touches it holds for `base settle seconds + extra pressure seconds` (more touches = longer), measured from the latest touch.
- **Settings:** `WallSettlingGuardEnabled`, `WallSettlingMinimumTouches`, `WallSettlingWindowMinutes`, `WallSettlingBaseSeconds`, `WallSettlingPressureExtraSeconds`, `WallSettlingSafetyBandCelsius`.

### Manual Comfort Grace
Leaves a manual wall change alone while the room still feels comfortable.
- **Watches:** time since the wall change and how far the room is above target.
- **Logic:** after cooldown it can keep waiting up to the grace minutes while the room stays within `target + grace band`. Touch Intent can extend the grace for clearly warmer changes.
- **Settings:** `ManualComfortGraceEnabled`, `ManualComfortGraceMinutes`, `ManualComfortGraceBandCelsius`.

### Touch Intent
Reads whether recent wall changes trend warmer, cooler, or mixed, and extends grace for a clear warmer pattern.
- **Watches:** the net sum of recent wall setpoint changes inside the intent window.
- **Logic:** if net movement is at least the warm threshold and the room is inside the safety band, it adds the extra grace minutes to Manual Comfort Grace.
- **Settings:** `TouchIntentEnabled`, `TouchIntentMinimumTouches`, `TouchIntentWindowMinutes`, `TouchIntentNetWarmThresholdCelsius`, `TouchIntentExtraGraceMinutes`, `TouchIntentSafetyBandCelsius`.

### Cooler Intent Fast Lane
When people keep dialing the wall cooler, it skips quiet waits so the room cools sooner.
- **Watches:** the net cooler movement of recent wall changes and whether the room is above target.
- **Logic:** if repeated touches move the wall cooler by at least the cool threshold and the room is above target, it clears the quiet waits (cooldown, grace, conflict quiet, cadence, repeat quiet, sensor rhythm, runway, …) for the hold minutes. It never lowers the website target.
- **Settings:** `CoolerIntentFastLaneEnabled`, `CoolerIntentMinimumTouches`, `CoolerIntentWindowMinutes`, `CoolerIntentHoldMinutes`, `CoolerIntentNetCoolThresholdCelsius`, `CoolerIntentSafetyBandCelsius`.

---

## Sensor timing

### Setpoint Echo
Waits for Home Assistant to report back the last setpoint before sending another safe command.
- **Logic:** after a command it waits up to the echo grace seconds for Home Assistant to report that setpoint within 0.15 °C. A too-warm room steps it aside.
- **Settings:** `SetpointEchoGuardEnabled`, `SetpointEchoGraceSeconds`, `SetpointEchoSafetyBandCelsius`.

### Repeat Quiet
Waits before sending the very same thermostat number again.
- **Logic:** if the next safe command repeats the last number, it waits at least the minimum wait plus extra pressure seconds (scaling with recent touches and commands). Different one-degree step-downs pass through.
- **Settings:** `RepeatCommandGuardEnabled`, `RepeatCommandMinimumWaitSeconds`, `RepeatCommandPressureExtraSeconds`, `RepeatCommandSafetyBandCelsius`.

### Sensor Rhythm
Times nudges to just after the normal Home Assistant reading beat so they look less mechanical.
- **Logic:** with at least the minimum samples in the rhythm window, it learns the median update interval and waits until just after the next beat plus a small jitter.
- **Settings:** `SensorRhythmGuardEnabled`, `SensorRhythmMinimumSamples`, `SensorRhythmWindowMinutes`, `SensorRhythmJitterSeconds`, `SensorRhythmSafetyBandCelsius`.

### Cooling Runway
Gives the AC time to work after cooling starts before nudging the setpoint again.
- **Logic:** when `hvac_action` turns to cooling it records the start and holds for the minimum runway seconds plus extra pressure seconds. If cooling stops or the room gets too warm, it clears immediately.
- **Settings:** `CoolingRunwayGuardEnabled`, `CoolingRunwayMinimumSeconds`, `CoolingRunwayPressureExtraSeconds`, `CoolingRunwaySafetyBandCelsius`.

### Room Trend Guard
Keeps observing when the room is already stable or cooling after a wall change.
- **Logic:** comparing the oldest and newest room samples in the trend window, a cooling room (delta below the negative stable tolerance) holds for the trend hold minutes so cooling can continue. Warming or above-band rooms proceed.
- **Settings:** `RoomTrendGuardEnabled`, `RoomTrendWindowMinutes`, `RoomTrendStableToleranceCelsius`, `RoomTrendHoldMinutes`.

### Thermal Momentum
Waits when the room is already cooling fast enough to reach target soon on its own.
- **Logic:** it estimates the cooling rate and minutes-to-target. If the rate is at least the minimum C/hour and target is within the look-ahead minutes, it holds for the momentum hold minutes.
- **Settings:** `ThermalMomentumGuardEnabled`, `ThermalMomentumMinimumCoolingRateCelsiusPerHour`, `ThermalMomentumLookAheadMinutes`, `ThermalMomentumHoldMinutes`.

### Weather Drift Timing
Times safe corrections to real outdoor-weather movement instead of firing immediately.
- **Logic:** after a wall touch, while the room is inside the weather safety band, stable or cooling outdoor temperatures hold for the weather hold minutes. Once the outdoor temperature warms by the minimum change, the hold clears.
- **Settings:** `WeatherDriftGuardEnabled`, `WeatherDriftWindowMinutes`, `WeatherDriftMinimumChangeCelsius`, `WeatherDriftHoldMinutes`, `WeatherDriftSafetyBandCelsius`.

---

## Safety & system

### Dynamic Cooldown
A frequency-based quiet period after a manual thermostat change.
- **Formula:** `cooldown = min(MaxCooldownSeconds, BaseCooldownSeconds × recentTouchCount) + randomQuietDelay`, where `recentTouchCount` is counted inside `TouchFrequencyWindowMinutes`.
- **Settings:** `BaseCooldownSeconds`, `MaxCooldownSeconds`, `TouchFrequencyWindowMinutes`.

### Fan Energy Saver
Optionally moves the fan to an energy-saving mode when the room is near target.
- **Logic:** when enabled and the room is within the threshold of target, if the configured fan mode exists on the device it calls `climate.set_fan_mode`.
- **Settings:** `FanEnergySaverEnabled`, `FanEnergySaverThresholdCelsius`, `FanEnergySaverMode`.

### Upstairs Comfort Guard
Prioritizes cooling when upstairs rooms get hot while someone is home.
- **Logic:** if the hottest upstairs room exceeds the comfort maximum, it lowers the target toward the comfort target and adds the cooling boost. Severe upstairs heat bypasses cooldown. When presence is required and nobody is detected, it assumes home rather than under-cooling.
- **Settings:** `UpstairsComfortEnabled`, `UpstairsTemperatureEntityIds`, `UpstairsMaxComfortCelsius`, `UpstairsComfortTargetCelsius`, `UpstairsComfortBoostCelsius`, `HomePresenceRequired`, `PresenceEntityIds`.

### Schedule & Weather Rules
Time-of-day target rules, each gated by a weather activation condition.
- **Logic:** when the custom schedule is on, the matching rule supplies the target. Weather rules (`always`, `room-above-outdoor`, `room-below-outdoor`, `outdoor-above-target`, `outdoor-below-target`) decide whether corrective action is allowed. The defender still reads Home Assistant 24/7 when a rule blocks correction.
- **Settings:** `ScheduleEnabled`, `WeatherActivationMode`, and per-rule Days / Start / End / Target / Weather.

### Website Debounce
Blocks repeated website button taps for two minutes so the UI does not spam Home Assistant.
- **Logic:** the first click runs; later clicks within the debounce window show the remaining wait. Emergency actions bypass the debounce and then start a fresh window.

### Emergency Protocols
One-tap stand-down modes, run from the Controls page.
- **Too cold** (30 min): pauses the defender and turns the thermostat off.
- **Someone upset** (45 min) and **Suspicion quiet** (90 min): keep reading the thermostat 24/7 but send no corrective commands until the window ends.
- Emergency actions bypass the website debounce.

### Cooling Failure Watch
Raises a repeating mega-alert when cool mode is demanded but the AC is not really cooling.
- **Logic:** it alerts if the entity is in `cool`, the room is clearly above the setpoint, and `hvac_action` stays idle for several minutes (possible breaker/equipment), or if the action says cooling but the room does not drop over the retained window (possible compressor/airflow). Alerts repeat about once a minute. It never changes thermostat commands.
