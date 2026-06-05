# API

## State

```text
GET /api/status
GET /api/settings
GET /api/status/stream
```

`/api/status/stream` is a Server-Sent Events endpoint that emits the full defender snapshot every second.

The status snapshot includes:

- `cooldownSeconds`: remaining dynamic/manual-touch cooldown.
- `naturalRecovery`: quiet recovery status, wait seconds, recent touch count, nudge size, and hold chance.
- `thermostatChanges`: external thermostat touch audit log.
- `comfort`: upstairs comfort and presence status.

## Target And Defender

```text
POST /api/target/generate
POST /api/target
POST /api/defender
POST /api/settings
```

`POST /api/settings` accepts the automation, Comfort Sync, fan saver, upstairs comfort, presence, and schedule settings used by the MudBlazor settings page.

## Real Thermostat Commands

```text
POST /api/thermostat/refresh
POST /api/thermostat/force-target
POST /api/thermostat/force-boost
POST /api/thermostat/fan
```

All thermostat command endpoints act on the real configured Home Assistant climate entity.
