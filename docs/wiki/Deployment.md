# Deployment

The app is designed to run in Docker Compose.

## Compose

```bash
docker compose up -d --build
```

The compose file publishes:

```text
8888:8080
```

The website is reachable at:

```text
http://<host>:8888
```

## Runtime State

Runtime state is persisted in the Docker volume `ac-defender-data` at:

```text
/data/defender-state.json
```

## Secrets

Home Assistant credentials and tokens belong in `.env` on the deployment host. They must not be committed to Git.
