namespace HomeAssistantAcDefender.Options;

public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    public string? AccessToken { get; set; }

    public string EntityId { get; set; } = "climate.dining_room";

    public string WeatherEntityId { get; set; } = "";

    public string OutdoorTemperatureEntityId { get; set; } = "";

    // Key-free Open-Meteo fallback. Home Assistant weather and outdoor-temperature entities
    // always remain the primary sources. When coordinates are omitted, the app reads the real
    // installation latitude/longitude from Home Assistant's /api/config endpoint.
    public bool OpenMeteoBackupEnabled { get; set; } = true;

    public double? OpenMeteoLatitude { get; set; }

    public double? OpenMeteoLongitude { get; set; }

    // The client enforces a ten-minute minimum even if configuration asks for less.
    public int OpenMeteoRefreshMinutes { get; set; } = 30;

    public string UsagePowerEntityId { get; set; } = "sensor.alectra_hui_current_power";

    public string UsageEnergyEntityId { get; set; } = "sensor.alectra_hui_energy_today";

    public string UsageCostEntityId { get; set; } = "sensor.alectra_hui_cost_today";

    public string UsageHourlyCostEntityId { get; set; } = "sensor.alectra_hui_hourly_cost";

    public string UsageCurrentBillEntityId { get; set; } = "sensor.alectra_hui_current_bill";

    public string UsageCurrentBillDueEntityId { get; set; } = "sensor.alectra_hui_current_bill_due";

    public string UsageCurrentBillStatusEntityId { get; set; } = "sensor.alectra_hui_current_bill_status";

    public string? Username { get; set; }

    public string? Password { get; set; }

    // Adjustment-statistics context entities (all optional). The tracked person is a nickname-labelled
    // presence/person/device_tracker entity; the master-bedroom triggers are any motion/occupancy
    // sensors and/or lights whose "on" means the (hottest) bedroom is occupied.
    public string TrackedPersonLabel { get; set; } = "Taylor Swift";

    public string TrackedPersonEntityIds { get; set; } = "";

    public string MasterBedroomEntityIds { get; set; } = "";

    // Home Assistant notify service name (e.g. "mobile_app_owner_phone" → notify.mobile_app_owner_phone)
    // used by the Desired-State Enforcer. Empty disables notifications even if the Enforcer asks for one.
    public string NotifyService { get; set; } = "";
}
