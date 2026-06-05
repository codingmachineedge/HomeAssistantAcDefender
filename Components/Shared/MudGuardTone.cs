using HomeAssistantAcDefender.Guards;
using MudBlazor;

namespace HomeAssistantAcDefender.Components.Shared;

/// <summary>Maps the MudBlazor-free <see cref="GuardTone"/> onto MudBlazor colours and severities.</summary>
public static class MudGuardTone
{
    public static Color ToColor(this GuardTone tone) => tone switch
    {
        GuardTone.Info => Color.Info,
        GuardTone.Active => Color.Secondary,
        GuardTone.Holding => Color.Warning,
        GuardTone.Warning => Color.Warning,
        GuardTone.Alert => Color.Error,
        GuardTone.Success => Color.Success,
        _ => Color.Default
    };

    public static Severity ToSeverity(this GuardTone tone) => tone switch
    {
        GuardTone.Alert => Severity.Error,
        GuardTone.Warning or GuardTone.Holding => Severity.Warning,
        GuardTone.Success => Severity.Success,
        GuardTone.Off => Severity.Normal,
        _ => Severity.Info
    };

    /// <summary>CSS modifier suffix used by the status dot (e.g. "holding").</summary>
    public static string Css(this GuardTone tone) => tone.ToString().ToLowerInvariant();
}
