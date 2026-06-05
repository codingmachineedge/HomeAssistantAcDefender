# Architecture

The application is one ASP.NET Core process with two responsibilities:

- Blazor Server website with MudBlazor dashboard and settings components.
- Hosted background worker for continuous defender checks.

## Main Components

- `HomeAssistantClient`: Calls Home Assistant REST endpoints for climate, weather, temperature, and fan services.
- `DefenderStateStore`: Persists target, settings, schedule, weather, audit log, cooldown state, Comfort Sync timing, and command tracking.
- `AcDefenderService`: Runs the defender cycle and executes real Home Assistant corrections.
- `AcDefenderWorker`: Runs `AcDefenderService` continuously using the configured polling interval.
- `Components/Pages/Dashboard.razor`: MudBlazor dashboard with live polling, real thermostat actions, fan controls, audit log, and helper descriptions.
- `Components/Pages/Settings.razor`: MudBlazor settings page with schedule editor and helper descriptions under controls.
- `/api/status/stream`: Server-Sent Events endpoint retained for external real-time clients.

## Command Attribution

The app records setpoints it commands itself and treats matching Home Assistant updates inside the command grace window as app-originated. Other setpoint changes are logged as external thermostat touches.

## Comfort Sync

Comfort Sync is implemented inside `DefenderStateStore` and consumed by `AcDefenderService`. The worker still talks only to the real Home Assistant climate entity. Comfort Sync decides whether to wait, whether to hold briefly, and whether to cap a correction to a small nudge before `HomeAssistantClient` sends the command.
