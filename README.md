# Home Assistant AC Defender

Home Assistant AC Defender is an ASP.NET Core Blazor website plus a hosted background worker that continuously watches a real Home Assistant climate entity and defends the dining room AC target.

The app is designed for Docker hosting on Linux and is currently published by `docker-compose.yml` on host port `8888`.

## What It Does

- Shows the real dining room thermostat state from Home Assistant.
- Generates or accepts a target temperature from the website.
- Checks the thermostat 24/7 on a short polling interval.
- Restores the thermostat to `cool` if anyone changes HVAC mode away from cooling, with an optional short delay while the room is still safe.
- Detects when someone changes the thermostat outside the website.
- Logs external thermostat touches with date, time, previous setpoint, new setpoint, room temperature, outdoor temperature, and weather condition when Home Assistant exposes those values.
- Uses a dynamic cooldown after manual thermostat touches so corrections do not happen instantly every time.
- Adds Conflict Quiet so repeated wall touches can trigger a temporary stand-down while the room is still safe.
- Adds Comfort Sync quiet recovery: randomized extra waits, optional extra holds, command spacing, adaptive quiet levels, and small setpoint nudges so repeated wall changes do not create an obvious immediate tug-of-war.
- Adds Manual Comfort Grace so a wall thermostat change can be left alone while the room remains within the configured comfort band.
- Adds Room Trend Guard so the defender keeps observing when the room is stable or cooling after a wall change, and resumes when it starts warming.
- Adds Thermal Momentum so the defender can wait when the room is already cooling fast enough to reach target soon.
- Adds Natural Walkback so safe-band recovery moves get smaller and less predictable after repeated wall thermostat touches.
- Adds Touch Signature so safe nudges can match the size of recent real wall thermostat steps.
- Adds Visibility Guard so safe nudges slow down when a wall touch happens soon after a defender command.
- Adds Routine Timing so safe corrections after repeated touches wait for normal-looking comfort-check intervals.
- Adds Comfort Budget so repeated safe corrections can rest before another adjustment.
- Adds Natural Cadence so repeated safe corrections wait for a variable future slot based on wall-touch pressure.
- Adds Comfort Compromise so repeated wall choices can influence a temporary safe target that fades back naturally.
- Adds Comfort Memory so repeated safe wall choices can teach a small time-of-day bias that expires automatically.
- Adds Touch Intent so clear warmer wall-choice patterns can get extra safe grace instead of an obvious immediate fight-back.
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
3. Restore HVAC mode to `cool` if another mode is selected, using the cool-mode restore delay only while comfort is still safe.
4. Detect external setpoint changes by comparing the latest Home Assistant setpoint to the previously observed setpoint.
5. Ignore setpoint changes that match commands recently sent by the app.
6. Apply active schedule target if schedule is enabled.
7. Evaluate the weather activation rule.
8. Respect Conflict Quiet when repeated wall touches suggest someone is fighting the thermostat.
9. Respect dynamic cooldown after manual thermostat changes.
10. Respect Manual Comfort Grace when the room is still comfortable after a wall change.
11. Respect Room Trend Guard when the room is stable or cooling after a wall change.
12. Respect Thermal Momentum when the room is already cooling fast enough to reach target soon.
13. Apply Comfort Sync quiet recovery timing unless the room or upstairs is too warm.
14. Shape safe-band recovery commands through Natural Walkback when repeated wall touches make obvious corrections risky.
15. Shape safe-band nudge size through Touch Signature when recent wall changes show a common step size.
16. Respect Visibility Guard when a wall touch happens soon after a defender command.
17. Hold safe corrections for Routine Timing when repeated wall changes make an immediate correction too obvious.
18. Respect Comfort Budget when too many safe adjustments happened recently.
19. Respect Natural Cadence when repeated touches need a less exact safe-correction slot.
20. Apply bounded Comfort Memory for the current time window when room comfort is still safe.
21. Blend repeated safe wall choices through Comfort Compromise and fade them back toward the website target.
22. Extend safe wall-change grace through Touch Intent when recent wall choices clearly ask for warmer air.
23. Optionally set fan saver mode when near target.
24. Correct the thermostat setpoint when it does not match the defender decision.
25. Update the real-time dashboard status.

When the room is above the target, a new defender correction starts by commanding a setpoint exactly 1 C below the current room temperature to force cooling. If Home Assistant reports that cooling is idle/off while the room remains above target, it lowers the setpoint one additional degree per cycle. Normal defender cooling will not go below the website target, and when the room reaches target, the setpoint returns to the exact website target.

## Cool Mode Restore

Cool Mode Restore keeps the rule that HVAC mode must return to `cool`, but can wait between `CoolModeRestoreMinimumDelaySeconds` and `CoolModeRestoreMaximumDelaySeconds` so the change does not always happen instantly.

It only waits while the room is still safe:

```text
currentRoomTemperature <= targetTemperature + CoolModeRestoreComfortBandCelsius
```

If the room gets warmer than that, upstairs comfort becomes severe, or the safety override is crossed, it skips the wait and restores `cool` right away.

## Dynamic Cooldown

Cooldown starts only after an external setpoint change. The formula is frequency-based:

```text
cooldown = min(maxCooldownSeconds, baseCooldownSeconds * recentTouchCount) + randomQuietDelay
```

`recentTouchCount` is counted inside `TouchFrequencyWindowMinutes`. More repeated manual changes cause longer cooldowns.

## Conflict Quiet

Conflict Quiet is for obvious tug-of-war moments. When recent wall touches reach `ConflictQuietTouchThreshold`, the defender stands down for `ConflictQuietMinutes` instead of sending another visible correction.

It only stands down while the room is still safe:

```text
currentRoomTemperature <= targetTemperature + ConflictQuietComfortBandCelsius
```

If the room gets warmer than that, severe upstairs heat is active, or the safety override is crossed, Conflict Quiet ends and the correction path resumes.

## Comfort Sync Quiet Recovery

Comfort Sync is the natural-change algorithm. It affects timing, command spacing, and softer non-warm corrections for real Home Assistant commands. Warm-room cooling corrections use the room-temperature defender target, not the wall thermostat setpoint.

- `AdaptiveQuietnessEnabled`: lets repeated manual touches automatically increase quietness.
- `AdaptiveQuietTouchThreshold`: number of recent wall touches needed before adaptive quietness starts.
- `MaximumAdaptiveDelaySeconds`: longest random delay adaptive quietness may use.
- `MinimumAdaptiveStepCelsius`: smallest automatic nudge size during repeated touches.
- `MaximumAdaptiveHoldChancePercent`: highest chance of waiting one more short period.
- `MaximumAdaptiveCommandGapSeconds`: longest spacing between automatic setpoint commands.
- `MinimumNaturalDelaySeconds` and `MaximumNaturalDelaySeconds`: random extra wait after a manual wall thermostat change.
- `NaturalStepCelsius`: biggest setpoint move for softer non-warm corrections. Warm-room cooling starts from the room-temperature target.
- `NaturalHoldChancePercent`: chance to wait one more short period after cooldown expires.
- `MaxNaturalHolds`: cap on those extra waits so recovery cannot stall forever.
- `MinimumCommandGapSeconds`: minimum spacing between automatic setpoint commands.
- `NaturalSafetyOverrideCelsius`: if room temperature is this far above target, skip quiet waits and restore comfort faster.
- `NaturalWalkbackEnabled`: enables small safe-band walkback nudges after repeated wall touches.
- `NaturalWalkbackTriggerTouches`: recent wall touches needed before walkback starts.
- `NaturalWalkbackStepCelsius`: normal maximum nudge size while walkback is active.
- `NaturalWalkbackJitterCelsius`: tiny variation added to walkback step size so nudges are not identical.
- `NaturalWalkbackSafetyBandCelsius`: extra room warmth allowed before walkback stops being subtle.
- `TouchSignatureEnabled`: learns recent real wall-step size and shapes safe nudges to match.
- `TouchSignatureTriggerTouches`: recent wall steps needed before signature shaping starts.
- `TouchSignatureRetentionMinutes`: how long recent wall steps remain useful.
- `TouchSignatureMinimumStepCelsius`: smallest learned safe nudge size.
- `TouchSignatureMaximumStepCelsius`: biggest learned safe nudge size.
- `TouchSignatureSafetyBandCelsius`: extra room warmth allowed before touch signature stops.
- `VisibilityGuardEnabled`: holds safe corrections when a wall touch happens soon after a defender command.
- `VisibilityGuardTriggerNotices`: noticed correction signals needed before visibility hold starts.
- `VisibilityGuardNoticeWindowMinutes`: how long noticed signals remain useful.
- `VisibilityGuardAfterCommandSeconds`: how soon after a defender command a wall touch counts as noticed.
- `VisibilityGuardMinimumHoldMinutes`: shortest safe visibility hold.
- `VisibilityGuardMaximumHoldMinutes`: longest safe visibility hold.
- `VisibilityGuardSafetyBandCelsius`: extra room warmth allowed before visibility guard stops waiting.
- `RoutineTimingEnabled`: waits for a normal-looking comfort-check rhythm after repeated safe wall changes.
- `RoutineTimingTriggerTouches`: recent wall touches needed before routine timing can hold.
- `RoutineTimingIntervalMinutes`: minute rhythm used for safe correction timing.
- `RoutineTimingJitterMinutes`: small extra random wait added to the rhythm.
- `RoutineTimingMaxDelayMinutes`: longest safe routine timing hold.
- `RoutineTimingSafetyBandCelsius`: extra room warmth allowed before routine timing stops waiting.
- `ComfortBudgetEnabled`: limits repeated safe corrections inside a rolling window.
- `ComfortBudgetWindowMinutes`: how long recent automatic setpoint commands count.
- `ComfortBudgetMaxCommands`: safe corrections allowed inside the window.
- `ComfortBudgetSafetyBandCelsius`: extra room warmth allowed before the budget stops waiting.
- `NaturalCadenceEnabled`: picks a variable future slot for safe nudges after repeated wall touches.
- `NaturalCadenceTriggerTouches`: recent wall touches needed before cadence starts.
- `NaturalCadenceMinimumMinutes`: shortest safe cadence wait.
- `NaturalCadenceMaximumMinutes`: longest safe cadence wait.
- `NaturalCadenceJitterMinutes`: small time wobble added around cadence waits.
- `NaturalCadenceSafetyBandCelsius`: extra room warmth allowed before cadence stops waiting.
- `ComfortCompromiseEnabled`: lets repeated wall choices temporarily influence the effective target while safe.
- `ComfortCompromiseTriggerTouches`: recent wall touches needed before a compromise starts.
- `ComfortCompromiseHoldMinutes`: how long the wall preference can rest before fading back.
- `ComfortCompromiseDecayMinutes`: how long the preference takes to fade back to the website target.
- `ComfortCompromiseMaxOffsetCelsius`: maximum temporary difference from the website target.
- `ComfortCompromiseSafetyBandCelsius`: extra room warmth allowed before compromise stops.
- `ComfortMemoryEnabled`: lets repeated safe wall choices teach a small time-of-day target bias.
- `ComfortMemoryLearningTouches`: recent wall touches needed before memory learns.
- `ComfortMemoryRetentionHours`: how long a learned time-window bias remains valid.
- `ComfortMemoryMaxOffsetCelsius`: largest remembered target adjustment.
- `ComfortMemorySafetyBandCelsius`: extra room warmth allowed before memory stops applying.
- `ManualComfortGraceEnabled`: lets a wall thermostat change rest while the room is still comfortable.
- `ManualComfortGraceMinutes`: maximum time to leave that wall change alone.
- `ManualComfortGraceBandCelsius`: extra room warmth allowed above target before the defender resumes.
- `TouchIntentEnabled`: learns whether recent wall changes are warmer, cooler, or mixed.
- `TouchIntentMinimumTouches`: wall choices needed before the intent is trusted.
- `TouchIntentWindowMinutes`: how long wall choices remain part of the intent pattern.
- `TouchIntentNetWarmThresholdCelsius`: net warmer movement needed before extra grace is allowed.
- `TouchIntentExtraGraceMinutes`: extra safe grace added for clear warmer intent.
- `TouchIntentSafetyBandCelsius`: extra room warmth allowed before Touch Intent stops extending grace.
- `SensorRhythmGuardEnabled`: waits for the learned Home Assistant sensor beat before safe nudges.
- `SensorRhythmMinimumSamples`: real Home Assistant readings needed before the beat is trusted.
- `SensorRhythmWindowMinutes`: how long real reading timestamps remain useful.
- `SensorRhythmJitterSeconds`: small extra wait after the learned beat.
- `SensorRhythmSafetyBandCelsius`: extra room warmth allowed before Sensor Rhythm stops waiting.
- `RoomTrendGuardEnabled`: lets the defender observe real room temperature trend before nudging.
- `RoomTrendWindowMinutes`: how far back real room-temperature samples are compared.
- `RoomTrendStableToleranceCelsius`: small temperature changes counted as stable.
- `RoomTrendHoldMinutes`: how long to keep observing when room trend is stable or cooling.
- `ThermalMomentumGuardEnabled`: lets the defender wait when the room is cooling toward target quickly enough.
- `ThermalMomentumMinimumCoolingRateCelsiusPerHour`: minimum real cooling speed before momentum can hold.
- `ThermalMomentumLookAheadMinutes`: only hold when target is estimated within this many minutes.
- `ThermalMomentumHoldMinutes`: how long to let cooling continue before checking again.

Example: if the room is `25.0 C`, the website target is `22.0 C`, and the thermostat was manually moved to `26.0 C`, the first defender command is `24.0 C` because it starts one degree below current room temperature, not one degree below the wall setting. If Home Assistant says cooling has stopped while the room is still above target, later decisions continue down to `23.0 C`, then `22.0 C`.

Adaptive quiet levels are shown on the dashboard:

- `Calm`: no recent wall touches.
- `Light`: a small number of recent wall touches, using base quiet settings.
- `Quiet`: repeated touches started, so waits and spacing begin increasing.
- `Extra quiet`: more repeated touches, smaller nudges and higher hold chance.
- `Softest`: maximum adaptive quietness before comfort safety overrides.

Natural Walkback is the last command-shaping layer before a real setpoint command is sent. When recent wall touches reach the trigger count and the room is still inside the walkback safe band, the defender uses smaller safe-band nudges with tiny variation. If the room needs warm-room defense, it skips walkback and still commands one degree below current room temperature.

Touch Signature learns from recent real wall thermostat changes. If enough wall steps exist and the room is still inside the signature safe band, safe nudges use about the learned bounded wall-step size. If the room gets too warm or upstairs comfort needs direct cooling, the signature clears and the real correction path continues.

Visibility Guard watches for wall touches that happen soon after a defender command. Those touches can mean the correction was noticed, so the next safe-band correction waits for a variable hold. It clears immediately when the room needs direct cooling, and it never changes the rule that warm-room defense starts 1 C below current room temperature.

Routine Timing is a timing layer for safe corrections. When repeated wall changes happen and the room is still inside its safe band, the next correction waits until a normal minute rhythm, with small wiggle time. If the room gets too warm or upstairs comfort needs direct cooling, Routine Timing clears and the real correction path continues.

Comfort Budget is a rolling command limiter for safe corrections. If too many automatic setpoint adjustments happened inside the configured window, the defender rests until the oldest one leaves the window. If the room gets too warm or upstairs comfort needs direct cooling, the budget clears and the real correction path continues.

Natural Cadence picks a variable future slot for safe corrections after repeated wall touches. The slot gets later as touch pressure or recent command pressure rises, and it has a small jitter so safe nudges do not land at identical times. If the room gets too warm or upstairs comfort needs direct cooling, cadence clears and the real correction path continues.

Comfort Compromise is a temporary effective target. If wall changes repeat and the room is still inside the compromise safe band, the latest wall setpoint can influence the defender target up to the configured maximum offset. After the hold time, that influence fades back to the website target over the decay window. If the room gets too warm, the compromise clears immediately and normal warm-room defense resumes.

Comfort Memory is slower than Comfort Compromise. It learns a small offset for the current hour after repeated safe wall choices, then applies that offset on later checks in the same time window. Learned memory expires after the configured retention hours and is skipped when the room is warm, the safety override is crossed, or upstairs is already hot and the memory would relax cooling.

Manual Comfort Grace is different from cooldown. Cooldown waits after a manual touch. Manual Comfort Grace can keep waiting after cooldown if the room is still within the comfort band. If the room rises above the band, the HVAC mode changes away from `cool`, or upstairs becomes severely hot, grace ends and the real thermostat correction path resumes.

Touch Intent watches recent real wall changes and classifies the pattern as warmer, cooler, mixed, or learning. If the pattern is clearly warmer and the real room is still inside the intent safe band, it can extend Manual Comfort Grace by the configured extra minutes. If the room gets too warm or upstairs heat needs direct cooling, Touch Intent steps aside immediately.

Sensor Rhythm watches real Home Assistant reading timestamps and learns the normal interval between poll updates. When a correction is safe, it can wait until just after the learned sensor beat plus a small wiggle, making the next command look less mechanically immediate. If the room gets too warm or upstairs heat needs direct cooling, Sensor Rhythm clears and the real correction path continues.

Room Trend Guard uses real Home Assistant room-temperature readings. It compares the oldest and newest room samples inside the configured trend window. If the room is stable or cooling after a wall change, it can keep observing before sending a nudge. If the room is warming, above the grace band, or beyond the safety override, it lets the real correction continue.

Thermal Momentum also uses real Home Assistant room-temperature readings. After a recent wall touch, it estimates cooling speed and minutes to target. If the room is already cooling fast enough and the target looks close, it holds briefly so the room can keep cooling without another obvious thermostat command.

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
