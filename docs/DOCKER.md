# Running in Docker

## Host networking is required

The Govee LAN protocol sends discovery packets to a multicast group and expects
replies on UDP port 4002. Neither works from inside Docker's default bridge
network, so the container must use host networking.

That also means the web UI binds directly to the host's port 8056; there is no
port mapping to configure.

Only one process on the host can bind UDP 4002. If you also run the
`Govee LAN Control` Home Assistant integration or `homebridge-govee` with LAN
enabled, disable one of them — the bridge will fail to start with an explanatory
error otherwise.

## Docker Compose

```bash
cp .env.example .env
```

Fill in `.env` — at minimum `GOVEE_MQTT_HOST` — then:

```bash
docker compose up -d
```

```bash
docker compose logs -f
```

The bundled `docker-compose.yml` already sets `network_mode: host` and mounts a
named volume at `/data` for the cache.

## docker run

```bash
docker run -d --name novee2mqtt --restart unless-stopped --network host -v novee2mqtt-cache:/data --env-file .env ghcr.io/borexola/novee2mqtt:latest
```

## Verifying it works

Open `http://<host>:8056/` for the control panel, or:

```bash
curl -s http://localhost:8056/api/devices | jq
```

The image ships a `HEALTHCHECK`, so container health reflects whether the bridge
is genuinely working — a connected broker and at least one device found:

```bash
docker inspect --format '{{.State.Health.Status}}' novee2mqtt
```

```bash
curl -s http://localhost:8056/api/health | jq
```

For Prometheus, scrape `http://<host>:8056/metrics`; it exposes per-device power,
brightness, colour temperature and reading age, plus transport connectivity.

To check LAN discovery specifically, without starting the bridge:

```bash
docker run --rm --network host ghcr.io/borexola/novee2mqtt:latest lan-disco
```

## Building the image yourself

```bash
docker build -t novee2mqtt:local .
```

The build publishes a self-contained binary, so the runtime image needs no .NET
installed. It supports `linux/amd64`, `linux/arm64` and `linux/arm/v7`:

```bash
docker buildx build --platform linux/amd64,linux/arm64,linux/arm/v7 -t novee2mqtt:local .
```

## Persistent data

`/data` holds the SQLite cache of device lists, scene lists and login tokens.
Losing it is harmless — the bridge refetches everything — but keeping it across
restarts avoids hammering Govee's rate-limited API.
