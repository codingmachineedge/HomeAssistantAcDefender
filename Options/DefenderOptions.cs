namespace HomeAssistantAcDefender.Options;

public sealed class DefenderOptions
{
    public const string SectionName = "Defender";

    public double DefaultTargetCelsius { get; set; } = 22.0;

    public double MinimumGeneratedTargetCelsius { get; set; } = 20.0;

    public double MaximumGeneratedTargetCelsius { get; set; } = 24.0;

    public double GeneratedTargetStepCelsius { get; set; } = 0.5;

    public double MinimumCoolingSetPointCelsius { get; set; } = 16.0;

    public double MaximumBoostOffsetCelsius { get; set; } = 5.0;

    public double TemperatureToleranceCelsius { get; set; } = 0.1;

    public int PollIntervalSeconds { get; set; } = 5;

    public string StateFilePath { get; set; } = "/data/defender-state.json";

    public int CommandGraceSeconds { get; set; } = 120;

    /// <summary>
    /// How far below the current room temperature the warm-room defender sets the wall, and the size of
    /// each step-down toward the website target. Smaller values (e.g. 0.5 C) keep the setpoint tracking
    /// just under the room so the cooling is far less noticeable to other people than a full 1 C gap.
    /// </summary>
    public double WarmRoomApproachCelsius { get; set; } = 0.5;

    /// <summary>
    /// Outdoor-temperature power rule. When it is cool outside, cooling is rarely needed, so the
    /// defender stands down below <see cref="OutdoorSilenceBelowCelsius"/> (default 20 C) and runs in a
    /// gentler "lite mode" between that and <see cref="OutdoorLiteBelowCelsius"/> (default 22 C), where it
    /// only corrects once the room is more than <see cref="OutdoorLiteModeBandCelsius"/> above target.
    /// All of it yields to the comfort safety bypass, so a genuinely hot room still cools.
    /// </summary>
    public bool OutdoorPowerRuleEnabled { get; set; } = true;

    public double OutdoorSilenceBelowCelsius { get; set; } = 20.0;

    public double OutdoorLiteBelowCelsius { get; set; } = 22.0;

    public double OutdoorLiteModeBandCelsius { get; set; } = 1.0;
}
