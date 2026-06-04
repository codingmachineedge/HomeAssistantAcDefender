# Home Assistant AC Defender

Home Assistant AC Defender is an ASP.NET Core website plus a hosted background worker that continuously watches a real Home Assistant climate entity and defends the dining room AC target.

The app is designed for Docker hosting on Linux and is currently published by `docker-compose.yml` on host port `8888`.

## What It Does

- Shows the real dining room thermostat state from Home Assistant.
- Generates or accepts a target temperature from the website.
- Checks the thermostat 24/7 on a short polling interval.
- Restores the thermostat to `cool` if anyone changes HVAC mode away from cooling, even while temperature corrections are paused.
- Detects when someone changes the thermostat outside the website.
- Logs external thermostat touches with date, time, previous setpoint, new setpoint, room temperature, outdoor temperature, and weather condition when Home Assistant exposes those values.
- Uses a dynamic cooldown after manual thermostat touches so corrections do not happen instantly every time.
- Shows the next defender action in a live status label.
- Supports a custom schedule for target temperatures.
- Supports weather-based activation rules.
- Prioritizes upstairs comfort when upstairs temperature sensors report hot rooms.
- Can use Home Assistant presence entities so upstairs priority applies only while someone is home.
- Exposes fan mode and can optionally move the fan to an energy-saving mode when the room is near target.
- Streams website state in real time with Server-Sent Events, so the user does not need to refresh.

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
9. Optionally set fan saver mode when near target.
10. Correct the thermostat setpoint when it does not match the defender decision.
11. Update the real-time dashboard status.

When the room is above the target, the app commands a setpoint below target to force cooling. If Home Assistant reports that cooling is idle while the room remains above target, it lowers the setpoint one additional degree per cycle, bounded by `Defender__MinimumCoolingSetPointCelsius`. When the room reaches target, the setpoint returns to the exact target.

## Dynamic Cooldown

Cooldown starts only after an external setpoint change. The formula is frequency-based:

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount)
```

`recentTouchCount` is counted inside `TouchFrequencyWindowMinutes`. More repeated manual changes cause longer cooldowns.

## Schedule And Weather Rules

The settings page has a mobile-friendly schedule editor with card-style rules, day chips, clear start/end controls, and a live summary for each rule. Each rule supports:

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

## Real-Time Website

The dashboard and settings page subscribe to:

```text
/api/status/stream
```

This is a Server-Sent Events stream. Mutations still use JSON APIs, but the visible state updates from the live stream.

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
