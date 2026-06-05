namespace HomeAssistantAcDefender.Options;

public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    public string? AccessToken { get; set; }

    public string EntityId { get; set; } = "climate.dining_room";

    public string WeatherEntityId { get; set; } = "";

    public string OutdoorTemperatureEntityId { get; set; } = "";

    public string UsagePowerEntityId { get; set; } = "sensor.alectra_hui_current_power";

    public string UsageEnergyEntityId { get; set; } = "sensor.alectra_hui_energy_today";

    public string UsageCostEntityId { get; set; } = "sensor.alectra_hui_cost_today";

    public string? Username { get; set; }

    public string? Password { get; set; }
}
