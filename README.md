# Home Assistant AC Defender

ASP.NET Core dashboard and hosted background worker for defending the dining room AC target.

## Persistence

Runtime state is saved to `/data/defender-state.json` in Docker. The compose file mounts that path to the `ac-defender-data` Docker volume, so the generated target, dummy thermostat state, defender toggle, and event history survive container restarts, Docker restarts, and host reboots.

Copy `.env.example` to `.env` for local Docker secrets. `.env` is ignored by Git.

```powershell
docker compose up -d --build
```

The compose file publishes the app on host port `8888`, so remote deployments are reachable at `http://<host>:8888`.
