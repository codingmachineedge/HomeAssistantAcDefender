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
    private string? discoveredEntityId;

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

        var entityId = await DiscoverEntityIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        return await TryGetClimateStateAsync(entityId, cancellationToken);
    }

    public async Task SetCoolingAsync(string entityId, double setPointCelsius, CancellationToken cancellationToken)
    {
        await CallServiceAsync("climate", "set_hvac_mode", new
        {
            entity_id = entityId,
            hvac_mode = "cool"
        }, cancellationToken);

        await CallServiceAsync("climate", "set_temperature", new
        {
            entity_id = entityId,
            temperature = setPointCelsius
        }, cancellationToken);
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

    private async Task<string?> DiscoverEntityIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(discoveredEntityId))
        {
            return discoveredEntityId;
        }

        using var response = await SendAsync(HttpMethod.Get, "api/states", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (var entity in document.RootElement.EnumerateArray())
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
                discoveredEntityId = entityId;
                logger.LogInformation("Discovered dining room climate entity {EntityId}", entityId);
                return discoveredEntityId;
            }
        }

        return null;
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
            TryGetAttributeString(root, "hvac_action") ?? hvacMode);
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

    private static double? TryGetAttributeDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty("attributes", out var attributes)
            || !attributes.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var numeric) => numeric,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
