# All-Day Outdoor Lockout Handoff

Date: 2026-07-20

## Completion decisions

- The ordinary Outdoor Power Rule remains a 24/7 guard: every five-second worker cycle refreshes real Home Assistant climate/weather evidence before the rule decides whether automatic cooling work may continue.
- The production default now keeps the defender fully stood down while a fresh outdoor reading is below 23 C. The threshold is explicit in `appsettings.json` and in `DefenderOptions`, so the deployed behavior does not depend on an undocumented host override.
- Exactly 23 C leaves the full lockout and enters the existing gentle band. Lite mode now covers 23-25 C and still allows recovery when the room rises beyond its configured band.
- Direct room-comfort safety remains authoritative. A genuinely hot dining room or the existing severe-comfort bypass clears the outdoor hold; stale or missing weather cannot create a lockout.
- The rule does not create fake thermostat or weather state and does not pause observation. The background worker continues reading the real `climate.dining_room` entity while the lockout is active.

## Validation and cleanup record

- `dotnet run --project HomeAssistantAcDefender.Tests/HomeAssistantAcDefender.Tests.csproj`: full regression console passed, including 22.9 C checks at 02:00, 14:00, and 22:00, the exact 23.0 C boundary, the hot-room bypass, stale-weather rejection, and the recording fake's zero climate-service-call assertion.
- `dotnet build HomeAssistantAcDefender.csproj`: passed with zero warnings and zero errors.
- Dashboard and Settings rendered in the authenticated Development app with no browser console errors; visual review found no regression.
- The pre-release repository audit found one clean `master` checkout before this task, no linked worktrees, no stashes, and no unrelated work to preserve. Temporary browser diagnostics were removed before staging.
- Release target remains `master`, production host port `8888`, and `/home/docker/HomeAssistantAcDefender`; production secrets remain only in the host-managed `.env`.
