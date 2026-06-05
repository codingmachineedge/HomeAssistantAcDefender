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

    public async Task<IReadOnlyList<FrontDoorPersonReading>> GetFrontDoorPersonDetectorsAsync(string configuredEntityIds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Array.Empty<FrontDoorPersonReading>();
        }

        var configured = SplitEntityIds(configuredEntityIds).ToArray();
        if (configured.Length > 0)
        {
            var readings = new List<FrontDoorPersonReading>();
            foreach (var entityId in configured)
            {
                var reading = await TryGetFrontDoorPersonDetectorAsync(entityId, cancellationToken);
                if (reading is not null)
                {
                    readings.Add(reading);
                }
            }

            return readings;
        }

        return (await GetStatesAsync(cancellationToken))
            .Where(IsLikelyFrontDoorPersonDetector)
            .Select(ParseFrontDoorPersonDetector)
            .Take(8)
            .ToArray();
    }

    public async Task<UsageLiveSnapshot> GetLiveUsageAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new UsageLiveSnapshot(null, null, null, null, null, null, null, Array.Empty<UsageEntityReading>(), false, DateTimeOffset.UtcNow);
        }

        var current = options.CurrentValue;
        var power = await TryGetUsageEntityAsync(current.UsagePowerEntityId, cancellationToken);
        var energy = await TryGetUsageEntityAsync(current.UsageEnergyEntityId, cancellationToken);
        var cost = await TryGetUsageEntityAsync(current.UsageCostEntityId, cancellationToken);
        var hourlyCost = await TryGetUsageEntityAsync(current.UsageHourlyCostEntityId, cancellationToken);
        var currentBill = await TryGetUsageEntityAsync(current.UsageCurrentBillEntityId, cancellationToken);
        var currentBillDue = await TryGetUsageEntityAsync(current.UsageCurrentBillDueEntityId, cancellationToken);
        var currentBillStatus = await TryGetUsageEntityAsync(current.UsageCurrentBillStatusEntityId, cancellationToken);
        var alectraHuiEntities = await GetAlectraHuiEntitiesAsync(cancellationToken);

        return new UsageLiveSnapshot(power, energy, cost, hourlyCost, currentBill, currentBillDue, currentBillStatus, alectraHuiEntities, true, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<UsageEntityReading>> GetAlectraHuiEntitiesAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Array.Empty<UsageEntityReading>();
        }

        var entities = new List<UsageEntityReading>();
        foreach (var entity in await GetStatesAsync(cancellationToken))
        {
            var entityId = GetEntityId(entity);
            if (string.IsNullOrWhiteSpace(entityId)
                || !entityId.Contains("alectra_hui", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entities.Add(ParseUsageEntity(entity));
        }

        return entities
            .OrderBy(entity => EntityDomainRank(entity.EntityId))
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AlectraPeakPowerReading> GetAlectraPeakPowerAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new AlectraPeakPowerReading(false, null, null, null, null, DateTimeOffset.UtcNow);
        }

        var current = options.CurrentValue;
        var entities = await GetAlectraHuiEntitiesAsync(cancellationToken);
        var power = FindUsageEntity(entities, current.UsagePowerEntityId, "current_power");
        var price = FindUsageEntity(entities, null, "current_price");
        var touPeriod = FindUsageEntity(entities, null, "current_tou_period");
        var currentPlan = FindUsageEntity(entities, "select.alectra_hui_current_plan", "current_plan")
            ?? FindUsageEntity(entities, "sensor.alectra_hui_current_plan", "current_plan");

        return new AlectraPeakPowerReading(
            true,
            NormalizePowerKilowatts(power),
            NormalizePriceCentsPerKwh(price),
            NormalizeBlank(touPeriod?.State),
            NormalizeBlank(currentPlan?.State),
            DateTimeOffset.UtcNow);
    }

    public async Task<UsageHistorySnapshot> GetUsageHistoryAsync(
        string? entityId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Home Assistant token is not configured.");
        }

        var targetEntityId = string.IsNullOrWhiteSpace(entityId)
            ? options.CurrentValue.UsageEnergyEntityId
            : entityId.Trim();
        if (string.IsNullOrWhiteSpace(targetEntityId))
        {
            throw new InvalidOperationException("Usage history entity is not configured.");
        }

        if (to <= from)
        {
            throw new InvalidOperationException("Usage history end time must be after start time.");
        }

        var path = $"api/history/period/{Uri.EscapeDataString(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
            $"?filter_entity_id={Uri.EscapeDataString(targetEntityId)}" +
            $"&end_time={Uri.EscapeDataString(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}";
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseUsageHistory(document.RootElement, targetEntityId, from, to);
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

    private async Task<UsageEntityReading?> TryGetUsageEntityAsync(string? entityId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId.Trim())}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseUsageEntity(document.RootElement);
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

    private async Task<FrontDoorPersonReading?> TryGetFrontDoorPersonDetectorAsync(string entityId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Home Assistant front-door person entity {EntityId} was not found", entityId);
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseFrontDoorPersonDetector(document.RootElement);
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

    private static int EntityDomainRank(string entityId)
    {
        if (entityId.StartsWith("sensor.", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (entityId.StartsWith("switch.", StringComparison.OrdinalIgnoreCase)
            || entityId.StartsWith("select.", StringComparison.OrdinalIgnoreCase)
            || entityId.StartsWith("number.", StringComparison.OrdinalIgnoreCase)
            || entityId.StartsWith("button.", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
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
            TryGetAttributeStringArray(root, "fan_modes"),
            TryGetContext(root));
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

    private static FrontDoorPersonReading ParseFrontDoorPersonDetector(JsonElement root)
    {
        var entityId = GetEntityId(root);
        var state = TryGetState(root);
        return new FrontDoorPersonReading(
            entityId,
            TryGetAttributeString(root, "friendly_name") ?? entityId,
            state,
            IsDetectedState(state),
            TryGetTimestamp(root, "last_changed") ?? TryGetTimestamp(root, "last_updated"));
    }

    private static UsageEntityReading ParseUsageEntity(JsonElement root)
    {
        var entityId = GetEntityId(root);
        var name = TryGetAttributeString(root, "friendly_name") ?? entityId;
        var unit = TryGetAttributeString(root, "unit_of_measurement") ?? string.Empty;
        return new UsageEntityReading(
            entityId,
            name,
            TryParseStateDouble(root),
            unit,
            TryGetState(root),
            TryGetTimestamp(root, "last_changed") ?? TryGetTimestamp(root, "last_updated"));
    }

    private static UsageEntityReading? FindUsageEntity(
        IReadOnlyList<UsageEntityReading> entities,
        string? exactEntityId,
        string entityIdNeedle)
    {
        if (!string.IsNullOrWhiteSpace(exactEntityId))
        {
            var exact = entities.FirstOrDefault(entity =>
                string.Equals(entity.EntityId, exactEntityId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return entities.FirstOrDefault(entity =>
            entity.EntityId.Contains(entityIdNeedle, StringComparison.OrdinalIgnoreCase)
            || entity.Name.Contains(entityIdNeedle.Replace('_', ' '), StringComparison.OrdinalIgnoreCase));
    }

    private static double? NormalizePowerKilowatts(UsageEntityReading? reading)
    {
        if (reading?.Value is not { } value)
        {
            return null;
        }

        var unit = reading.Unit.Trim();
        if (unit.Equals("W", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Round(value / 1000.0, 3);
        }

        if (unit.Equals("MW", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Round(value * 1000.0, 3);
        }

        return Math.Round(value, 3);
    }

    private static double? NormalizePriceCentsPerKwh(UsageEntityReading? reading)
    {
        if (reading?.Value is not { } value)
        {
            return null;
        }

        var unit = reading.Unit.Trim();
        if (unit.Contains("$", StringComparison.OrdinalIgnoreCase)
            || unit.Contains("CAD", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Round(value * 100.0, 2);
        }

        return Math.Round(value, 2);
    }

    private static string? NormalizeBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static UsageHistorySnapshot ParseUsageHistory(JsonElement root, string entityId, DateTimeOffset from, DateTimeOffset to)
    {
        var states = default(JsonElement);
        if (root.ValueKind == JsonValueKind.Array)
        {
            var entities = root.EnumerateArray();
            if (entities.MoveNext())
            {
                states = entities.Current;
            }
        }

        var samples = new List<UsageHistorySample>();
        var name = entityId;
        var unit = string.Empty;

        if (states.ValueKind == JsonValueKind.Array)
        {
            foreach (var state in states.EnumerateArray())
            {
                if (state.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                name = TryGetAttributeString(state, "friendly_name") ?? name;
                unit = TryGetAttributeString(state, "unit_of_measurement") ?? unit;
                var timestamp = TryGetTimestamp(state, "last_changed")
                    ?? TryGetTimestamp(state, "last_updated")
                    ?? from;
                samples.Add(new UsageHistorySample(timestamp, TryParseStateDouble(state), TryGetState(state)));
            }
        }

        var values = samples
            .Where(sample => sample.Value is not null)
            .Select(sample => sample.Value!.Value)
            .ToArray();
        var first = values.FirstOrDefault();
        var last = values.LastOrDefault();

        return new UsageHistorySnapshot(
            entityId,
            name,
            unit,
            from,
            to,
            samples.Count,
            values.Length == 0 ? null : first,
            values.Length == 0 ? null : last,
            values.Length == 0 ? null : values.Min(),
            values.Length == 0 ? null : values.Max(),
            values.Length == 0 ? null : last - first,
            samples);
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

    private static bool IsLikelyFrontDoorPersonDetector(JsonElement root)
    {
        var entityId = GetEntityId(root);
        if (!entityId.StartsWith("binary_sensor.", StringComparison.OrdinalIgnoreCase)
            && !entityId.StartsWith("sensor.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = $"{entityId} {TryGetAttributeString(root, "friendly_name")}".ToLowerInvariant();
        var nearFrontDoor = (text.Contains("front") && text.Contains("door"))
            || text.Contains("porch")
            || text.Contains("entry")
            || text.Contains("entrance");
        var detectsPerson = text.Contains("person")
            || text.Contains("human")
            || text.Contains("occupancy")
            || text.Contains("occupant")
            || text.Contains("visitor")
            || text.Contains("motion");

        return nearFrontDoor && detectsPerson;
    }

    private static bool IsDetectedState(string state)
    {
        var value = (state ?? string.Empty).Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric > 0;
        }

        return value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("detected", StringComparison.OrdinalIgnoreCase)
            || value.Equals("person", StringComparison.OrdinalIgnoreCase)
            || value.Equals("human", StringComparison.OrdinalIgnoreCase)
            || value.Equals("occupied", StringComparison.OrdinalIgnoreCase)
            || value.Equals("present", StringComparison.OrdinalIgnoreCase)
            || value.Equals("home", StringComparison.OrdinalIgnoreCase)
            || value.Equals("motion", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
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

    private static HomeAssistantStateContext? TryGetContext(JsonElement root)
    {
        if (!root.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new HomeAssistantStateContext(
            TryGetString(context, "id"),
            TryGetString(context, "parent_id"),
            TryGetString(context, "user_id"));
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;
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
