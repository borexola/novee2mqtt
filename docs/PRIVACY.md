# Privacy

## What the bridge does with your credentials

Your Govee email, password and API key are read from the environment or the
add-on options, held in memory, and sent only to Govee's own endpoints:

| Endpoint | What for |
|---|---|
| `openapi.api.govee.com` | The documented Platform API, authenticated with your API key |
| `app2.govee.com` | Account login, device and room list, AWS IoT credentials, scene catalog |
| `community-api.govee.com` | Login for the Tap-to-Run shortcut list |
| Your account's AWS IoT endpoint | Live status updates and commands, over MQTT with a client certificate |

Nothing is sent anywhere else. There is no telemetry, no analytics and no
crash reporting.

## What is stored on disk

The cache at `GOVEE_CACHE_DIR` (`/data` in the container) holds:

* the device and room lists,
* per-device scene and DIY scene lists,
* the app scene catalog per SKU,
* the account login response, which includes a bearer token, your account's MQTT
  topic and the AWS IoT client certificate.

Your password itself is never written to disk. The cached tokens are as sensitive
as the password, though, so treat the cache volume accordingly. Deleting it is
always safe — the bridge refetches everything.

## Logging

Your password, account token and the IoT key material are never logged.

The account and per-device AWS IoT topics act as credentials, so they are logged
as `REDACTED`. Setting `GOVEE_LOG_SENSITIVE_DATA=true` shows them instead; only
do that while debugging, and do not paste the resulting logs into a public issue.

At `trace` level the bridge logs full MQTT payloads and LAN packets. Those
contain device identifiers and state, so review before sharing.

## A note on the undocumented API

The account login, device list, AWS IoT and Tap-to-Run features use endpoints
that the Govee mobile app uses but that Govee does not document or support. They
can change or stop working without notice. If you would rather not give the
bridge your account credentials, leave them unset: it will still work through the
LAN API and, with an API key, the Platform API. You will lose room names, live
push updates and Tap-to-Run scenes.
