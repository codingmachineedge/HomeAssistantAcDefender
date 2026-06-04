using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using Microsoft.Extensions.Options;

namespace HomeAssistantAcDefender.Services;

public sealed class HomeAssistantClient
{
    private readonly HttpClient httpClient;
    private readonly IOptionsMonitor<HomeAssistantOptions> options;
    private readonly ILogger<HomeAssistantClient> logger;
    private string? discoveredClimateEntityId;
    private string? discoveredWeatherEntityId;

    public HomeAssistantClient(HttpClient httpClient, IOptionsMonitor<HomeAssistantOptions> options, ILogger<HomeAssistantClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.AccessToken);

    public async Task<ThermostatReading?> GetDiningRoomClimateAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var configuredEntityId = options.CurrentValue.EntityId;
        if (!string.IsNullOrWhiteSpace(configuredEntityId))
        {
            var configuredReading = await TryGetClimateStateAsync(configuredEntityId.Trim(), cancellationToken);
            if (configuredReading is not null)
            {
                return configuredReading;
            }
        }

        var entityId = await DiscoverClimateEntityIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        return await TryGetClimateStateAsync(entityId, cancellationToken);
    }

    public async Task<WeatherReading?> GetWeatherAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var configuredWeatherEntityId = options.CurrentValue.WeatherEntityId;
        if (!string.IsNullOrWhiteSpace(configuredWeatherEntityId))
        {
            var configuredReading = await TryGetWeatherStateAsync(configuredWeatherEntityId.Trim(), cancellationToken);
            if (configuredReading is not null)
            {
                return configuredReading;
            }
        }

        var entityId = await DiscoverWeatherEntityIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return await TryGetOutdoorTemperatureSensorAsync(cancellationToken);
        }

        return await TryGetWeatherStateAsync(entityId, cancellationToken);
    }

    public async Task<IReadOnlyList<TemperatureSensorReading>> GetUpstairsTemperatureSensorsAsync(string configuredEntityIds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Array.Empty<TemperatureSensorReading>();
        }

        var configured = SplitEntityIds(configuredEntityIds).ToArray();
        if (configured.Length > 0)
        {
            var readings = new List<TemperatureSensorReading>();
            foreach (var entityId in configured)
            {
                var reading = await TryGetTemperatureSensorAsync(entityId, cancellationToken);
                if (reading is not null)
                {
                    readings.Add(reading);
                }
            }

            return readings;
        }

        return (await GetStatesAsync(cancellationToken))
            .Where(IsLikelyUpstairsTemperatureSensor)
            .Select(ParseTemperatureSensor)
            .Where(reading => reading.TemperatureCelsius is not null)
            .OrderByDescending(reading => reading.TemperatureCelsius)
            .Take(8)
            .ToArray();
    }

    public async Task<IReadOnlyList<PresenceReading>> GetPresenceAsync(string configuredEntityIds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Array.Empty<PresenceReading>();
        }

        var configured = SplitEntityIds(configuredEntityIds).ToArray();
        if (configured.Length > 0)
        {
            var readings = new List<PresenceReading>();
            foreach (var entityId in configured)
            {
                var reading = await TryGetPresenceAsync(entityId, cancellationToken);
                if (reading is not null)
                {
                    readings.Add(reading);
                }
            }

            return readings;
        }

        return (await GetStatesAsync(cancellationToken))
            .Where(IsPresenceEntity)
            .Select(ParsePresence)
            .Take(12)
            .ToArray();
    }

    public async Task SetCoolingAsync(string entityId, double setPointCelsius, CancellationToken cancellationToken)
    {
        await SetHvacModeAsync(entityId, "cool", cancellationToken);

        await CallServiceAsync("climate", "set_temperature", new
        {
            entity_id = entityId,
            temperature = setPointCelsius
        }, cancellationToken);
    }

    public async Task SetHvacModeAsync(string entityId, string hvacMode, CancellationToken cancellationToken)
    {
        await CallServiceAsync("climate", "set_hvac_mode", new
        {
            entity_id = entityId,
            hvac_mode = hvacMode
        }, cancellationToken);
    }

    public async Task SetFanModeAsync(string entityId, string fanMode, CancellationToken cancellationToken)
    {
        await CallServiceAsync("climate", "set_fan_mode", new
        {
            entity_id = entityId,
            fan_mode = fanMode
        }, cancellationToken);
    }

    private async Task<TemperatureSensorReading?> TryGetTemperatureSensorAsync(string entityId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseTemperatureSensor(document.RootElement);
    }

    private async Task<PresenceReading?> TryGetPresenceAsync(string entityId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParsePresence(document.RootElement);
    }

    private async Task<ThermostatReading?> TryGetClimateStateAsync(string entityId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Home Assistant climate entity {EntityId} was not found", entityId);
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseClimateState(document.RootElement, entityId);
    }

    private async Task<WeatherReading?> TryGetWeatherStateAsync(string entityId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Home Assistant weather entity {EntityId} was not found", entityId);
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseWeatherState(document.RootElement, entityId);
    }

    private async Task<WeatherReading?> TryGetOutdoorTemperatureSensorAsync(CancellationToken cancellationToken)
    {
        var configuredSensor = options.CurrentValue.OutdoorTemperatureEntityId;
        if (string.IsNullOrWhiteSpace(configuredSensor))
        {
            return null;
        }

        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(configuredSensor.Trim())}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var temperature = TryParseStateDouble(document.RootElement);
        return new WeatherReading(configuredSensor.Trim(), temperature, "sensor");
    }

    private async Task<string?> DiscoverClimateEntityIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(discoveredClimateEntityId))
        {
            return discoveredClimateEntityId;
        }

        foreach (var entity in await GetStatesAsync(cancellationToken))
        {
            if (!entity.TryGetProperty("entity_id", out var entityIdElement))
            {
                continue;
            }

            var entityId = entityIdElement.GetString() ?? string.Empty;
            if (!entityId.StartsWith("climate.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var friendlyName = TryGetAttributeString(entity, "friendly_name") ?? string.Empty;
            if (entityId.Contains("dining", StringComparison.OrdinalIgnoreCase)
                || friendlyName.Contains("dining", StringComparison.OrdinalIgnoreCase))
            {
                discoveredClimateEntityId = entityId;
                logger.LogInformation("Discovered dining room climate entity {EntityId}", entityId);
                return discoveredClimateEntityId;
            }
        }

        return null;
    }

    private async Task<string?> DiscoverWeatherEntityIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(discoveredWeatherEntityId))
        {
            return discoveredWeatherEntityId;
        }

        foreach (var entity in await GetStatesAsync(cancellationToken))
        {
            if (!entity.TryGetProperty("entity_id", out var entityIdElement))
            {
                continue;
            }

            var entityId = entityIdElement.GetString() ?? string.Empty;
            if (entityId.StartsWith("weather.", StringComparison.OrdinalIgnoreCase))
            {
                discoveredWeatherEntityId = entityId;
                logger.LogInformation("Discovered weather entity {EntityId}", entityId);
                return discoveredWeatherEntityId;
            }
        }

        return null;
    }

    private async Task<JsonElement.ArrayEnumerator> GetStatesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/states", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone().EnumerateArray();
    }

    private async Task CallServiceAsync(string domain, string service, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, $"api/services/{domain}/{service}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, BuildUri(relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CurrentValue.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = options.CurrentValue.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://homeassistant.local:8123";
        }

        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = $"http://{baseUrl}";
        }

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new Uri(new Uri(baseUrl), relativePath);
    }

    private static ThermostatReading ParseClimateState(JsonElement root, string entityId)
    {
        var hvacMode = root.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString() ?? "unknown"
            : "unknown";

        return new ThermostatReading(
            entityId,
            TryGetAttributeDouble(root, "current_temperature") ?? 0.0,
            TryGetAttributeDouble(root, "temperature") ?? 0.0,
            hvacMode,
            TryGetAttributeString(root, "hvac_action") ?? hvacMode,
            TryGetAttributeString(root, "fan_mode"),
            TryGetAttributeStringArray(root, "fan_modes"));
    }

    private static WeatherReading ParseWeatherState(JsonElement root, string entityId)
    {
        var condition = root.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString()
            : null;

        return new WeatherReading(
            entityId,
            TryGetAttributeDouble(root, "temperature") ?? TryGetAttributeDouble(root, "apparent_temperature"),
            condition);
    }

    private static TemperatureSensorReading ParseTemperatureSensor(JsonElement root)
    {
        var entityId = GetEntityId(root);
        var name = TryGetAttributeString(root, "friendly_name") ?? entityId;
        var unit = TryGetAttributeString(root, "unit_of_measurement") ?? string.Empty;
        var temperature = TryParseStateDouble(root);
        if (temperature is not null && unit.Contains('F', StringComparison.OrdinalIgnoreCase))
        {
            temperature = (temperature.Value - 32.0) * 5.0 / 9.0;
        }

        return new TemperatureSensorReading(
            entityId,
            name,
            temperature is null ? null : Math.Round(temperature.Value, 1),
            TryGetState(root));
    }

    private static PresenceReading ParsePresence(JsonElement root)
    {
        var entityId = GetEntityId(root);
        var state = TryGetState(root);
        return new PresenceReading(
            entityId,
            TryGetAttributeString(root, "friendly_name") ?? entityId,
            state,
            string.Equals(state, "home", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetAttributeString(JsonElement root, string name)
    {
        if (!root.TryGetProperty("attributes", out var attributes)
            || !attributes.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool IsLikelyUpstairsTemperatureSensor(JsonElement root)
    {
        var entityId = GetEntityId(root);
        if (!entityId.StartsWith("sensor.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deviceClass = TryGetAttributeString(root, "device_class") ?? string.Empty;
        var unit = TryGetAttributeString(root, "unit_of_measurement") ?? string.Empty;
        var name = $"{entityId} {TryGetAttributeString(root, "friendly_name")}";
        var looksLikeTemperature = deviceClass.Equals("temperature", StringComparison.OrdinalIgnoreCase)
            || unit.Contains('C', StringComparison.OrdinalIgnoreCase)
            || unit.Contains('F', StringComparison.OrdinalIgnoreCase);
        var looksUpstairs = name.Contains("upstairs", StringComparison.OrdinalIgnoreCase)
            || name.Contains("second", StringComparison.OrdinalIgnoreCase)
            || name.Contains("2nd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("bedroom", StringComparison.OrdinalIgnoreCase)
            || name.Contains("master", StringComparison.OrdinalIgnoreCase);

        return looksLikeTemperature && looksUpstairs && TryParseStateDouble(root) is not null;
    }

    private static bool IsPresenceEntity(JsonElement root)
    {
        var entityId = GetEntityId(root);
        return entityId.StartsWith("person.", StringComparison.OrdinalIgnoreCase)
            || entityId.StartsWith("device_tracker.", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitEntityIds(string entityIds)
    {
        return (entityIds ?? string.Empty)
            .Split([',', '\n', '\r', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string GetEntityId(JsonElement root)
    {
        return root.TryGetProperty("entity_id", out var entityIdElement)
            ? entityIdElement.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string TryGetState(JsonElement root)
    {
        return root.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString() ?? "unknown"
            : "unknown";
    }

    private static IReadOnlyList<string> TryGetAttributeStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty("attributes", out var attributes)
            || !attributes.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static double? TryGetAttributeDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty("attributes", out var attributes)
            || !attributes.TryGetProperty(name, out var value))
        {
            return null;
        }

        return TryGetDouble(value);
    }

    private static double? TryParseStateDouble(JsonElement root)
    {
        if (!root.TryGetProperty("state", out var stateElement))
        {
            return null;
        }

        return TryGetDouble(stateElement);
    }

    private static double? TryGetDouble(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var numeric) => numeric,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
