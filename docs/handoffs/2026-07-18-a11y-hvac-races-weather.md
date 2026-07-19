# Accessibility, HVAC Race, and Weather Fallback Handoff

Date: 2026-07-18

## Completion decisions

- Accessibility was checked in the real authenticated app across every route, both themes, desktop and 320 px mobile layouts. The 33 authenticated configurations and four login configurations had zero automated axe findings and no horizontal overflow. Reduced-motion behavior, dialog focus isolation, Cantonese document language, the Defense evidence drawer, and Energy controls/charts were also exercised.
- Thermostat reads remain real Home Assistant reads. Invalid/unavailable climate payloads are rejected; a real OFF entity may retain the existing `0` setpoint sentinel when the integration reports zero or omits its target.
- Automatic OFF and COOL actions are serialized with explicit priorities. Safety OFF cannot be canceled by lower-priority work; re-enable and siesta-cancel can supersede stale non-safety park/siesta work; direct comfort steps around conflicting unconfirmed intents.
- The automatic compressor policy now includes five-minute OFF and ON dwell tracking from both accepted commands and observed Home Assistant transitions. A cooling-failure restore cannot immediately recycle COOL to OFF from retained room history; a fresh shutdown waits for the minimum-ON dwell. Front-door safety OFF remains exempt.
- Definite command rejection uses bounded exponential retry. A later safety/direct-comfort command may escalate once past an ordinary rejection, while rejection of the urgent command itself is still backed off to avoid five-second request hammering.
- Status-bearing Home Assistant API failures are reported as request failures and do not impersonate a vanished thermostat or arm Tamper Truce.
- The key-free Open-Meteo fallback runs only when real Home Assistant weather is unavailable. It uses configured coordinates or rounded Home Assistant installation coordinates, never sends the Home Assistant token to the weather host, preserves observation timestamps, caches results, and fails closed when data expires.
- Settings and authentication persistence use atomic/serialized writes. Settings Git commands are non-interactive, bounded by one shared deadline, recover interrupted journal writes, cache healthy state, and back off automatic recovery for one minute after failure so Git cannot stall every HVAC poll.

## Validation and cleanup record

- `dotnet build HomeAssistantAcDefender.csproj -t:Rebuild --no-restore`: passed with zero warnings and zero errors.
- `dotnet run --project HomeAssistantAcDefender.Tests/HomeAssistantAcDefender.Tests.csproj --no-restore`: full regression console passed.
- Temporary browser-audit scripts containing local test credentials were removed before staging.
- Pre-release repository audit found one `master` checkout, no linked worktrees, and no stashes. No `.env`, `App_Data`, deployment archive, token, account credential, or browser-audit credential is part of the intended commit.
- Release target remains `master`, production host port `8888`, and `/home/docker/HomeAssistantAcDefender`; production secrets stay only in the host-managed `.env`.
