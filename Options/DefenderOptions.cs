using HomeAssistantAcDefender.Services;

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

    /// <summary>
    /// Electricity-cost tracking. The defender integrates the configured Alectra power sensor (W→kWh)
    /// over time, multiplies each interval by the current Alectra time-of-use rate, and accumulates
    /// total / today / this-month cost. Rates below are the ENERGY COMMODITY portion only (¢/kWh);
    /// a real bill also has delivery, regulatory charges, the Ontario Electricity Rebate, and HST —
    /// approximate an all-in bill with <see cref="ElectricityAllInMultiplier"/> and
    /// <see cref="ElectricityAllInAdderCentsPerKwh"/>, which default to commodity-only. Update these
    /// when the OEB changes the TOU prices. See <see cref="Services.AlectraTouSchedule"/> for the
    /// summer/winter/weekend/holiday schedule.
    /// </summary>
    public bool ElectricityCostTrackingEnabled { get; set; } = true;

    public double ElectricityOnPeakCentsPerKwh { get; set; } = TouRateTable.DefaultOnPeakCentsPerKwh;

    public double ElectricityMidPeakCentsPerKwh { get; set; } = TouRateTable.DefaultMidPeakCentsPerKwh;

    public double ElectricityOffPeakCentsPerKwh { get; set; } = TouRateTable.DefaultOffPeakCentsPerKwh;

    public double ElectricityAllInMultiplier { get; set; } = 1.0;

    public double ElectricityAllInAdderCentsPerKwh { get; set; } = 0.0;

    /// <summary>
    /// All-in "out of pocket" bill components layered on top of the commodity cost, in Ontario/Alectra
    /// residential bill order:
    ///   all_in = (commodity + delivery_fixed + delivery_variable + regulatory) × (1 − OER) × (1 + HST)
    /// The Ontario Electricity Rebate (OER) is a percentage credit on the pre-tax subtotal, applied
    /// BEFORE HST. Commodity, OER, and HST are standard province-wide; the DELIVERY and REGULATORY
    /// numbers are customer- and rate-class-specific — copy the exact values from your own Alectra bill
    /// for a precise figure. The defaults below are only reasonable Ontario placeholders.
    /// The fixed monthly delivery/service charge is accrued smoothly over the month (per second) so it
    /// splits cleanly across the total / today / this-month buckets.
    /// </summary>
    public double ElectricityDeliveryFixedDollarsPerMonth { get; set; } = 30.0;

    public double ElectricityDeliveryVariableCentsPerKwh { get; set; } = 5.0;

    public double ElectricityRegulatoryCentsPerKwh { get; set; } = 0.7;

    // Ontario Electricity Rebate: 23.5% credit on the pre-tax subtotal, effective Nov 1, 2025.
    public double ElectricityOntarioRebatePercent { get; set; } = 0.235;

    public double ElectricityHstPercent { get; set; } = 0.13;

    /// <summary>
    /// Budget-preferring control. When enabled, the defender tracks month-to-date all-in spend against a
    /// pro-rated target (budget × fraction-of-month-elapsed) and, when running ahead of that pace, lets
    /// the room run a little warmer to spend less — biased toward holding off during the expensive
    /// on/mid-peak periods. It is a PREFERENCE, not a cutoff: the raise is bounded by
    /// <see cref="ElectricityBudgetMaxSetpointOffsetCelsius"/> and always yields to the safety guardrail
    /// <see cref="ElectricityBudgetSafetyMaxCelsius"/> (at or above that room temperature the budget
    /// offset is dropped so dangerous heat is always cooled).
    /// </summary>
    public bool ElectricityBudgetEnabled { get; set; } = false;

    public double ElectricityMonthlyBudgetDollars { get; set; } = 150.0;

    // 0 = no biasing, 1 = full biasing up to the max offset.
    public double ElectricityBudgetAggressiveness { get; set; } = 0.5;

    public double ElectricityBudgetMaxSetpointOffsetCelsius { get; set; } = 1.5;

    public double ElectricityBudgetSafetyMaxCelsius { get; set; } = 26.0;
}
