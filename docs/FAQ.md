# FAQ

## Is my device supported?

If it appears in the Govee app, the bridge will most likely find it. What you can
*do* with it depends on which APIs reach it:

* **LAN API** — [supported models are listed here](https://app-h5.govee.com/user-manual/wlan-guide).
  Fastest, works offline, must be enabled per device in the Govee app.
* **Platform API** — needs an API key. Covers most WiFi devices, and is the only
  source for scenes, segments and sensor readings.
* **AWS IoT** — needs your Govee account. Gives push status updates for lights
  and a few other device types.

Bluetooth-only devices cannot be controlled: there is no BLE support. The bridge
recognises the common BLE-only models and skips advertising them rather than
creating entities that never work.

Run `govee list` to see what was found and from where.

## Nothing appears in Home Assistant

Work through these in order:

1. Is the MQTT integration configured in Home Assistant, and is the bridge
   pointed at the *same* broker? Check the log for `Connected to MQTT broker`.
2. Does the log show `Waiting … for Home Assistant to settle on N entity configs`
   with a non-zero N? If N is 0, no devices were discovered — see below.
3. Does `gv2mqtt/availability` carry `online`? If not, the bridge did not finish
   registering.

## The log says a device "should be available via the LAN API" but did not respond

The bridge knows the model supports LAN control but got no answer. Causes, in
rough order of likelihood:

1. LAN control has not been enabled for that device in the Govee app.
2. The device is offline.
3. Multicast or broadcast UDP is not getting between the bridge and the device —
   see [LAN.md](LAN.md).
4. The device needs a firmware update before LAN API can be enabled.
5. The hardware revision is too old to support it.

## It cannot bind UDP port 4002

Something else on the host already has it. The usual culprits are the
`Govee LAN Control` Home Assistant integration and `homebridge-govee` with LAN
control enabled. Only one process can use the Govee LAN protocol on a host at a
time; disable one of them.

## No scenes in the effect list

Scenes come from the Platform API, so an API key is required for most devices.
Without one, the bridge falls back to the app's scene catalog, which it can only
apply to devices reachable over the LAN.

If you have a key and scenes are still missing, press **Purge Caches** on the
`Govee to MQTT` device — scene lists are cached for a week.

## A device shows the wrong state, or none at all

Open the device's **Status** diagnostic sensor in Home Assistant. Its attributes
show what each API reported and when, plus which one the bridge is currently
believing. `Missing` means nothing has been heard for longer than a poll interval.

The **Request Platform API State** button forces a fresh read for that device.

## Why is the state sometimes stale?

Each source is trusted only as far as it goes:

* Devices reachable over LAN or AWS IoT report changes themselves, usually within
  a couple of seconds.
* Everything else is polled, by default every 15 minutes, because the Platform
  API is rate limited. A kettle that is switched on is polled every minute
  instead, since its temperature is actually changing.
* After the bridge sends a command to a device it can only reach through the
  Platform API, it waits a few seconds before re-reading, because Govee's state
  endpoint is not immediately consistent with the command.

## Changes made in the Govee app do not show up

Only for devices without LAN or IoT coverage. Those are polled, so it can take up
to a poll interval. Press **Request Platform API State** if you need it sooner.

## Turning off a light with segments turns it back on

That was the behaviour when segment brightness was set to zero, which powers the
whole device on. The bridge deliberately does nothing when Home Assistant sends
`OFF` to a segment; use the parent light entity to turn the device off.

## Why does a device show no scenes?

Scenes come from the Govee app's scene catalog, which the bridge only fetches for
devices it could actually apply a scene to. A device with neither an API key nor
LAN reachability reports an empty effect list rather than one that cannot work.

## How do I move from the Rust govee2mqtt?

Stop the old bridge, then start this one against the same broker — both publish
to the same topics, so they must not run at the same time. MQTT topics and entity
unique ids are identical, so Home Assistant reuses the existing entities along
with their history and any automations referring to them. Configuration uses the
same `GOVEE_*` environment variable names.

Two differences worth knowing. `RUST_LOG` is no longer read — use `GOVEE_LOG`,
which the old bridge also accepted. And this bridge does not fetch the app scene
catalog for devices it could not apply a scene to anyway, so a device with
neither an API key nor LAN reachability reports an empty effect list instead of
an unusable one.

## How do I report a problem?

Run with `GOVEE_LOG=debug` (or `trace` for protocol detail) and include the
device's **Status** attributes. Check what you paste: at trace level the logs
contain device identifiers, and `GOVEE_LOG_SENSITIVE_DATA=true` additionally
un-redacts tokens.
