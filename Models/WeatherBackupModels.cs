namespace HomeAssistantAcDefender.Models;

public sealed record HomeAssistantCoordinates(double Latitude, double Longitude);

public sealed record OpenMeteoWeatherBundle(
    WeatherReading Current,
    WeatherForecastReading Forecast,
    DateTimeOffset ObservedAt,
    DateTimeOffset FetchedAt,
    double Latitude,
    double Longitude);
