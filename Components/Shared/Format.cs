using HomeAssistantAcDefender.Models;
using MudBlazor;

namespace HomeAssistantAcDefender.Components.Shared;

/// <summary>Shared display formatting used across the redesigned pages (temps, Toronto time, usage).</summary>
public static class Format
{
    private static readonly TimeZoneInfo Toronto = ResolveToronto();

    public static string Temp(double? value) => value is null ? "--" : $"{value.Value:0.0} C";

    public static DateTimeOffset ToToronto(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Toronto);

    public static DateTimeOffset TorontoNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Toronto);

    public static string ShortTime(DateTimeOffset? value) => value is null ? "--" : ToToronto(value.Value).ToString("HH:mm:ss");

    public static string DateTimeText(DateTimeOffset value) => ToToronto(value).ToString("yyyy-MM-dd HH:mm:ss");

    public static string SensorValue(TemperatureSensorReading sensor) =>
        sensor.TemperatureCelsius is null ? sensor.State : $"{sensor.TemperatureCelsius.Value:0.0} C";

    public static string UsageValue(UsageEntityReading? reading)
    {
        if (reading is null)
        {
            return "--";
        }

        if (reading.Value is null)
        {
            return string.IsNullOrWhiteSpace(reading.State) ? "--" : reading.State;
        }

        return string.IsNullOrWhiteSpace(reading.Unit)
            ? $"{reading.Value.Value:0.###}"
            : $"{reading.Value.Value:0.###} {reading.Unit}";
    }

    public static string UsageHelp(UsageEntityReading? reading) =>
        reading is null
            ? "Usage entity is not configured or was not found."
            : $"{reading.Name} / {reading.EntityId}";

    public static string UsageHistoryValue(double? value, string? unit) =>
        value is null
            ? "--"
            : string.IsNullOrWhiteSpace(unit) ? $"{value.Value:0.###}" : $"{value.Value:0.###} {unit}";

    public static Severity EventSeverity(string level) => level.ToLowerInvariant() switch
    {
        "warning" => Severity.Warning,
        "error" => Severity.Error,
        _ => Severity.Info
    };

    /// <summary>Space-separated AND search across the supplied values (case-insensitive).</summary>
    public static bool MatchesSearch(string? search, params string?[] values)
    {
        var terms = (search ?? string.Empty)
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return terms.Length == 0
            || terms.All(term => values.Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static TimeZoneInfo ResolveToronto()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
