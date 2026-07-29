# Home Assistant Add-on: Govee to MQTT Bridge (.NET)

Brings your Govee devices into Home Assistant through the MQTT integration.

Prefers local LAN control where the device supports it, uses Govee's push
notification service for live status updates, and falls back to the Govee
Platform API for scenes, segments and sensors.

## Installation

1. **Settings → Add-ons → Add-on Store**.
2. ⋮ menu → **Repositories** → add `https://github.com/borexola/Novee2Mqtt`.
3. Install **Govee to MQTT Bridge (.NET)**.
4. Configure it, then start it.

The [MQTT integration](https://www.home-assistant.io/integrations/mqtt/) must be
set up first; the add-on will not start without a broker.

## Configuration

| Option | Purpose |
|---|---|
| `govee_email` / `govee_password` | Your Govee account. Enables room names, live status updates and Tap-to-Run scenes. |
| `govee_api_key` | Govee Platform API key. Enables scenes, music modes, segments and sensors. |
| `temperature_scale` | `C` or `F`. |
| `mqtt_host` / `mqtt_port` / `mqtt_username` / `mqtt_password` | Override the broker Home Assistant already provides. |
| `debug_level` | `trace`, `debug`, `info`, `warn`, `error`. |
| `no_multicast` / `broadcast_all` / `global_broadcast` / `scan` | LAN discovery tuning. |
| `http_port` | Status page port. Default 8056. |

Full documentation: <https://github.com/borexola/Novee2Mqtt/blob/main/docs/CONFIG.md>

## Notes

* The add-on uses host networking, which the Govee LAN protocol requires. Only
  one process per host can use that protocol — disable the `Govee LAN Control`
  integration if you have it.
* You must enable LAN control per device in the Govee Home app before this
  add-on can use it for that device.
