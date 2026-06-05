# Repository Instructions

This project controls a real Home Assistant climate entity. Do not add dummy thermostats, simulators, fake state, or fallback automation that pretends to control HVAC.

## Safety

- Never commit Home Assistant tokens, usernames, passwords, `.env`, `App_Data`, or deployment archives.
- Keep the Docker deployment on host port `8888` unless the user explicitly changes it.
- Treat `climate.dining_room` as a real device. Any command endpoint should act on Home Assistant or return a real error.
- Keep the background worker checking 24/7. Paused or weather-blocked defender states should still refresh Home Assistant state.
- Cool-mode restore delay must still restore HVAC mode to `cool`; delay is allowed only while room comfort remains inside the configured safety band.
- Natural Walkback may soften only safe-band recovery moves after repeated wall touches. Warm-room defender commands must still start one degree below current room temperature, not one degree below the wall setpoint, and continue toward the website target.
- Comfort Compromise may adjust only the temporary effective target while room comfort is inside the configured safety band. Website target changes, schedule target changes, and upstairs comfort changes must clear it.
- Comfort Memory may apply only small, expiring time-of-day offsets learned from real wall touches while room comfort is inside the configured safety band. It must skip warmer offsets when upstairs is hot.
- Comfort Sync / quiet recovery may delay or step real commands, but it must not create fake thermostat state or simulator-only behavior.
- Conflict Quiet may stand down after repeated wall touches only while real room temperature remains inside the configured safety band.
- Thermal momentum and room-trend decisions must use real Home Assistant room-temperature samples, not fake timing or simulated cooling.

## Validation

- Run `dotnet build` before pushing.
- Use the dashboard and settings page in a browser after frontend changes.
- Check mobile layout for horizontal overflow after CSS/layout changes.
- After each coherent change set, commit, push, rebuild, and redeploy the Docker Compose stack on the Linux host.

## Deployment

Remote host:

```text
docker@192.168.50.242
```

Project path:

```text
/home/docker/homeassistant-ac-defender
```

Deployment command:

```bash
cd /home/docker/homeassistant-ac-defender
docker compose up -d --build
```

The remote `.env` file is created directly on the host and must not be copied into Git.
