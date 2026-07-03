namespace HomeAssistantAcDefender.Services;

/// <summary>Ontario time-of-use period. Higher value == more expensive.</summary>
public enum TouPeriod
{
    OffPeak = 0,
    MidPeak = 1,
    OnPeak = 2,
}

/// <summary>
/// The configurable Alectra Utilities time-of-use rate table (energy commodity portion, ¢/kWh).
/// Defaults are the verified Alectra/OEB commodity rates; they can be overridden from configuration
/// when the OEB updates the rates. <see cref="AllInMultiplier"/> and <see cref="AllInAdderCentsPerKwh"/>
/// let a caller approximate an all-in bill (delivery, regulatory, Ontario Electricity Rebate, HST),
/// but default to 1.0 / 0.0 so the tracker reports the commodity cost only, as requested.
/// </summary>
public sealed record TouRateTable(
    double OnPeakCentsPerKwh,
    double MidPeakCentsPerKwh,
    double OffPeakCentsPerKwh,
    double AllInMultiplier,
    double AllInAdderCentsPerKwh)
{
    // Verified from alectrautilities.com — energy commodity portion, ¢/kWh (OEB TOU prices).
    public const double DefaultOnPeakCentsPerKwh = 20.3;
    public const double DefaultMidPeakCentsPerKwh = 15.7;
    public const double DefaultOffPeakCentsPerKwh = 9.8;

    public static TouRateTable Default { get; } = new(
        DefaultOnPeakCentsPerKwh,
        DefaultMidPeakCentsPerKwh,
        DefaultOffPeakCentsPerKwh,
        1.0,
        0.0);

    /// <summary>Commodity ¢/kWh for the period, before the optional all-in multiplier/adder.</summary>
    public double CommodityCentsPerKwh(TouPeriod period) => period switch
    {
        TouPeriod.OnPeak => OnPeakCentsPerKwh,
        TouPeriod.MidPeak => MidPeakCentsPerKwh,
        _ => OffPeakCentsPerKwh,
    };

    /// <summary>Effective ¢/kWh the tracker bills for the period: commodity × multiplier + adder.</summary>
    public double EffectiveCentsPerKwh(TouPeriod period) =>
        CommodityCentsPerKwh(period) * AllInMultiplier + AllInAdderCentsPerKwh;
}

/// <summary>
/// Ontario/Alectra time-of-use schedule. Given a LOCAL wall-clock time it returns the TOU period,
/// honouring summer/winter schedules, weekends, and Ontario statutory holidays (all off-peak).
///
/// "Assume the most expensive rate when possible": whenever the period is determinable we use the
/// exact TOU period, but when the time is unknown/ambiguous (e.g. a missing timestamp) we fall back
/// to On-Peak — the most expensive rate — so cost is never under-estimated. The fallback is explicit
/// via the <c>usedFallback</c> out parameter.
/// </summary>
public static class AlectraTouSchedule
{
    // TOU hour boundaries (24h local). Off-peak covers 19:00–07:00 in both seasons.
    private const int OffPeakEndHour = 7;   // 07:00 — off-peak (overnight) ends
    private const int MorningEndHour = 11;  // 07:00–11:00 shoulder
    private const int MiddayEndHour = 17;   // 11:00–17:00 middle band
    private const int EveningEndHour = 19;  // 17:00–19:00 shoulder, then off-peak resumes

    /// <summary>True for May 1 – Oct 31 (summer schedule); false for Nov 1 – Apr 30 (winter).</summary>
    public static bool IsSummer(DateTime localTime) => localTime.Month is >= 5 and <= 10;

    /// <summary>
    /// Determine the TOU period for a known local time. Weekends and Ontario statutory holidays are
    /// off-peak all day, year-round.
    /// </summary>
    public static TouPeriod GetPeriod(DateTime localTime)
    {
        if (localTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            || IsOntarioStatutoryHoliday(DateOnly.FromDateTime(localTime)))
        {
            return TouPeriod.OffPeak;
        }

        var hour = localTime.Hour;
        if (hour < OffPeakEndHour || hour >= EveningEndHour)
        {
            return TouPeriod.OffPeak;
        }

        // Weekday daytime bands differ by season.
        if (IsSummer(localTime))
        {
            // Summer: mid 07–11, on 11–17, mid 17–19.
            return hour < MorningEndHour ? TouPeriod.MidPeak
                : hour < MiddayEndHour ? TouPeriod.OnPeak
                : TouPeriod.MidPeak;
        }

        // Winter: on 07–11, mid 11–17, on 17–19.
        return hour < MorningEndHour ? TouPeriod.OnPeak
            : hour < MiddayEndHour ? TouPeriod.MidPeak
            : TouPeriod.OnPeak;
    }

    /// <summary>
    /// Determine the TOU period for a possibly-unknown local time. A null time is ambiguous, so it
    /// falls back to the most expensive period (On-Peak) and sets <paramref name="usedFallback"/>.
    /// </summary>
    public static TouPeriod GetPeriod(DateTime? localTime, out bool usedFallback)
    {
        if (localTime is not { } time)
        {
            usedFallback = true;
            return TouPeriod.OnPeak;
        }

        usedFallback = false;
        return GetPeriod(time);
    }

    /// <summary>Human label for a TOU period (matches the Alectra Hui wording).</summary>
    public static string PeriodLabel(TouPeriod period) => period switch
    {
        TouPeriod.OnPeak => "On-Peak",
        TouPeriod.MidPeak => "Mid-Peak",
        _ => "Off-Peak",
    };

    /// <summary>
    /// True if the date is an Ontario statutory holiday observed as off-peak by the OEB/Alectra:
    /// New Year's, Family Day, Good Friday, Victoria Day, Canada Day, Civic Holiday, Labour Day,
    /// Thanksgiving, Christmas, and Boxing Day. Fixed-date holidays that fall on a weekend are
    /// observed on the next weekday (the "bumped" weekday is the off-peak day).
    /// </summary>
    public static bool IsOntarioStatutoryHoliday(DateOnly date) =>
        ObservedHolidays(date.Year).Contains(date);

    private static HashSet<DateOnly> ObservedHolidays(int year)
    {
        var observed = new HashSet<DateOnly>();

        // Floating Monday/Friday holidays — always land on a weekday, so no observance shift.
        observed.Add(NthWeekdayOfMonth(year, 2, DayOfWeek.Monday, 3));   // Family Day: 3rd Mon Feb
        observed.Add(EasterSunday(year).AddDays(-2));                    // Good Friday
        observed.Add(VictoriaDay(year));                                 // Mon before May 25
        observed.Add(NthWeekdayOfMonth(year, 8, DayOfWeek.Monday, 1));   // Civic Holiday: 1st Mon Aug
        observed.Add(NthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 1));   // Labour Day: 1st Mon Sep
        observed.Add(NthWeekdayOfMonth(year, 10, DayOfWeek.Monday, 2));  // Thanksgiving: 2nd Mon Oct

        // Fixed-date holidays — if on a weekend, the following weekday is the off-peak day.
        AddObserved(observed, new DateOnly(year, 1, 1));   // New Year's Day
        AddObserved(observed, new DateOnly(year, 7, 1));   // Canada Day
        AddObserved(observed, new DateOnly(year, 12, 25));  // Christmas Day
        AddObserved(observed, new DateOnly(year, 12, 26));  // Boxing Day

        return observed;
    }

    private static void AddObserved(HashSet<DateOnly> set, DateOnly date)
    {
        // Weekends are already off-peak, but the OEB rolls a weekend stat holiday to the next
        // weekday, so that weekday also becomes off-peak. Skip past weekends and any weekday that is
        // already an observed holiday (e.g. Boxing Day when Christmas has already bumped onto it).
        var observed = date;
        while (observed.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || set.Contains(observed))
        {
            observed = observed.AddDays(1);
        }

        // Keep the original date too when it is a weekday (it is genuinely a holiday); the roll-forward
        // only adds the extra off-peak weekday when the holiday itself is on a weekend.
        if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        {
            set.Add(date);
        }

        set.Add(observed);
    }

    private static DateOnly NthWeekdayOfMonth(int year, int month, DayOfWeek weekday, int occurrence)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (occurrence - 1) * 7);
    }

    private static DateOnly VictoriaDay(int year)
    {
        // The Monday on or before May 24 (i.e. the Monday preceding May 25).
        var may25 = new DateOnly(year, 5, 25);
        var offset = ((int)may25.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return may25.AddDays(-offset == 0 ? -7 : -offset);
    }

    private static DateOnly EasterSunday(int year)
    {
        // Anonymous Gregorian algorithm (Meeus/Jones/Butcher).
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }
}
