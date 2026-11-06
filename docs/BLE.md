# Bluetooth control

Some Govee lights have no LAN API, and a light on a network with no internet
route is invisible to Govee's cloud: both the Platform API and the AWS IoT
channel hand commands to Govee's servers, which can only deliver them to a
device that is holding its own connection to those servers. The cloud answers
`Device is offline. Please check the Wi-Fi connection` and nothing happens.

Every one of those lights still has a Bluetooth radio, which is how the Govee
app reaches it when the phone is in the same room. The bridge can do the same:
it connects to the light from the Bluetooth adapter on the host it runs on and
writes the same 20-byte frames the app does.

## What you need

* **A Bluetooth adapter on the host**, within range of the light. Home
  Assistant OS exposes the built-in or USB adapter of the machine it runs on.
  ESPHome Bluetooth proxies are not used: they are only reachable from Home
  Assistant's own Bluetooth stack, not from an add-on talking to BlueZ.
* **BlueZ reachable over the system D-Bus.** The add-on sets this up itself: it
  asks for `host_dbus`, and ships an AppArmor profile that allows BlueZ, because
  the default profile refuses D-Bus calls. In Docker, mounting the socket with
  `-v /run/dbus:/run/dbus:ro` is not enough on its own. `dbus-daemon` closes the
  connection for a user it cannot look up, and the image's own user does not
  exist on the host. On hosts that enforce AppArmor, such as Ubuntu, Docker's
  default profile also blocks D-Bus calls. The container therefore has to run as
  a user the host knows, under a profile that allows D-Bus.

With an adapter available, Bluetooth is on by default; set `bluetooth: false` (or `GOVEE_BLE=false`) to turn it off.

## How a light is matched

The bridge scans for 15 seconds at startup and attaches every light that has no
LAN path to a Bluetooth device, trying these in order:

1. An address you configured with `GOVEE_BLE_ADDRESSES`.
2. The Bluetooth address and name your Govee account reports, when an email and
   password are configured. This is the reliable source.
3. The address implied by the device id. The last six octets of an id such as
   `2C:67:5C:E7:53:BC:97:9C` are the light's WiFi MAC, and its Bluetooth address
   is normally that plus one, so `5C:E7:53:BC:97:9D`.
4. A device advertising the same model and id suffix.

Devices still unmatched are looked for again every few minutes, backing off to
hourly. The startup log shows the result:

```
  Bluetooth: address=5C:E7:53:BC:97:9D name=ihoment_H6008_979D colour_mode=0x0d brightness=percent
```

If a light is matched wrongly or not at all, map it explicitly:

```
GOVEE_BLE_ADDRESSES=2C:67:5C:E7:53:BC:97:9C=AA:BB:CC:DD:EE:FF
```

## When Bluetooth is used

The order of preference is unchanged for devices the cloud can reach: LAN
first, then AWS IoT, then the Platform API, with Bluetooth as a last resort if
a cloud command fails.

For a device the cloud reports offline, or that has no cloud path at all,
Bluetooth comes straight after LAN. Set `bluetooth_prefer`
(`GOVEE_BLE_PREFER=true`) to use it ahead of the cloud for every device.

Connections are opened on demand and closed after 30 seconds without a
command (`GOVEE_BLE_IDLE_SECONDS`). A Govee light accepts one Bluetooth
connection at a time, so holding it open would lock the Govee app out.

## State

Bluetooth gives few push updates. After a command the bridge records the state
it asked for, then asks the light to confirm its power, brightness and colour
on the same connection. Segmented lights do not report their colour this way,
so the bridge keeps showing the one it last set. Lights the cloud cannot reach are also re-read over
Bluetooth on the normal poll interval, so a change made from the Govee app
over Bluetooth is eventually picked up.

The control panel shows `BLE` as the source for state that came this way.

## What works over Bluetooth

Power, brightness, colour and colour temperature. Scenes, music modes and
per-segment colour still go through the cloud, so they fail for a light the
cloud cannot reach.

## Encrypted firmware

Govee runs two Bluetooth protocols. On connecting, the bridge reads the light's
encrypt version, falling back to the flag in its advertisement, and on firmware
that asks for it runs the handshake that encrypts every later frame. This is
automatic, and firmware updates can change the answer for the same model.
`ble-scan` shows what each light advertises in its `encrypted` column.

A newer scheme, encrypt version 2, is not supported, and the bridge says so
rather than sending frames the light would ignore.

## Frame layouts

Govee has changed the colour and brightness frames several times, so the bridge
keeps a table by model:

| Layout | Models | Colour frame | Evidence |
|---|---|---|---|
| `0d` | Bulbs (H6004, H6006, H6008, ...) | `33 05 0d R G B`, white `33 05 0d <tint> Kh Kl <tint>` | Captured from the Govee app and confirmed on hardware |
| `15` | TV backlights and segmented lights (H66A0, H66A1, ...) | `33 05 15 01 R G B ... mask` | Traced from the app; the default for unknown models |
| `02` | Older strips (H6127, H6159, ...) | `33 05 02 R G B`, brightness 20 to 254 | Community captures |

A light acknowledges a frame it then ignores, so a command can succeed while
nothing visibly changes. If colour or brightness does not respond on a model,
try the other layouts directly:

```bash
docker run --rm -v /run/dbus:/run/dbus:ro ghcr.io/borexola/novee2mqtt:latest ble-control --address C0:17:55:BF:B2:A5 --sku H66A1 --color-mode 0d color red
```

Then make the working one permanent:

```
GOVEE_BLE_PROFILE=H66A1=0d
```

The value is `SKU=mode`, optionally followed by `/byte` for models that take
brightness as a raw byte rather than a percentage. Please report what worked so
the table can grow.

## Checking

The easiest check needs no terminal: press the Bluetooth button in the control
panel's header. It scans for 15 seconds, attaches anything new it finds, and
shows every Govee device in range with its signal strength and which account
device it maps to, plus the devices it expected but did not see. The same
report is available as `POST /api/ble/scan` (add `?seconds=30` for a longer
scan), and the add-on's log shows the result of the scan it runs on startup.

From a shell on the host:

```bash
docker run --rm -v /run/dbus:/run/dbus:ro --env-file .env ghcr.io/borexola/novee2mqtt:latest ble-scan
```

Lists every Govee device in range with its signal strength and which account
device it maps to, and names the account devices that were expected but not
seen. A device that is not seen is out of range, has Bluetooth switched off in
the Govee app, or is currently connected to a phone.

```bash
docker run --rm -v /run/dbus:/run/dbus:ro ghcr.io/borexola/novee2mqtt:latest ble-control --address 5C:E7:53:BC:97:9D --sku H6008 on
```

Drives one light directly, bypassing the bridge's own state, and prints what
the light reports afterwards.

## Troubleshooting

* **`Cannot reach BlueZ over the system D-Bus`** — the socket is not mounted
  (Docker), `host_dbus` is missing (an older add-on build), or the host has no
  bluetooth service running. `Connection closed by peer` or `AccessDenied` at
  the end of that message means D-Bus refused the container: see the Docker
  notes at the top of this page. In the add-on it points at its AppArmor
  profile: look for `apparmor="DENIED"` in the host log and report what it names.
* **`No Bluetooth adapter is available to BlueZ`** — the host has no adapter,
  it is blocked by `rfkill`, or another process has claimed it exclusively.
* **`is not advertising within Bluetooth range`** — the light is too far from
  the host, or a phone with the Govee app is already connected to it.
* **The light connects but ignores everything** — check `encrypted` in
  `ble-scan`, then the frame layout above.
* **Commands are slow** — each command on an idle light costs a Bluetooth
  connection, typically one to three seconds. Raise `GOVEE_BLE_IDLE_SECONDS`
  to keep the connection open longer between commands.
