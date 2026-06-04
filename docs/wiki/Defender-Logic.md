# Defender Logic

Every cycle performs these steps:

1. Read weather and outdoor temperature from Home Assistant.
2. Read the real dining room climate entity.
3. Detect whether the thermostat setpoint changed outside the website.
4. Apply the active schedule target when scheduling is enabled.
5. Evaluate the weather activation rule.
6. Respect dynamic cooldown after external changes.
7. Apply fan energy saver when enabled and near target.
8. Correct the real thermostat when needed.
9. Update the next-action status label.

## Cooling Behavior

When room temperature is above target, the defender sets the thermostat below target to force cooling. If Home Assistant reports idle while the room is still above target, the defender lowers the setpoint one additional degree per cycle until the configured minimum cooling setpoint.

When room temperature reaches target, the defender returns the thermostat setpoint to the exact target.

## Cooldown

Cooldown only starts after an external thermostat touch.

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount)
```

Recent touches are counted within the configured touch frequency window.
