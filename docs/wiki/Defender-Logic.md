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
3. Pull real front-door person detector entities when the guard is enabled.
4. **Front-door Guard Post** pauses the defender and can turn the thermostat off if a person is detected.
5. **Emergency protocols** stand down if a too-cold, someone-upset, or suspicion window is active.
6. If the defender is **paused**, keep reading 24/7 but send nothing.
7. **Cool Mode Restore** brings the HVAC mode back to `cool` after a short safe delay.
8. **Schedule & weather rules** choose the target and decide whether corrective action is allowed.
9. **Upstairs Comfort Guard** lowers the target and adds boost when upstairs is hot.
10. Decide whether **severe upstairs heat**, **Cooler Intent Fast Lane**, or **Super Defender** should bypass quiet timing.
11. **Wall Settling**, **Conflict Quiet**, **Manual Comfort Grace**, and **Dynamic Cooldown** may each hold.
12. **Alectra Peak Power Saver** makes safe cooling more chill during On-peak, high-price, or high-power usage.
13. **Fan Energy Saver** moves the fan to a saver mode when near target.
14. Compute the **expected setpoint**: 1 C below room when the room is warm.
15. If the setpoint needs to change, walk the timing guards in order: **Alectra Peak Power Saver -> Comfort Envelope -> Room Trend -> Thermal Momentum -> Weather Drift -> Setpoint Echo -> Cooling Runway -> Sensor Rhythm -> HVAC Alibi -> Comfort Sync -> Comfort Pace -> Routine Timing -> Comfort Budget -> Command Camouflage -> Stealth Governor -> Visibility Guard -> Natural Cadence**.
16. Shape the command size with **Natural Walkback**, **Touch Signature**, and **Human Nudge**, then **Repeat Quiet**.
17. Send the corrected setpoint to Home Assistant.
18. **Cooling Failure Watch** runs alongside and raises a mega-alert if cooling is demanded but not real.

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

### Human Nudge
Makes the final safe setpoint command look like a normal thermostat step instead of a precise bot number.
- **Watches:** recent wall touches, the candidate defender command, the current thermostat setpoint, and room temperature.
- **Logic:** after repeated touches and while the room is inside the safe band, it snaps only safe follow-up commands to the configured human step size. Direct warm-room cooling, upstairs heat, or quiet-timing bypasses skip this shaper.
- **Settings:** `HumanNudgeEnabled`, `HumanNudgeTriggerTouches`, `HumanNudgeStepCelsius`, `HumanNudgeSafetyBandCelsius`.

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

### Command Camouflage
Gives a recent helper command time to look normal before another safe correction appears.
- **Watches:** the last helper setpoint command, recent helper-command pressure, recent wall-touch pressure, and room temperature.
- **Logic:** after a setpoint command, it waits at least the minimum gap plus pressure-scaled extra seconds before another safe correction. A room over the safety band or any comfort bypass clears it.
- **Settings:** `CommandCamouflageEnabled`, `CommandCamouflageMinimumGapSeconds`, `CommandCamouflagePressureExtraSeconds`, `CommandCamouflageSafetyBandCelsius`.

### Stealth Governor
Runs a whole-system low-profile hold when the defender looks too active.
- **Watches:** wall-touch pressure, noticed-correction pressure, Home Assistant remote changes, helper command count, and room temperature.
- **Logic:** it computes a 0-100 score. If the score reaches the trigger and the room is inside the safety band, it holds only safe corrections for a min-to-max low-profile window.
- **Settings:** `StealthGovernorEnabled`, `StealthGovernorTriggerScore`, `StealthGovernorMinimumHoldMinutes`, `StealthGovernorMaximumHoldMinutes`, `StealthGovernorSafetyBandCelsius`.

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

### Super Defender
Treats repeated phone/Home Assistant changes as a stricter signal than a single wall touch.
- **Watches:** Home Assistant climate state `context.user_id`, `context.parent_id`, and `context.id` from real thermostat readings.
- **Logic:** `user_id` is labeled as a Home Assistant user or phone app change, `parent_id` as an automation/script/service chain, and context without either field as a thermostat/device change. When enough remote-style changes happen inside the configured window, Super Defender arms for a hold period. While armed, if the room is above target and not inside the configured safe natural-recovery band, it can bypass quiet waits so the normal 1 C-below-room correction runs sooner. It does not automatically block Wi-Fi, router, or firewall access because that can remove thermostat monitoring and recovery; the app shows a manual-only network-lockdown warning instead.
- **Settings:** `SuperDefenderModeEnabled`, `SuperDefenderRemoteChangeThreshold`, `SuperDefenderWindowMinutes`, `SuperDefenderHoldMinutes`, `SuperDefenderSafetyBandCelsius`, `SuperDefenderBypassQuietTiming`.

### Remote Settling Guard
Gives repeated Home Assistant user/phone or automation thermostat changes a quiet safe window before answering back.
- **Watches:** real Home Assistant context source attribution, recent remote-style change count, room temperature, and the expected setpoint.
- **Logic:** after enough Home Assistant user/phone or automation changes inside the configured window, it holds only safe corrections for the quiet hold minutes. A too-warm room, cooler-intent fast lane, direct comfort bypass, matching expected setpoint, disabled setting, or expired hold clears it.
- **Settings:** `RemoteSettlingGuardEnabled`, `RemoteSettlingTriggerChanges`, `RemoteSettlingWindowMinutes`, `RemoteSettlingHoldMinutes`, `RemoteSettlingSafetyBandCelsius`.

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

### Setpoint Stillness
Waits until the wall setpoint stops moving before a safe correction answers back.
- **Watches:** real Home Assistant climate readings, the current reported setpoint, recent wall touches, and room temperature.
- **Logic:** after repeated external touches, while the room is still inside the safe band, it requires the configured number of consecutive real readings at the same setpoint before allowing a safe correction. A too-warm room, cooler-intent bypass, matching expected setpoint, or max-hold expiry releases it.
- **Settings:** `SetpointStillnessGuardEnabled`, `SetpointStillnessTriggerTouches`, `SetpointStillnessRequiredSamples`, `SetpointStillnessMaxHoldSeconds`, `SetpointStillnessToleranceCelsius`, `SetpointStillnessSafetyBandCelsius`.

### Sensor Rhythm
Times nudges to just after the normal Home Assistant reading beat so they look less mechanical.
- **Logic:** with at least the minimum samples in the rhythm window, it learns the median update interval and waits until just after the next beat plus a small jitter.
- **Settings:** `SensorRhythmGuardEnabled`, `SensorRhythmMinimumSamples`, `SensorRhythmWindowMinutes`, `SensorRhythmJitterSeconds`, `SensorRhythmSafetyBandCelsius`.

### HVAC Alibi
Waits for a real HVAC action transition so a safe correction lands near a normal thermostat event.
- **Watches:** the current Home Assistant `hvac_action`, the last action transition, recent wall touches, and room temperature.
- **Logic:** after repeated wall touches, while the room is inside the safety band, it can hold a safe correction until `hvac_action` changes. A recent action transition can also clear the hold. Direct comfort needs, upstairs heat, or a too-warm room bypass it immediately.
- **Settings:** `HvacActionAlibiEnabled`, `HvacActionAlibiTriggerTouches`, `HvacActionAlibiTransitionWindowSeconds`, `HvacActionAlibiMaxHoldMinutes`, `HvacActionAlibiSafetyBandCelsius`.

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

### Alectra Peak Power Saver
Makes safe cooling more chill and resource-saving when Alectra Hui says power is expensive or high.
- **Logic:** the worker refreshes Alectra Hui usage sensors on the configured interval. On-peak TOU, current price at or above the c/kWh threshold, or current power at or above the kW threshold arms the saver window. While active, it holds only safe commands that would demand more cooling, and can set the configured fan saver mode. If the room or upstairs gets too hot, or the command would save energy by raising the setpoint, it steps aside.
- **Settings:** `PeakPowerSaverEnabled`, `PeakPowerSaverOnPeakEnabled`, `PeakPowerSaverHighPowerEnabled`, `PeakPowerSaverPowerThresholdKilowatts`, `PeakPowerSaverPriceThresholdCentsPerKwh`, `PeakPowerSaverHoldMinutes`, `PeakPowerSaverRefreshSeconds`, `PeakPowerSaverSafetyBandCelsius`, `PeakPowerSaverFanSaverEnabled`, `PeakPowerSaverFanMode`.

### Front-door Guard Post
Pauses the defender when a real front-door person detector reports a person, and can immediately turn the thermostat off.
- **Logic:** the worker reads configured front-door person detector entity IDs, or auto-discovers likely front-door, porch, entry, or entrance person sensors. If any detector is active, it pauses the defender, holds the guard window, and sends thermostat `off` when that setting is enabled. The command source is tagged as `front-door-kill-switch`, so its Home Assistant echo is not treated like a wall-control touch.
- **Settings:** `FrontDoorKillSwitchEnabled`, `FrontDoorPersonEntityIds`, `FrontDoorKillSwitchHoldMinutes`, `FrontDoorKillSwitchRefreshSeconds`, `FrontDoorKillSwitchTurnsThermostatOff`.

### Upstairs Comfort Guard
Prioritizes cooling when upstairs rooms get hot while someone is home.
- **Logic:** if the hottest upstairs room exceeds the comfort maximum, it lowers the target toward the comfort target and adds the cooling boost. Severe upstairs heat bypasses cooldown. When presence is required and nobody is detected, it assumes home rather than under-cooling.
- **Settings:** `UpstairsComfortEnabled`, `UpstairsTemperatureEntityIds`, `UpstairsMaxComfortCelsius`, `UpstairsComfortTargetCelsius`, `UpstairsComfortBoostCelsius`, `HomePresenceRequired`, `PresenceEntityIds`.

### Schedule & Weather Rules
Time-of-day target rules, each gated by a weather activation condition.
- **Logic:** when the custom schedule is on, the matching rule supplies the target. Weather rules (`always`, `room-above-outdoor`, `room-below-outdoor`, `outdoor-above-target`, `outdoor-below-target`) decide whether corrective action is allowed. The defender still reads Home Assistant 24/7 when a rule blocks correction.
- **Settings:** `ScheduleEnabled`, `WeatherActivationMode`, and per-rule Days / Start / End / Target / Weather.

### Website Debounce
Blocks repeated thermostat-affecting website button taps for two minutes so the UI does not spam Home Assistant.
- **Logic:** the first thermostat-affecting click runs; later thermostat-affecting clicks within the debounce window show the remaining wait. Defender activation, settings save, refresh, search/filter controls, and non-thermostat emergency pauses bypass the thermostat debounce.

### Emergency Protocols
One-tap stand-down modes, run from the Controls page.
- **Too cold** (30 min): pauses the defender and turns the thermostat off.
- **Someone upset** (45 min) and **Suspicion quiet** (90 min): keep reading the thermostat 24/7 but send no corrective commands until the window ends.
- Too-cold uses the thermostat debounce because it turns the thermostat off. Someone-upset and suspicion bypass the thermostat debounce because they only pause defender commands.

### Cooling Failure Watch (MEGA → OMEGA)
Raises a repeating **mega-alert** when cool mode is demanded but the AC is not really cooling, and escalates to a full-site **OMEGA alert** once a rising room confirms the failure.
- **MEGA logic:** it alerts if the entity is in `cool`, the room is clearly above the setpoint, and `hvac_action` stays idle for several minutes (possible breaker/equipment), or if the action says cooling but the room does not drop over the retained window (possible compressor/airflow). Alerts repeat about once a minute. It never changes thermostat commands.
- **OMEGA logic (confirmed breaker off):** the mega alert only proves the AC is *not cooling*; OMEGA adds proof that the room is actually *getting warmer*. While the **idle/breaker** mega branch is active, if the room has risen at least `OmegaMinimumRiseCelsius` (0.4 °C) over the last `OmegaRiseWindowSeconds` (5 min), it escalates to OMEGA and shows a site-wide overlay. False positives are kept low by three gates: only the idle branch can escalate (a unit that reports cooling still has power), the rise must be **sustained over a window** (not a single noisy reading), and the room must still be above setpoint. If the room stops rising or starts dropping, OMEGA clears immediately.
