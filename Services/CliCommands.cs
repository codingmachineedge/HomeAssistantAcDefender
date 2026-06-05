using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HomeAssistantAcDefender.Models;
using Microsoft.Extensions.Configuration;

namespace HomeAssistantAcDefender.Services;

public static class CliCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<bool> TryRunAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length == 0)
        {
            return false;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var offset = 1;
        if (command == "usage" && args.Length > 1)
        {
            command = $"usage-{args[1].Trim().ToLowerInvariant()}";
            offset = 2;
        }

        if (command is "--help" or "-h" or "help")
        {
            PrintHelp();
            return true;
        }

        if (command is not ("usage-live" or "usage-history"))
        {
            return false;
        }

        try
        {
            var options = CliUsageOptions.Create(args[offset..], configuration);
            using var httpClient = CreateClient(options);
            if (command == "usage-live")
            {
                await PrintLiveUsageAsync(httpClient, options);
            }
            else
            {
                await PrintUsageHistoryAsync(httpClient, options);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return true;
        }
    }

    private static async Task PrintLiveUsageAsync(HttpClient httpClient, CliUsageOptions options)
    {
        var snapshot = new UsageLiveSnapshot(
            await TryGetUsageEntityAsync(httpClient, options.PowerEntityId),
            await TryGetUsageEntityAsync(httpClient, options.EnergyEntityId),
            await TryGetUsageEntityAsync(httpClient, options.CostEntityId),
            await TryGetUsageEntityAsync(httpClient, options.CurrentBillEntityId),
            await TryGetUsageEntityAsync(httpClient, options.CurrentBillDueEntityId),
            await TryGetUsageEntityAsync(httpClient, options.CurrentBillStatusEntityId),
            true,
            DateTimeOffset.UtcNow);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return;
        }

        Console.WriteLine("Home Assistant live usage");
        PrintReading("Power", snapshot.Power);
        PrintReading("Energy", snapshot.Energy);
        PrintReading("Cost", snapshot.Cost);
        PrintReading("Current bill", snapshot.CurrentBill);
        PrintReading("Bill due", snapshot.CurrentBillDue);
        PrintReading("Bill status", snapshot.CurrentBillStatus);
    }

    private static async Task PrintUsageHistoryAsync(HttpClient httpClient, CliUsageOptions options)
    {
        var entityId = string.IsNullOrWhiteSpace(options.HistoryEntityId)
            ? options.EnergyEntityId
            : options.HistoryEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new InvalidOperationException("No history entity configured. Pass --entity sensor.name.");
        }

        var path = $"api/history/period/{Uri.EscapeDataString(options.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
            $"?filter_entity_id={Uri.EscapeDataString(entityId)}" +
            $"&end_time={Uri.EscapeDataString(options.To.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}";
        using var response = await httpClient.GetAsync(path);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var history = ParseHistory(document.RootElement, entityId, options.From, options.To);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(history, JsonOptions));
            return;
        }

        Console.WriteLine($"Home Assistant history for {history.EntityId}");
        Console.WriteLine($"Window: {history.From.LocalDateTime:yyyy-MM-dd HH:mm:ss} to {history.To.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Samples: {history.Count}");
        Console.WriteLine($"First: {FormatNumber(history.First, history.Unit)}");
        Console.WriteLine($"Last: {FormatNumber(history.Last, history.Unit)}");
        Console.WriteLine($"Min: {FormatNumber(history.Min, history.Unit)}");
        Console.WriteLine($"Max: {FormatNumber(history.Max, history.Unit)}");
        Console.WriteLine($"Delta: {FormatNumber(history.Delta, history.Unit)}");
    }

    private static HttpClient CreateClient(CliUsageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AccessToken))
        {
            throw new InvalidOperationException("HomeAssistant__AccessToken is required for CLI usage commands.");
        }

        var client = new HttpClient
        {
            BaseAddress = BuildBaseUri(options.BaseUrl)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<UsageEntityReading?> TryGetUsageEntityAsync(HttpClient httpClient, string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        using var response = await httpClient.GetAsync($"api/states/{Uri.EscapeDataString(entityId.Trim())}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return ParseUsageEntity(document.RootElement);
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

    private static UsageHistorySnapshot ParseHistory(JsonElement root, string entityId, DateTimeOffset from, DateTimeOffset to)
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

    private static void PrintReading(string label, UsageEntityReading? reading)
    {
        if (reading is null)
        {
            Console.WriteLine($"{label}: not configured or not found");
            return;
        }

        Console.WriteLine($"{label}: {FormatNumber(reading.Value, reading.Unit)} ({reading.EntityId}, {reading.State})");
    }

    private static string FormatNumber(double? value, string unit)
    {
        return value is null
            ? "--"
            : string.IsNullOrWhiteSpace(unit) ? $"{value.Value:0.###}" : $"{value.Value:0.###} {unit}";
    }

    private static Uri BuildBaseUri(string? baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? "http://homeassistant.local:8123" : baseUrl.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = $"http://{value}";
        }

        if (!value.EndsWith('/'))
        {
            value += "/";
        }

        return new Uri(value);
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

    private static double? TryParseStateDouble(JsonElement root)
    {
        if (!root.TryGetProperty("state", out var stateElement))
        {
            return null;
        }

        return stateElement.ValueKind switch
        {
            JsonValueKind.Number when stateElement.TryGetDouble(out var numeric) => numeric,
            JsonValueKind.String when double.TryParse(stateElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
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

    private static void PrintHelp()
    {
        Console.WriteLine("""
Home Assistant AC Defender CLI

Usage:
  dotnet run -- usage-live [--json]
  dotnet run -- usage-history [--entity sensor.name] [--hours 24] [--from 2026-06-05T00:00:00] [--to 2026-06-05T23:59:59] [--json]

Options:
  --base-url URL     Overrides HomeAssistant__BaseUrl.
  --token TOKEN      Overrides HomeAssistant__AccessToken.
  --power ENTITY     Overrides HomeAssistant__UsagePowerEntityId for usage-live.
  --energy ENTITY    Overrides HomeAssistant__UsageEnergyEntityId.
  --cost ENTITY      Overrides HomeAssistant__UsageCostEntityId for usage-live.
  --bill ENTITY      Overrides HomeAssistant__UsageCurrentBillEntityId for usage-live.
  --bill-due ENTITY  Overrides HomeAssistant__UsageCurrentBillDueEntityId.
  --bill-status ENTITY Overrides HomeAssistant__UsageCurrentBillStatusEntityId.
  --entity ENTITY    Entity used by usage-history. Defaults to UsageEnergyEntityId.
  --hours NUMBER     History window ending now. Defaults to 24.
""");
    }

    private sealed record CliUsageOptions(
        string BaseUrl,
        string? AccessToken,
        string PowerEntityId,
        string EnergyEntityId,
        string CostEntityId,
        string CurrentBillEntityId,
        string CurrentBillDueEntityId,
        string CurrentBillStatusEntityId,
        string? HistoryEntityId,
        DateTimeOffset From,
        DateTimeOffset To,
        bool Json)
    {
        public static CliUsageOptions Create(string[] args, IConfiguration configuration)
        {
            var now = DateTimeOffset.Now;
            var hours = TryGetDouble(args, "--hours") ?? 24;
            var to = TryGetDate(args, "--to") ?? now;
            var from = TryGetDate(args, "--from") ?? to.AddHours(-Math.Max(0.1, hours));
            return new CliUsageOptions(
                TryGetValue(args, "--base-url") ?? configuration["HomeAssistant:BaseUrl"] ?? "http://homeassistant.local:8123",
                TryGetValue(args, "--token") ?? configuration["HomeAssistant:AccessToken"],
                TryGetValue(args, "--power") ?? configuration["HomeAssistant:UsagePowerEntityId"] ?? "sensor.alectra_hui_current_power",
                TryGetValue(args, "--energy") ?? configuration["HomeAssistant:UsageEnergyEntityId"] ?? "sensor.alectra_hui_energy_today",
                TryGetValue(args, "--cost") ?? configuration["HomeAssistant:UsageCostEntityId"] ?? "sensor.alectra_hui_cost_today",
                TryGetValue(args, "--bill") ?? configuration["HomeAssistant:UsageCurrentBillEntityId"] ?? "sensor.alectra_hui_current_bill",
                TryGetValue(args, "--bill-due") ?? configuration["HomeAssistant:UsageCurrentBillDueEntityId"] ?? "sensor.alectra_hui_current_bill_due",
                TryGetValue(args, "--bill-status") ?? configuration["HomeAssistant:UsageCurrentBillStatusEntityId"] ?? "sensor.alectra_hui_current_bill_status",
                TryGetValue(args, "--entity"),
                from,
                to,
                args.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase)));
        }

        private static string? TryGetValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                if (args[i].StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i][(name.Length + 1)..];
                }
            }

            return null;
        }

        private static double? TryGetDouble(string[] args, string name)
        {
            return double.TryParse(TryGetValue(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static DateTimeOffset? TryGetDate(string[] args, string name)
        {
            return DateTimeOffset.TryParse(TryGetValue(args, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
                ? value
                : null;
        }
    }
}
