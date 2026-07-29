# Configuration

Every setting can be given as a command-line flag or an environment variable.
The flag wins where both are present. The Home Assistant add-on sets the
environment variables for you from its options panel.

## Govee credentials

The bridge runs without any Govee credentials, but then it can only discover and
control devices for which you have already enabled LAN control.

Configure at least your Govee email and password before the first run: that is
the only way for the bridge to learn your room names, which it uses to pre-assign
lights to the right Home Assistant areas.

Scene control on devices that do not support the LAN API needs a Govee API key.
[Instructions for obtaining one are here](https://developer.govee.com/reference/apply-you-govee-api-key).

| CLI | Environment | Add-on option | Purpose |
|---|---|---|---|
| `--govee-email` | `GOVEE_EMAIL` | `govee_email` | The email address registered with your Govee account |
| `--govee-password` | `GOVEE_PASSWORD` | `govee_password` | The password for your Govee account |
| `--api-key` | `GOVEE_API_KEY` | `govee_api_key` | The API key you requested from Govee |

*Concerned about sharing your credentials? See [Privacy](PRIVACY.md).*

## MQTT

For devices to appear in Home Assistant you need an MQTT broker configured in
Home Assistant — [follow these steps](https://www.home-assistant.io/integrations/mqtt/#configuration) —
and the bridge pointed at the same broker.

| CLI | Environment | Add-on option | Purpose |
|---|---|---|---|
| `--mqtt-host` | `GOVEE_MQTT_HOST` | `mqtt_host` | Hostname or IP of your broker |
| `--mqtt-port` | `GOVEE_MQTT_PORT` | `mqtt_port` | Broker port, default `1883` |
| `--mqtt-username` | `GOVEE_MQTT_USER` | `mqtt_username` | Username, if the broker requires one |
| `--mqtt-password` | `GOVEE_MQTT_PASSWORD` | `mqtt_password` | Password, if the broker requires one |
| `--hass-discovery-prefix` | `GOVEE_HASS_DISCOVERY_PREFIX` | — | Discovery prefix, default `homeassistant` |

Inside the add-on, the broker configured in Home Assistant's MQTT service is used
automatically; the options above only need setting to override it.

## LAN control

A number of Govee devices support a local control protocol that works without
your internet connection. It is the lowest-latency path and the one the bridge
prefers.

The [Govee LAN API is described here](https://app-h5.govee.com/user-manual/wlan-guide),
including the list of supported devices.

*You must enable the LAN API for each individual device in the Govee Home app
before the bridge can control it that way.*

In theory the LAN API needs no configuration. In practice it relies on your
network passing multicast UDP, which is unreliable across some access points and
routers — hence the options below.

| CLI | Environment | Add-on option | Purpose |
|---|---|---|---|
| `--no-multicast` | `GOVEE_LAN_NO_MULTICAST=true` | `no_multicast` | Stop sending to the multicast group `239.255.255.250`. Not recommended. |
| `--broadcast-all` | `GOVEE_LAN_BROADCAST_ALL=true` | `broadcast_all` | Send a discovery packet to the broadcast address of every non-loopback interface. A good option when multicast is unreliable. |
| `--global-broadcast` | `GOVEE_LAN_BROADCAST_GLOBAL=true` | `global_broadcast` | Send to the global broadcast address `255.255.255.255`. |
| `--scan` | `GOVEE_LAN_SCAN=10.0.0.1,10.0.0.2` | `scan` | Probe these addresses directly. Each entry can be a device's IP (give it a static lease first) or a network broadcast address such as `10.0.0.255` for a subnet that is reachable but not directly attached. |
| `--disco-timeout` | `GOVEE_LAN_DISCO_TIMEOUT` | — | How long the one-shot `list` and `lan-disco` commands wait, in seconds. Default 3. |

See [LAN.md](LAN.md) for the network requirements in detail.

## Presentation and runtime

| CLI | Environment | Add-on option | Purpose |
|---|---|---|---|
| `--temperature-scale` | `GOVEE_TEMPERATURE_SCALE` | `temperature_scale` | `C` or `F`. Default `C`. |
| `--http-port` | `GOVEE_HTTP_PORT` | `http_port` | Port for the status page and REST API. Default `8056`. |
| `--amazon-root-ca` | — | — | PEM bundle used to validate Govee's AWS IoT endpoint. Defaults to the copy shipped with the binary. |
| — | `GOVEE_CACHE_DIR` | — | Where to keep the on-disk cache. Defaults to `/data` in the container. |
| — | `GOVEE_LOG` | `debug_level` | `trace`, `debug`, `info`, `warn` or `error`. Default `info`. |
| — | `GOVEE_LOG_SENSITIVE_DATA` | — | Set to `true` to stop redacting the AWS IoT topics in logs. Only for debugging. |

## Caching

Govee's Platform API is rate limited, so device lists, scene lists, login tokens
and the app scene catalog are cached on disk under `GOVEE_CACHE_DIR`. Each entry
has three expiry times: a soft TTL after which the bridge tries to refresh, a
hard TTL after which the entry is discarded, and a negative TTL controlling how
long a failure is remembered. Where it is safe to do so, a failed refresh keeps
serving the previous value, which is what lets the bridge ride out a Govee
outage.

Press **Purge Caches** on the `Govee to MQTT` device in Home Assistant, or
`POST /api/purge-caches`, to clear it and re-register everything.
