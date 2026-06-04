# Home Assistant AC Defender

ASP.NET Core dashboard and hosted background worker for defending the real dining room Home Assistant climate entity.

## Behavior

- The dashboard generates or accepts an exact target temperature.
- The background worker polls the configured Home Assistant climate entity.
- If the room is above target, it commands cooling below the target to force AC operation.
- If cooling is idle while the room is still above target, it lowers the setpoint by one degree per cycle, bounded by `Defender__MinimumCoolingSetPointCelsius`.
- Once the room reaches the generated target, it returns the thermostat setpoint to the exact target.
- If Home Assistant is unavailable, the app shows the real error and does not simulate a thermostat.

## Docker

Runtime state is saved to `/data/defender-state.json` in Docker. The compose file mounts that path to the `ac-defender-data` Docker volume so the generated target, defender toggle, and event history survive restarts.

Copy `.env.example` to `.env` for local Docker secrets. `.env` is ignored by Git.

```powershell
docker compose up -d --build
```

The compose file publishes the app on host port `8888`, so remote deployments are reachable at `http://<host>:8888`.
