using HomeAssistantAcDefender.Models;

namespace HomeAssistantAcDefender.Guards;

/// <summary>A single labelled live reading shown on a card (label, value, one-line help).</summary>
public sealed record MetricItem(string Label, string Value, string Help = "");

/// <summary>Which decision stage a guard belongs to, used to group the Defense board and the Guide.</summary>
public enum GuardCategory
{
    /// <summary>Always-on cooling spine: cool-mode restore and the quiet-recovery pacing.</summary>
    Core,

    /// <summary>Reactions to people changing the wall thermostat.</summary>
    WallTouch,

    /// <summary>Timing that lines corrections up with real Home Assistant sensor signals.</summary>
    Sensor,

    /// <summary>Safety, rate-limiting, and emergency layers.</summary>
    System
}

/// <summary>Normalized colour intent for a guard's current state, mapped to MudBlazor colours in the UI.</summary>
public enum GuardTone
{
    Off,
    Calm,
    Info,
    Active,
    Holding,
    Warning,
    Alert,
    Success
}

/// <summary>
/// The live, render-ready view of one guard for a given snapshot. Built by each guard's
/// <see cref="GuardInfo.Project"/> lambda so the divergent snapshot shapes
/// (Active vs Holding vs Waiting vs Alerting) are normalized in exactly one place.
/// </summary>
public sealed record GuardLiveView(
    bool Enabled,
    bool Busy,
    string StateLabel,
    GuardTone Tone,
    string StatusText,
    IReadOnlyList<MetricItem> Metrics)
{
    /// <summary>
    /// Standard three-way state used by most guards: Off when disabled, a busy label when the guard is
    /// holding/waiting/active, otherwise "Watching".
    /// </summary>
    public static GuardLiveView Standard(
        bool enabled,
        bool busy,
        string busyLabel,
        string statusText,
        IReadOnlyList<MetricItem> metrics,
        GuardTone busyTone = GuardTone.Holding)
    {
        var (label, tone) = !enabled
            ? ("Off", GuardTone.Off)
            : busy
                ? (busyLabel, busyTone)
                : ("Watching", GuardTone.Calm);

        return new GuardLiveView(enabled, busy, label, tone, statusText, metrics);
    }
}

/// <summary>
/// Static description of one defender algorithm: its identity, a plain-English explanation
/// (summary / what it watches / how it decides / what it does), the settings that tune it, and an
/// optional <see cref="Project"/> that turns a live snapshot into a <see cref="GuardLiveView"/>.
/// Algorithms with no live card (for example Dynamic Cooldown) leave <see cref="Project"/> null and
/// appear only in the Guide. This record is the single source of truth shared by the Defense board,
/// the Guide page, and the GuardCard help expanders.
/// </summary>
public sealed record GuardInfo(
    string Name,
    GuardCategory Category,
    string Summary,
    string Watches,
    string Logic,
    string Output,
    IReadOnlyList<string> Settings,
    Func<DefenderSnapshot, GuardLiveView>? Project = null);
