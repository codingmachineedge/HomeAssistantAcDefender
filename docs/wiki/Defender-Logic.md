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
10. Apply fan energy saver when enabled and near target.
11. Correct the real thermostat setpoint when needed.
12. Update the next-action status label.

## Cooling Behavior

When room temperature is above target, a new defender correction starts by setting the thermostat exactly 1 C below the current room temperature to force cooling. If Home Assistant reports idle/off while the room is still above target, the defender lowers the setpoint one additional degree per cycle. Normal defender cooling will not go below the website target.

When room temperature reaches target, the defender returns the thermostat setpoint to the exact website target.

If the thermostat mode is changed to anything other than `cool`, the defender sends `climate.set_hvac_mode` with `hvac_mode: cool` before pause, schedule, weather, cooldown, or setpoint logic continues.

## Cooldown

Cooldown only starts after an external thermostat touch.

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount)
```

Recent touches are counted within the configured touch frequency window.

## Upstairs Comfort

The upstairs comfort guard watches configured or auto-discovered upstairs temperature sensors. When upstairs is above the comfort threshold and presence rules allow it, the guard can lower the target and increase cooling boost.

The guard can override weather blocking when upstairs is hot. If upstairs temperature is at least 1 C above the configured comfort maximum, it can bypass cooldown so comfort is restored quickly.
