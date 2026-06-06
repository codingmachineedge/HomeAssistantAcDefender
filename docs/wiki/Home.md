# Home Assistant AC Defender Wiki

Home Assistant AC Defender is a Docker-hosted ASP.NET Core Blazor website and background worker that watches a real Home Assistant climate entity and defends the dining room AC target.

Key capabilities:

- MudBlazor dashboard with automatic live updates and 24-hour time display.
- 24/7 Home Assistant thermostat checking.
- External thermostat touch detection.
- Audit log with timestamp, old setpoint, new setpoint, room temperature, outdoor temperature, and weather condition.
- Dynamic cooldown after manual changes.
- Home Assistant context attribution for user/phone, automation, and thermostat/device changes.
- Super Defender strict timing for repeated remote-style thermostat changes, with network lockout left as a manual router decision.
- Custom schedule with weather activation rules.
- Helper descriptions under controls and action labels.
- Upstairs comfort guard with optional home-presence awareness.
- Cooler Intent Fast Lane that skips quiet waits briefly when repeated real wall touches ask for cooler air.
- Setpoint Stillness that waits for repeated real readings to show the wall setpoint has stopped changing.
- Weather Drift Timing that uses real outdoor temperature movement to time safe corrections.
- HVAC Alibi that waits for real Home Assistant `hvac_action` transitions before safe corrections.
- Alectra Peak Power Saver that relaxes safe cooling during On-peak, high-price, or high-power usage.
- Tabbed Alectra Hui Energy page with search, desk filters, grouped entity cards, charts, and a mobile-friendly table.
- Front-door Guard Post that can pause the defender and turn the thermostat off when a real front-door person detector trips.
- Optional fan energy saver near target temperature.

No simulator or dummy thermostat is used. Every control acts on the configured Home Assistant climate entity or returns a real error.
