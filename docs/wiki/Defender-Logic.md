# Defender Logic

Every cycle performs these steps:

1. Read weather and outdoor temperature from Home Assistant.
2. Read the real dining room climate entity.
3. Restore HVAC mode to `cool` when Home Assistant reports another mode, using the cool-mode restore delay only while comfort is still safe.
4. Detect whether the thermostat setpoint changed outside the website.
5. Read upstairs temperature sensors and presence entities.
6. Apply the active schedule target when scheduling is enabled.
7. Evaluate the weather activation rule.
8. Apply upstairs comfort rules.
9. Respect Conflict Quiet when repeated wall touches suggest someone is fighting the thermostat.
10. Respect dynamic cooldown after external changes unless severe upstairs heat bypasses it.
11. Respect Manual Comfort Grace when the room is still within the configured band after a wall change.
12. Respect Room Trend Guard when real room readings are stable or cooling after a wall change.
13. Respect Thermal Momentum when the room is already cooling fast enough to reach target soon.
14. Respect Setpoint Echo so safe follow-up commands wait for Home Assistant to report the last setpoint back.
15. Respect Sensor Rhythm so safe corrections can land just after a normal Home Assistant reading beat.
16. Apply Comfort Sync quiet recovery timing unless comfort is too warm.
17. Shape safe-band recovery commands through Natural Walkback when repeated wall touches make obvious corrections risky.
18. Shape safe-band nudge size through Touch Signature when recent wall changes show a common step size.
19. Respect Visibility Guard when a wall touch happens soon after a defender command.
20. Hold safe corrections for Routine Timing when repeated wall changes make an immediate correction too obvious.
21. Respect Comfort Budget when too many safe adjustments happened recently.
22. Respect Natural Cadence when repeated touches need a less exact safe-correction slot.
23. Apply bounded Comfort Memory for the current time window when room comfort is still safe.
24. Blend repeated safe wall choices through Comfort Compromise and fade them back toward the website target.
25. Extend safe wall-change grace through Touch Intent when recent wall choices clearly ask for warmer air.
26. Apply fan energy saver when enabled and near target.
27. Respect Repeat Quiet if the exact command about to be sent matches the last defender setpoint.
28. Correct the real thermostat setpoint when needed.
29. Update the next-action status label.

## Cooling Behavior

When room temperature is above target, a new defender correction starts by setting the thermostat exactly 1 C below the current room temperature to force cooling. It does not start one degree below the wall setpoint someone chose. If Home Assistant reports idle/off while the room is still above target, the defender lowers the setpoint one additional degree per cycle. Normal defender cooling will not go below the website target.

When room temperature reaches target, the defender returns the thermostat setpoint to the exact website target.

If the thermostat mode is changed to anything other than `cool`, the defender can wait a short configured delay before sending `climate.set_hvac_mode` with `hvac_mode: cool`. The delay is skipped when the room is above the mode safe band, the normal safety override is crossed, or severe upstairs heat is active. Paused defender state still restores `cool`.

## Cool Mode Restore

Cool Mode Restore keeps the hard rule that the thermostat must return to `cool`, but avoids doing it at the exact same instant every time when the room is still safe.

It waits between the configured minimum and maximum seconds only while:

```text
currentRoomTemperature <= targetTemperature + coolModeRestoreComfortBandCelsius
```

After a restore command is sent, the worker waits through the command grace window before sending another mode command. That prevents repeated service calls while Home Assistant is still confirming the first restore.

## Cooldown

Cooldown only starts after an external thermostat touch.

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount) + randomQuietDelay
```

Recent touches are counted within the configured touch frequency window.

## Conflict Quiet

Conflict Quiet watches the same recent-touch counter as cooldown. If repeated wall touches reach the configured threshold, the defender stands down for the configured minutes instead of continuing an obvious command tug-of-war.

It only stands down while the real room temperature is at or below:

```text
targetTemperature + conflictQuietComfortBandCelsius
```

It ends immediately when the room gets too warm, severe upstairs heat bypasses quiet timing, or the normal safety override is crossed.

## Comfort Sync Quiet Recovery

Quiet recovery makes automatic corrections less abrupt after someone changes the wall thermostat:

- Adds a random wait between the configured minimum and maximum quiet wait.
- Waits longer when repeated wall touches happen inside the touch window.
- Can hold briefly one or more times based on hold chance and max holds.
- Spaces commands by the configured minimum command gap.
- Caps softer non-warm corrections to the configured nudge size.
- Sends warm-room corrections to the room-temperature defender target instead of walking down from the wall setpoint.
- Automatically changes quiet level when repeated wall touches happen, shrinking nudge size and increasing wait/hold/command spacing.
- Uses Natural Walkback for small safe-band setpoint steps when repeated wall touches make a direct correction too obvious.
- Uses Touch Signature to learn the size of recent wall thermostat steps and shape safe nudges with the same bounded step style.
- Uses Visibility Guard to slow safe corrections when a wall touch happens soon after a defender command.
- Uses Routine Timing so safe corrections land on normal-looking comfort-check intervals.
- Uses Comfort Budget so repeated safe corrections can rest before another adjustment.
- Uses Natural Cadence so repeated safe corrections wait for a variable future slot based on touch pressure and recent command pressure.
- Uses Comfort Memory to remember a tiny expiring time-of-day preference after repeated safe wall choices.
- Uses Comfort Compromise to temporarily blend repeated safe wall choices into the effective target.
- Uses Touch Intent to classify recent wall choices and extend safe grace only when warmer intent is clear.
- Uses Setpoint Echo so safe follow-up commands wait for Home Assistant to report the last setpoint back.
- Uses Repeat Quiet so identical follow-up commands wait longer when recent wall-touch or command pressure is high.
- Uses Sensor Rhythm so safe corrections can wait until just after the learned Home Assistant reading beat.
- Skips quiet waits when room temperature is above the safety override or upstairs comfort is severely hot.

Quiet recovery does not fake thermostat state and does not run a simulator. It only changes the timing and selected command target sent to the real Home Assistant climate entity.

Adaptive quiet levels:

- `Calm`: no recent wall touches.
- `Light`: a small number of recent wall touches.
- `Quiet`: repeated touches crossed the configured threshold.
- `Extra quiet`: repeated touches are continuing.
- `Softest`: maximum adaptive quietness before safety override wins.

## Natural Walkback

Natural Walkback is a command-shaping layer for safe-band recovery. It calculates a touch score from recent wall thermostat touches and recency. When the trigger count is reached and the real room temperature is still inside:

```text
targetTemperature + naturalWalkbackSafetyBandCelsius
```

the next safe-band correction walks toward the website target in smaller nudges. A tiny optional variation changes the nudge size so every correction is not identical.

Natural Walkback never changes the warm-room defender rule. If the room needs active cooling, the command still starts one degree below current room temperature and continues down toward the website target. If the room crosses the safety band, the normal direct comfort correction path wins.

## Touch Signature

Touch Signature reads the real external thermostat touch audit log and learns a bounded step size from recent wall changes. When enough recent wall steps exist and the room is still safe, safe nudges use about that learned step size instead of always using the same configured nudge.

It only applies while the real room temperature is inside:

```text
targetTemperature + touchSignatureSafetyBandCelsius
```

If the room crosses the safety band, the normal safety override is reached, or upstairs heat bypasses quiet timing, Touch Signature steps aside. It never changes warm-room defense: active cooling still starts one degree below current room temperature and continues toward the website target.

## Visibility Guard

Visibility Guard watches noticed correction signals: wall thermostat touches that happen soon after a defender command. When enough signals occur inside the configured notice window, the next safe correction gets a variable hold between the minimum and maximum visibility hold.

It only waits while the real room temperature is inside:

```text
targetTemperature + visibilityGuardSafetyBandCelsius
```

If upstairs heat bypasses quiet timing, the room crosses the safety override, or the room rises above the visibility safe band, the hold clears and direct comfort correction continues. It never changes warm-room defense: active cooling still starts one degree below current room temperature and then steps down by one degree after cooling turns idle/off until the website target is reached.

## Routine Timing

Routine Timing is a safe-correction timing guard. After repeated wall changes, it can hold the next correction until a normal minute rhythm plus a small random wiggle:

```text
currentRoomTemperature <= targetTemperature + routineTimingSafetyBandCelsius
```

It only delays while the room remains safe. If upstairs heat bypasses quiet timing, the room crosses the safety override, or the room rises above the routine safe band, the hold clears and the real correction path continues.

## Comfort Budget

Comfort Budget limits repeated safe automatic setpoint commands inside a rolling window:

```text
recentSafeCommands < comfortBudgetMaxCommands
```

If the budget is full, the defender rests until the oldest command leaves the configured window. It only rests while the room remains safe:

```text
currentRoomTemperature <= targetTemperature + comfortBudgetSafetyBandCelsius
```

If upstairs heat bypasses quiet timing, the room crosses the safety override, or the room rises above the budget safe band, the budget clears and the real correction path continues.

## Natural Cadence

Natural Cadence is a safe-correction timing layer for repeated wall touches. When the trigger count is reached, it picks a future slot using touch pressure, recent automatic command pressure, and a small random wiggle. The selected slot is persisted so the next-action label keeps counting down in real time.

It only waits while the real room temperature is inside:

```text
targetTemperature + naturalCadenceSafetyBandCelsius
```

If upstairs heat bypasses quiet timing, the room crosses the safety override, or the room rises above the cadence safe band, cadence clears immediately and the real correction path continues. It never changes the warm-room defender rule: active cooling still starts one degree below current room temperature and continues toward the website target.

## Comfort Compromise

Comfort Compromise creates a temporary effective target after repeated wall changes. It starts only when the touch trigger is reached and the real room temperature is still inside:

```text
targetTemperature + comfortCompromiseSafetyBandCelsius
```

The preferred wall setpoint is capped by `comfortCompromiseMaxOffsetCelsius`, held for the configured hold minutes, and then faded back toward the website target across the decay window.

If the room rises above the safety band, the compromise is cleared immediately. Schedule changes, website target changes, and upstairs comfort target changes also clear it.

## Comfort Memory

Comfort Memory learns a small offset for the current local hour after repeated wall choices while the room is safe:

```text
currentRoomTemperature <= targetTemperature + comfortMemorySafetyBandCelsius
```

The learned offset is capped by `comfortMemoryMaxOffsetCelsius` and expires after `comfortMemoryRetentionHours`. The next time the same hour is active, the offset can adjust the effective target before temporary compromise is applied.

Comfort Memory does not apply if the room crosses the safety band or normal safety override. If upstairs is hot, warmer learned offsets are skipped so upstairs comfort keeps priority.

## Manual Comfort Grace

Manual Comfort Grace starts after an external wall thermostat setpoint change. While grace is active, the defender can leave the wall change alone if the room is still at or below:

```text
targetTemperature + manualComfortGraceBandCelsius
```

Grace ends when the configured grace time expires, the room rises above the band, upstairs severe heat bypasses quiet timing, or the room crosses the safety override. This reduces obvious back-and-forth while still restoring comfort before the room gets too warm.

## Touch Intent

Touch Intent classifies recent real wall thermostat changes inside its configured window:

```text
netChange = sum(newSetPoint - previousSetPoint)
```

When enough touches exist and the net change is at or above `touchIntentNetWarmThresholdCelsius`, the pattern is treated as warmer intent. If the real room remains inside:

```text
targetTemperature + touchIntentSafetyBandCelsius
```

Manual Comfort Grace can extend by `touchIntentExtraGraceMinutes`. Cooler and mixed patterns do not add warmer grace. If the room crosses the safety band, the normal safety override is reached, or upstairs heat bypasses quiet timing, Touch Intent steps aside and the direct correction path continues.

## Setpoint Echo

Setpoint Echo uses the real pending command record created whenever the defender sends a Home Assistant setpoint. If Home Assistant has not reported that setpoint back yet, the next safe correction can wait until `setpointEchoGraceSeconds`.

It only waits while the real room temperature is inside:

```text
targetTemperature + setpointEchoSafetyBandCelsius
```

If Home Assistant reports the pending setpoint, the echo clears. If upstairs heat bypasses quiet timing, the room crosses the safety override, or the room rises above the echo safe band, the hold steps aside immediately.

## Repeat Quiet

Repeat Quiet checks the final real setpoint immediately before a Home Assistant command is sent. If that setpoint matches the last defender setpoint, the command can wait until:

```text
lastDefenderCommandAt + repeatCommandMinimumWaitSeconds + pressure extra
```

The pressure extra rises from recent wall-touch pressure and recent defender command pressure. This makes identical follow-up commands slow down when the thermostat has been touched frequently.

Repeat Quiet does not hold a different setpoint, so the one-degree step-down path can continue toward the website target. It only waits while the room is inside:

```text
targetTemperature + repeatCommandSafetyBandCelsius
```

If the room crosses that band, the normal safety override is reached, or upstairs heat bypasses quiet timing, Repeat Quiet clears immediately and the real correction path continues.

## Sensor Rhythm

Sensor Rhythm records real Home Assistant climate reading timestamps and learns the median interval between updates. Once enough real readings exist, a safe correction can wait until just after the next learned beat plus `sensorRhythmJitterSeconds`.

It only waits while the real room temperature is inside:

```text
targetTemperature + sensorRhythmSafetyBandCelsius
```

If upstairs heat bypasses quiet timing, the room crosses the safety override, or the room rises above the rhythm safe band, the hold clears immediately and the real correction path continues.

## Room Trend Guard

Room Trend Guard records real dining room temperature samples from Home Assistant. It compares the oldest and newest samples inside the configured trend window:

```text
delta = newestRoomTemperature - oldestRoomTemperature
```

If `delta` is above the stable tolerance, the room is warming and correction can continue. If `delta` is inside the tolerance or below it, the room is stable or cooling and the defender can keep observing for the configured hold time.

Trend Guard only applies after recent external wall touches. It steps aside when the room is above the grace band, crosses the safety override, or severe upstairs heat bypasses quiet timing.

## Thermal Momentum

Thermal Momentum uses the same real dining room temperature samples. After a recent wall touch, it estimates cooling speed:

```text
coolingRate = temperatureDrop / elapsedHours
etaMinutes = (currentRoomTemperature - targetTemperature) / coolingRate * 60
```

If the room is above target but already cooling at or above the configured rate, and the target is estimated inside the look-ahead window, the defender holds briefly instead of sending another visible thermostat command. It steps aside when the room is not cooling, the target is too far away, the room crosses the safety override, or severe upstairs heat bypasses quiet timing.

## Upstairs Comfort

The upstairs comfort guard watches configured or auto-discovered upstairs temperature sensors. When upstairs is above the comfort threshold and presence rules allow it, the guard can lower the target and increase cooling boost.

The guard can override weather blocking when upstairs is hot. If upstairs temperature is at least 1 C above the configured comfort maximum, it can bypass cooldown so comfort is restored quickly.
