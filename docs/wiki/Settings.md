# Settings

The MudBlazor settings page controls defender behavior without editing configuration files. Each input, button, and action label includes short helper text below it. Times are displayed with a 24-hour clock.

## Automation

- Schedule enabled: turns custom schedule rules on or off.
- Weather activation: controls when defender corrections are allowed.
- Base cooldown seconds: minimum cooldown after a manual thermostat change.
- Max cooldown seconds: cap for repeated manual changes.
- Touch window minutes: time window used for frequency-based cooldown.
- Delay cool restore: waits briefly before switching HVAC mode back to cool when the room is still safe.
- Min mode wait seconds: shortest wait before restoring cool mode.
- Max mode wait seconds: longest wait before restoring cool mode.
- Mode safe band C: extra warmth allowed above target before cool mode restore stops waiting.
- Conflict quiet mode: stands down when wall touches keep happening.
- Touches to stand down: how many recent wall changes trigger the stand-down.
- Stand-down minutes: how long to stop fighting back while the room is still safe.
- Safe room band C: extra warmth allowed above target while standing down.

## Comfort Sync

- Use quiet recovery: turns natural delayed corrections on or off.
- Auto quiet level: lets the defender get slower and softer when wall touches repeat.
- Touches to quiet: how many recent wall changes start the automatic quiet level.
- Max auto wait: longest wait the automatic quiet level can choose.
- Smallest auto nudge C: smallest setpoint move during repeated wall touches.
- Max auto hold %: highest chance to pause again before nudging.
- Max auto gap: longest spacing between automatic nudges.
- Min quiet wait: smallest random extra wait after someone changes the wall thermostat.
- Max quiet wait: largest random extra wait after someone changes the wall thermostat.
- Nudge size C: caps softer non-warm corrections. Warm-room cooling starts 1 C below current room temperature.
- Hold chance %: chance to pause one more time before nudging.
- Max holds: maximum number of extra pauses before a correction must continue.
- Command gap seconds: minimum time between automatic setpoint commands.
- Too-hot override C: if the room is this far above target, skip quiet waits and prioritize comfort.
- Respect wall changes: leaves a wall thermostat change alone while the room is still okay.
- Grace minutes: maximum time that wall change can rest.
- Grace room band C: extra warmth allowed above target before the defender resumes.
- Watch room trend: waits if real room readings are stable or cooling after a wall change.
- Trend window minutes: how far back the defender compares room temperature.
- Stable change C: small room temperature movement counted as steady.
- Trend hold minutes: how long to observe before nudging when room trend is okay.
- Use cooling momentum: waits when real room readings show the room is already cooling fast enough.
- Cooling rate C/hour: minimum cooling speed needed before the defender waits.
- Look-ahead minutes: only wait if the target is estimated within this many minutes.
- Momentum hold minutes: how long to let cooling continue before checking again.

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

Each schedule card contains name, enabled flag, day buttons, start time, end time, target temperature, weather activation rule, and a live summary.
