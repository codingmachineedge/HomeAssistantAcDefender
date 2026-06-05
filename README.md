# Home Assistant AC Defender

Home Assistant AC Defender is an ASP.NET Core Blazor website plus a hosted background worker that continuously watches a real Home Assistant climate entity and defends the dining room AC target.

The app is designed for Docker hosting on Linux and is currently published by `docker-compose.yml` on host port `8888`.

## What It Does

- Shows the real dining room thermostat state from Home Assistant.
- Generates or accepts a target temperature from the website.
- Checks the thermostat 24/7 on a short polling interval.
- Restores the thermostat to `cool` if anyone changes HVAC mode away from cooling, even while temperature corrections are paused.
- Detects when someone changes the thermostat outside the website.
- Logs external thermostat touches with date, time, previous setpoint, new setpoint, room temperature, outdoor temperature, and weather condition when Home Assistant exposes those values.
- Uses a dynamic cooldown after manual thermostat touches so corrections do not happen instantly every time.
- Adds Comfort Sync quiet recovery: randomized extra waits, optional extra holds, command spacing, adaptive quiet levels, and small setpoint nudges so repeated wall changes do not create an obvious immediate tug-of-war.
- Adds Manual Comfort Grace so a wall thermostat change can be left alone while the room remains within the configured comfort band.
- Shows the next defender action in a live status label.
- Supports a custom schedule for target temperatures.
- Supports weather-based activation rules.
- Prioritizes upstairs comfort when upstairs temperature sensors report hot rooms.
- Can use Home Assistant presence entities so upstairs priority applies only while someone is home.
- Exposes fan mode and can optionally move the fan to an energy-saving mode when the room is near target.
- Uses a MudBlazor front end with real-time dashboard polling and 24-hour time display, so the user does not need to refresh.

There is no simulator or dummy thermostat. If Home Assistant is unavailable, the app shows the real error and does not fake state.

## Home Assistant Integration

The app uses the Home Assistant REST API with a long-lived access token.

Required environment variables:

```text
HomeAssistant__BaseUrl=http://homeassistant.local:8123
HomeAssistant__EntityId=climate.dining_room
HomeAssistant__AccessToken=replace-with-token
```

Optional environment variables:

```text
HomeAssistant__WeatherEntityId=weather.home
HomeAssistant__OutdoorTemperatureEntityId=sensor.outdoor_temperature
HomeAssistant__Username=optional-bookkeeping-only
HomeAssistant__Password=optional-bookkeeping-only
```

If `HomeAssistant__WeatherEntityId` is blank, the app discovers the first `weather.*` entity. If no weather entity exists, `HomeAssistant__OutdoorTemperatureEntityId` can provide only outdoor temperature.

## Defender Logic

Every cycle:

1. Pull weather/outdoor temperature.
2. Pull the real dining room climate entity.
3. Restore HVAC mode to `cool` immediately if another mode is selected, even when the temperature defender is paused.
4. Detect external setpoint changes by comparing the latest Home Assistant setpoint to the previously observed setpoint.
5. Ignore setpoint changes that match commands recently sent by the app.
6. Apply active schedule target if schedule is enabled.
7. Evaluate the weather activation rule.
8. Respect dynamic cooldown after manual thermostat changes.
9. Respect Manual Comfort Grace when the room is still comfortable after a wall change.
10. Apply Comfort Sync quiet recovery timing and small nudge sizing unless the room or upstairs is too warm.
11. Optionally set fan saver mode when near target.
12. Correct the thermostat setpoint when it does not match the defender decision.
13. Update the real-time dashboard status.

When the room is above the target, a new defender correction starts by commanding a setpoint exactly 1 C below the current room temperature to force cooling. If Home Assistant reports that cooling is idle/off while the room remains above target, it lowers the setpoint one additional degree per cycle. Normal defender cooling will not go below the website target, and when the room reaches target, the setpoint returns to the exact website target.

## Dynamic Cooldown

Cooldown starts only after an external setpoint change. The formula is frequency-based:

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount) + randomQuietDelay
```

`recentTouchCount` is counted inside `TouchFrequencyWindowMinutes`. More repeated manual changes cause longer cooldowns.

## Comfort Sync Quiet Recovery

Comfort Sync is the natural-change algorithm. It only affects timing and setpoint step size for real Home Assistant commands.

- `AdaptiveQuietnessEnabled`: lets repeated manual touches automatically increase quietness.
- `AdaptiveQuietTouchThreshold`: number of recent wall touches needed before adaptive quietness starts.
- `MaximumAdaptiveDelaySeconds`: longest random delay adaptive quietness may use.
- `MinimumAdaptiveStepCelsius`: smallest automatic nudge size during repeated touches.
- `MaximumAdaptiveHoldChancePercent`: highest chance of waiting one more short period.
- `MaximumAdaptiveCommandGapSeconds`: longest spacing between automatic setpoint commands.
- `MinimumNaturalDelaySeconds` and `MaximumNaturalDelaySeconds`: random extra wait after a manual wall thermostat change.
- `NaturalStepCelsius`: biggest setpoint move per automatic correction.
- `NaturalHoldChancePercent`: chance to wait one more short period after cooldown expires.
- `MaxNaturalHolds`: cap on those extra waits so recovery cannot stall forever.
- `MinimumCommandGapSeconds`: minimum spacing between automatic setpoint commands.
- `NaturalSafetyOverrideCelsius`: if room temperature is this far above target, skip quiet waits and restore comfort faster.
- `ManualComfortGraceEnabled`: lets a wall thermostat change rest while the room is still comfortable.
- `ManualComfortGraceMinutes`: maximum time to leave that wall change alone.
- `ManualComfortGraceBandCelsius`: extra room warmth allowed above target before the defender resumes.

Example: if the room is `25.0 C`, the website target is `22.0 C`, and the thermostat was manually moved to `26.0 C`, the defender decision is `24.0 C` because it starts one degree below current room temperature. With a `1.0 C` nudge size, the first automatic command can move from `26.0 C` to `25.0 C`, then later to `24.0 C`. If Home Assistant says cooling has stopped while the room is still above target, later decisions continue down toward `22.0 C`.

Adaptive quiet levels are shown on the dashboard:

- `Calm`: no recent wall touches.
- `Light`: a small number of recent wall touches, using base quiet settings.
- `Quiet`: repeated touches started, so waits and spacing begin increasing.
- `Extra quiet`: more repeated touches, smaller nudges and higher hold chance.
- `Softest`: maximum adaptive quietness before comfort safety overrides.

Manual Comfort Grace is different from cooldown. Cooldown waits after a manual touch. Manual Comfort Grace can keep waiting after cooldown if the room is still within the comfort band. If the room rises above the band, the HVAC mode changes away from `cool`, or upstairs becomes severely hot, grace ends and the real thermostat correction path resumes.

## Schedule And Weather Rules

The settings page has a MudBlazor mobile-friendly schedule editor with card-style rules, day buttons, clear start/end controls, helper text under each control, and a live summary for each rule. Each rule supports:

- Name
- Enabled flag
- Days
- Start and end time
- Target temperature
- Weather activation rule

Supported weather rules:

- `always`
- `room-above-outdoor`
- `room-below-outdoor`
- `outdoor-above-target`
- `outdoor-below-target`

The defender still checks Home Assistant 24/7 even if a weather rule prevents corrective action.

## Fan Energy Saver

Fan energy saver is optional. When enabled, the worker compares the room temperature to the target. If the room is within the configured threshold and Home Assistant exposes the configured fan mode, the app calls `climate.set_fan_mode`.

Manual fan control is also available on the dashboard.

## Upstairs Comfort Guard

The upstairs comfort guard is intended to prevent hot upstairs rooms while someone is home.

It can use exact entity IDs from settings:

```text
sensor.upstairs_temperature, sensor.primary_bedroom_temperature
```

If no upstairs temperature entity IDs are configured, the app searches Home Assistant for temperature sensors whose entity ID or friendly name contains words like `upstairs`, `second`, `2nd`, `bedroom`, or `master`.

Comfort settings:

- Upstairs comfort enabled
- Upstairs temperature entity IDs
- Max upstairs comfort temperature
- Comfort target temperature
- Extra cooling boost
- Optional home-presence requirement
- Presence entity IDs

If upstairs is above the configured comfort maximum, the guard can lower the target and increase cooling boost. If upstairs is severely hot, it can bypass cooldown so comfort wins over subtle correction timing.

Presence can be configured with entities such as:

```text
person.me, device_tracker.phone
```

If no presence entities are configured, the app auto-discovers `person.*` and `device_tracker.*`. When presence is required and no presence signal is found, the app assumes home for comfort rather than under-cooling.

## Website

The front end is built with Blazor Server and MudBlazor. Dashboard data refreshes automatically from the in-process defender state, and controls call the same services used by the JSON API.

The API also exposes a Server-Sent Events stream for external clients:

```text
/api/status/stream
```

This stream emits the full defender snapshot every second.

## API Summary

```text
GET  /api/status
GET  /api/settings
GET  /api/status/stream
POST /api/target/generate
POST /api/target
POST /api/defender
POST /api/settings
POST /api/thermostat/refresh
POST /api/thermostat/force-target
POST /api/thermostat/force-boost
POST /api/thermostat/fan
```

## Docker

Copy `.env.example` to `.env` and fill in Home Assistant values.

```powershell
docker compose up -d --build
```

The compose file publishes the app on host port `8888`:

```text
http://<host>:8888
```

Runtime state is saved to `/data/defender-state.json` inside the container and persisted in the `ac-defender-data` Docker volume.

## Development

Build locally:

```powershell
dotnet build
```

Run locally on port `8888`:

```powershell
dotnet run --urls http://127.0.0.1:8888
```

Do not commit `.env`, `App_Data`, build output, deployment archives, or Home Assistant tokens.
