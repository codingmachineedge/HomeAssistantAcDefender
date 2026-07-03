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
- Classifies Home Assistant user/phone changes, Home Assistant automation changes, and thermostat/device changes when Home Assistant includes context data.
- Uses a dynamic cooldown after manual thermostat touches so corrections do not happen instantly every time.
- Adds Conflict Quiet so repeated wall touches can trigger a temporary stand-down while the room is still safe.
- Adds Tug-of-War Truce so alternating up/down thermostat fights get a temporary safe answer-back hold.
- Adds Comfort Sync quiet recovery: randomized extra waits, optional extra holds, command spacing, adaptive quiet levels, and small setpoint nudges so repeated wall changes do not create an obvious immediate tug-of-war.
- Adds Manual Comfort Grace so a wall thermostat change can be left alone while the room remains within the configured comfort band.
- Adds Room Trend Guard so the defender keeps observing when the room is stable or cooling after a wall change, and resumes when it starts warming.
- Adds Thermal Momentum so the defender can wait when the room is already cooling fast enough to reach target soon.
- Adds Weather Drift Timing so safe corrections can wait for real outdoor weather movement instead of happening immediately.
- Adds HVAC Alibi so safe corrections can wait for a real Home Assistant `hvac_action` transition before they land.
- Adds Telemetry Alibi so safe corrections can wait for a normal Home Assistant, weather, or Alectra Hui update after repeated touches.
- Adds Alectra Peak Power Saver so safe cooling corrections get more chill during On-peak, high-price, or high-power periods.
- Adds a Front-door Guard Post kill switch that pauses the defender and can turn the thermostat off when a real front-door person detector trips.
- Adds Natural Walkback so safe-band recovery moves get smaller and less predictable after repeated wall thermostat touches.
- Adds Touch Signature so safe nudges can match the size of recent real wall thermostat steps.
- Adds Human Nudge so safe commands are snapped to normal thermostat-looking step values instead of suspicious precise numbers.
- Adds Visibility Guard so safe nudges slow down when a wall touch happens soon after a defender command.
- Adds Routine Timing so safe corrections after repeated touches wait for normal-looking comfort-check intervals.
- Adds Comfort Budget so repeated safe corrections can rest before another adjustment.
- Adds Command Camouflage so a recent helper command gets a believable gap before another safe correction.
- Adds Stealth Governor so high overall touch/command pressure can trigger a low-profile safe hold.
- Adds Natural Cadence so repeated safe corrections wait for a variable future slot based on wall-touch pressure.
- Adds Comfort Pace so frequent wall changes can wait for a calmer weather, Home Assistant sensor, or clock-aligned climate slot before a safe correction.
- Adds Comfort Envelope so small safe wall setpoint differences can rest briefly after repeated touches instead of being corrected immediately.
- Adds Comfort Compromise so repeated wall choices can influence a temporary safe target that fades back naturally.
- Adds Comfort Memory so repeated safe wall choices can teach a small time-of-day bias that expires automatically.
- Adds Touch Intent so clear warmer wall-choice patterns can get extra safe grace instead of an obvious immediate fight-back.
- Adds Cooler Intent Fast Lane so repeated cooler wall choices can skip quiet waits and cool sooner without changing the website target.
- Adds Super Defender so repeated Home Assistant user/phone or automation changes can temporarily bypass quiet waits while the room still needs cooling.
- Adds Remote Settling Guard so repeated Home Assistant user/phone or automation changes get a quiet safe window before the defender answers back.
- Adds Setpoint Stillness so safe corrections wait until real Home Assistant readings show the wall setpoint has stopped changing.
- Adds a two-minute website command debounce only around controls that can affect thermostat temperature or mode.
- Adds emergency protocols for too-cold, someone-upset, and suspicion quiet situations.
- Adds a repeated mega alert when cool mode is demanded but the thermostat stays idle or cooling does not lower room temperature.
- Shows the next defender action in a live status label.
- Shows clickable log details with JSON context for wall touches and defender events.
- Supports persisted light/dark theme selection and Toronto 24-hour timestamps.
- Supports a custom schedule for target temperatures.
- Supports weather-based activation rules.
- Prioritizes upstairs comfort when upstairs temperature sensors report hot rooms.
- Can use Home Assistant presence entities so upstairs priority applies only while someone is home.
- Exposes fan mode and can optionally move the fan to an energy-saving mode when the room is near target.
- Reads optional Home Assistant usage sensors for live power, daily energy, daily cost, and 24-hour recorder history.
- Adds a tabbed Alectra Hui command tent with search, desk filters, grouped entity cards, tables, and charts.
- Includes CLI commands for live and historical usage checks without starting the web app.
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

If `HomeAssistant__WeatherEntityId` is blank, the app discovers the first `weather.*` entity. If no weather entity exists, `HomeAssistant__OutdoorTemperatureEntityId` can provide only outdoor temperature.

The usage entities are optional Home Assistant sensor IDs. `UsagePowerEntityId` is shown as current live power, `UsageEnergyEntityId` is used for daily energy and default history, `UsageHourlyCostEntityId` is the newest hourly interval cost, `UsageCostEntityId` is shown as current daily cost, and the bill entity IDs show the current bill amount, due date, and bill fetch status when available. Historical usage uses Home Assistant recorder history from `api/history/period`, so the entity must be recorded by Home Assistant.

For Alectra readings, install the Alectra Hui Home Assistant integration or run the Windows publisher first. It creates `sensor.alectra_hui_current_power`, `sensor.alectra_hui_energy_today`, `sensor.alectra_hui_hourly_cost`, `sensor.alectra_hui_cost_today`, and the `sensor.alectra_hui_current_bill*` bill entities; AC Defender only reads those entities after Home Assistant has created them. The Energy page has an Alectra Hui command tent with tabs for overview, searchable grouped entities, 24-hour charts, and a table view. It lists every Home Assistant entity whose entity ID contains `alectra_hui`, including setting controls like the auto-switch, current-plan selector, and live-poll number. The Energy page is read-only except for refresh; it does not send thermostat commands.

When Home Assistant includes a climate state `context`, the defender stores it with the thermostat reading and audit log. A `user_id` is treated as a Home Assistant user or phone app change. A `parent_id` is treated as a Home Assistant automation, script, or service-chain change. A context ID without either field is treated as a thermostat/device-side change. This source label is an attribution helper, not a fake thermostat state.

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
16. Shape the final safe command through Human Nudge so it looks like a normal thermostat step.
17. Respect Visibility Guard when a wall touch happens soon after a defender command.
18. Hold safe corrections for Routine Timing when repeated wall changes make an immediate correction too obvious.
19. Respect Comfort Budget when too many safe adjustments happened recently.
20. Respect Command Camouflage when a recent helper command needs a believable gap before another safe correction.
21. Respect Stealth Governor when the overall activity pressure score is high.
22. Respect Natural Cadence when repeated touches need a less exact safe-correction slot.
23. Respect Setpoint Echo and Setpoint Stillness so safe follow-up commands wait for real Home Assistant confirmation and a settled wall setpoint.
24. Respect HVAC Alibi when repeated safe wall changes can wait for a real `hvac_action` transition.
25. Respect Telemetry Alibi when repeated safe wall changes can wait for a normal Home Assistant, weather, or Alectra Hui update.
26. Respect Comfort Pace when frequent wall changes need a calmer weather, sensor, or clock-aligned climate slot.
27. Respect Comfort Envelope when a repeated wall setpoint is still inside the safe accepted range.
28. Respect Tug-of-War Truce when alternating up/down thermostat flips suggest someone is watching the fight.
29. Apply bounded Comfort Memory for the current time window when room comfort is still safe.
30. Blend repeated safe wall choices through Comfort Compromise and fade them back toward the website target.
31. Extend safe wall-change grace through Touch Intent when recent wall choices clearly ask for warmer air.
32. Activate Cooler Intent Fast Lane when repeated cooler wall choices show the person wants cooling sooner.
33. Activate Super Defender when repeated Home Assistant user/phone or automation changes happen inside the configured window.
34. Respect Remote Settling Guard when repeated Home Assistant-side changes should get a quiet safe window.
35. Respect Weather Drift Timing when outdoor temperature is stable or cooling and the room is still safe.
36. Respect Alectra Peak Power Saver when Alectra Hui reports On-peak, high current price, or high current power and the room is still safe.
37. Respect Front-door Guard Post when a real front-door person detector reports a person; pause the defender and turn the thermostat off if enabled.
38. Optionally set fan saver mode when near target.
39. Correct the thermostat setpoint when it does not match the defender decision.
39. Update the real-time dashboard status.

When the room is above the target, a new defender correction starts by commanding a setpoint exactly 1 C below the current room temperature to force cooling. If Home Assistant reports that cooling is idle/off while the room remains above target, it lowers the setpoint one additional degree per cycle. Normal defender cooling will not go below the website target, and when the room reaches target, the setpoint returns to the exact website target.

Website command debounce is a separate two-minute guard around manual website actions that affect the thermostat or a future thermostat command: target generation, target changes, force exact target, force cooling, fan changes, thermostat-off controls, and the too-cold emergency. Defender activation, settings saves, and refresh-only actions bypass the thermostat debounce. The first thermostat-affecting click is accepted; later thermostat-affecting clicks show the remaining wait instead of spamming Home Assistant.

Cooling failure watch reads only real Home Assistant data. It raises a repeated mega alert when the climate entity is in `cool`, room temperature is clearly above the setpoint, and `hvac_action` stays idle for several minutes. It also watches for the fallback case where `hvac_action` says cooling but the room does not drop over the retained sample window.

Emergency protocols are real controls on the dashboard. `Too cold` pauses the defender and turns the thermostat off through Home Assistant. `Someone upset` and `Suspicion quiet` start observe-only windows where the worker keeps reading the thermostat 24/7 but does not send corrective thermostat commands until the quiet window ends.

Super Defender is the strict response mode for repeated phone/Home Assistant changes. It watches only real Home Assistant context data from the climate entity. When enough remote-style changes happen inside the configured window, it arms for a hold period. While armed, if the room is still above target and not inside a safe natural-recovery band, it can bypass quiet timing so the normal warm-room correction runs sooner. The app intentionally does not send router or Wi-Fi blocking commands. If you want to block thermostat network access, use router/MAC controls manually and only if you accept the risk that the defender may lose thermostat monitoring and recovery.

Remote Settling Guard is the quieter partner to Super Defender. It also uses real Home Assistant context attribution, but instead of getting stricter it gives repeated phone/user or automation changes a quiet settling window. During that window only safe corrections wait; if the room gets too warm, cooler intent is active, or direct cooling is needed, it clears immediately.

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

## Tug-of-War Truce

Tug-of-War Truce is for the fun little "is this thermostat arguing with me?" moment. It reads the real external thermostat audit log, converts recent setpoint changes into an `up -> down -> up` style direction pattern, and counts direction flips inside `TugOfWarTruceWindowMinutes`.

When `TugOfWarTruceMinimumFlips` is reached, it holds only safe answer-back corrections for `TugOfWarTruceHoldMinutes` while:

```text
currentRoomTemperature <= targetTemperature + TugOfWarTruceSafetyBandCelsius
```

If the room gets too warm, upstairs comfort needs direct cooling, Cooler Intent Fast Lane is active, or Super Defender strict timing is active, the truce clears and the normal comfort path continues.

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
- `HumanNudgeEnabled`: snaps safe commands to normal thermostat-looking step values.
- `HumanNudgeTriggerTouches`: recent wall touches needed before human nudge starts.
- `HumanNudgeStepCelsius`: step size to imitate for the final safe command.
- `HumanNudgeSafetyBandCelsius`: extra room warmth allowed before human nudge stops shaping.
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
- `CommandCamouflageEnabled`: waits after a recent helper setpoint command before another safe correction.
- `CommandCamouflageMinimumGapSeconds`: shortest cover gap after the last helper setpoint command.
- `CommandCamouflagePressureExtraSeconds`: extra cover gap added as recent touches or helper commands rise.
- `CommandCamouflageSafetyBandCelsius`: extra room warmth allowed before command camouflage stops waiting.
- `StealthGovernorEnabled`: enables the overall low-profile pressure guard.
- `StealthGovernorTriggerScore`: 0-100 pressure score that starts a safe low-profile hold.
- `StealthGovernorMinimumHoldMinutes`: shortest low-profile hold after the score is crossed.
- `StealthGovernorMaximumHoldMinutes`: longest low-profile hold as the score rises.
- `StealthGovernorSafetyBandCelsius`: extra room warmth allowed before stealth governor stops waiting.
- `NaturalCadenceEnabled`: picks a variable future slot for safe nudges after repeated wall touches.
- `NaturalCadenceTriggerTouches`: recent wall touches needed before cadence starts.
- `NaturalCadenceMinimumMinutes`: shortest safe cadence wait.
- `NaturalCadenceMaximumMinutes`: longest safe cadence wait.
- `NaturalCadenceJitterMinutes`: small time wobble added around cadence waits.
- `NaturalCadenceSafetyBandCelsius`: extra room warmth allowed before cadence stops waiting.
- `NaturalChangePlannerEnabled`: enables Comfort Pace after frequent wall changes.
- `NaturalChangePlannerTriggerTouches`: recent wall touches needed before Comfort Pace starts.
- `NaturalChangePlannerMinimumMinutes`: shortest calm climate-slot wait.
- `NaturalChangePlannerMaximumMinutes`: longest calm climate-slot wait as touch pressure rises.
- `NaturalChangePlannerJitterMinutes`: small random wiggle around the selected climate slot.
- `NaturalChangePlannerSafetyBandCelsius`: extra room warmth allowed before Comfort Pace stops waiting.
- `NaturalChangePlannerPreferWeatherSlots`: lets real weather updates or outdoor warming shorten the selected wait.
- `NaturalChangePlannerPreferSensorBeat`: lines the selected wait up with the learned Home Assistant climate reading rhythm.
- `TugOfWarTruceEnabled`: enables an up/down flip detector for obvious thermostat back-and-forth.
- `TugOfWarTruceMinimumFlips`: direction flips needed before the truce can hold a safe answer-back.
- `TugOfWarTruceWindowMinutes`: how long real external thermostat changes count for flip detection.
- `TugOfWarTruceHoldMinutes`: how long safe answer-back commands wait after the flip trigger.
- `TugOfWarTruceSafetyBandCelsius`: extra room warmth allowed before Tug-of-War Truce stops waiting.
- `ComfortEnvelopeEnabled`: lets small safe wall setpoint differences rest briefly after repeated touches.
- `ComfortEnvelopeTriggerTouches`: recent wall touches needed before Comfort Envelope can hold.
- `ComfortEnvelopeHoldMinutes`: how long the safe accepted range can rest.
- `ComfortEnvelopeMaxOffsetCelsius`: maximum setpoint difference allowed inside the accepted range.
- `ComfortEnvelopeSafetyBandCelsius`: extra room warmth allowed before Comfort Envelope stops waiting.
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
- `CoolerIntentFastLaneEnabled`: lets repeated cooler wall touches skip quiet waits while the room is above target.
- `CoolerIntentMinimumTouches`: cooler wall choices needed before fast lane is trusted.
- `CoolerIntentWindowMinutes`: how long cooler wall choices remain part of the pattern.
- `CoolerIntentHoldMinutes`: how long fast lane can keep quiet waits out of the way.
- `CoolerIntentNetCoolThresholdCelsius`: net cooler movement needed before fast lane starts.
- `CoolerIntentSafetyBandCelsius`: extra room warmth allowed before fast lane lets normal safety rules lead.
- `RemoteSettlingGuardEnabled`: waits after repeated Home Assistant user/phone or automation changes before a safe correction answers back.
- `RemoteSettlingTriggerChanges`: remote-style changes needed before Remote Settling can hold.
- `RemoteSettlingWindowMinutes`: how long Home Assistant-side changes remain useful to the pattern.
- `RemoteSettlingHoldMinutes`: quiet safe hold after the remote pattern is detected.
- `RemoteSettlingSafetyBandCelsius`: extra room warmth allowed before Remote Settling stops waiting.
- `SetpointEchoGuardEnabled`: waits for Home Assistant to report the last helper setpoint before another safe command.
- `SetpointEchoGraceSeconds`: maximum safe wait for that Home Assistant setpoint echo.
- `SetpointEchoSafetyBandCelsius`: extra room warmth allowed before Setpoint Echo stops waiting.
- `RepeatCommandGuardEnabled`: waits before sending the same setpoint number again while the room is safe.
- `RepeatCommandMinimumWaitSeconds`: smallest wait before an identical follow-up command.
- `RepeatCommandPressureExtraSeconds`: extra wait added as recent wall touches and helper commands rise.
- `RepeatCommandSafetyBandCelsius`: extra room warmth allowed before Repeat Quiet stops waiting.
- `SetpointStillnessGuardEnabled`: waits until repeated real readings show the wall setpoint has stopped changing.
- `SetpointStillnessTriggerTouches`: recent external thermostat touches needed before Stillness can hold.
- `SetpointStillnessRequiredSamples`: matching Home Assistant setpoint readings needed before a safe correction continues.
- `SetpointStillnessMaxHoldSeconds`: longest safe wait for the wall setpoint to settle.
- `SetpointStillnessToleranceCelsius`: allowed difference between readings that still counts as the same wall setpoint.
- `SetpointStillnessSafetyBandCelsius`: extra room warmth allowed before Setpoint Stillness stops waiting.
- `SensorRhythmGuardEnabled`: waits for the learned Home Assistant sensor beat before safe nudges.
- `SensorRhythmMinimumSamples`: real Home Assistant readings needed before the beat is trusted.
- `SensorRhythmWindowMinutes`: how long real reading timestamps remain useful.
- `SensorRhythmJitterSeconds`: small extra wait after the learned beat.
- `SensorRhythmSafetyBandCelsius`: extra room warmth allowed before Sensor Rhythm stops waiting.
- `HvacActionAlibiEnabled`: waits for a real Home Assistant `hvac_action` transition before safe corrections.
- `HvacActionAlibiTriggerTouches`: recent external thermostat touches needed before HVAC Alibi can wait.
- `HvacActionAlibiTransitionWindowSeconds`: how recently a real action transition can clear the alibi wait.
- `HvacActionAlibiMaxHoldMinutes`: longest safe wait for a real action transition.
- `HvacActionAlibiSafetyBandCelsius`: extra room warmth allowed before HVAC Alibi stops waiting.
- `TelemetryAlibiEnabled`: waits for normal house telemetry before a safe correction after repeated touches.
- `TelemetryAlibiTriggerTouches`: recent external thermostat touches needed before Telemetry Alibi can wait.
- `TelemetryAlibiMinimumHoldSeconds`: short quiet hold before telemetry updates can release the wait.
- `TelemetryAlibiMaxHoldMinutes`: longest safe wait for a real telemetry signal.
- `TelemetryAlibiSafetyBandCelsius`: extra room warmth allowed before Telemetry Alibi stops waiting.
- `TelemetryAlibiUseWeather`, `TelemetryAlibiUseSensorBeat`, `TelemetryAlibiUsePeakPower`: which real signals count as cover.
- `CoolingRunwayGuardEnabled`: waits after Home Assistant reports a fresh cooling start before another safe nudge.
- `CoolingRunwayMinimumSeconds`: smallest wait after cooling starts.
- `CoolingRunwayPressureExtraSeconds`: extra wait added as recent wall touches and helper commands rise.
- `CoolingRunwaySafetyBandCelsius`: extra room warmth allowed before Cooling Runway stops waiting.
- `RoomTrendGuardEnabled`: lets the defender observe real room temperature trend before nudging.
- `RoomTrendWindowMinutes`: how far back real room-temperature samples are compared.
- `RoomTrendStableToleranceCelsius`: small temperature changes counted as stable.
- `RoomTrendHoldMinutes`: how long to keep observing when room trend is stable or cooling.
- `ThermalMomentumGuardEnabled`: lets the defender wait when the room is cooling toward target quickly enough.
- `ThermalMomentumMinimumCoolingRateCelsiusPerHour`: minimum real cooling speed before momentum can hold.
- `ThermalMomentumLookAheadMinutes`: only hold when target is estimated within this many minutes.
- `ThermalMomentumHoldMinutes`: how long to let cooling continue before checking again.
- `WeatherDriftGuardEnabled`: lets safe corrections wait for a real outdoor-weather timing slot.
- `WeatherDriftWindowMinutes`: how far back real outdoor temperature samples are compared.
- `WeatherDriftMinimumChangeCelsius`: outdoor warming needed before a safe correction can continue as weather-driven.
- `WeatherDriftHoldMinutes`: how long to wait for the weather slot while room comfort is still safe.
- `WeatherDriftSafetyBandCelsius`: extra room warmth allowed before Weather Drift stops waiting.
- `PeakPowerSaverEnabled`: makes safe cooling more relaxed when Alectra Hui reports peak/high-cost/high-load usage.
- `PeakPowerSaverOnPeakEnabled`: treats Alectra Hui `On-peak` TOU period as a saver trigger.
- `PeakPowerSaverHighPowerEnabled`: treats current power above the configured kW threshold as a saver trigger.
- `PeakPowerSaverPowerThresholdKilowatts`: current power threshold for high-load saving.
- `PeakPowerSaverPriceThresholdCentsPerKwh`: current price threshold for high-price saving.
- `PeakPowerSaverHoldMinutes`: how long to keep the saver window after the latest peak signal.
- `PeakPowerSaverRefreshSeconds`: how often the worker refreshes Alectra Hui usage sensors.
- `PeakPowerSaverSafetyBandCelsius`: extra room warmth allowed before comfort overrides peak saving.
- `PeakPowerSaverFanSaverEnabled`: sets the configured fan saver mode during peak saving when the room is still safe.
- `PeakPowerSaverFanMode`: fan mode used during peak saving, usually `auto`.
- `FrontDoorKillSwitchEnabled`: enables the real front-door person detector kill switch.
- `FrontDoorPersonEntityIds`: comma-separated Home Assistant front-door person detector entities. Leave blank to auto-discover likely front-door/porch/entry person sensors.
- `FrontDoorKillSwitchHoldMinutes`: how long the front-door guard window stays active after a person detection.
- `FrontDoorKillSwitchRefreshSeconds`: how often the worker polls the front-door detector entities.
- `FrontDoorKillSwitchTurnsThermostatOff`: sends `climate.set_hvac_mode` `off` when the front-door guard fires.
- `SuperDefenderModeEnabled`: watches repeated Home Assistant user/phone or automation changes and can arm strict timing.
- `SuperDefenderRemoteChangeThreshold`: remote-style changes needed before Super Defender arms.
- `SuperDefenderWindowMinutes`: time window used to count remote-style changes.
- `SuperDefenderHoldMinutes`: how long strict response stays armed after the threshold is crossed.
- `SuperDefenderSafetyBandCelsius`: extra room warmth allowed before Super Defender leaves normal quiet timing in place.
- `SuperDefenderBypassQuietTiming`: lets armed Super Defender bypass quiet waits while cooling is still needed.

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

Command Camouflage spaces safe follow-up corrections after the last helper setpoint command. It waits for the configured minimum gap plus extra pressure seconds from recent wall touches or helper commands, so the next safe correction does not appear instantly after the previous one. If the room gets too warm, upstairs comfort needs direct cooling, or a quiet-timing bypass is active, camouflage clears immediately.

Stealth Governor is the broad low-profile layer. It scores recent wall touches, noticed corrections, Home Assistant remote changes, and helper command frequency into a 0-100 pressure score. If the score crosses the trigger and the room is still inside the safety band, it holds only safe corrections for a min-to-max window scaled by the score. If the room gets too warm, upstairs comfort needs direct cooling, or a quiet-timing bypass is active, it clears immediately.

Natural Cadence picks a variable future slot for safe corrections after repeated wall touches. The slot gets later as touch pressure or recent command pressure rises, and it has a small jitter so safe nudges do not land at identical times. If the room gets too warm or upstairs comfort needs direct cooling, cadence clears and the real correction path continues.

Comfort Pace is the high-frequency wall-change planner. When enough wall touches happen and the room is still inside its safety band, it chooses a calmer slot from touch pressure, recent command pressure, real outdoor weather movement, the learned Home Assistant sensor beat, and 5/10-minute local clock boundaries. It waits only for safe corrections; if the room gets too warm or direct cooling is needed, it clears immediately and the real correction path continues.

Tug-of-War Truce watches the same real external touch audit log, but it looks specifically for alternating direction flips. If the wall setpoint bounces up/down enough times inside the configured window, the defender assumes somebody may be watching and holds only safe answer-back corrections for the truce window. If the room gets too warm, upstairs heat needs direct cooling, or a strict quiet bypass is active, it clears immediately.

Comfort Envelope accepts tiny safe wall preferences for a short time. When repeated wall touches keep the thermostat inside the configured setpoint range and the real room is still under the safety band, the defender observes instead of correcting that small difference immediately. The dashboard shows the accepted range, preferred wall setpoint, remaining hold, and status. If the room gets too warm, the setpoint moves outside the range, or direct cooling is needed, the envelope clears and the real correction path continues.

Comfort Compromise is a temporary effective target. If wall changes repeat and the room is still inside the compromise safe band, the latest wall setpoint can influence the defender target up to the configured maximum offset. After the hold time, that influence fades back to the website target over the decay window. If the room gets too warm, the compromise clears immediately and normal warm-room defense resumes.

Comfort Memory is slower than Comfort Compromise. It learns a small offset for the current hour after repeated safe wall choices, then applies that offset on later checks in the same time window. Learned memory expires after the configured retention hours and is skipped when the room is warm, the safety override is crossed, or upstairs is already hot and the memory would relax cooling.

Human Nudge is the last safe command shaper before duplicate-command protection. If repeated wall touches are present and the room is still inside the safe band, it turns an odd-looking safe candidate such as `22.8 C` into a normal one-step thermostat move such as `23.5 C`. Direct warm-room cooling, upstairs heat, and quiet-timing bypasses skip it, so the current-room-minus-1 C cooling rule still wins when comfort needs help.

Manual Comfort Grace is different from cooldown. Cooldown waits after a manual touch. Manual Comfort Grace can keep waiting after cooldown if the room is still within the comfort band. If the room rises above the band, the HVAC mode changes away from `cool`, or upstairs becomes severely hot, grace ends and the real thermostat correction path resumes.

Touch Intent watches recent real wall changes and classifies the pattern as warmer, cooler, mixed, or learning. If the pattern is clearly warmer and the real room is still inside the intent safe band, it can extend Manual Comfort Grace by the configured extra minutes. If the room gets too warm or upstairs heat needs direct cooling, Touch Intent steps aside immediately.

Cooler Intent Fast Lane uses the real wall-touch audit log too. If repeated touches clearly move the wall thermostat cooler and the room is still above the website target, it clears safe quiet waits such as cooldown, Manual Comfort Grace, Conflict Quiet, cadence, repeat quiet, sensor rhythm, and cooling runway for a short configured window. It does not lower the website target; the normal warm-room command still starts at current room temperature minus 1 C and walks only toward the website target.

Setpoint Echo reuses the real pending setpoint that the defender already tracks for command attribution. After a defender setpoint command, it can wait for Home Assistant to report that setpoint back before sending another safe command. If the room gets too warm or upstairs heat needs direct cooling, Setpoint Echo steps aside.

Setpoint Stillness watches real Home Assistant climate readings after repeated external thermostat touches. If the room is still inside the safe band, it waits until the same wall setpoint appears for the configured number of readings before a safe correction answers back. This lets phone, Home Assistant, or wall-control tapping settle first. The max hold prevents it from waiting forever, and direct comfort needs clear it immediately.

Repeat Quiet watches the actual setpoint that is about to be sent. If the next safe command would repeat the same number as the last defender command, it waits longer based on recent wall-touch pressure and recent helper command pressure. Different one-degree step-down commands are allowed through, and if the room gets too warm, Repeat Quiet steps aside.

Sensor Rhythm watches real Home Assistant reading timestamps and learns the normal interval between poll updates. When a correction is safe, it can wait until just after the learned sensor beat plus a small wiggle, making the next command look less mechanically immediate. If the room gets too warm or upstairs heat needs direct cooling, Sensor Rhythm clears and the real correction path continues.

HVAC Alibi watches real Home Assistant `hvac_action` transitions. After repeated wall touches, if the room is still inside the safe band, it can hold a safe correction until `hvac_action` changes, such as idle to cooling or cooling to idle. A recent real transition also clears the hold, and direct comfort needs bypass it immediately.

Telemetry Alibi waits for a normal house signal before letting a safe correction through. After repeated wall touches, while the room is still inside the safe band, it starts a short quiet hold and then waits for the next enabled telemetry signal: Home Assistant reading beat, weather update, or Alectra Hui usage update. If the room gets too warm, direct comfort needs bypass it immediately.

Cooling Runway watches the real Home Assistant `hvac_action`. When it changes into cooling, the defender can wait before another safe nudge so it looks like the AC is being given time to work. The wait grows with recent wall-touch and helper-command pressure. If cooling stops or the room gets too warm, Cooling Runway clears immediately.

Room Trend Guard uses real Home Assistant room-temperature readings. It compares the oldest and newest room samples inside the configured trend window. If the room is stable or cooling after a wall change, it can keep observing before sending a nudge. If the room is warming, above the grace band, or beyond the safety override, it lets the real correction continue.

Thermal Momentum also uses real Home Assistant room-temperature readings. After a recent wall touch, it estimates cooling speed and minutes to target. If the room is already cooling fast enough and the target looks close, it holds briefly so the room can keep cooling without another obvious thermostat command.

Weather Drift Timing uses real Home Assistant outdoor temperature readings. After a recent wall touch, if the room is still inside the weather safe band and outdoor temperature is stable or cooling, it can hold a safe correction for a short configured window. If the outdoor temperature has genuinely warmed enough, the hold clears so the next correction can line up with real weather movement. If the room gets too warm, it clears immediately.

Alectra Peak Power Saver uses real Alectra Hui Home Assistant usage sensors. It watches `sensor.alectra_hui_current_tou_period`, `sensor.alectra_hui_current_price`, `sensor.alectra_hui_current_power`, and the current plan entity when present. If Alectra reports On-peak, price above the configured threshold, or power above the configured kW threshold, it holds only safe cooling commands that would demand more cooling. If the room or upstairs gets too hot, or the command would save energy by raising the setpoint, it steps aside. It can also set the fan to the configured saver mode while the room remains inside the safe band.

The Energy page organizes Alectra Hui data into four tabs:

- **Overview**: a quick dashboard for power, energy, cost, bill, TOU, price, plan, and peak-saver state.
- **Alectra Hui**: search box, desk filter, grouped entity cards, and helper text for each search/filter action.
- **Charts**: Home Assistant recorder line chart for the configured 24-hour energy entity plus entity-count bar charts.
- **Entity Table**: the filtered Alectra Hui entities in a compact table that stacks cleanly on mobile.

Front-door Guard Post uses real Home Assistant front-door person detector entities. Configure exact entity IDs such as `binary_sensor.front_door_person`, or leave the setting blank so the worker auto-discovers likely front-door, porch, entry, or entrance person sensors. When any detector reports a person, the defender pauses immediately and, if enabled, sends `climate.set_hvac_mode` with `off`. The command is tagged as `front-door-kill-switch`, so later Home Assistant echoes do not look like wall-control touches.

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

## Rival Schedule Watch (the AC app's own temperature schedule)

The AC vendor app has its own "Temperature schedules" tab (per weekday) that pushes the wall setpoint
on a timer. The reference schedule (screenshot: Friday):

| Block | Starts | Setpoints (low/high) |
| --- | --- | --- |
| SLEEP | 12:00 a.m. | 21.5 / 23.0 |
| DEEP SLEEP | 2:00 a.m. | 23.5 / 26.0 |
| GOOD MORNING | 9:00 a.m. | 22.5 / 24.0 |

The plan behind it, in the schedule owner's own words: *"I only turn on my AC when I sleep and set a
timer for 2 hours. Assuming most of us sleep at 12am, we shouldn't notice AC being set to 25 — I'll
move to 25."* — i.e. the AC starts at sleep time and the DEEP SLEEP block quietly drifts the room
toward ~25–26 °C while everyone is asleep.

**AC Defender does not follow this schedule.** It is the *other side's* plan, kept in configuration so
the defender can recognize and answer it while still respecting **my temp** (the website target):

- A setpoint change that is **not** from a Home Assistant user and lands on the active block's
  low/high number (within `RivalScheduleSetpointToleranceCelsius`) is attributed to
  `rival-schedule` in the audit log instead of a human wall touch.
- A schedule push starts **no human quiet bookkeeping**: no cooldown, no Manual Comfort Grace, no
  touch counters, no rage detector, no peace offering gift, and it teaches **nothing** to comfort
  memory/compromise/intent — otherwise the nightly 26 °C push would train the defender to prefer the
  rival's warm blocks.
- While the wall sits at a scheduled setpoint **above my temp** and the room is warm, quiet waits are
  bypassed so the walk-back to my temp happens promptly — a schedule is a machine running while the
  household sleeps, so nobody is watching the correction. The website target is never changed, and
  above `RivalScheduleSafetyBandCelsius` the normal hot-room comfort paths lead instead.
- Block boundaries are announced as events ("Rival schedule block 'DEEP SLEEP' started (23.5/26.0 C).
  My temp 22.0 C stays the goal."), and the Defense page has a **Rival Schedule Watch** card with the
  active block, next boundary, and attributed pushes.
- A real person on a phone or at the wall still gets the full human/stealth treatment, even during a
  rival block — only changes matching the block's exact numbers without a Home Assistant `user_id`
  are treated as the schedule.

Times and setpoints are configuration, not code (`Defender` section, `appsettings.json` /
environment). Blocks run from their start time until the next applicable block starts, wrapping past
midnight, per weekday like the vendor app:

```jsonc
"RivalScheduleWatchEnabled": true,
"RivalScheduleSetpointToleranceCelsius": 0.3,
"RivalScheduleBypassQuietTiming": true,
"RivalScheduleSafetyBandCelsius": 3.0,
"RivalScheduleBlocks": [
  { "Name": "SLEEP",        "Start": "00:00", "LowSetPointCelsius": 21.5, "HighSetPointCelsius": 23.0, "Days": "Mon,Tue,Wed,Thu,Fri,Sat,Sun" },
  { "Name": "DEEP SLEEP",   "Start": "02:00", "LowSetPointCelsius": 23.5, "HighSetPointCelsius": 26.0, "Days": "Mon,Tue,Wed,Thu,Fri,Sat,Sun" },
  { "Name": "GOOD MORNING", "Start": "09:00", "LowSetPointCelsius": 22.5, "HighSetPointCelsius": 24.0, "Days": "Mon,Tue,Wed,Thu,Fri,Sat,Sun" }
],
"RivalFanScheduleBlocks": []
```

The vendor app also has a "Fan schedules" tab; `RivalFanScheduleBlocks` is reserved for it
(`Name` / `Start` / `FanMode` / `Days`) but is not enforced yet.

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

## Electricity Cost (Alectra TOU)

AC Defender tracks the cost of the electricity it (and the rest of the house) uses. Every worker
cycle it reads the configured Alectra power sensor (`HomeAssistant:UsagePowerEntityId`, normalized to
kW), integrates it over time into energy (kWh), prices each interval at the current Alectra
time-of-use rate, and accumulates three running totals — **TOTAL** (all-time since tracking began),
**THIS MONTH** (resets on the 1st), and **TODAY** (resets at local midnight). Times use the
container's local timezone (`America/Toronto`). Gaps/downtime are capped at two minutes per interval
so a missed poll is never billed as hours of runtime. The counters survive restarts in
`defender-state.json` and reset the "last sample" on load so downtime is not charged.

The rate engine lives in [`Services/AlectraTouRates.cs`](Services/AlectraTouRates.cs) — a pure,
tested module with the summer/winter schedules and the Ontario statutory-holiday calendar.

### Estimated AC-only cost (no sensor needed)

The whole-house tracker above needs the Alectra Hui power sensor; when that integration is down (or
was never installed) the Dashboard still shows an **estimated AC-only cost** under the AC RUNTIME
hours (TODAY / THIS MONTH / LIFETIME). Every second of real compressor runtime
(`hvac_action = cooling`) is priced as a fixed assumed load — amps × volts, default **30 A × 240 V =
7.2 kW** — at the Alectra TOU rate in force at that moment, and the one-time runtime backfill from
past recorder logs prices the logged history the same way (each historical interval at its own
historical TOU period). It is an estimate of the energy commodity portion: a 30 A breaker rating is a
ceiling, not a measured draw, so tune the amps to your unit's real running load for a tighter number.

```jsonc
"AcCostEstimateEnabled": true,
"AcEstimatedAmps": 30.0,
"AcEstimatedVolts": 240.0
```

### AC usage calendar (Energy page → Calendar tab)

An airline fare-style calendar for the AC: every day cell shows that day's real compressor hours and
the estimated AC-only cost, colour-heated like a flight price calendar (cheap days green, expensive
days toward red, relative to the month's most expensive day). Use the arrows to move between months,
click any day for a detail line (hours, dollars, ≈ $/h at that day's TOU mix), or pick any start/end
day in the range picker for exact range totals — a single day, a week, or whole months. The data
comes from a persistent per-day ledger (`acDailyLedger` in the state file): fed live every cycle,
seeded once from the recorder-history backfill so the logged past is split into calendar days too,
and pruned to roughly the last 13 months.

### Sensors (on the Energy page snapshot)

Commodity line (energy portion only):

- `cost_total`, `cost_this_month`, `cost_today` — CAD dollars of the TOU energy commodity.
- `energy_total/month/today` — kWh behind each bucket.
- `current_tou_period` — Off-Peak / Mid-Peak / On-Peak.
- `current_rate` — ¢/kWh for the current period.

All-in "out of pocket" line (what the bill actually costs):

- `all_in_total`, `all_in_this_month`, `all_in_today` — CAD dollars including delivery, regulatory,
  the Ontario Electricity Rebate, and HST.

Budget-preferring control:

- `budget`, `budget_month_to_date`, `budget_pro_rated_target`, `budget_projected_month_end`,
  `budget_over_under`, `budget_setpoint_offset`, and a human status string.

### TOU schedule and rates

Commodity rates (¢/kWh, energy portion only — verified from alectrautilities.com):

| Period | Rate |
| --- | --- |
| On-Peak | 20.3 |
| Mid-Peak | 15.7 |
| Off-Peak | 9.8 |

Weekday schedule (Ontario, set by the OEB):

- **Summer (May 1 – Oct 31):** 07:00–11:00 Mid, 11:00–17:00 On, 17:00–19:00 Mid, 19:00–07:00 Off.
- **Winter (Nov 1 – Apr 30):** 07:00–11:00 On, 11:00–17:00 Mid, 17:00–19:00 On, 19:00–07:00 Off.
- **Weekends and Ontario statutory holidays:** Off-Peak all day, year-round. Holidays: New Year's,
  Family Day, Good Friday, Victoria Day, Canada Day, Civic Holiday, Labour Day, Thanksgiving,
  Christmas, Boxing Day — with the "holiday on a weekend rolls to the next weekday" observance rule.

**Most-expensive fallback:** the period is derived from local time and is essentially always
determinable, but if the timestamp is missing/ambiguous the tracker falls back to **On-Peak** (the
most expensive rate) so cost is never under-estimated. The fallback is explicit
(`UsingMostExpensiveFallback`).

### All-in formula (Ontario/Alectra bill order)

```
all_in = (commodity + delivery_fixed + delivery_variable + regulatory) × (1 − OER) × (1 + HST)
```

The **Ontario Electricity Rebate (OER)** is a percentage credit on the pre-tax subtotal applied
**before** HST (default 23.5%, effective Nov 1 2025); **HST** (default 13%) applies to the post-OER
subtotal. The fixed monthly delivery/service charge is accrued smoothly per-second across the month
so it splits cleanly across the total/today/this-month buckets.

> **Commodity, OER, and HST are standard province-wide.** The **delivery** and **regulatory** numbers
> vary per customer and rate class — the defaults are only reasonable Ontario placeholders. Copy the
> exact `Delivery` and `Regulatory` values from your own Alectra bill for a precise out-of-pocket
> figure.

### Budget-preferring control

Set a monthly budget and AC Defender will *prefer* staying inside it. **Turn it on and tune it in the
UI: Settings → Electricity budget** (switch, monthly dollars, aggressiveness, max warmer offset,
safety max, and the pacing basis). The appsettings values below only seed these settings once on
first start; after that the UI owns them. Each cycle the defender compares month-to-date spend
against a pro-rated target (`budget × fraction-of-month-elapsed`). When running **ahead** of that
pace it raises the effective cooling target by a bounded amount — biased toward the expensive
on/mid-peak periods (so it prefers running when power is cheap) — which widens the deadband and
reduces runtime to spend less. When **under** pace it relaxes back to normal comfort.

**Pacing basis (reliability):** the budget can measure spend two ways —

- `all-in` — the whole-house out-of-pocket line from the live Alectra power sensor.
- `ac-estimate` — the sensor-free AC-only estimate (assumed amps×volts load × **static Alectra TOU
  prices**), so budgeting works even when the Alectra integration is down.

Choosing `all-in` is still safe: if no fresh Alectra power sample arrives for 15 minutes, the budget
**automatically falls back** to the `ac-estimate` basis (shown as "ac-estimate (sensor stale)" on the
Energy page's Monthly budget card) instead of silently pacing on a flatlined number.

It is a **preference, not a cutoff**: the raise is capped by
`ElectricityBudgetMaxSetpointOffsetCelsius`, and a **safety maximum room temperature**
(`ElectricityBudgetSafetyMaxCelsius`, default 26 °C) always overrides it — at or above that
temperature the budget offset is dropped entirely so dangerous heat is always cooled.

### Configuration

All keys live under the `Defender` section (`appsettings.json` / environment), so they can be updated
when the OEB changes rates — no code change. Cost tracking defaults to **commodity-only** as
requested; the all-in factors and the budget are opt-in/tunable.

```jsonc
"ElectricityCostTrackingEnabled": true,
"ElectricityOnPeakCentsPerKwh": 20.3,
"ElectricityMidPeakCentsPerKwh": 15.7,
"ElectricityOffPeakCentsPerKwh": 9.8,
"ElectricityAllInMultiplier": 1.0,        // simple all-in scaler on the commodity rate (optional)
"ElectricityAllInAdderCentsPerKwh": 0.0,

"ElectricityDeliveryFixedDollarsPerMonth": 30.0,   // copy from your Alectra bill
"ElectricityDeliveryVariableCentsPerKwh": 5.0,     // copy from your Alectra bill
"ElectricityRegulatoryCentsPerKwh": 0.7,           // copy from your Alectra bill
"ElectricityOntarioRebatePercent": 0.235,          // OER, applied before HST
"ElectricityHstPercent": 0.13,

"ElectricityBudgetEnabled": false,
"ElectricityMonthlyBudgetDollars": 150.0,
"ElectricityBudgetAggressiveness": 0.5,            // 0 = off, 1 = full bias up to the max offset
"ElectricityBudgetMaxSetpointOffsetCelsius": 1.5,
"ElectricityBudgetSafetyMaxCelsius": 26.0          // always cool at/above this room temperature
```

## Website

The front end is built with Blazor Server and MudBlazor. It uses a responsive navigation drawer with
routed pages instead of a single crowded page:

- **Dashboard** (`/`) — summary-first hero (target, live readings, connection, defender switch), priority alerts, a "defense at a glance" summary, and quick controls.
- **Defense** (`/defense`) — every timing/comfort/safety guard as a live card with "How this works" and "More extra-specific info" drawers. The extra drawer now shows a per-card decision brief, what the worker will do next, what triggered or did not trigger, what would trigger the card next, what evidence it is reading, what can overrule it, the command effect, the exact algorithm path, and live evidence metrics; searchable and filterable by category.
- **Comfort** (`/comfort`) — upstairs comfort guard, sensors, and presence.
- **Energy** (`/energy`) — tabbed Alectra Hui overview, search/filter entity groups, charts, table view, live usage sensors, and 24-hour history.
- **Logs** (`/logs`) — the wall-touch audit log and activity events with clickable JSON detail.
- **Controls** (`/controls`) — target, fan, refresh/force/emergency thermostat actions.
- **Settings** (`/settings`) — grouped expanders for every guard plus the schedule editor.
- **Guide** (`/guide`) — the in-app algorithm reference: the decision cycle and a section per algorithm.

All live pages share one per-second poll through a scoped `DefenderStateProvider`, so the header and
page bodies update from the same snapshot without refreshing. Light/dark theme is persisted. The guard
cards and the Guide are generated from a single descriptor table (`Guards/GuardCatalog.cs`), which is
also the source of truth for `docs/wiki/Defender-Logic.md`. Controls call the same services used by the
JSON API.

The API also exposes a Server-Sent Events stream for external clients:

```text
/api/status/stream
```

This stream emits the full defender snapshot every second.

## API Summary

```text
GET  /api/status
GET  /api/settings
GET  /api/usage/live
GET  /api/usage/alectra-hui
GET  /api/usage/history?hours=24
GET  /api/status/stream
POST /api/target/generate
POST /api/target
POST /api/defender
POST /api/settings
POST /api/thermostat/refresh
POST /api/thermostat/force-target
POST /api/thermostat/force-boost
POST /api/thermostat/fan
POST /api/thermostat/off
POST /api/emergency
```

## CLI

Usage commands talk directly to Home Assistant and exit without starting the Blazor site.

```powershell
dotnet run -- usage-live
dotnet run -- usage-live --json
dotnet run -- usage-history --hours 24
dotnet run -- usage-history --entity sensor.alectra_hui_energy_today --from 2026-06-05T00:00:00 --to 2026-06-05T23:59:59 --json
```

Each command reads `HomeAssistant__BaseUrl`, `HomeAssistant__AccessToken`, and the usage sensor settings from environment/config. You can override them with `--base-url`, `--token`, `--power`, `--energy`, `--hourly-cost`, `--cost`, and `--entity`.

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
