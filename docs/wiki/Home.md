# Home Assistant AC Defender Wiki

Home Assistant AC Defender is a Docker-hosted ASP.NET Core Blazor website and background worker that watches a real Home Assistant climate entity and defends the dining room AC target.

Key capabilities:

- MudBlazor dashboard with automatic live updates and 24-hour time display.
- 24/7 Home Assistant thermostat checking.
- External thermostat touch detection.
- Audit log with timestamp, old setpoint, new setpoint, room temperature, outdoor temperature, and weather condition.
- Dynamic cooldown after manual changes.
- Custom schedule with weather activation rules.
- Helper descriptions under controls and action labels.
- Upstairs comfort guard with optional home-presence awareness.
- Cooler Intent Fast Lane that skips quiet waits briefly when repeated real wall touches ask for cooler air.
- Optional fan energy saver near target temperature.

No simulator or dummy thermostat is used. Every control acts on the configured Home Assistant climate entity or returns a real error.
