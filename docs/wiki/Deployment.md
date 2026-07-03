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

Runtime state is persisted in the Docker volume `ac-defender-data` at:

```text
/data/defender-state.json
```

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

Any `Defender` option from `appsettings.json` can be overridden the same way, e.g.
`Defender__RivalScheduleWatchEnabled=false` or `Defender__AcEstimatedAmps=20`. See
[Energy & Costs](Energy-and-Costs.html) for the electricity/TOU keys and
[Defender Logic](Defender-Logic.html) for the guard keys.

When a Home Assistant climate state includes `context`, the defender stores it with the
reading and the audit log: a `user_id` means a Home Assistant user/phone change, a
`parent_id` means an automation/script/service chain, and a context ID without either is a
thermostat/device-origin change. This attribution powers Super Defender, Remote Settling,
the Desired-State Enforcer, and Rival Schedule Watch.
