---
name: run-ac-defender
description: Build, run, and drive the Home Assistant AC Defender Blazor app on Windows. Use when asked to start, launch, build, or run AC Defender, take a screenshot of its dashboard/defense/energy/settings pages, sign in to the UI, run its tests, or verify a frontend change in the real running app.
---

AC Defender is an **ASP.NET Core Blazor Server** web app (.NET 10, MudBlazor) that
guards a real Home Assistant climate entity. Drive it by starting the server with
`dotnet run`, then driving the browser with the committed Playwright harness
**`.Codex/skills/run-ac-defender/driver.mjs`** — it signs in (creating the first-run
owner account automatically) and screenshots every page.

All paths below are relative to the **repo root** (`HomeAssistantAcDefender/`).
Commands are written for **PowerShell** (this machine's primary shell). The app is
auth-gated and was verified here with **no real Home Assistant** — the UI renders
fully and shows graceful "Home Assistant offline" placeholders, which is expected.

## Prerequisites

Already present on this machine (versions that worked):

- **.NET SDK 10** — `dotnet --list-sdks` showed `10.0.301`. The csproj targets
  `net10.0`. (A newer SDK like the 11.0 preview also builds it; see Gotchas.)
- **Node.js 18+** for the driver — verified with Node `v26.2.0`.
- **Playwright + Chromium** — installed via `npm install` in the skill dir below;
  the Chromium build ships with Playwright, so no system Chrome is needed.

If a runtime is missing, install the **.NET 10 SDK** from
https://dotnet.microsoft.com/download and **Node.js** from https://nodejs.org .

## Build

From the repo root:

```powershell
dotnet build HomeAssistantAcDefender.csproj
```

Builds in ~10s, `0 Warning(s) 0 Error(s)` (an informational `NETSDK1057` preview
notice may print — harmless).

## Run (agent path)

### 1. Start the server (background) on port 8899

From the repo root. `Start-Process` is used so the call returns immediately; the
explicit `-WorkingDirectory` is **required** (it does not inherit the shell's
current directory).

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
Start-Process -FilePath "dotnet" `
  -ArgumentList "run --project HomeAssistantAcDefender.csproj --urls http://127.0.0.1:8899 --no-launch-profile" `
  -WorkingDirectory $PWD `
  -RedirectStandardOutput "$env:TEMP\acdefender.out.log" `
  -RedirectStandardError  "$env:TEMP\acdefender.err.log" `
  -WindowStyle Hidden
foreach ($i in 1..40) {
  try { $r = Invoke-WebRequest "http://127.0.0.1:8899/login" -UseBasicParsing -TimeoutSec 2; Write-Output "UP -> HTTP $($r.StatusCode)"; break } catch { Start-Sleep -Seconds 1 }
}
```

Expect `UP -> HTTP 200` (within ~2s after the first build). The first `dotnet run`
compiles, so allow extra time on a cold build.

### 2. Drive it — sign in + screenshot every page

First time only, install the driver's dependency:

```powershell
cd .Codex\skills\run-ac-defender
npm install
```

Then run the harness (it auto-creates the first-run **owner** account `owner` /
`defender123`, or signs in if it already exists):

```powershell
cd .Codex\skills\run-ac-defender
node driver.mjs shots
```

Output — one PNG per page in `.Codex\skills\run-ac-defender\shots\`:

```
[auth] no account yet -> creating owner "owner"
[auth] signed in, landed on http://127.0.0.1:8899/
[shot] /          -> shots\dashboard.png   (Dashboard - AC Defender)
[shot] /defense   -> shots\defense.png     (Defense - AC Defender)
[shot] /energy    -> shots\energy.png      (Energy - AC Defender)
[shot] /comfort   -> shots\comfort.png     (Comfort - AC Defender)
[shot] /controls  -> shots\controls.png    (Controls - AC Defender)
[shot] /logs      -> shots\logs.png        (Logs - AC Defender)
[shot] /settings  -> shots\settings.png    (Settings - AC Defender)
[shot] /guide     -> shots\guide.png       (Guide - AC Defender)

[console] no browser console errors
```

Other driver commands:

```powershell
node driver.mjs shot /energy    # one page (leading slash OK in PowerShell)
node driver.mjs shot energy     # same — slashless form works in ANY shell
node driver.mjs shot dashboard  # the dashboard ("/")
node driver.mjs probe           # sign in and list pages, take no shots
```

Driver env knobs (PowerShell `$env:NAME = "..."` before the command):
`BASE_URL` (default `http://127.0.0.1:8899`), `AC_USER` (`owner`), `AC_PASS`
(`defender123`), `SHOT_DIR` (`./shots`), `VIEWPORT` (`desktop`|`mobile`),
`HEADED` (`1` to watch a window). Mobile pass, used for the overflow checks in
`AGENTS.md`:

```powershell
$env:VIEWPORT = "mobile"; $env:SHOT_DIR = "./shots/mobile"; node driver.mjs shots
```

### 3. Stop the server

```powershell
Get-NetTCPConnection -LocalPort 8899 -State Listen | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
```

### Quick HTTP smoke (no browser)

Works in PowerShell or Git Bash:

```bash
curl -s -o /dev/null -w "login=%{http_code}\n" http://127.0.0.1:8899/login   # 200
curl -s -o /dev/null -w "root=%{http_code}\n"  http://127.0.0.1:8899/        # 302 -> /login
```

## Test

A custom console runner (exits non-zero on failure) covers the defender setpoint /
guard logic:

```powershell
dotnet run --project HomeAssistantAcDefender.Tests\HomeAssistantAcDefender.Tests.csproj
```

Prints `Defender setpoint regression checks passed.` and exits `0`.

## Run (human path)

`dotnet run --urls http://127.0.0.1:8888` (per the README) starts the same server in
the foreground and blocks until Ctrl-C; open the URL in a browser and sign in
manually. Useless for an automated agent — use the background launch above instead.
There is also a CLI that talks straight to Home Assistant and exits (needs a real HA
token): `dotnet run -- usage-live` / `dotnet run -- usage-history --hours 24`.

## Gotchas

- **Auth gates everything but `/login`.** The **first** account created becomes the
  owner and needs **no** registration code; later sign-ups need an owner-set code.
  The driver creates that owner on first run. Accounts persist in `App_Data\`
  (gitignored). **To reset to a fresh owner-signup screen, delete
  `App_Data\auth-config.json`** and restart the server.
- **"Home Assistant offline" is expected here.** Without a real HA, live readings
  show `--`, the dashboard shows "Waiting for Home Assistant connection", and Energy
  shows "Usage history error: Home Assistant token is not configured." The pages
  still render — that is correct graceful degradation, not a failure. Per `AGENTS.md`,
  do **not** add fake/simulated thermostat state to make it "look connected."
- **Blazor Server holds a SignalR/SSE connection open**, so Playwright's
  `networkidle` never settles and would hang. The driver waits on `domcontentloaded`
  plus a short fixed settle instead.
- **Git Bash mangles leading-slash arguments.** `node driver.mjs shot /energy` in
  Git Bash becomes `.../Git/energy` (MSYS path translation) and fails. Use
  **PowerShell**, or pass the route **without** a leading slash (`shot energy`) —
  the driver normalizes both forms (and `shot dashboard` → `/`).
- **`Start-Process` ignores the shell's working directory.** Without
  `-WorkingDirectory $PWD`, `dotnet` reports `The provided file path does not exist:
  HomeAssistantAcDefender.csproj`. Always pass it, and run the launch from the repo
  root.
- **Default `dotnet` may resolve to a preview SDK.** Here `dotnet --version` returned
  `11.0.100-preview`, which still builds `net10.0` fine but prints `NETSDK1057`. Add
  a `global.json` pinning `10.0.301` if you want to silence it.
- **Two ports in play:** `8899` (this skill / `.Codex/launch.json`) vs `8888`
  (Docker compose & README). The driver defaults to `8899`; override with
  `$env:BASE_URL`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Cannot navigate to invalid URL .../Git/energy` | Git Bash path mangling — run in PowerShell or use the slashless route (`shot energy`). |
| Start-Process: `The provided file path does not exist: HomeAssistantAcDefender.csproj` | Add `-WorkingDirectory $PWD` and launch from the repo root. |
| Driver: `sign-in failed (still on /login): Incorrect username or password.` | An account already exists with different creds. Reset by deleting `App_Data\auth-config.json`, or set `$env:AC_PASS` to the right password. |
| `target machine actively refused ... 8899` | Server not up yet (first run builds first). Re-poll; check `$env:TEMP\acdefender.err.log`. |
| Port 8899 already in use | Another instance is running — stop it with the `Get-NetTCPConnection` one-liner above. |
| `node driver.mjs` → `Cannot find package 'playwright'` | Run `npm install` inside `.Codex\skills\run-ac-defender` first. |
