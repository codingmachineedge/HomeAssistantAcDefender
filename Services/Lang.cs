namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Tiny language layer for the three-way display mode: "en" (English), "yue" (口語廣東話 —
/// colloquial written Cantonese, Traditional script), and "both" (English with the Cantonese
/// underneath). The mode is chosen from the switcher in the top bar on every page, persisted
/// per browser, and cascaded to every component as the "LangMode" cascading value.
///
/// Translations are looked up by the EXACT English source string, so pages and the guard
/// catalog keep one source of truth in English and the Cantonese lives in the Yue* tables.
/// A missing translation falls back to English — never a blank.
///
/// Live SERVER-GENERATED sentences (next-action messages, event log lines, statuses computed
/// inside the store) are data, not chrome — they stay English by design.
/// </summary>
public static class Lang
{
    public const string English = "en";
    public const string Cantonese = "yue";
    public const string Both = "both";

    /// <summary>Renders a source string for the given mode. In "both" mode the Cantonese is
    /// appended on a new line (containers use white-space: pre-line so it stacks).</summary>
    public static string T(string? mode, string? english)
    {
        if (string.IsNullOrEmpty(english))
        {
            return english ?? "";
        }

        var yue = Yue.Lookup(english);
        return mode switch
        {
            Cantonese => yue ?? english,
            Both => yue is null || string.Equals(yue, english, StringComparison.Ordinal)
                ? english
                : english + "\n" + yue,
            _ => english,
        };
    }

    /// <summary>Like T, but single-line for attribute contexts (input labels, tooltips) where a
    /// newline cannot render: "both" joins with " · " instead of stacking.</summary>
    public static string TInline(string? mode, string? english)
    {
        if (string.IsNullOrEmpty(english))
        {
            return english ?? "";
        }

        var yue = Yue.Lookup(english);
        return mode switch
        {
            Cantonese => yue ?? english,
            Both => yue is null || string.Equals(yue, english, StringComparison.Ordinal)
                ? english
                : english + " · " + yue,
            _ => english,
        };
    }

    /// <summary>Normalizes a persisted/raw mode value to one of the three known modes.</summary>
    public static string NormalizeMode(string? mode) => mode switch
    {
        Cantonese => Cantonese,
        Both => Both,
        _ => English,
    };
}

/// <summary>
/// The English → Cantonese dictionary, assembled from the per-area tables (chrome, guards,
/// settings, pages). Each table is a static class with an AddTo(dictionary) method so the
/// translation files stay reviewable per area.
/// </summary>
public static class Yue
{
    private static readonly Dictionary<string, string> Map = Build();

    public static string? Lookup(string english) =>
        Map.TryGetValue(english, out var yue) ? yue : null;

    public static int Count => Map.Count;

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        YueCommon.AddTo(map);
        YueGuards.AddTo(map);
        YueSettings.AddTo(map);
        YuePages.AddTo(map);
        return map;
    }
}
