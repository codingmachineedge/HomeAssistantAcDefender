using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Key-free Open-Meteo fallback for real outdoor conditions and a 48-hour forecast. This typed
/// client is intentionally separate from <see cref="HomeAssistantClient"/>, so Home Assistant
/// authorization headers can never be sent to the external weather endpoint.
/// </summary>
public sealed class OpenMeteoWeatherClient
{
    public const string SourceId = "open-meteo";
    private static readonly TimeSpan MinimumRefresh = TimeSpan.FromMinutes(10);

    private readonly HttpClient httpClient;
    private readonly IOptionsMonitor<HomeAssistantOptions> options;
    private readonly ILogger<OpenMeteoWeatherClient> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private OpenMeteoWeatherBundle? cached;
    private DateTimeOffset nextAttemptAt;
    private double? lastAttemptLatitude;
    private double? lastAttemptLongitude;

    public OpenMeteoWeatherClient(
        HttpClient httpClient,
        IOptionsMonitor<HomeAssistantOptions> options,
        ILogger<OpenMeteoWeatherClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;

        httpClient.BaseAddress ??= new Uri("https://api.open-meteo.com/");
        httpClient.Timeout = TimeSpan.FromSeconds(15);
        // Defense in depth: even if a future global HttpClient convention adds authorization,
        // this public weather client must never forward Home Assistant or any other bearer token.
        httpClient.DefaultRequestHeaders.Authorization = null;
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "HomeAssistantAcDefender/1.0 (+https://github.com/codingmachineedge/HomeAssistantAcDefender)");
        }

        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<OpenMeteoWeatherBundle?> GetWeatherAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        if (!IsValidCoordinates(latitude, longitude))
        {
            logger.LogWarning("Open-Meteo fallback skipped because its coordinates are invalid.");
            return null;
        }

        // Open-Meteo's forecast grid does not need household-level GPS precision. Round only the
        // outbound/cached location to roughly kilometre-scale locality before contacting the public
        // service; Home Assistant's exact installation coordinates remain inside the HA client.
        latitude = Math.Round(latitude, 2, MidpointRounding.AwayFromZero);
        longitude = Math.Round(longitude, 2, MidpointRounding.AwayFromZero);

        var now = DateTimeOffset.UtcNow;
        if (IsFreshCachedValue(latitude, longitude, now))
        {
            return cached;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (IsFreshCachedValue(latitude, longitude, now))
            {
                return cached;
            }

            var sameLocationAsLastAttempt = CoordinatesMatch(latitude, longitude, lastAttemptLatitude, lastAttemptLongitude);
            if (sameLocationAsLastAttempt && now < nextAttemptAt)
            {
                return null;
            }

            lastAttemptLatitude = latitude;
            lastAttemptLongitude = longitude;
            var query = string.Create(
                CultureInfo.InvariantCulture,
                $"v1/forecast?latitude={latitude:0.######}&longitude={longitude:0.######}&current=temperature_2m,weather_code&hourly=temperature_2m,weather_code&forecast_hours=48&temperature_unit=celsius&timezone=UTC");

            try
            {
                using var response = await httpClient.GetAsync(query, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Open-Meteo fallback returned HTTP {StatusCode}.", (int)response.StatusCode);
                    nextAttemptAt = now.Add(MinimumRefresh);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var parsed = ParseResponse(document.RootElement, latitude, longitude, now);
                if (parsed is null)
                {
                    logger.LogWarning("Open-Meteo fallback response did not contain a valid current temperature.");
                    nextAttemptAt = now.Add(MinimumRefresh);
                    return null;
                }

                cached = parsed;
                nextAttemptAt = now.Add(GetRefreshInterval());
                logger.LogInformation(
                    "Open-Meteo backup refreshed at {ObservedAt} with {ForecastCount} hourly forecast entries.",
                    parsed.ObservedAt,
                    parsed.Forecast.Entries.Count);
                return parsed;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                nextAttemptAt = now.Add(MinimumRefresh);
                logger.LogWarning(ex, "Open-Meteo fallback timed out; retrying no sooner than ten minutes.");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                nextAttemptAt = now.Add(MinimumRefresh);
                logger.LogWarning(ex, "Open-Meteo fallback request failed; retrying no sooner than ten minutes.");
                return null;
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private bool IsFreshCachedValue(double latitude, double longitude, DateTimeOffset now) =>
        cached is not null
        && CoordinatesMatch(latitude, longitude, cached.Latitude, cached.Longitude)
        && now - cached.FetchedAt < GetRefreshInterval();

    private TimeSpan GetRefreshInterval() =>
        TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.OpenMeteoRefreshMinutes, 10, 24 * 60));

    private static OpenMeteoWeatherBundle? ParseResponse(
        JsonElement root,
        double requestedLatitude,
        double requestedLongitude,
        DateTimeOffset fetchedAt)
    {
        if (!root.TryGetProperty("current", out var current)
            || !current.TryGetProperty("temperature_2m", out var currentTemperatureElement)
            || !currentTemperatureElement.TryGetDouble(out var currentTemperature)
            || !double.IsFinite(currentTemperature))
        {
            return null;
        }

        var currentCode = current.TryGetProperty("weather_code", out var currentCodeElement)
            && currentCodeElement.TryGetInt32(out var parsedCurrentCode)
                ? parsedCurrentCode
                : (int?)null;
        var observedAt = current.TryGetProperty("time", out var currentTimeElement)
            ? ParseUtcTimestamp(currentTimeElement.GetString()) ?? fetchedAt
            : fetchedAt;

        var entries = new List<ForecastEntry>(48);
        if (root.TryGetProperty("hourly", out var hourly)
            && hourly.TryGetProperty("time", out var times)
            && times.ValueKind == JsonValueKind.Array
            && hourly.TryGetProperty("temperature_2m", out var temperatures)
            && temperatures.ValueKind == JsonValueKind.Array)
        {
            hourly.TryGetProperty("weather_code", out var codes);
            var timeValues = times.EnumerateArray().ToArray();
            var temperatureValues = temperatures.EnumerateArray().ToArray();
            var codeValues = codes.ValueKind == JsonValueKind.Array ? codes.EnumerateArray().ToArray() : [];
            var count = Math.Min(48, Math.Min(timeValues.Length, temperatureValues.Length));
            for (var index = 0; index < count; index++)
            {
                var timestamp = ParseUtcTimestamp(timeValues[index].GetString());
                if (timestamp is null
                    || !temperatureValues[index].TryGetDouble(out var temperature)
                    || !double.IsFinite(temperature))
                {
                    continue;
                }

                var code = index < codeValues.Length && codeValues[index].TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : (int?)null;
                entries.Add(new ForecastEntry(timestamp.Value, temperature, MapWeatherCode(code)));
            }
        }

        var currentReading = new WeatherReading(SourceId, currentTemperature, MapWeatherCode(currentCode));
        var forecast = new WeatherForecastReading(SourceId, "hourly", entries);
        return new OpenMeteoWeatherBundle(
            currentReading,
            forecast,
            observedAt,
            fetchedAt,
            requestedLatitude,
            requestedLongitude);
    }

    private static DateTimeOffset? ParseUtcTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
                ? timestamp
                : null;

    private static string MapWeatherCode(int? code) => code switch
    {
        0 => "sunny",
        1 => "mostly-sunny",
        2 => "partlycloudy",
        3 => "cloudy",
        45 or 48 => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing-drizzle",
        61 or 63 or 65 => "rainy",
        66 or 67 => "freezing-rain",
        71 or 73 or 75 or 77 => "snowy",
        80 or 81 or 82 => "pouring",
        85 or 86 => "snowy-rainy",
        95 => "lightning",
        96 or 99 => "lightning-rainy",
        _ => code is { } value ? $"wmo-{value}" : "unknown",
    };

    private static bool IsValidCoordinates(double latitude, double longitude) =>
        double.IsFinite(latitude)
        && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90
        && longitude is >= -180 and <= 180;

    private static bool CoordinatesMatch(
        double latitude,
        double longitude,
        double? otherLatitude,
        double? otherLongitude) =>
        otherLatitude is { } lat
        && otherLongitude is { } lon
        && Math.Abs(latitude - lat) < 0.000001
        && Math.Abs(longitude - lon) < 0.000001;
}
