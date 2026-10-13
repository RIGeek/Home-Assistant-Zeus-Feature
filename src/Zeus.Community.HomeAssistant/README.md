# Home Assistant — Zeus community feature

Buttons, switches and sensor readouts for your Home Assistant entities, in a Zeus panel that looks and
behaves like the RZ1ZR LDMOS amplifier panel.

* **Switches** (switch, light, fan, input_boolean, automation, siren) light up when on and turn on or off with one click
* **Buttons** (button, input_button, script, scene) press once
* **Readouts** for any entity: numbers with their unit, binary sensors in Home Assistant's own words (Open / Closed,
  Detected / Clear ...), timestamps as local time
* **Bar meters** for numeric sensors, with the amplifier panel's green / yellow / red zones, including a
  "low values are bad" mode for batteries and signal levels
* **Lock while transmitting** (on by default for every switch and button): while Zeus reports MOX, those tiles are
  disabled in the panel and refused by the backend
* **Ask before switching**: a tile that needs a second press within three seconds, for things like station power
* Tiles can be labelled, grouped (Station, Antennas, Rotator ...) and reordered; everything is set up from the gear icon
* Live: states arrive over Home Assistant's WebSocket API as they change; no polling of Home Assistant

The feature only *reads* the radio (`ReadRadioState`, for the transmit lock); it never retunes or keys it.

## Setting up

1. In Home Assistant, open your profile, the **Security** tab, and create a **long-lived access token**.
   A separate Home Assistant user for Zeus (non-administrator) is a good idea.
2. In Zeus, add the **Home Assistant** panel, press the gear, and enter the address
   (for example `http://homeassistant.local:8123`, or `https://...` behind a reverse proxy; a path prefix is kept)
   and the token. Press **Connect**.
3. Search for entities under **Add a tile**, adjust each one with the pencil, and press **Save tiles**.

## Safety and security

* **The panel cannot call arbitrary services.** It can only press a configured tile; the backend decides which Home
  Assistant service that means from a fixed table (`turn_on` / `turn_off` for switches, `press` / `turn_on` for
  buttons, scripts and scenes). Locks, covers (garage doors), alarm panels and climate can be shown as readouts but
  never operated.
* **The token is write-only.** It is stored with Zeus's plugin settings, sent only to the Home Assistant address you
  entered, and never returned to the browser or written to the log. "Forget token" removes it.
* **A rejected token is not retried.** Home Assistant can ban an address after repeated failed logins, so after
  `auth_invalid` the feature waits until you press Connect again. Network drops are retried with backoff (2 s to 60 s).
* State-changing routes refuse cross-site browser requests and anything that is not JSON, the same rules as the
  amplifier feature. Request bodies are size-capped.
* "Accept a self-signed certificate" turns off certificate checking for that address. Use it only for your own
  server on your own network.
* If the transmit state cannot be read, locked tiles stay locked. If this Zeus build has no radio state at all, the
  panel says that nothing locks.

## How it talks to Home Assistant

WebSocket API at `/api/websocket`: `auth`, then `subscribe_events` for `state_changed` followed by `get_states`
(subscribing first means no change can fall in the gap; a change that arrives while the snapshot is in flight is
kept if it is newer), `call_service` for tiles, and a `ping` every 30 s so a half-open connection is noticed.
Only `unavailable` makes a tile unusable; buttons and scenes sit at `unknown` until first used.

## Build and test

    cd web && npm run build && cd ..
    dotnet build src/Zeus.Community.HomeAssistant/Zeus.Community.HomeAssistant.csproj -c Release
    dotnet run tests/HomeAssistant.Tests.cs          # 18 tests against a mock Home Assistant
    cd src/Zeus.Community.HomeAssistant && pwsh ./build-package.ps1   # script lives beside the .csproj, as in the template

License: GPL-2.0-or-later.
