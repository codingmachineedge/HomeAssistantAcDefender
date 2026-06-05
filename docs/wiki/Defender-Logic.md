# Defender Logic

Every cycle performs these steps:

1. Read weather and outdoor temperature from Home Assistant.
2. Read the real dining room climate entity.
3. Restore HVAC mode to `cool` immediately if Home Assistant reports another mode, even when temperature corrections are paused.
4. Detect whether the thermostat setpoint changed outside the website.
5. Read upstairs temperature sensors and presence entities.
6. Apply the active schedule target when scheduling is enabled.
7. Evaluate the weather activation rule.
8. Apply upstairs comfort rules.
9. Respect dynamic cooldown after external changes unless severe upstairs heat bypasses it.
10. Respect Manual Comfort Grace when the room is still within the configured band after a wall change.
11. Respect Room Trend Guard when real room readings are stable or cooling after a wall change.
12. Apply Comfort Sync quiet recovery timing and one-step nudge sizing unless comfort is too warm.
13. Apply fan energy saver when enabled and near target.
14. Correct the real thermostat setpoint when needed.
15. Update the next-action status label.

## Cooling Behavior

When room temperature is above target, a new defender correction starts by setting the thermostat exactly 1 C below the current room temperature to force cooling. If Home Assistant reports idle/off while the room is still above target, the defender lowers the setpoint one additional degree per cycle. Normal defender cooling will not go below the website target.

When room temperature reaches target, the defender returns the thermostat setpoint to the exact website target.

If the thermostat mode is changed to anything other than `cool`, the defender sends `climate.set_hvac_mode` with `hvac_mode: cool` before pause, schedule, weather, cooldown, or setpoint logic continues.

## Cooldown

Cooldown only starts after an external thermostat touch.

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount) + randomQuietDelay
```

Recent touches are counted within the configured touch frequency window.

## Comfort Sync Quiet Recovery

Quiet recovery makes automatic corrections less abrupt after someone changes the wall thermostat:

- Adds a random wait between the configured minimum and maximum quiet wait.
- Waits longer when repeated wall touches happen inside the touch window.
- Can hold briefly one or more times based on hold chance and max holds.
- Spaces commands by the configured minimum command gap.
- Caps each automatic setpoint change to the configured nudge size.
- Automatically changes quiet level when repeated wall touches happen, shrinking nudge size and increasing wait/hold/command spacing.
- Skips quiet waits when room temperature is above the safety override or upstairs comfort is severely hot.

Quiet recovery does not fake thermostat state and does not run a simulator. It only changes the timing and size of commands sent to the real Home Assistant climate entity.

Adaptive quiet levels:

- `Calm`: no recent wall touches.
- `Light`: a small number of recent wall touches.
- `Quiet`: repeated touches crossed the configured threshold.
- `Extra quiet`: repeated touches are continuing.
- `Softest`: maximum adaptive quietness before safety override wins.

## Manual Comfort Grace

Manual Comfort Grace starts after an external wall thermostat setpoint change. While grace is active, the defender can leave the wall change alone if the room is still at or below:

```text
targetTemperature + manualComfortGraceBandCelsius
```

Grace ends when the configured grace time expires, the room rises above the band, upstairs severe heat bypasses quiet timing, or the room crosses the safety override. This reduces obvious back-and-forth while still restoring comfort before the room gets too warm.

## Room Trend Guard

Room Trend Guard records real dining room temperature samples from Home Assistant. It compares the oldest and newest samples inside the configured trend window:

```text
delta = newestRoomTemperature - oldestRoomTemperature
```

If `delta` is above the stable tolerance, the room is warming and correction can continue. If `delta` is inside the tolerance or below it, the room is stable or cooling and the defender can keep observing for the configured hold time.

Trend Guard only applies after recent external wall touches. It steps aside when the room is above the grace band, crosses the safety override, or severe upstairs heat bypasses quiet timing.

## Upstairs Comfort

The upstairs comfort guard watches configured or auto-discovered upstairs temperature sensors. When upstairs is above the comfort threshold and presence rules allow it, the guard can lower the target and increase cooling boost.

The guard can override weather blocking when upstairs is hot. If upstairs temperature is at least 1 C above the configured comfort maximum, it can bypass cooldown so comfort is restored quickly.
