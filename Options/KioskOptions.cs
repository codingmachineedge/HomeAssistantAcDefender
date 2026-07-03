namespace HomeAssistantAcDefender.Options;

/// <summary>
/// Energy kiosk: keeps a neutral "Energy" dashboard cast to a Google display 24/7. The kiosk is
/// deliberately unbranded — nothing on that screen mentions the defender. Disabled until
/// <see cref="MediaPlayerEntity"/> is configured.
/// </summary>
public class KioskOptions
{
    public const string SectionName = "Kiosk";

    public bool Enabled { get; set; }

    /// <summary>Cast target, e.g. media_player.nesthubcc18.</summary>
    public string MediaPlayerEntity { get; set; } = "";

    /// <summary>Lovelace dashboard url_path to cast.</summary>
    public string DashboardPath { get; set; } = "energy-kiosk";

    /// <summary>View path inside the dashboard.</summary>
    public string ViewPath { get; set; } = "main";

    /// <summary>After something else takes over the screen, wait this long before re-casting.</summary>
    public int InterruptCooldownMinutes { get; set; } = 30;

    /// <summary>How often the worker checks the screen and refreshes the kiosk sensors.</summary>
    public int CheckIntervalSeconds { get; set; } = 45;

    /// <summary>Seconds to wait after casting before restoring the volume (covers the connect chime).</summary>
    public int VolumeRestoreDelaySeconds { get; set; } = 8;

    /// <summary>Volume to restore when the pre-cast volume could not be read (0..1).</summary>
    public double DefaultRestoreVolume { get; set; } = 0.4;

    /// <summary>The input_button on the kiosk ("Show this &amp; last month's usage").</summary>
    public string UsageButtonEntity { get; set; } = "input_button.show_monthly_usage";

    /// <summary>How long the this-month/last-month review cards stay visible after a press.</summary>
    public int ReviewMinutes { get; set; } = 5;
}
