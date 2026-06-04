# Settings

The settings page controls defender behavior without editing configuration files.

## Automation

- Schedule enabled: turns custom schedule rules on or off.
- Weather activation: controls when defender corrections are allowed.
- Base cooldown seconds: minimum cooldown after a manual thermostat change.
- Max cooldown seconds: cap for repeated manual changes.
- Touch window minutes: time window used for frequency-based cooldown.

## Weather Rules

- `always`
- `room-above-outdoor`
- `room-below-outdoor`
- `outdoor-above-target`
- `outdoor-below-target`

## Fan Energy Saver

When enabled, the app can set a configured Home Assistant fan mode when the room is close to target. This is optional and depends on the climate entity exposing supported `fan_modes`.

## Upstairs Comfort

Settings include:

- Protect upstairs comfort
- Upstairs temperature entity IDs
- Max upstairs comfort C
- Comfort target C
- Extra boost C

If upstairs temperature entity IDs are blank, the app tries to discover temperature sensors with names like upstairs, second floor, 2nd floor, bedroom, or master.

## Home Presence

Presence settings include:

- Only prioritize upstairs when someone is home
- Presence entity IDs

If presence entity IDs are blank, the app discovers `person.*` and `device_tracker.*` entities.

## Schedule Rows

Each schedule row contains name, enabled flag, days, start time, end time, target temperature, and weather activation rule.
