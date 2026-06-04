namespace HomeAssistantAcDefender.Models;

public sealed record TargetTemperatureRequest(double TemperatureCelsius);

public sealed record DefenderEnabledRequest(bool Enabled);

public sealed record DummyThermostatRequest(
    double? CurrentTemperatureCelsius,
    double? SetPointCelsius,
    string? HvacMode);
