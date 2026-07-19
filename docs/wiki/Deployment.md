---
layout: doc
title: "Deployment"
---

# Deployment

The app is designed to run in Docker Compose.

## Compose

```bash
docker compose up -d --build
```

The compose file publishes:

```text
8888:8080
```

The website is reachable at:

```text
http://<host>:8888
```

## Runtime State

Runtime state is bind-mounted from the deployment host so a no-cache rebuild, a
Compose project-name change, or a fresh image cannot hide the existing settings
behind a new empty named volume.

Inside the container:

```text
/data/defender-state.json
/data/thermostat-history.jsonl
/data/settings-repo
/app/App_Data
```

On the host, those paths come from:

```text
./App_Data/defender -> /data
./App_Data/auth     -> /app/App_Data
```

`/data/settings-repo` is a local git repository managed by the app. It stores
the website target, defender switch, Settings page values, and schedule history
only. It does not store Home Assistant tokens, accounts, DataProtection keys,
`.env`, raw runtime telemetry, or thermostat history.

## Secrets

Home Assistant credentials and tokens belong in `.env` on the deployment host. They must not be committed to Git.

## Configuration reference (environment variables)

Required:

```text
HomeAssistant__BaseUrl=http://homeassistant.local:8123
HomeAssistant__EntityId=climate.dining_room
HomeAssistant__AccessToken=replace-with-token
```

Optional Home Assistant entities:

```text
HomeAssistant__WeatherEntityId=weather.home
HomeAssistant__OutdoorTemperatureEntityId=sensor.outdoor_temperature
HomeAssistant__OpenMeteoBackupEnabled=true
HomeAssistant__OpenMeteoLatitude=
HomeAssistant__OpenMeteoLongitude=
HomeAssistant__OpenMeteoRefreshMinutes=30
HomeAssistant__UsagePowerEntityId=sensor.alectra_hui_current_power
HomeAssistant__UsageEnergyEntityId=sensor.alectra_hui_energy_today
HomeAssistant__UsageCostEntityId=sensor.alectra_hui_cost_today
HomeAssistant__UsageHourlyCostEntityId=sensor.alectra_hui_hourly_cost
HomeAssistant__UsageCurrentBillEntityId=sensor.alectra_hui_current_bill
HomeAssistant__UsageCurrentBillDueEntityId=sensor.alectra_hui_current_bill_due
HomeAssistant__UsageCurrentBillStatusEntityId=sensor.alectra_hui_current_bill_status
HomeAssistant__Username=optional-bookkeeping-only
HomeAssistant__Password=optional-bookkeeping-only
```

If `WeatherEntityId` is blank the app discovers the first `weather.*` entity; with no weather
entity, `OutdoorTemperatureEntityId` can still provide outdoor temperature. The usage entities
come from the Alectra Hui integration; AC Defender only reads them once Home Assistant has
created them, and historical usage needs the entity recorded by the recorder
(`api/history/period`).

The Open-Meteo backup is enabled by default and is used only when Home Assistant cannot supply
the corresponding real outdoor condition or forecast. If latitude and longitude are blank, AC
Defender reads the installation coordinates from Home Assistant's authenticated `/api/config`
endpoint and caches them. Set both coordinate values to override that location, or set
`OpenMeteoBackupEnabled=false` to disable external weather calls. An incomplete one-coordinate
override is ignored as a pair so two different locations can never be combined. Before the public
request, the location is rounded to two decimal places (roughly kilometre-scale) so exact household
coordinates never leave the Home Assistant client. The refresh
interval is clamped to at least 10 minutes; one request returns the current `temperature_2m` /
`weather_code` and 48
hourly forecast points, so the default cadence stays far below the free non-commercial API limit.
The Open-Meteo client is separate from the Home Assistant client and never receives the Home
Assistant access token. Weather data is provided by [Open-Meteo](https://open-meteo.com/) under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); AC Defender maps WMO weather codes to
display labels but does not otherwise alter the temperature forecast.

The key-free endpoint is intended for non-commercial use and has no uptime guarantee; commercial
deployments should review Open-Meteo's current plan and terms before enabling it. A failed backup
request never creates synthetic weather, and AC Defender continues retrying on its throttled cadence.

Any `Defender` option from `appsettings.json` can be overridden the same way, e.g.
`Defender__RivalScheduleWatchEnabled=false` or `Defender__AcEstimatedAmps=20`. See
[Energy & Costs](Energy-and-Costs.html) for the electricity/TOU keys and
[Defender Logic](Defender-Logic.html) for the guard keys.

When a Home Assistant climate state includes `context`, the defender stores it with the
reading and the audit log: a `user_id` means a Home Assistant user/phone change, a
`parent_id` means an automation/script/service chain, and a context ID without either is a
thermostat/device-origin change. This attribution powers Super Defender, Remote Settling,
the Desired-State Enforcer, and Rival Schedule Watch.
