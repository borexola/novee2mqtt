# Home Assistant add-on

For Home Assistant OS and Supervised installs. If you run Home Assistant Core or
Container, use [Docker](DOCKER.md) instead — add-ons are not available there.

## Install

1. In Home Assistant, go to **Settings → Add-ons → Add-on Store**.
2. From the ⋮ menu, choose **Repositories**.
3. Add `https://github.com/borexola/Novee2Mqtt` and close the dialog.
4. Find **Govee to MQTT Bridge (.NET)** in the store and click **Install**.

The add-on declares `mqtt:need`, so Home Assistant will not let it start until
you have the [MQTT integration](https://www.home-assistant.io/integrations/mqtt/)
set up. Do that first if you have not already.

## Configure

Open the add-on's **Configuration** tab.

| Option | Notes |
|---|---|
| `govee_email`, `govee_password` | Your Govee account. Provides room names, live status updates and Tap-to-Run scenes. |
| `govee_api_key` | A [Govee Platform API key](https://developer.govee.com/reference/apply-you-govee-api-key). Needed for scenes, segments and sensors. |
| `temperature_scale` | `C` or `F`. |
| `mqtt_host`, `mqtt_port`, `mqtt_username`, `mqtt_password` | Only needed to override the broker Home Assistant already knows about. |
| `debug_level` | `trace`, `debug`, `info`, `warn` or `error`. |
| `no_multicast`, `broadcast_all`, `global_broadcast`, `scan` | LAN discovery tuning — see [CONFIG.md](CONFIG.md). |
| `http_port` | Port for the add-on's status page. Default 8056. |

Leaving everything blank still works: the add-on will find devices that have LAN
control enabled and publish them through the broker Home Assistant provides.

## Start it

Start the add-on and watch the **Log** tab. On first run it lists every device it
found and which APIs are available for each, including warnings for devices that
should support LAN control but did not answer.

Devices appear in Home Assistant automatically through MQTT discovery. Ones with
a room set in the Govee app are suggested into the matching area.

The **Open Web UI** button shows the bridge's own status page, which is useful
for confirming what state it is holding and where that state came from.

## Notes

* The add-on runs with host networking, which the Govee LAN protocol requires.
  Only one process on the host can bind UDP port 4002 — if you also run the
  `Govee LAN Control` integration, disable it.
* Cached device and scene metadata lives in the add-on's `/data` and survives
  restarts and updates.
* MQTT topics and entity unique ids match the `govee2mqtt` bridge, so migrating
  from it keeps your entities, their history and your automations. Stop the old
  bridge before starting this one — both would publish to the same topics.
