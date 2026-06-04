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
}
