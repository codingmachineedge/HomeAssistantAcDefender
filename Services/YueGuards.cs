namespace HomeAssistantAcDefender.Services;

/// <summary>
/// 口語廣東話 — the guard catalog: every guard's Name, Summary, Watches, Logic, and Output
/// drawer texts plus live-card metric labels/helps. Keys are the EXACT English strings from
/// Guards/GuardCatalog.cs. Populated by the translation pass; missing entries fall back to
/// English automatically.
/// </summary>
public static class YueGuards
{
    public static void AddTo(Dictionary<string, string> map)
    {
        // Populated by the Cantonese translation pass (one entry per GuardCatalog string).
    }
}
