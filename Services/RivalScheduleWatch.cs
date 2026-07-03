using System.Globalization;
using HomeAssistantAcDefender.Options;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// One parsed block of the rival AC app's temperature schedule. The two setpoints are the pair shown
/// in the vendor app for the block (orange = lower number, blue = upper number). <see cref="Days"/>
/// is the set of weekdays the block STARTS on; a block runs from its start time until the next
/// applicable block starts (wrapping past midnight), exactly like the schedule list in the AC app.
/// </summary>
public sealed record RivalScheduleBlock(
    string Name,
    TimeSpan Start,
    double LowSetPointCelsius,
    double HighSetPointCelsius,
    IReadOnlySet<DayOfWeek> Days);

/// <summary>
/// Pure helper for the rival AC-app schedule (the "Temperature schedules" tab in the vendor app —
/// e.g. SLEEP 21.5/23 at 12:00 a.m., DEEP SLEEP 23.5/26 at 2:00 a.m., GOOD MORNING 22.5/24 at
/// 9:00 a.m.). AC Defender does NOT follow this schedule — it is the known plan of the OTHER side,
/// kept so a scheduled push (for example DEEP SLEEP stepping the wall toward 26 C at 2:00 a.m. so a
/// sleeping room drifts to ~25 C) is recognized as a machine change instead of a human wall touch,
/// and answered back toward the user's own target ("my temp") without human-style quiet waits.
/// The vendor app also has a "Fan schedules" tab; its blocks are accepted in configuration as a
/// placeholder but are not enforced yet.
/// </summary>
public static class RivalScheduleWatch
{
    /// <summary>Parses configured blocks, skipping invalid times/setpoints, ordered by start time.</summary>
    public static IReadOnlyList<RivalScheduleBlock> Parse(IEnumerable<RivalScheduleBlockOptions>? blocks)
    {
        if (blocks is null)
        {
            return [];
        }

        var parsed = new List<RivalScheduleBlock>();
        foreach (var block in blocks)
        {
            if (block is null
                || !TimeSpan.TryParseExact((block.Start ?? string.Empty).Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out var start))
            {
                continue;
            }

            // Mode-off style nonsense values (a real thermostat setpoint is never <= 5 C here).
            if (block.LowSetPointCelsius <= 5.0 || block.HighSetPointCelsius <= 5.0)
            {
                continue;
            }

            var low = Math.Min(block.LowSetPointCelsius, block.HighSetPointCelsius);
            var high = Math.Max(block.LowSetPointCelsius, block.HighSetPointCelsius);
            var name = string.IsNullOrWhiteSpace(block.Name) ? $"Block {start:hh\\:mm}" : block.Name.Trim();
            parsed.Add(new RivalScheduleBlock(name, start, Math.Round(low, 1), Math.Round(high, 1), ParseDays(block.Days)));
        }

        return parsed.OrderBy(item => item.Start).ToArray();
    }

    /// <summary>
    /// The block currently in force at <paramref name="localTime"/>: the latest applicable start at
    /// or before now, looking back up to a week so a block wraps past midnight and across skipped days.
    /// </summary>
    public static RivalScheduleBlock? GetActiveBlock(IReadOnlyList<RivalScheduleBlock> blocks, DateTime localTime)
    {
        for (var daysBack = 0; daysBack <= 7; daysBack++)
        {
            var date = localTime.Date.AddDays(-daysBack);
            RivalScheduleBlock? best = null;
            var bestStart = DateTime.MinValue;
            foreach (var block in blocks)
            {
                if (!block.Days.Contains(date.DayOfWeek))
                {
                    continue;
                }

                var start = date + block.Start;
                if (start <= localTime && start > bestStart)
                {
                    best = block;
                    bestStart = start;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The next block start strictly after <paramref name="localTime"/>, up to a week out.</summary>
    public static (RivalScheduleBlock Block, DateTime StartsAt)? GetNextStart(IReadOnlyList<RivalScheduleBlock> blocks, DateTime localTime)
    {
        for (var daysAhead = 0; daysAhead <= 7; daysAhead++)
        {
            var date = localTime.Date.AddDays(daysAhead);
            RivalScheduleBlock? best = null;
            var bestStart = DateTime.MaxValue;
            foreach (var block in blocks)
            {
                if (!block.Days.Contains(date.DayOfWeek))
                {
                    continue;
                }

                var start = date + block.Start;
                if (start > localTime && start < bestStart)
                {
                    best = block;
                    bestStart = start;
                }
            }

            if (best is not null)
            {
                return (best, bestStart);
            }
        }

        return null;
    }

    /// <summary>True when a wall setpoint equals either of the block's scheduled numbers within tolerance.</summary>
    public static bool MatchesSetpoint(RivalScheduleBlock block, double setPointCelsius, double toleranceCelsius)
    {
        // Small epsilon so binary floating point (e.g. 26.3 - 26.0) cannot push an exactly-on-tolerance
        // match just over the line.
        var tolerance = Math.Max(0.05, toleranceCelsius) + 0.001;
        return Math.Abs(setPointCelsius - block.LowSetPointCelsius) <= tolerance
            || Math.Abs(setPointCelsius - block.HighSetPointCelsius) <= tolerance;
    }

    private static IReadOnlySet<DayOfWeek> ParseDays(string? days)
    {
        var result = new HashSet<DayOfWeek>();
        foreach (var token in (days ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                if (string.Equals(day.ToString()[..3], token, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(day);
                }
            }
        }

        // Blank/invalid day lists mean "every day", matching ScheduleEntry's default.
        if (result.Count == 0)
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                result.Add(day);
            }
        }

        return result;
    }
}
