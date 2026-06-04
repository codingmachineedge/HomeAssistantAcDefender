namespace HomeAssistantAcDefender.Models;

public sealed record TargetTemperatureRequest(double TemperatureCelsius);

public sealed record DefenderEnabledRequest(bool Enabled);
