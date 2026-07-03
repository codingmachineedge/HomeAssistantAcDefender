# AC Defender Desktop

A native desktop client (Windows `.exe`) for [Home Assistant AC Defender](../README.md),
built with **Tauri 2 + React + TypeScript**. It signs into your running defender and puts
the essentials on your desktop:

- Live wall unit: room temperature, setpoint, HVAC mode/action, fan
- **My temp** stepper with one-click APPLY
- Defender master switch (defend / stand down)
- Step toward my temp, force cooling, thermostat OFF
- Emergency buttons (Too cold, Brother mad)
- AC runtime hours with estimated dollars, budget status, and the live activity feed

## How it connects

All HTTP happens on the **Rust side** (reqwest with a cookie store), not in the webview —
so there is no CORS, and the app signs in through the same antiforgery login form the
website uses. Enter the defender address (e.g. `http://192.168.50.242:8888`) plus your
website username/password. "Remember password" stores the connection in your OS app-config
folder (`connection.json`) — leave it unchecked to keep the password off disk.

## Build

Prerequisites: Node 18+, Rust (stable), and on Windows the MSVC C++ build tools + WebView2
(preinstalled on Windows 11).

```powershell
cd desktop
npm install
npm run tauri dev      # develop with hot reload
npm run tauri build    # release build
```

Artifacts land in `src-tauri/target/release/`:

- `AC Defender Desktop.exe` — the portable executable
- `bundle/nsis/AC Defender Desktop_<version>_x64-setup.exe` — the installer

## Layout

```
desktop/
  src/            React + TypeScript UI (App.tsx, api.ts)
  src-tauri/      Rust bridge: login/session, typed commands over the defender's JSON API
```
