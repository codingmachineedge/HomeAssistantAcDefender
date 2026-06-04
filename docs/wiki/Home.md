# Home Assistant AC Defender Wiki

Home Assistant AC Defender is a Docker-hosted ASP.NET Core website and background worker that watches a real Home Assistant climate entity and defends the dining room AC target.

Key capabilities:

- Real-time dashboard through Server-Sent Events.
- 24/7 Home Assistant thermostat checking.
- External thermostat touch detection.
- Audit log with timestamp, old setpoint, new setpoint, room temperature, outdoor temperature, and weather condition.
- Dynamic cooldown after manual changes.
- Custom schedule with weather activation rules.
- Optional fan energy saver near target temperature.

No simulator or dummy thermostat is used. Every control acts on the configured Home Assistant climate entity or returns a real error.
