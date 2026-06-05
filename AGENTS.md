# Repository Instructions

This project controls a real Home Assistant climate entity. Do not add dummy thermostats, simulators, fake state, or fallback automation that pretends to control HVAC.

## Safety

- Never commit Home Assistant tokens, usernames, passwords, `.env`, `App_Data`, or deployment archives.
- Keep the Docker deployment on host port `8888` unless the user explicitly changes it.
- Treat `climate.dining_room` as a real device. Any command endpoint should act on Home Assistant or return a real error.
- Keep the background worker checking 24/7. Paused or weather-blocked defender states should still refresh Home Assistant state.
- Comfort Sync / quiet recovery may delay or step real commands, but it must not create fake thermostat state or simulator-only behavior.
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
