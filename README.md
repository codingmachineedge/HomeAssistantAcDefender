# Home Assistant AC Defender

An ASP.NET Core Blazor website plus a 24/7 background worker that watches a **real** Home
Assistant climate entity and defends the dining room AC target — *my temp* — against the AC
app's own schedule, phone changes, and wall touches, while staying polite, safe, and cheap
to run.

There is no simulator and no dummy thermostat: every control acts on the real Home Assistant
entity or shows a real error.

![Command Center](docs/wiki/images/dashboard.png)

## What it does, in five ideas

1. **My temp is law.** Your chosen temperature is a hard floor. Warm rooms are walked back
   toward it in gentle 0.5 °C steps that start just under the current room temperature —
   never a suspicious snap.
2. **A team of guards, not one rule.** Dozens of small guards handle courtesy waits,
   tug-of-war truces, stealth timing, night shutdown, peak-power saving, emergency
   apologies, and more. Each one is a live card on the **Defense** page with a plain-English
   drawer, and each has a section in the in-app **Guide** — both generated from one source
   of truth in the code.
3. **People get courtesy; machines don't.** Human wall touches earn cooldowns, comfort
   grace, and natural-looking corrections. The AC vendor app's own temperature schedule
   (SLEEP → DEEP SLEEP → GOOD MORNING, quietly drifting the room to ~26 °C at 2 a.m.) is
   recognized by **Rival Schedule Watch** and answered back to my temp without the human
   niceties.
4. **Money awareness built in.** Real compressor hours are priced at Alectra time-of-use
   rates with **no power sensor needed**, shown under the runtime counters, on an
   airline-fare-style **usage calendar**, and steered by an optional **monthly budget**
   that eases cooling when you're spending too fast — never past a safety temperature.
5. **Safety always wins.** Hot rooms bypass every stealth wait, emergencies stop
   everything, and a front-door person detector can kill the AC instantly.
6. **Fully automated getting-yelled-at detection.** An angry setpoint jump or a burst of
   thermostat touches means someone is about to yell — the rage detector sees it coming,
   apologizes automatically, eases the AC up as a peace gesture, and stands down for two
   hours. It stops *before* it happens, to prevent tears.
7. **A persistent human always wins.** The truce family: insist on the same warmer number
   three times and the defender adopts it for four hours (Repeated-Raise Surrender); a
   thermostat that vanishes mid-argument triggers the **ULTRA OMEGA ALERT** Tamper Truce —
   two hours of stand-down, not alarms; and a bedroom door sensor opening at dawn warms
   the target before the person reaches the hallway (Wake-Up Truce).

| The money page | The usage calendar |
| --- | --- |
| ![Energy](docs/wiki/images/energy-overview.png) | ![Calendar](docs/wiki/images/energy-calendar.png) |

## Documentation

The full documentation lives in the **[wiki](docs/wiki/Home.md)** — start with the
**[Website Tour](docs/wiki/Website-Tour.md)**, a picture-book walk through every page that
anyone can follow.

| Page | What it covers |
| --- | --- |
| [Website Tour](docs/wiki/Website-Tour.md) | Every page, with screenshots, in plain words |
| [Algorithms](docs/wiki/Algorithms.md) | Search every AC Defender algorithm and open the full article for each one |
| [Every Guard, Explained Simply](docs/wiki/Every-Guard-Explained.md) | Every single algorithm, described so anyone can follow |
| [Energy & Costs](docs/wiki/Energy-and-Costs.md) | TOU rates, the sensor-free AC cost estimate, the calendar, the monthly budget |
| [Defender Logic](docs/wiki/Defender-Logic.md) | The decision cycle and every guard's exact rules |
| [Settings](docs/wiki/Settings.md) | Every knob on the Settings page |
| [API](docs/wiki/API.md) | JSON endpoints and the `/api/status/stream` SSE feed |
| [Architecture](docs/wiki/Architecture.md) | How the code is put together |
| [Deployment](docs/wiki/Deployment.md) | Docker, volumes, and the full environment-variable reference |

## Quick start

### Docker (recommended)

```powershell
# 1. Configure
cp .env.example .env     # fill in the Home Assistant values below

# 2. Run (publishes on host port 8888)
docker compose up -d --build
```

Required environment variables:

```text
HomeAssistant__BaseUrl=http://homeassistant.local:8123
HomeAssistant__EntityId=climate.dining_room
HomeAssistant__AccessToken=replace-with-token
```

Open `http://<host>:8888` — the first account you create becomes the owner. All optional
entities (weather, outdoor temperature, Alectra Hui usage sensors) and every `Defender__*`
tuning knob are listed in [Deployment](docs/wiki/Deployment.md).

### Local development

```powershell
dotnet build                                   # build
dotnet run --urls http://127.0.0.1:8888        # run the site
dotnet run --project HomeAssistantAcDefender.Tests/HomeAssistantAcDefender.Tests.csproj   # regression suite
```

### CLI (no web server)

```powershell
dotnet run -- usage-live
dotnet run -- usage-history --hours 24
```

## The website

Routed pages behind a responsive navigation drawer, all sharing one per-second live
snapshot — no refreshing, ever:

- **Command Center** (`/`) — my temp, the live wall unit, runtime hours **with estimated
  dollars**, direct orders, and the master switch.
- **Defense** (`/defense`) — every guard as a live card with "How this works" and
  extra-specific decision drawers.
- **Comfort** (`/comfort`) — the upstairs heat check with presence awareness.
- **Energy** (`/energy`) — costs, Alectra Hui intel, charts, and the AC usage **Calendar**.
- **Logs** (`/logs`) — the wall-touch audit trail with source attribution
  (person / phone / automation / rival schedule) and JSON detail.
- **Controls** (`/controls`) — target, fan, force, off, and emergency buttons.
- **Settings** (`/settings`) — every guard's dials, the **Electricity budget** switch, and
  the schedule editor.
- **Guide** (`/guide`) — the built-in manual, generated from the guard catalog.

See the [Website Tour](docs/wiki/Website-Tour.md) for all of it with screenshots.

## Development notes

- Run `dotnet build` before pushing; run the regression suite for logic changes.
- Do not commit `.env`, `App_Data`, build output, deployment archives, or Home Assistant
  tokens.
- `AGENTS.md` holds the safety rules every guard must respect (no fake state, my temp is a
  hard floor, safety bands always win).
