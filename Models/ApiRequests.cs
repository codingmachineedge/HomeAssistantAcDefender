namespace HomeAssistantAcDefender.Models;

public sealed record TargetTemperatureRequest(double TemperatureCelsius);

public sealed record DefenderEnabledRequest(bool Enabled);

public sealed record FanModeRequest(string FanMode);

public sealed record SettingsRequest(
    bool ScheduleEnabled,
    string WeatherActivationMode,
    int BaseCooldownSeconds,
    int MaxCooldownSeconds,
    int TouchFrequencyWindowMinutes,
    bool FanEnergySaverEnabled,
    double FanEnergySaverThresholdCelsius,
    string FanEnergySaverMode,
    bool UpstairsComfortEnabled,
    string UpstairsTemperatureEntityIds,
    double UpstairsMaxComfortCelsius,
    double UpstairsComfortTargetCelsius,
    double UpstairsComfortBoostCelsius,
    bool HomePresenceRequired,
    string PresenceEntityIds,
    IReadOnlyList<ScheduleEntry> Schedule);
