# API

## State

```text
GET /api/status
GET /api/settings
GET /api/usage/live
GET /api/usage/history
GET /api/status/stream
```

`/api/status/stream` is a Server-Sent Events endpoint that emits the full defender snapshot every second.

The status snapshot includes:

- `cooldownSeconds`: remaining dynamic/manual-touch cooldown.
- `coolModeRestore`: cool-mode restore delay status, remaining seconds, and due time.
- `naturalRecovery`: quiet recovery status, quiet level, wait seconds, recent touch count, base/effective nudge size, base/effective hold chance, and effective command gap.
- `naturalWalkback`: safe-band walkback status, active flag, touch score, and current walkback step.
- `touchSignature`: safe nudge signature status, active flag, sample count, learned step, and effective step.
- `visibilityGuard`: safe correction visibility status, active flag, noticed signal count, pressure score, remaining seconds, and hold expiry.
- `routineTiming`: safe correction rhythm status, wait seconds, interval, jitter, and due time.
- `comfortBudget`: safe correction budget status, wait seconds, recent command count, max commands, and due time.
- `naturalCadence`: variable safe-correction slot status, wait seconds, touch pressure, recent command count, and due time.
- `comfortCompromise`: temporary blended target status, preferred wall setpoint, effective target, remaining seconds, and expiry time.
- `comfortMemory`: learned time-window status, active flag, sample count, learned offset, and effective target.
- `conflictQuiet`: repeated-touch stand-down status, remaining seconds, trigger touch count, comfort band, and expiry time.
- `manualComfortGrace`: wall-change grace status, remaining seconds, comfort band, and expiry time.
- `touchIntent`: wall-choice intent status, active flag, direction, recent touch count, net change, and extra grace minutes.
- `setpointEcho`: pending setpoint confirmation status, wait seconds, pending target, and expiry time.
- `repeatCommand`: identical-command hold status, wait seconds, pressure, last defender setpoint, and expiry time.
- `sensorRhythm`: Home Assistant reading cadence status, wait seconds, learned median interval, sample count, and due time.
- `roomTrend`: real room trend direction, delta, sample count, hold status, and remaining hold seconds.
- `thermalMomentum`: real cooling rate, estimated minutes to target, hold status, and remaining hold seconds.
- `thermostatChanges`: external thermostat touch audit log.
- `comfort`: upstairs comfort and presence status.

## Usage

```text
GET /api/usage/live
GET /api/usage/history?hours=24
GET /api/usage/history?entityId=sensor.alectra_hui_energy_today&from=2026-06-05T00:00:00Z&to=2026-06-05T23:59:59Z
```

`/api/usage/live` returns the configured Home Assistant usage sensors for current power, daily energy, and daily cost.

`/api/usage/history` reads Home Assistant recorder history for the configured energy entity by default. Pass `entityId` to inspect another sensor, `hours` for a window ending now, or explicit `from` and `to` timestamps.

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

## CLI Usage

```text
dotnet run -- usage-live [--json]
dotnet run -- usage-history [--entity sensor.name] [--hours 24] [--from timestamp] [--to timestamp] [--json]
```

CLI commands use the same Home Assistant base URL, token, and usage sensor configuration as the web app. They can be overridden with `--base-url`, `--token`, `--power`, `--energy`, `--cost`, and `--entity`.
