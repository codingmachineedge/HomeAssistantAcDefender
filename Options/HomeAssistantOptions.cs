namespace HomeAssistantAcDefender.Options;

public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    public string? AccessToken { get; set; }

    public string EntityId { get; set; } = "climate.dining_room";

    public string WeatherEntityId { get; set; } = "";

    public string OutdoorTemperatureEntityId { get; set; } = "";

    public string? Username { get; set; }

    public string? Password { get; set; }
}
