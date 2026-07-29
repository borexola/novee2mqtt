# Novee2Mqtt

A bridge between [Govee](https://govee.com) devices and Home Assistant, over the
[Home Assistant MQTT integration](https://www.home-assistant.io/integrations/mqtt/).

It runs as a container, either standalone (Docker / Podman / Compose) or as a
Home Assistant add-on. Built on C# / .NET 10, with its own control panel and HTTP
API alongside the MQTT bridge.

Novee2Mqtt began as a port of the Rust [govee2mqtt](https://github.com/wez/govee2mqtt)
bridge by Wez Furlong, and keeps its MQTT topics and entity unique ids, so an
existing Home Assistant install migrates across without losing entities, history
or automations. See [Licence and credits](#licence-and-credits).

## Why this exists

The original bridge is written in Rust. That is a barrier if you do not work in
Rust: you cannot fix what breaks on your own hardware, and you cannot contribute
the fix back. Govee also keeps changing its APIs, so a bridge you cannot edit is
a bridge that eventually stops working for the devices you actually own.

This is the same job done in C#, so that anyone comfortable in .NET can read it,
debug it against their own lights and change it. That is what the MIT licence on
the original is for, and it is why this project is MIT too.

## Features

* **LAN-first.** Not every Govee device supports LAN control, but for those that
  do you get the lowest latency and control that keeps working when your internet
  connection is down.
* **Live status updates** through Govee's undocumented AWS IoT interface —
  changes usually show up within a couple of seconds.
* **Platform API support** for everything the LAN and IoT paths cannot do:
  scenes, DIY scenes, music modes, segment colours and sensor readings.
* **Per-device scenes and modes**, exposed as Home Assistant effects, selects,
  sliders and preset buttons depending on what the device supports.
* **A built-in control panel** at `http://<host>:8056/` — power, brightness,
  colour, white balance and scenes for every device, grouped by room and updated
  live. Self-contained, so it works on a network with no internet route.
* **Health, metrics and a change stream** for running this unattended:
  `/api/health`, Prometheus `/metrics`, and server-sent events on `/api/events`.

| Feature | Requires | Where it shows up |
|---|---|---|
| DIY scenes | API key | The light's effect list |
| Music modes | API key | The light's effect list, prefixed `Music:` |
| Tap-to-Run / One Click | Govee account | Home Assistant scenes, and the `Govee to MQTT` device |
| Live status updates | LAN and/or Govee account | Devices report changes within seconds |
| Segment colour | API key | `Segment 00X` light entities under the main device |
| Room assignment | Govee account | Suggested area on first discovery |

Where:

* **API key** means a [Govee Platform API key](https://developer.govee.com/reference/apply-you-govee-api-key).
* **Govee account** means your Govee email and password, used against Govee's
  *undocumented and unsupported* app API and AWS IoT service.
* **LAN** means you have enabled the [Govee LAN API](https://app-h5.govee.com/user-manual/wlan-guide)
  on the supported devices, and that the protocol works on your network.

None of these are strictly required — with no configuration at all the bridge
will still find and control devices that have LAN control enabled — but with none
of them there is also no broker to publish to.

## Getting started

* [Installing the Home Assistant add-on](docs/ADDON.md)
* [Running it in Docker](docs/DOCKER.md)
* [Configuration reference](docs/CONFIG.md)
* [LAN API requirements](docs/LAN.md)
* [FAQ](docs/FAQ.md)
* [Privacy](docs/PRIVACY.md)

## Quick start with Docker Compose

```bash
cp .env.example .env
```

Fill in at least `GOVEE_MQTT_HOST`, then:

```bash
docker compose up -d
```

Host networking is required — the Govee LAN protocol needs to receive replies on
UDP port 4002 and to send multicast discovery packets.

## Command line

The image's entrypoint is the `novee2mqtt` binary; `serve` is the default command.
The other subcommands are useful for setup and troubleshooting:

```bash
docker run --rm --network host --env-file .env ghcr.io/borexola/novee2mqtt:latest list
```

| Command | What it does |
|---|---|
| `serve` | Run the bridge |
| `list` | List devices from every configured source |
| `list-http` | List devices known to the Platform API |
| `lan-disco` | Probe the LAN and report which devices answer |
| `lan-control --ip <addr> …` | Drive one device directly over the LAN |
| `http-control --id <id> …` | Drive one device through the Platform API |
| `undoc show-one-click` | Show the Tap-to-Run shortcuts we can trigger |
| `health` | Probe a running instance; exits non-zero when unhealthy |

Run `novee2mqtt help` for the full list of options.

## HTTP API

Everything the control panel does is a plain HTTP call, so the same endpoints
work from a script, a wall panel or a Home Assistant `rest_command`.

| Endpoint | Purpose |
|---|---|
| `GET /api/devices` | Every device with its current state |
| `GET /api/device/{id}` | One device |
| `GET /api/device/{id}/power/{on\|off}` | Power |
| `GET /api/device/{id}/toggle` | Flip the current power state |
| `GET /api/device/{id}/brightness/{0-100}` | Brightness, as a percentage |
| `GET /api/device/{id}/color/{css-colour}` | Colour: hex, `rgb()` or a CSS name |
| `GET /api/device/{id}/colortemp/{kelvin}` | White balance |
| `GET /api/device/{id}/scenes` | Scenes the device supports |
| `GET /api/device/{id}/scene/{name}` | Activate a scene |
| `GET /api/rooms` | Rooms, with device and on counts |
| `GET /api/room/{room}/power/{on\|off}` | Power every device in a room |
| `GET /api/oneclicks` | Tap-to-Run shortcuts |
| `GET /api/oneclick/activate/{name}` | Run a Tap-to-Run shortcut |
| `POST /api/purge-caches` | Drop cached device data and re-register |
| `GET /api/health` | Aggregate status; `503` when degraded |
| `GET /api/events` | Server-sent events, pushed when state changes |
| `GET /metrics` | Prometheus exposition format |

The container declares a `HEALTHCHECK` that runs `novee2mqtt health` against
`/api/health`, so `docker ps` reports whether the bridge is actually working
rather than merely running.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet test
```

```bash
docker build -t novee2mqtt:local .
```

## Licence and credits

MIT. See [LICENSE.md](LICENSE.md).

This project is a port of [wez/govee2mqtt](https://github.com/wez/govee2mqtt) by
Wez Furlong, which is where the protocol work comes from: the LAN and AWS IoT
reverse engineering, the BLE packet encoding and the per-SKU device quirks table.
The AWS IoT support in that project was in turn made possible by
[@bwp91](https://github.com/bwp91)'s work in
[homebridge-govee](https://github.com/bwp91/homebridge-govee/). That upstream
copyright notice is retained in `LICENSE.md` as the MIT licence requires.

Written for this project: the control panel, the HTTP/REST API with its health,
metrics and event-stream endpoints, the .NET application and container
architecture, the Home Assistant add-on packaging, the documentation and the test
suite.
