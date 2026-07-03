# Repository Instructions

This project controls a real Home Assistant climate entity. Do not add dummy thermostats, simulators, fake state, or fallback automation that pretends to control HVAC.

## Safety

- Never commit Home Assistant tokens, usernames, passwords, `.env`, `App_Data`, or deployment archives.
- Keep the Docker deployment on host port `8888` unless the user explicitly changes it.
- Treat `climate.dining_room` as a real device. Any command endpoint should act on Home Assistant or return a real error.
- Keep the background worker checking 24/7. Paused or weather-blocked defender states should still refresh Home Assistant state.
- Cool-mode restore delay must still restore HVAC mode to `cool`; delay is allowed only while room comfort remains inside the configured safety band.
- Natural Walkback may soften only safe-band recovery moves after repeated wall touches. Warm-room defender commands must still start the configured `WarmRoomApproachCelsius` below current room temperature (default 0.5 °C, kept small so the cooling is less noticeable to others), not below the wall setpoint, and continue toward the website target.
- Touch Signature may shape only safe-band nudge size from real wall thermostat changes. It must clear immediately when room comfort needs direct cooling.
- Human Nudge may shape only safe, non-bypass setpoint commands into normal thermostat-looking steps. It must not change direct warm-room current-minus-approach corrections.
- Visibility Guard may hold only safe corrections after wall touches that happen soon after defender commands. It must clear immediately when room comfort needs direct cooling.
- Routine Timing may delay only safe corrections after repeated wall touches so they land on normal-looking comfort-check intervals. It must clear immediately when room comfort needs direct cooling.
- Comfort Budget may limit only repeated safe setpoint commands inside its window. It must clear immediately when room comfort needs direct cooling.
- Command Camouflage may delay only safe follow-up corrections after a recent helper setpoint command. It must clear immediately when room comfort needs direct cooling or a quiet-timing bypass is active.
- Stealth Governor may delay only safe corrections when the combined activity pressure score is high. It must clear immediately when room comfort needs direct cooling or a quiet-timing bypass is active.
- Natural Cadence may choose a variable future slot only for safe corrections after repeated wall touches. It must clear immediately when room comfort needs direct cooling.
- Comfort Compromise may adjust only the temporary effective target while room comfort is inside the configured safety band. Website target changes, schedule target changes, and upstairs comfort changes must clear it.
- Comfort Memory may apply only small, expiring time-of-day offsets learned from real wall touches while room comfort is inside the configured safety band. It must skip warmer offsets when upstairs is hot.
- Touch Intent may extend only safe wall-change grace after a clear warmer wall-touch pattern. It must clear or step aside immediately when room comfort needs direct cooling.
- Cooler Intent Fast Lane may bypass quiet waits only after repeated real cooler wall touches while the room is above target. It must not lower the website target or change the warm-room current-minus-approach command rule.
- Tug-of-War Truce may delay only safe answer-back corrections after real external setpoint direction flips. It must use the real thermostat audit log and clear immediately when room comfort needs direct cooling or a quiet-timing bypass is active.
- Super Defender may bypass quiet waits only after repeated Home Assistant user/phone or automation-sourced changes while the room still needs cooling. It must use real Home Assistant context metadata and must not send automated router, Wi-Fi, or firewall blocking commands.
- Remote Settling Guard may delay only safe corrections after repeated Home Assistant user/phone or automation-sourced changes. It must use real Home Assistant context metadata and clear immediately when room comfort needs direct cooling.
- Setpoint Echo may delay only safe follow-up setpoint commands while Home Assistant has not reported back the last defender setpoint. It must clear or step aside immediately when room comfort needs direct cooling.
- Setpoint Stillness may delay only safe corrections until repeated real Home Assistant climate readings show the wall setpoint has stopped changing. It must clear or step aside immediately when room comfort needs direct cooling.
- Repeat Quiet may delay only safe identical follow-up setpoint commands. It must not block a different one-degree step-down command, and it must clear or step aside immediately when room comfort needs direct cooling.
- Sensor Rhythm may delay only safe corrections so commands land near real Home Assistant reading cadence. It must clear immediately when room comfort needs direct cooling.
- HVAC Alibi may delay only safe corrections using real Home Assistant `hvac_action` transitions. It must clear on direct comfort needs and must never create fake thermostat action state.
- Telemetry Alibi may delay only safe corrections using real Home Assistant/weather/Alectra telemetry signals. It must clear on direct comfort needs and must never create fake telemetry or simulated cover events.
- Cooling Runway may delay only safe corrections after real Home Assistant `hvac_action` changes into cooling. It must clear immediately when cooling stops or room comfort needs direct cooling.
- Weather Drift Timing may delay only safe post-touch corrections using real Home Assistant outdoor weather samples. It must clear when outdoor warming provides a natural slot, the hold expires, or direct cooling is needed.
- Alectra Peak Power Saver may delay only safe cooling commands using real Alectra Hui Home Assistant usage sensors. It must step aside when room/upstairs comfort needs cooling or when a command would save energy by raising the setpoint.
- Front-door Guard Post must use real Home Assistant detector entities only. If it turns the thermostat off, tag the command source as the front-door kill switch so Home Assistant echoes are not logged as wall-control touches.
- Rival Schedule Watch describes the AC vendor app's own temperature schedule; the defender must never adopt its setpoints as the target or learn comfort preferences from its pushes. It may attribute only non-user-context setpoint changes matching the active block's numbers, and may bypass quiet waits only while the wall sits at a scheduled setpoint above the user's target, the room still needs cooling, and comfort stays inside the configured safety band. Changes with a Home Assistant user_id always keep the human treatment. The fan schedule blocks are configuration placeholders and must not be enforced until a real fan implementation exists.
- Comfort Sync / quiet recovery may delay or step real commands, but it must not create fake thermostat state or simulator-only behavior.
- Conflict Quiet may stand down after repeated wall touches only while real room temperature remains inside the configured safety band.
- Thermal momentum and room-trend decisions must use real Home Assistant room-temperature samples, not fake timing or simulated cooling.

## Validation

- Run `dotnet build` before pushing.
- Use the dashboard and settings page in a browser after frontend changes.
- Use the Defense page in a browser after guard-card changes; verify the hidden "More extra-specific info" drawer shows next step, trigger verdict, future trigger, overrule rules, algorithm path, and live evidence without clipping on desktop or mobile.
- Use the Energy page in a browser after Alectra Hui frontend changes; verify tabs, search/filter controls, charts, and mobile layout.
- Check mobile layout for horizontal overflow after CSS/layout changes.
- After each coherent change set, commit, push, rebuild, and redeploy the Docker Compose stack on the Linux host.

## Deployment

Remote host:

```text
docker@192.168.50.242
```

Project path:

```text
/home/docker/HomeAssistantAcDefender
```

Deployment command:

```bash
cd /home/docker/HomeAssistantAcDefender
docker compose build --no-cache
docker compose up -d
```

The remote `.env` file is created directly on the host and must not be copied into Git.
