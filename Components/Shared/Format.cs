using System.Text;
using HomeAssistantAcDefender.Models;
using MudBlazor;

namespace HomeAssistantAcDefender.Components.Shared;

/// <summary>Shared display formatting used across the redesigned pages (temps, Toronto time, usage).</summary>
public static class Format
{
    private static readonly TimeZoneInfo Toronto = ResolveToronto();

    public static string Temp(double? value) => value is null ? "--" : $"{value.Value:0.0} C";

    /// <summary>Temperature with a degree sign and no space ("23.8°C"), the SCP-redesign house style.</summary>
    public static string TempDeg(double? value) => value is null ? "--" : $"{value.Value:0.0}°C";

    /// <summary>Coarse "time ago" phrasing for commit/log lists ("just now", "2h ago", "yesterday", "3 days ago").</summary>
    public static string RelativeTime(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromMinutes(60))
        {
            var m = Math.Max(1, (int)delta.TotalMinutes);
            return $"{m}m ago";
        }

        if (delta < TimeSpan.FromHours(24))
        {
            var h = Math.Max(1, (int)delta.TotalHours);
            return $"{h}h ago";
        }

        var days = (int)delta.TotalDays;
        return days switch
        {
            <= 1 => "yesterday",
            < 30 => $"{days} days ago",
            < 60 => "last month",
            < 365 => $"{days / 30} months ago",
            _ => $"{days / 365} year{(days / 365 == 1 ? "" : "s")} ago"
        };
    }

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

    /// <summary>Lower-kebab slug matching the wiki article filenames (e.g. "HVAC Alibi" → "hvac-alibi").</summary>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var prevDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }

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
