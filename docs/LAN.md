# LAN API requirements

LAN control is the fastest and most reliable path to a Govee device, and the only
one that keeps working when your internet connection is down. It is worth getting
working.

## Enable it per device

The LAN API must be switched on for each individual device in the Govee Home app.
Only [certain models support it](https://app-h5.govee.com/user-manual/wlan-guide);
some need a firmware update first, and older hardware revisions never will.

## What the protocol needs from your network

| Direction | Port | Purpose |
|---|---|---|
| Bridge → device | UDP 4001 | Discovery requests, sent to multicast group `239.255.255.250` |
| Device → bridge | UDP 4002 | Discovery replies and status responses |
| Bridge → device | UDP 4003 | Control commands |

Three consequences follow:

* **The container must use host networking.** Replies are sent to port 4002 on
  the host, and multicast does not traverse Docker's bridge network.
* **Only one process on the host can bind UDP 4002.** If the bridge reports that
  it cannot bind, something else already has it — commonly the `Govee LAN Control`
  Home Assistant integration or `homebridge-govee` with LAN enabled. Disable one.
* **Your devices and the bridge must be able to exchange multicast UDP.** This is
  where most problems are.

## When multicast does not work

Multicast is frequently dropped between wireless access points and wired
segments, blocked by "AP isolation" or "client isolation" settings, or lost
across VLANs. Symptoms: the bridge logs that a device *should* be reachable over
the LAN API but did not answer, while the device works fine in the Govee app.

Options, roughly in order of preference:

1. **Fix the network.** Enable IGMP snooping and multicast forwarding on your
   access points, and turn off client isolation for the VLAN your devices are on.
2. **`broadcast_all` / `GOVEE_LAN_BROADCAST_ALL=true`.** Sends discovery to the
   broadcast address of each non-loopback interface. Usually enough when the
   bridge and the devices are on the same subnet.
3. **`scan` / `GOVEE_LAN_SCAN`.** Probe specific addresses directly. Give each
   device a static DHCP lease first, otherwise its address will change and the
   entry will go stale. You can also list a subnet's broadcast address, such as
   `10.0.0.255`, for a network that is routable but not directly attached.
4. **`global_broadcast` / `GOVEE_LAN_BROADCAST_GLOBAL=true`.** Sends to
   `255.255.255.255`. A blunt instrument, but sometimes the one that works.

## Checking

```bash
docker run --rm --network host ghcr.io/borexola/novee2mqtt:latest lan-disco
```

That probes for a few seconds and prints every device that answers, along with
its current state. To probe one specific address:

```bash
docker run --rm --network host ghcr.io/borexola/novee2mqtt:latest lan-control --ip 10.0.0.42 on
```

## What you lose without it

Devices are still controllable through the Platform API and, with a Govee
account configured, through AWS IoT. You lose the lowest-latency path and local
control during an internet outage, and the bridge falls back to polling more
often, which consumes Platform API quota.
