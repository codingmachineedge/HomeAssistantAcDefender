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

    /// <summary>
    /// Estimated AC-only electricity cost, shown under the runtime hours on the Dashboard. The
    /// Alectra whole-house sensor can be down (or absent), so this needs no sensor at all: every
    /// second of real compressor runtime (hvac_action = cooling) is priced as a fixed assumed load —
    /// amps × volts (default 30 A × 240 V = 7.2 kW) — at the Alectra time-of-use rate in force at
    /// that moment. Runtime backfilled from past recorder logs is priced the same way, so today /
    /// this-month / lifetime cost estimates cover the full logged history. This is an ESTIMATE of the
    /// energy commodity portion: a 30 A breaker rating is the ceiling, not the measured draw.
    /// </summary>
    public bool AcCostEstimateEnabled { get; set; } = true;

    public double AcEstimatedAmps { get; set; } = 30.0;

    public double AcEstimatedVolts { get; set; } = 240.0;

    /// <summary>
    /// Rival Schedule Watch. The AC vendor app has its own "Temperature schedules" tab (per weekday)
    /// that pushes the wall setpoint on a timer — e.g. SLEEP 21.5/23 at 12:00 a.m., DEEP SLEEP
    /// 23.5/26 at 2:00 a.m. (the "set a 2-hour timer, drift toward 25 while everyone sleeps" plan),
    /// GOOD MORNING 22.5/24 at 9:00 a.m. AC Defender does NOT follow that schedule; these settings
    /// describe it so the defender can recognize a scheduled machine push, keep it out of the
    /// human-touch bookkeeping/learning, and answer it back toward the user's own target ("my temp").
    /// See <see cref="Services.RivalScheduleWatch"/>.
    /// </summary>
    public bool RivalScheduleWatchEnabled { get; set; } = true;

    // How close a new wall setpoint must be to a block's low/high number to count as the schedule.
    public double RivalScheduleSetpointToleranceCelsius { get; set; } = 0.3;

    /// <summary>
    /// While the wall sits at a scheduled setpoint above my temp and the room is warm, skip the
    /// human-oriented quiet waits — a schedule is a machine, and per the household's own words
    /// everyone is asleep when it runs, so nobody is watching the correction.
    /// </summary>
    public bool RivalScheduleBypassQuietTiming { get; set; } = true;

    // Above target + this band, the normal hot-room safety paths lead instead of the rival bypass.
    public double RivalScheduleSafetyBandCelsius { get; set; } = 3.0;

    // The rival app's Temperature schedule blocks. Defaults are empty; the real blocks live in
    // appsettings.json / environment so times and setpoints stay configuration, not code.
    public List<RivalScheduleBlockOptions> RivalScheduleBlocks { get; set; } = [];

    // Placeholder for the vendor app's "Fan schedules" tab: parsed/stored for future use, not enforced.
    public List<RivalFanScheduleBlockOptions> RivalFanScheduleBlocks { get; set; } = [];
}

/// <summary>
/// One block of the rival AC app's temperature schedule as configured. <c>Start</c> is local 24-hour
/// "HH:mm"; the block runs until the next applicable block starts. The two setpoints are the pair the
/// vendor app shows per block (orange = lower target, blue = upper).
/// </summary>
public sealed class RivalScheduleBlockOptions
{
    public string Name { get; set; } = "";

    public string Start { get; set; } = "00:00";

    public double LowSetPointCelsius { get; set; }

    public double HighSetPointCelsius { get; set; }

    public string Days { get; set; } = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
}

/// <summary>Placeholder shape for the vendor app's Fan schedule tab (not enforced yet).</summary>
public sealed class RivalFanScheduleBlockOptions
{
    public string Name { get; set; } = "";

    public string Start { get; set; } = "00:00";

    public string FanMode { get; set; } = "auto";

    public string Days { get; set; } = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
}
