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

Comfort Sync is implemented inside `DefenderStateStore` and consumed by `AcDefenderService`. The worker still talks only to the real Home Assistant climate entity. Comfort Sync decides whether to wait, whether to hold briefly, and which real setpoint command `HomeAssistantClient` sends. Warm-room corrections are anchored to current room temperature, so a raised wall setpoint does not become the starting point for the next cooling command.

Natural Walkback is part of that same state-store command selection path. It calculates touch pressure from recent external wall changes, then uses smaller safe-band setpoint steps before `AcDefenderService` sends the real Home Assistant command. It is skipped when the room needs the direct one-degree-below-room cooling correction.

Comfort Compromise is evaluated inside `CalculateExpectedSetPoint`. It can temporarily adjust the effective target from repeated wall choices, but only while the real room temperature remains inside its safety band. Target changes from the website, schedule, or upstairs comfort clear the compromise.

Adaptive quietness is also calculated in `DefenderStateStore`. It turns recent external thermostat touches into a quiet level and effective delay, hold chance, command gap, and nudge size. The dashboard displays those effective values so the UI matches the worker decision.

Cool Mode Restore is stored in `DefenderStateStore` and evaluated by `AcDefenderService` before pause, schedule, weather, cooldown, or setpoint logic. It can delay the real `climate.set_hvac_mode` restore command while room temperature is inside the safe band, and it skips the delay when comfort safety requires cooling now.

Manual Comfort Grace is stored and evaluated in `DefenderStateStore` as well. `AcDefenderService` asks it whether a recent wall thermostat change should be left alone while room temperature is still within the configured comfort band. It never creates fake state; it only delays real correction commands.

Conflict Quiet is stored in `DefenderStateStore` and evaluated by `AcDefenderService` before normal cooldown. It turns repeated external wall touches into a temporary stand-down while room temperature remains within the configured safe band. It does not fake a thermostat update; it only avoids sending an obvious corrective command for a while.

Room Trend Guard is also stored and evaluated in `DefenderStateStore`. It keeps a small history of real dining room temperature samples and classifies the room as warming, stable, or cooling before `AcDefenderService` sends a correction. It only affects timing; all commands still go through `HomeAssistantClient`.

Thermal Momentum is another `DefenderStateStore` decision that uses those same real room-temperature samples. It estimates cooling rate and minutes to target after a recent wall touch, then lets `AcDefenderService` wait when the room is already moving toward the target fast enough. It never invents thermostat state and does not issue fake commands.
