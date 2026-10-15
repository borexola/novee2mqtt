# Changelog

## 0.0.3

### Added

- The control panel is available in the Home Assistant sidebar, served through
  ingress, so it works from the mobile app and over remote access. The panel
  needs the bridge on port 8056.

### Fixed

- The control panel showed loading placeholders forever when no devices had
  been found, instead of explaining what to configure.

## 0.0.2

Makes the bridge cheaper on Govee's rate-limited API, more resilient to cloud
outages, and kinder to Home Assistant's database.

### Fixed

- Scene lists were re-fetched from the Platform API on every device state
  change, which could exhaust the daily request quota on installs with more than
  a dozen LAN lights, and stalled commands for up to 10 seconds on networks with
  no internet route. State changes now publish without touching the cloud.
- The `Status` diagnostic sensor republished its attributes (with fresh
  timestamps) every minute for every LAN device, adding a new row to the Home
  Assistant recorder each time. Attributes are now only republished when
  something other than a timestamp changed.
- Command-line flags for the MQTT port, HTTP port, discovery prefix and
  temperature scale were overridden by the matching environment variables.
- Changing the Govee account or API key could keep serving the previous
  account's cached device list and tokens until they expired.
- Picking the blank entry in a light's effect list logged an error instead of
  clearing the remembered scene.
- Commands and status packets could be applied out of order under load.
- A device that stopped answering on the LAN was never polled through the cloud
  again; it is now checked hourly.

### Added

- Cloud APIs that fail at startup (Govee's login endpoint is quick to return
  "too many requests") are retried in the background with backoff instead of
  staying off until a restart.
- Device lists are re-read hourly, so devices added in the Govee app appear
  without restarting the add-on.
- Platform API rate limiting is honoured: a 429 pauses further calls for the
  time Govee asks, and server errors are retried once.
- The REST control endpoints accept `POST` as well as `GET`.

### Changed

- Scene lists are refreshed every six hours instead of every five minutes.
- Home Assistant is only re-registered on its `online` status, not on `offline`.
- The cache file is created readable by the add-on user only, and expired
  entries are cleaned up on start.

## 0.0.1

Initial release.
