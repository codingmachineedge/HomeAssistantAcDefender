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
    private readonly HomeAssistantTokenStore? tokenStore;
    private string? discoveredClimateEntityId;
    private string? discoveredWeatherEntityId;
    private readonly SemaphoreSlim locationGate = new(1, 1);
    private HomeAssistantCoordinates? cachedInstallationCoordinates;
    private DateTimeOffset nextInstallationCoordinateAttemptAt;

    public HomeAssistantClient(HttpClient httpClient, IOptionsMonitor<HomeAssistantOptions> options, ILogger<HomeAssistantClient> logger, HomeAssistantTokenStore? tokenStore = null)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        this.tokenStore = tokenStore;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(EffectiveAccessToken);

    // The environment/config token always wins; the website-entered token only fills the gap.
    private string? EffectiveAccessToken =>
        !string.IsNullOrWhiteSpace(options.CurrentValue.AccessToken)
            ? options.CurrentValue.AccessToken
            : tokenStore?.Token;

    public async Task<ThermostatReading?> GetDiningRoomClimateAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var configuredEntityId = options.CurrentValue.EntityId;
        if (!string.IsNullOrWhiteSpace(configuredEntityId))
        {
            // A configured entity is an explicit real-device boundary. Never silently bind the
            // defender to another thermostat when this entity is missing or unavailable.
            return await TryGetClimateStateAsync(configuredEntityId.Trim(), cancellationToken);
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

        WeatherReading? conditionOnlyReading = null;
        var configuredWeatherEntityId = options.CurrentValue.WeatherEntityId;
        if (!string.IsNullOrWhiteSpace(configuredWeatherEntityId))
        {
            try
            {
                var configuredReading = await TryGetWeatherStateAsync(configuredWeatherEntityId.Trim(), cancellationToken);
                if (IsUsableWeatherReading(configuredReading))
                {
                    return configuredReading;
                }

                conditionOnlyReading = configuredReading;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Configured Home Assistant weather entity {EntityId} timed out.", configuredWeatherEntityId.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Configured Home Assistant weather entity {EntityId} is unavailable.", configuredWeatherEntityId.Trim());
            }
        }

        try
        {
            var entityId = await DiscoverWeatherEntityIdAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(entityId)
                && !string.Equals(entityId, configuredWeatherEntityId?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var discoveredReading = await TryGetWeatherStateAsync(entityId, cancellationToken);
                if (IsUsableWeatherReading(discoveredReading))
                {
                    return discoveredReading;
                }

                conditionOnlyReading ??= discoveredReading;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Home Assistant weather discovery timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Home Assistant weather discovery is unavailable.");
        }

        try
        {
            var sensorReading = await TryGetOutdoorTemperatureSensorAsync(cancellationToken);
            return IsUsableWeatherReading(sensorReading) ? sensorReading : conditionOnlyReading;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Configured Home Assistant outdoor-temperature sensor timed out.");
            return conditionOnlyReading;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Configured Home Assistant outdoor-temperature sensor is unavailable.");
            return conditionOnlyReading;
        }
    }

    /// <summary>
    /// Reads and caches the real Home Assistant installation coordinates. These are used only to
    /// locate the optional Open-Meteo weather fallback; the Home Assistant access token remains on
    /// this client's requests and is never passed to the external weather client.
    /// </summary>
    public async Task<HomeAssistantCoordinates?> GetInstallationCoordinatesAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        if (cachedInstallationCoordinates is not null)
        {
            return cachedInstallationCoordinates;
        }

        if (DateTimeOffset.UtcNow < nextInstallationCoordinateAttemptAt)
        {
            return null;
        }

        await locationGate.WaitAsync(cancellationToken);
        try
        {
            if (cachedInstallationCoordinates is not null)
            {
                return cachedInstallationCoordinates;
            }

            if (DateTimeOffset.UtcNow < nextInstallationCoordinateAttemptAt)
            {
                return null;
            }

            using var response = await SendAsync(HttpMethod.Get, "api/config", null, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var latitude = root.TryGetProperty("latitude", out var latitudeElement) ? TryGetDouble(latitudeElement) : null;
            var longitude = root.TryGetProperty("longitude", out var longitudeElement) ? TryGetDouble(longitudeElement) : null;
            if (latitude is not { } lat || longitude is not { } lon
                || !double.IsFinite(lat) || !double.IsFinite(lon)
                || lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                logger.LogWarning("Home Assistant /api/config did not return valid installation coordinates.");
                nextInstallationCoordinateAttemptAt = DateTimeOffset.UtcNow.AddMinutes(10);
                return null;
            }

            cachedInstallationCoordinates = new HomeAssistantCoordinates(lat, lon);
            return cachedInstallationCoordinates;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reading Home Assistant installation coordinates timed out.");
            nextInstallationCoordinateAttemptAt = DateTimeOffset.UtcNow.AddMinutes(10);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read installation coordinates from Home Assistant /api/config.");
            nextInstallationCoordinateAttemptAt = DateTimeOffset.UtcNow.AddMinutes(10);
            return null;
        }
        finally
        {
            locationGate.Release();
        }
    }

    /// <summary>
    /// Fetches the hourly forecast (daily fallback) for a weather entity via the
    /// weather.get_forecasts service. Returns null on any failure — the store schedules a
    /// retry; a broken forecast must never break the five-second cycle.
    /// </summary>
    public async Task<WeatherForecastReading?> GetWeatherForecastAsync(string entityId, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        var trimmed = entityId.Trim();
        var hourly = await TryGetForecastAsync(trimmed, "hourly", cancellationToken);
        if (hourly is { Entries.Count: > 0 })
        {
            return hourly;
        }

        return await TryGetForecastAsync(trimmed, "daily", cancellationToken);
    }

    private async Task<WeatherForecastReading?> TryGetForecastAsync(string entityId, string forecastType, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["entity_id"] = entityId,
                ["type"] = forecastType,
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await SendAsync(HttpMethod.Post, "api/services/weather/get_forecasts?return_response", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Forecast fetch ({ForecastType}) for {EntityId} returned HTTP {StatusCode}.", forecastType, entityId, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("service_response", out var serviceResponse)
                || !serviceResponse.TryGetProperty(entityId, out var entityResponse)
                || !entityResponse.TryGetProperty("forecast", out var forecastElement)
                || forecastElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<ForecastEntry>();
            foreach (var item in forecastElement.EnumerateArray())
            {
                if (!item.TryGetProperty("datetime", out var datetimeElement)
                    || datetimeElement.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(datetimeElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                {
                    continue;
                }

                double? temperature = item.TryGetProperty("temperature", out var temperatureElement)
                    && temperatureElement.ValueKind == JsonValueKind.Number
                        ? temperatureElement.GetDouble()
                        : null;
                var condition = item.TryGetProperty("condition", out var conditionElement)
                    && conditionElement.ValueKind == JsonValueKind.String
                        ? conditionElement.GetString()
                        : null;

                entries.Add(new ForecastEntry(timestamp, temperature, condition));
            }

            return entries.Count > 0 ? new WeatherForecastReading(entityId, forecastType, entries) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Forecast fetch ({ForecastType}) for {EntityId} failed.", forecastType, entityId);
            return null;
        }
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

    /// <summary>Reads an explicit list of HA entities and reports which are "active" (on/home/detected/
    /// occupied/motion/numeric&gt;0), reusing the same truthiness oracle as the front-door detector.</summary>
    public async Task<IReadOnlyList<EntityActivation>> GetActiveEntitiesAsync(string configuredEntityIds, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Array.Empty<EntityActivation>();
        }

        var configured = SplitEntityIds(configuredEntityIds).ToArray();
        if (configured.Length == 0)
        {
            return Array.Empty<EntityActivation>();
        }

        var readings = new List<EntityActivation>();
        foreach (var entityId in configured)
        {
            var reading = await TryGetEntityActivationAsync(entityId, cancellationToken);
            if (reading is not null)
            {
                readings.Add(reading);
            }
        }

        return readings;
    }

    /// <summary>Resolves the adjustment-statistics context: whether the tracked person is home and whether
    /// any master-bedroom trigger is active. Empty config returns "not configured" so stats can say so.</summary>
    public async Task<TrackedContextReading> GetTrackedContextAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var personConfigured = !string.IsNullOrWhiteSpace(current.TrackedPersonEntityIds);
        var bedroomConfigured = !string.IsNullOrWhiteSpace(current.MasterBedroomEntityIds);

        var personActivations = personConfigured
            ? await GetActiveEntitiesAsync(current.TrackedPersonEntityIds, cancellationToken)
            : Array.Empty<EntityActivation>();
        var bedroomActivations = bedroomConfigured
            ? await GetActiveEntitiesAsync(current.MasterBedroomEntityIds, cancellationToken)
            : Array.Empty<EntityActivation>();

        return new TrackedContextReading(
            string.IsNullOrWhiteSpace(current.TrackedPersonLabel) ? "Tracked person" : current.TrackedPersonLabel.Trim(),
            personConfigured,
            personActivations.Any(activation => activation.Active),
            bedroomConfigured,
            bedroomActivations.Any(activation => activation.Active));
    }

    private async Task<EntityActivation?> TryGetEntityActivationAsync(string entityId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Home Assistant entity {EntityId} was not found for adjustment-statistics context", entityId);
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var state = TryGetState(root);
        return new EntityActivation(
            GetEntityId(root),
            TryGetAttributeString(root, "friendly_name") ?? entityId,
            state,
            IsDetectedState(state));
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

    /// <summary>
    /// Lightweight single-entity read of the configured Alectra power sensor, normalized to kW. Used by
    /// the cost tracker every cycle; returns null when the sensor is unconfigured, missing, or non-numeric.
    /// </summary>
    public async Task<double?> GetUsagePowerKilowattsAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var reading = await TryGetUsageEntityAsync(options.CurrentValue.UsagePowerEntityId, cancellationToken);
        return NormalizePowerKilowatts(reading);
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

    /// <summary>
    /// Reads recorder history for the configured instantaneous power entity. Keeping this separate
    /// from <see cref="GetUsageHistoryAsync"/> prevents power charts from accidentally falling back
    /// to the cumulative energy entity when no entity id is supplied.
    /// </summary>
    public Task<UsageHistorySnapshot> GetUsagePowerHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var entityId = options.CurrentValue.UsagePowerEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new InvalidOperationException("Usage power history entity is not configured.");
        }

        return GetUsageHistoryAsync(entityId.Trim(), from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<ClimateHistorySample>> GetClimateHistoryAsync(
        string entityId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Home Assistant token is not configured.");
        }

        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new InvalidOperationException("Climate entity is not configured.");
        }

        if (to <= from)
        {
            throw new InvalidOperationException("History end time must be after start time.");
        }

        var path = $"api/history/period/{Uri.EscapeDataString(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
            $"?filter_entity_id={Uri.EscapeDataString(entityId.Trim())}" +
            $"&end_time={Uri.EscapeDataString(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}";
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseClimateHistory(document.RootElement);
    }

    private static IReadOnlyList<ClimateHistorySample> ParseClimateHistory(JsonElement root)
    {
        var samples = new List<ClimateHistorySample>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return samples;
        }

        foreach (var series in root.EnumerateArray())
        {
            if (series.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in series.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestamp = TryGetTimestamp(entry, "last_changed") ?? TryGetTimestamp(entry, "last_updated");
                if (timestamp is null)
                {
                    continue;
                }

                var hvacMode = entry.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.String
                    ? stateProp.GetString()
                    : null;

                string? hvacAction = null;
                if (entry.TryGetProperty("attributes", out var attrs)
                    && attrs.ValueKind == JsonValueKind.Object
                    && attrs.TryGetProperty("hvac_action", out var actionProp)
                    && actionProp.ValueKind == JsonValueKind.String)
                {
                    hvacAction = actionProp.GetString();
                }

                string? contextUserId = null;
                if (entry.TryGetProperty("context", out var contextElement)
                    && contextElement.ValueKind == JsonValueKind.Object
                    && contextElement.TryGetProperty("user_id", out var userProp)
                    && userProp.ValueKind == JsonValueKind.String)
                {
                    contextUserId = userProp.GetString();
                }

                samples.Add(new ClimateHistorySample(
                    timestamp.Value,
                    TryGetAttributeDouble(entry, "temperature"),
                    TryGetAttributeDouble(entry, "current_temperature"),
                    hvacMode,
                    hvacAction,
                    contextUserId));
            }
        }

        return samples;
    }

    public async Task SetCoolingAsync(string entityId, double setPointCelsius, CancellationToken cancellationToken)
    {
        // Put the intended target in place before enabling cooling. If Home Assistant rejects or
        // cancels either half of this two-call transition, the safer partial outcome is an OFF unit
        // with a prepared setpoint, never COOL running against a stale wall target.
        await SetTemperatureAsync(entityId, setPointCelsius, cancellationToken);
        await SetHvacModeAsync(entityId, "cool", cancellationToken);
    }

    public async Task SetTemperatureAsync(string entityId, double setPointCelsius, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Best-effort Home Assistant notification used by the Desired-State Enforcer. Does nothing when no
    /// notify service is configured, so a missing service name never turns into a per-cycle error.
    /// </summary>
    public async Task SendNotificationAsync(string? service, string title, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return;
        }

        var serviceName = service.Trim();
        if (serviceName.StartsWith("notify.", StringComparison.OrdinalIgnoreCase))
        {
            serviceName = serviceName["notify.".Length..];
        }

        await CallServiceAsync("notify", serviceName, new
        {
            title,
            message
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
        var reading = ParseClimateState(document.RootElement, entityId);
        if (reading is null)
        {
            logger.LogWarning(
                "Home Assistant climate entity {EntityId} returned an unavailable/unknown state or incomplete temperature attributes",
                entityId);
        }

        return reading;
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

    /// <summary>Generic service call for the hub kiosk (cast/volume). Throws on non-success.</summary>
    public Task CallHomeAssistantServiceAsync(string domain, string service, object payload, CancellationToken cancellationToken)
        => CallServiceAsync(domain, service, payload, cancellationToken);

    /// <summary>Raw state + selected attributes for any entity; null when missing/unreachable.</summary>
    public async Task<(string State, string? AppName, double? VolumeLevel)?> GetMediaPlayerStateAsync(string entityId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId.Trim())}", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            var attrs = root.TryGetProperty("attributes", out var a) ? a : default;
            string? app = attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("app_name", out var ap) ? ap.GetString() : null;
            double? vol = attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("volume_level", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            return (root.GetProperty("state").GetString() ?? "unknown", app, vol);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pushes a state-machine-only sensor into Home Assistant (POST api/states). These entities feed
    /// the kiosk dashboard; they vanish on HA restart, so the kiosk worker re-posts them every cycle.
    /// </summary>
    public async Task PushSensorStateAsync(string entityId, string state, object attributes, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { state, attributes });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, $"api/states/{Uri.EscapeDataString(entityId)}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EffectiveAccessToken);
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

    private static ThermostatReading? ParseClimateState(JsonElement root, string entityId)
    {
        var hvacMode = root.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString()?.Trim()
            : null;
        var currentTemperature = TryGetAttributeDouble(root, "current_temperature");
        var setPoint = TryGetAttributeDouble(root, "temperature");
        var modeOff = string.Equals(hvacMode, "off", StringComparison.OrdinalIgnoreCase);
        // Several real climate integrations omit the target or report 0 while OFF. The rest of
        // the defender already treats 0 as mode-off telemetry noise, so preserve the valid live
        // OFF reading with that sentinel instead of declaring the entity unavailable.
        var normalizedSetPoint = setPoint ?? (modeOff ? 0.0 : double.NaN);
        var setPointIsPlausible = modeOff
            ? normalizedSetPoint == 0.0 || (normalizedSetPoint > 5.0 && normalizedSetPoint <= 40.0)
            : normalizedSetPoint > 5.0 && normalizedSetPoint <= 40.0;
        if (string.IsNullOrWhiteSpace(hvacMode)
            || string.Equals(hvacMode, "unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hvacMode, "unknown", StringComparison.OrdinalIgnoreCase)
            || currentTemperature is not { } currentValue
            || !double.IsFinite(currentValue)
            || !double.IsFinite(normalizedSetPoint)
            || currentValue <= 5.0
            || currentValue > 50.0
            || !setPointIsPlausible)
        {
            return null;
        }

        return new ThermostatReading(
            entityId,
            currentValue,
            normalizedSetPoint,
            hvacMode,
            TryGetAttributeString(root, "hvac_action") ?? hvacMode,
            TryGetAttributeString(root, "fan_mode"),
            TryGetAttributeStringArray(root, "fan_modes"),
            TryGetContext(root),
            TryGetAttributeDouble(root, "min_temp"),
            TryGetAttributeDouble(root, "max_temp"));
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

    private static bool IsUsableWeatherReading(WeatherReading? reading) =>
        reading?.OutdoorTemperatureCelsius is not null
        && !string.Equals(reading.Condition, "unavailable", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(reading.Condition, "unknown", StringComparison.OrdinalIgnoreCase);

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
