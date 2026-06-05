using HomeAssistantAcDefender.Models;

namespace HomeAssistantAcDefender.Guards;

/// <summary>
/// The single, ordered table describing every defender algorithm. The Defense board renders the
/// entries that expose a live <see cref="GuardInfo.Project"/>; the Guide renders all of them. Keeping
/// the live display logic and the human explanation in one place removes the ~200 lines of ad-hoc
/// display properties the dashboard used to carry, and guarantees the in-app help matches what runs.
/// </summary>
public static class GuardCatalog
{
    // ---- formatting helpers (kept MudBlazor-free so the catalog stays unit-testable) ----
    private static string Temp(double? v) => v is null ? "--" : $"{v.Value:0.0} C";
    private static string Signed(double? v) => v is null ? "--" : $"{v.Value:+0.0;-0.0;0.0} C";
    private static string Wait(bool busy, int secs) => busy ? $"{secs}s" : "Ready";
    private static string OffWait(bool enabled, bool busy, int secs) => !enabled ? "Off" : busy ? $"{secs}s" : "Ready";
    private static string Score(bool enabled, int score) => enabled ? $"{score}/100" : "Off";

    /// <summary>All defender algorithms, in the order they are most useful to read.</summary>
    public static readonly IReadOnlyList<GuardInfo> All =
    [
        // ───────────────────────────── Core ─────────────────────────────
        new GuardInfo(
            "Comfort Sync (quiet recovery)", GuardCategory.Core,
            "Spaces out and softens corrections so a fixed thermostat does not look like an instant robot.",
            "Recent wall-touch count, time since the last defender command, and how far the room is above target.",
            "After a manual change it waits a random delay, may hold one or two extra short beats, enforces a minimum gap between commands, and shrinks the nudge size. Repeated touches raise the quiet level (Calm → Light → Quiet → Extra quiet → Softest), lengthening waits and shrinking steps. A warm room (over the safety override) skips all of it.",
            "Holds the correction until the chosen calm moment, then lets a softened nudge through.",
            ["NaturalRecoveryEnabled", "AdaptiveQuietnessEnabled", "MinimumNaturalDelaySeconds", "MaximumNaturalDelaySeconds", "NaturalStepCelsius", "NaturalHoldChancePercent", "MinimumCommandGapSeconds", "NaturalSafetyOverrideCelsius"],
            s =>
            {
                var n = s.NaturalRecovery;
                return GuardLiveView.Standard(n.Enabled, n.Waiting, "Waiting", n.Status,
                [
                    new("Quiet level", n.Enabled ? n.QuietLevel : "Off", "How soft Comfort Sync is being right now."),
                    new("Quiet wait", Wait(n.Waiting, n.SecondsRemaining), "How long it is waiting before the next nudge."),
                    new("Nudge size", n.Enabled ? $"{n.EffectiveStepCelsius:0.0} C" : "Off", "Biggest gentle setpoint move right now."),
                    new("Hold chance", n.Enabled ? $"{n.EffectiveHoldChancePercent}%" : "Off", "Chance it waits one more short beat."),
                    new("Command gap", n.Enabled ? $"{n.EffectiveCommandGapSeconds}s" : "Off", "Minimum spacing between automatic nudges."),
                    new("Touch count", n.RecentTouchCount.ToString(), "Wall changes counted in the touch window."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Cool Mode Restore", GuardCategory.Core,
            "Puts the thermostat back into cool mode whenever someone switches it to heat/off/auto.",
            "The Home Assistant HVAC mode, plus how far the room is above target.",
            "If the mode is not 'cool' it normally waits a short random delay (between the min and max seconds) so the change is not jarring — but only while the room stays within the comfort band. If the room is warmer than target + band, upstairs is severely hot, or the safety override is crossed, it restores cool immediately.",
            "Sends climate.set_hvac_mode = cool once the delay (if any) elapses.",
            ["CoolModeRestoreDelayEnabled", "CoolModeRestoreMinimumDelaySeconds", "CoolModeRestoreMaximumDelaySeconds", "CoolModeRestoreComfortBandCelsius"],
            s =>
            {
                var c = s.CoolModeRestore;
                return GuardLiveView.Standard(c.Enabled, c.Waiting, "Waiting", c.Status,
                [
                    new("Restore wait", Wait(c.Waiting, c.SecondsRemaining), "How long before cool mode comes back."),
                ], busyTone: GuardTone.Active);
            }),

        // ─────────────────────────── Wall touch ───────────────────────────
        new GuardInfo(
            "Natural Walkback", GuardCategory.WallTouch,
            "Walks a safe-band correction toward target in small, slightly random steps instead of one obvious jump.",
            "Recent wall-touch pressure (a 0–100 suspicion score) and how far the setpoint is from the defender target.",
            "Once recent touches reach the trigger count and the room is inside the walkback safety band, each command moves only about the walkback step (plus a tiny jitter) toward target. A warm room that needs direct cooling skips walkback and still commands 1 °C below the current room temperature.",
            "Shapes the size of the setpoint command just before it is sent.",
            ["NaturalWalkbackEnabled", "NaturalWalkbackTriggerTouches", "NaturalWalkbackStepCelsius", "NaturalWalkbackJitterCelsius", "NaturalWalkbackSafetyBandCelsius"],
            s =>
            {
                var w = s.NaturalWalkback;
                return GuardLiveView.Standard(w.Enabled, w.Active, "Walking back", w.Status,
                [
                    new("Touch pressure", Score(w.Enabled, w.SuspicionScore), "How strong recent wall changes look."),
                    new("Walk step", w.Enabled ? $"{w.StepCelsius:0.0} C" : "Off", "Safe-band nudge size after repeated touches."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Touch Signature", GuardCategory.WallTouch,
            "Matches safe nudges to the size of steps people actually use on the wall thermostat.",
            "The recent real wall-thermostat steps (their median size) inside the retention window.",
            "With enough recent steps and a room still inside the signature safety band, it learns the median wall-step size, clamps it between the min and max signature step, and caps safe nudges to that size. Too-warm rooms clear the signature so direct cooling resumes.",
            "Lowers the per-command nudge size used by Natural Walkback.",
            ["TouchSignatureEnabled", "TouchSignatureTriggerTouches", "TouchSignatureRetentionMinutes", "TouchSignatureMinimumStepCelsius", "TouchSignatureMaximumStepCelsius", "TouchSignatureSafetyBandCelsius"],
            s =>
            {
                var t = s.TouchSignature;
                return GuardLiveView.Standard(t.Enabled, t.Active, "Shaping", t.Status,
                [
                    new("Learned step", t.LearnedStepCelsius is { } step ? $"{step:0.0} C" : t.Enabled ? "--" : "Off", "Learned wall-step size for safe nudges."),
                    new("Samples", t.Enabled ? t.SampleCount.ToString() : "Off", "Recent wall steps used to learn."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Visibility Guard", GuardCategory.WallTouch,
            "Slows the next safe nudge when a wall touch lands right after a defender command (someone likely noticed).",
            "Wall touches that occur within the after-command window, counted as 'notices' over the notice window.",
            "Each notice adds pressure (0–100). When notices reach the trigger, the next safe correction waits a variable hold between the min and max hold minutes, scaled by pressure. A room over the safety band clears the hold.",
            "Delays the next safe correction so the AC's reaction looks less mechanical.",
            ["VisibilityGuardEnabled", "VisibilityGuardTriggerNotices", "VisibilityGuardNoticeWindowMinutes", "VisibilityGuardAfterCommandSeconds", "VisibilityGuardMinimumHoldMinutes", "VisibilityGuardMaximumHoldMinutes", "VisibilityGuardSafetyBandCelsius"],
            s =>
            {
                var v = s.VisibilityGuard;
                return GuardLiveView.Standard(v.Enabled, v.Active, "Holding", v.Status,
                [
                    new("Hold", Wait(v.Active, v.SecondsRemaining), "Time left on the visibility hold."),
                    new("Pressure", Score(v.Enabled, v.Pressure), "How strongly noticed corrections affect timing."),
                    new("Notices", v.Enabled ? v.NoticeCount.ToString() : "Off", "Touches seen soon after a command."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Routine Timing", GuardCategory.WallTouch,
            "Lines safe corrections up with a normal-looking comfort-check rhythm instead of firing instantly.",
            "Recent wall touches and the wall-clock minute.",
            "After repeated touches and while the room is safe, the next correction waits until the next interval boundary (the routine minutes) plus a little random wiggle, capped at the max routine delay. Too-warm rooms clear it.",
            "Delays the safe correction to the next tidy time slot.",
            ["RoutineTimingEnabled", "RoutineTimingTriggerTouches", "RoutineTimingIntervalMinutes", "RoutineTimingJitterMinutes", "RoutineTimingMaxDelayMinutes", "RoutineTimingSafetyBandCelsius"],
            s =>
            {
                var r = s.RoutineTiming;
                return GuardLiveView.Standard(r.Enabled, r.Waiting, "Waiting", r.Status,
                [
                    new("Routine wait", Wait(r.Waiting, r.SecondsRemaining), "Wait for a normal comfort-check slot."),
                    new("Rhythm", r.Enabled ? $"{r.IntervalMinutes}+{r.JitterMinutes} min" : "Off", "Minute rhythm plus wiggle time."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Comfort Budget", GuardCategory.WallTouch,
            "Caps how many safe corrections happen inside a rolling window so the AC is not constantly nudged.",
            "The count of recent automatic setpoint commands in the budget window.",
            "If the number of commands in the window reaches the max, it rests until the oldest command ages out of the window. A room over the safety band clears the budget.",
            "Holds new safe corrections until the budget frees up.",
            ["ComfortBudgetEnabled", "ComfortBudgetWindowMinutes", "ComfortBudgetMaxCommands", "ComfortBudgetSafetyBandCelsius"],
            s =>
            {
                var b = s.ComfortBudget;
                return GuardLiveView.Standard(b.Enabled, b.Holding, "Resting", b.Status,
                [
                    new("Budget wait", Wait(b.Holding, b.SecondsRemaining), "Rest time after repeated adjustments."),
                    new("Used", b.Enabled ? $"{b.RecentCommandCount}/{b.MaxCommands}" : "Off", "Safe adjustments used in the window."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Natural Cadence", GuardCategory.WallTouch,
            "Picks a variable future slot for safe nudges so they never land at identical, robotic times.",
            "Recent wall-touch pressure and recent command pressure.",
            "After repeated touches it chooses a wait between the min and max cadence minutes (later as pressure rises) plus a small jitter. Too-warm rooms clear it.",
            "Delays the safe correction to the chosen cadence slot.",
            ["NaturalCadenceEnabled", "NaturalCadenceTriggerTouches", "NaturalCadenceMinimumMinutes", "NaturalCadenceMaximumMinutes", "NaturalCadenceJitterMinutes", "NaturalCadenceSafetyBandCelsius"],
            s =>
            {
                var c = s.NaturalCadence;
                return GuardLiveView.Standard(c.Enabled, c.Waiting, "Waiting", c.Status,
                [
                    new("Cadence wait", Wait(c.Waiting, c.SecondsRemaining), "Quiet future slot for the next nudge."),
                    new("Pressure", Score(c.Enabled, c.TouchPressure), "How much repeated touching affects timing."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Comfort Pace", GuardCategory.WallTouch,
            "The high-frequency planner: under heavy wall fighting it waits for a calm weather, sensor, or clock-aligned slot.",
            "Touch pressure, command pressure, real outdoor-weather movement, the learned Home Assistant sensor beat, and 5/10-minute clock boundaries.",
            "When touches reach the trigger and the room is inside the safety band, it computes a base delay between the min and max pace minutes (scaling with pressure) and then snaps it to the nearest calm slot — a weather update, the sensor beat, or a clock boundary — recording why. Too-warm rooms clear it instantly.",
            "Delays the safe correction to the chosen calm climate slot.",
            ["NaturalChangePlannerEnabled", "NaturalChangePlannerTriggerTouches", "NaturalChangePlannerMinimumMinutes", "NaturalChangePlannerMaximumMinutes", "NaturalChangePlannerJitterMinutes", "NaturalChangePlannerPreferWeatherSlots", "NaturalChangePlannerPreferSensorBeat"],
            s =>
            {
                var p = s.NaturalChangePlanner;
                return GuardLiveView.Standard(p.Enabled, p.Waiting, "Pacing", p.Status,
                [
                    new("Pace wait", OffWait(p.Enabled, p.Waiting, p.SecondsRemaining), "Calm climate slot after frequent changes."),
                    new("Pressure", Score(p.Enabled, p.TouchPressure), "How strongly wall changes drive the pace."),
                    new("Reason", p.Enabled ? p.PlannedReason : "Off", "Why the next calm slot was chosen."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Comfort Envelope", GuardCategory.WallTouch,
            "Lets a tiny safe wall preference rest for a while instead of being corrected the instant it appears.",
            "The wall setpoint relative to the defender target and how far the room is above target.",
            "After repeated touches, if the wall setpoint stays within the accepted range (target ± max offset) and the room is under the safety band, it simply observes for the hold minutes. A setpoint outside the range, a too-warm room, or a direct-cooling need clears it.",
            "Suppresses the small correction while the wall preference is inside the safe range.",
            ["ComfortEnvelopeEnabled", "ComfortEnvelopeTriggerTouches", "ComfortEnvelopeHoldMinutes", "ComfortEnvelopeMaxOffsetCelsius", "ComfortEnvelopeSafetyBandCelsius"],
            s =>
            {
                var e = s.ComfortEnvelope;
                var range = e.MinimumAllowedSetPointCelsius is { } min && e.MaximumAllowedSetPointCelsius is { } max
                    ? $"{min:0.0}-{max:0.0} C"
                    : e.Enabled ? "--" : "Off";
                return GuardLiveView.Standard(e.Enabled, e.Active, "Observing", e.Status,
                [
                    new("Envelope wait", OffWait(e.Enabled, e.Active, e.SecondsRemaining), "How long the small preference can rest."),
                    new("Range", range, "Setpoint range accepted while safe."),
                    new("Wall setpoint", e.PreferredSetPointCelsius is { } sp ? $"{sp:0.0} C" : e.Enabled ? "--" : "Off", "Latest observed wall setpoint."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Comfort Compromise", GuardCategory.WallTouch,
            "Blends a repeated wall choice into a temporary target, then fades it back to the website target.",
            "The latest wall setpoint, the website target, and how far the room is above target.",
            "If touches repeat and the room is inside the compromise safety band, the wall setpoint pulls the effective target up to the max offset for the hold minutes, then eases back over the decay minutes. A too-warm room clears it immediately.",
            "Temporarily shifts the defender target the corrections aim for.",
            ["ComfortCompromiseEnabled", "ComfortCompromiseTriggerTouches", "ComfortCompromiseHoldMinutes", "ComfortCompromiseDecayMinutes", "ComfortCompromiseMaxOffsetCelsius", "ComfortCompromiseSafetyBandCelsius"],
            s =>
            {
                var c = s.ComfortCompromise;
                return GuardLiveView.Standard(c.Enabled, c.Active, "Blending", c.Status,
                [
                    new("Blend target", c.EffectiveTargetCelsius is { } t ? $"{t:0.0} C" : "Ready", "Temporary target while a wall choice fades back."),
                    new("Blend wait", Wait(c.Active, c.SecondsRemaining), "How long the temporary blend remains."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Comfort Memory", GuardCategory.WallTouch,
            "Learns a small time-of-day target bias from repeated safe wall choices and re-applies it later that hour.",
            "The current hour and the offsets learned for it; the room temperature.",
            "Repeated safe touches teach a bounded offset (± max offset) for the current hour slot. On later checks in the same window it nudges the target by that learned offset. Learned memory expires after the retention hours and is skipped when the room is warm or upstairs needs cooling.",
            "Adjusts the defender target by the learned hourly bias.",
            ["ComfortMemoryEnabled", "ComfortMemoryLearningTouches", "ComfortMemoryRetentionHours", "ComfortMemoryMaxOffsetCelsius", "ComfortMemorySafetyBandCelsius"],
            s =>
            {
                var m = s.ComfortMemory;
                return GuardLiveView.Standard(m.Enabled, m.Active, "Applied", m.Status,
                [
                    new("Memory bias", m.LearnedOffsetCelsius is { } o ? $"{o:+0.0;-0.0;0.0} C" : "Ready", "Small learned time-of-day adjustment."),
                    new("Memory target", m.EffectiveTargetCelsius is { } t ? $"{t:0.0} C" : "Ready", "Target after comfort memory is applied."),
                ], busyTone: GuardTone.Info);
            }),

        new GuardInfo(
            "Conflict Quiet", GuardCategory.WallTouch,
            "Stands the defender down during an obvious tug-of-war over the thermostat.",
            "Recent wall touches within the touch window and how far the room is above target.",
            "When touches reach the conflict threshold, it stops sending visible corrections for the stand-down minutes — but only while the room stays within target + comfort band. A warmer room, severe upstairs heat, or a crossed safety override ends it.",
            "Suppresses corrections for the stand-down period.",
            ["ConflictQuietModeEnabled", "ConflictQuietTouchThreshold", "ConflictQuietMinutes", "ConflictQuietComfortBandCelsius"],
            s =>
            {
                var c = s.ConflictQuiet;
                return GuardLiveView.Standard(c.Enabled, c.Active, "Standing down", c.Status,
                [
                    new("Stand-down", Wait(c.Active, c.SecondsRemaining), "How long conflict quiet is standing down."),
                    new("Trigger", c.Enabled ? c.TriggerTouchCount.ToString() : "Off", "Touches needed to stand down."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Wall Settling", GuardCategory.WallTouch,
            "Waits for someone who is still tapping the wall thermostat to stop before correcting.",
            "Recent touches inside the settling window and the room temperature.",
            "With enough recent touches it holds for the base settle seconds plus extra pressure seconds (more touches = longer), measured from the latest touch. A room over the safety band clears it.",
            "Holds the correction until the wall stops changing.",
            ["WallSettlingGuardEnabled", "WallSettlingMinimumTouches", "WallSettlingWindowMinutes", "WallSettlingBaseSeconds", "WallSettlingPressureExtraSeconds", "WallSettlingSafetyBandCelsius"],
            s =>
            {
                var w = s.WallSettling;
                return GuardLiveView.Standard(w.Enabled, w.Holding, "Settling", w.Status,
                [
                    new("Settle wait", OffWait(w.Enabled, w.Holding, w.SecondsRemaining), "Time for the wall to stop changing."),
                    new("Touches", w.Enabled ? w.RecentTouchCount.ToString() : "Off", "Recent touches in the settling window."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Manual Comfort Grace", GuardCategory.WallTouch,
            "Leaves a manual wall change alone while the room still feels comfortable.",
            "Time since the wall change and how far the room is above target.",
            "After cooldown it can keep waiting up to the grace minutes while the room stays within target + grace band. If the room rises above the band, the mode leaves cool, or upstairs becomes severely hot, grace ends. Touch Intent can extend the grace when recent changes are clearly warmer.",
            "Suppresses the correction while the wall change stays comfortable.",
            ["ManualComfortGraceEnabled", "ManualComfortGraceMinutes", "ManualComfortGraceBandCelsius"],
            s =>
            {
                var g = s.ManualComfortGrace;
                return GuardLiveView.Standard(g.Enabled, g.Active, "Grace", g.Status,
                [
                    new("Grace wait", Wait(g.Active, g.SecondsRemaining), "How long a wall change can rest."),
                    new("Grace band", g.Enabled ? $"{g.ComfortBandCelsius:0.0} C" : "Off", "Warmth allowed above target during grace."),
                ], busyTone: GuardTone.Active);
            }),

        new GuardInfo(
            "Touch Intent", GuardCategory.WallTouch,
            "Reads whether recent wall changes trend warmer, cooler, or mixed, and extends grace for a clear warmer pattern.",
            "The net sum of recent wall setpoint changes inside the intent window.",
            "If the net movement is at least the warm threshold and the room is inside the intent safety band, it adds the extra grace minutes to Manual Comfort Grace. Cooler or mixed patterns get no extra grace; a too-warm room steps it aside.",
            "Lengthens Manual Comfort Grace when people clearly want warmer air.",
            ["TouchIntentEnabled", "TouchIntentMinimumTouches", "TouchIntentWindowMinutes", "TouchIntentNetWarmThresholdCelsius", "TouchIntentExtraGraceMinutes", "TouchIntentSafetyBandCelsius"],
            s =>
            {
                var t = s.TouchIntent;
                var tone = !t.Enabled ? GuardTone.Off
                    : t.Active ? (t.Direction == "cooler" ? GuardTone.Info : GuardTone.Warning)
                    : GuardTone.Calm;
                var label = !t.Enabled ? "Off" : t.Active ? "Reading" : "Watching";
                return new GuardLiveView(t.Enabled, t.Active, label, tone, t.Status,
                [
                    new("Direction", t.Enabled ? t.Direction : "Off", "Recent wall choices: warmer, cooler, mixed, or learning."),
                    new("Net", t.Enabled ? $"{t.NetChangeCelsius:+0.0;-0.0;0.0} C" : "Off", "Net warmer or cooler movement."),
                    new("Extra grace", t.Enabled ? $"+{t.ExtraGraceMinutes} min" : "Off", "Extra grace available for warmer intent."),
                ]);
            }),

        new GuardInfo(
            "Cooler Intent Fast Lane", GuardCategory.WallTouch,
            "When people keep dialing the wall cooler, it skips quiet waits so the room cools sooner.",
            "The net cooler movement of recent wall changes and whether the room is above target.",
            "If repeated touches move the wall cooler by at least the cool threshold and the room is above target, it clears quiet waits (cooldown, grace, conflict quiet, cadence, repeat quiet, sensor rhythm, runway, and more) for the hold minutes. It never lowers the website target — cooling still starts at room minus 1 °C and stops at target. A room over the safety band hands control back to normal safety rules.",
            "Bypasses the quiet timing guards for a short window.",
            ["CoolerIntentFastLaneEnabled", "CoolerIntentMinimumTouches", "CoolerIntentWindowMinutes", "CoolerIntentHoldMinutes", "CoolerIntentNetCoolThresholdCelsius", "CoolerIntentSafetyBandCelsius"],
            s =>
            {
                var c = s.CoolerIntent;
                return GuardLiveView.Standard(c.Enabled, c.Active, "Fast lane", c.Status,
                [
                    new("Fast lane", OffWait(c.Enabled, c.Active, c.SecondsRemaining), "How long cooler intent skips quiet waits."),
                    new("Net", c.Enabled ? $"{c.NetChangeCelsius:+0.0;-0.0;0.0} C" : "Off", "Net cooler or warmer movement."),
                ], busyTone: GuardTone.Success);
            }),

        // ───────────────────────────── Sensor ─────────────────────────────
        new GuardInfo(
            "Setpoint Echo", GuardCategory.Sensor,
            "Waits for Home Assistant to report back the last setpoint before sending another safe command.",
            "The pending command setpoint and whether Home Assistant has echoed it yet.",
            "After a command it waits up to the echo grace seconds for Home Assistant to report that setpoint within 0.15 °C. Once echoed, or after the grace expires, the next command is allowed. A too-warm room steps it aside.",
            "Briefly holds the next safe command to avoid piling commands on a slow integration.",
            ["SetpointEchoGuardEnabled", "SetpointEchoGraceSeconds", "SetpointEchoSafetyBandCelsius"],
            s =>
            {
                var e = s.SetpointEcho;
                return GuardLiveView.Standard(e.Enabled, e.Waiting, "Waiting", e.Status,
                [
                    new("Echo wait", Wait(e.Waiting, e.SecondsRemaining), "Wait for Home Assistant to confirm the setpoint."),
                    new("Echo target", e.PendingSetPointCelsius is { } t ? $"{t:0.0} C" : "Ready", "Setpoint not yet echoed back."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Repeat Quiet", GuardCategory.Sensor,
            "Waits before sending the very same thermostat number again.",
            "The setpoint about to be sent versus the last defender command, plus touch and command pressure.",
            "If the next safe command would repeat the last number, it waits at least the minimum wait seconds plus extra pressure seconds (scaling with recent touches and commands). Different one-degree step-downs pass straight through; a too-warm room steps it aside.",
            "Holds an identical follow-up command until the wait elapses.",
            ["RepeatCommandGuardEnabled", "RepeatCommandMinimumWaitSeconds", "RepeatCommandPressureExtraSeconds", "RepeatCommandSafetyBandCelsius"],
            s =>
            {
                var r = s.RepeatCommand;
                return GuardLiveView.Standard(r.Enabled, r.Holding, "Holding", r.Status,
                [
                    new("Repeat wait", Wait(r.Holding, r.SecondsRemaining), "Wait before repeating the same number."),
                    new("Pressure", Score(r.Enabled, r.Pressure), "How strongly repeats are being slowed."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Sensor Rhythm", GuardCategory.Sensor,
            "Times nudges to just after the normal Home Assistant reading beat so they look less mechanical.",
            "Timestamps of real Home Assistant readings, used to learn the median update interval.",
            "With at least the minimum samples in the rhythm window, it learns the median interval between updates and waits until just after the next beat plus a small jitter. A too-warm room or upstairs heat clears it.",
            "Delays the safe correction to align with the sensor's update cadence.",
            ["SensorRhythmGuardEnabled", "SensorRhythmMinimumSamples", "SensorRhythmWindowMinutes", "SensorRhythmJitterSeconds", "SensorRhythmSafetyBandCelsius"],
            s =>
            {
                var r = s.SensorRhythm;
                return GuardLiveView.Standard(r.Enabled, r.Waiting, "Waiting", r.Status,
                [
                    new("Rhythm wait", Wait(r.Waiting, r.SecondsRemaining), "Wait for a normal sensor beat."),
                    new("Beat", r.Enabled && r.MedianIntervalSeconds > 0 ? $"{r.MedianIntervalSeconds}s" : r.Enabled ? "--" : "Off", "Learned reading interval."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Cooling Runway", GuardCategory.Sensor,
            "Gives the AC time to work after cooling starts before nudging the setpoint again.",
            "The Home Assistant hvac_action and how long ago cooling started, plus command pressure.",
            "When the action turns to cooling it records the start and holds for the minimum runway seconds plus extra pressure seconds. If cooling stops or the room gets too warm, it clears immediately.",
            "Holds the next safe nudge so a fresh cooling cycle is not interrupted.",
            ["CoolingRunwayGuardEnabled", "CoolingRunwayMinimumSeconds", "CoolingRunwayPressureExtraSeconds", "CoolingRunwaySafetyBandCelsius"],
            s =>
            {
                var c = s.CoolingRunway;
                return GuardLiveView.Standard(c.Enabled, c.Holding, "Holding", c.Status,
                [
                    new("Runway wait", Wait(c.Holding, c.SecondsRemaining), "Wait after cooling starts."),
                    new("Pressure", Score(c.Enabled, c.Pressure), "How strongly the runway is extended."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Room Trend Guard", GuardCategory.Sensor,
            "Keeps observing when the room is already stable or cooling after a wall change.",
            "Real room-temperature samples: the oldest versus newest inside the trend window.",
            "If the room is cooling (delta below the negative stable tolerance) it holds for the trend hold minutes so cooling can continue. Stable or warming rooms let the correction proceed; rooms above the grace band or safety override always proceed.",
            "Holds the correction while the room is trending cooler on its own.",
            ["RoomTrendGuardEnabled", "RoomTrendWindowMinutes", "RoomTrendStableToleranceCelsius", "RoomTrendHoldMinutes"],
            s =>
            {
                var r = s.RoomTrend;
                var tone = !r.Enabled ? GuardTone.Off
                    : r.Holding ? GuardTone.Holding
                    : r.Direction == "warming" ? GuardTone.Warning : GuardTone.Calm;
                var label = !r.Enabled ? "Off" : r.Holding ? "Observing" : "Watching";
                var delta = r.DeltaCelsius is { } d ? $" ({d:+0.0;-0.0;0.0} C)" : "";
                return new GuardLiveView(r.Enabled, r.Holding, label, tone, r.Status,
                [
                    new("Trend", r.Enabled ? $"{r.Direction}{delta}" : "Off", "Whether the room is warming, stable, or cooling."),
                    new("Trend hold", Wait(r.Holding, r.SecondsRemaining), "How long trend guard is observing."),
                ]);
            }),

        new GuardInfo(
            "Thermal Momentum", GuardCategory.Sensor,
            "Waits when the room is already cooling fast enough to reach target soon on its own.",
            "Real room-temperature samples (to estimate cooling rate) and the active cooling action.",
            "It estimates the cooling rate and minutes-to-target. If the rate is at least the minimum C/hour and target is within the look-ahead minutes, it holds for the momentum hold minutes. A room near target or above the safety band proceeds.",
            "Holds the correction so existing momentum can finish the job.",
            ["ThermalMomentumGuardEnabled", "ThermalMomentumMinimumCoolingRateCelsiusPerHour", "ThermalMomentumLookAheadMinutes", "ThermalMomentumHoldMinutes"],
            s =>
            {
                var m = s.ThermalMomentum;
                var eta = m.Holding ? $"{m.SecondsRemaining}s hold"
                    : m.EstimatedMinutesToTarget is { } e ? $"{e:0} min"
                    : m.Enabled ? "--" : "Off";
                return GuardLiveView.Standard(m.Enabled, m.Holding, "Holding", m.Status,
                [
                    new("Cooling rate", m.CoolingRateCelsiusPerHour is { } r ? $"{r:0.0} C/h" : m.Enabled ? "--" : "Off", "How fast the room is cooling."),
                    new("Target ETA", eta, "Estimated minutes until target."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Weather Drift Timing", GuardCategory.Sensor,
            "Times safe corrections to real outdoor-weather movement instead of firing immediately.",
            "Real outdoor-temperature samples (oldest versus newest) inside the weather window.",
            "After a wall touch, while the room is inside the weather safety band, stable or cooling outdoor temperatures let it hold for the weather hold minutes. Once the outdoor temperature genuinely warms by the minimum change, the hold clears so the correction lines up with real weather. A too-warm room clears it.",
            "Holds the safe correction until outdoor weather moves.",
            ["WeatherDriftGuardEnabled", "WeatherDriftWindowMinutes", "WeatherDriftMinimumChangeCelsius", "WeatherDriftHoldMinutes", "WeatherDriftSafetyBandCelsius"],
            s =>
            {
                var w = s.WeatherDrift;
                var tone = !w.Enabled ? GuardTone.Off
                    : w.Holding ? GuardTone.Holding
                    : w.Direction == "warming" ? GuardTone.Success : GuardTone.Calm;
                var label = !w.Enabled ? "Off" : w.Holding ? "Holding" : "Watching";
                var delta = w.OutdoorDeltaCelsius is { } d ? $" ({d:+0.0;-0.0;0.0} C)" : "";
                return new GuardLiveView(w.Enabled, w.Holding, label, tone, w.Status,
                [
                    new("Weather drift", w.Enabled ? $"{w.Direction}{delta}" : "Off", "Outdoor temperature direction."),
                    new("Weather hold", OffWait(w.Enabled, w.Holding, w.SecondsRemaining), "Wait for a natural weather slot."),
                ]);
            }),

        // ───────────────────────────── System ─────────────────────────────
        new GuardInfo(
            "Website Debounce", GuardCategory.System,
            "Blocks repeated website button taps for two minutes so the UI does not spam Home Assistant.",
            "The last website command name and time.",
            "The first click runs; later clicks within the debounce seconds show the remaining wait instead of resending. Emergency actions bypass the debounce and then start a fresh window.",
            "Rejects duplicate website actions until the window clears.",
            ["(fixed at 120 seconds)"],
            s =>
            {
                var d = s.WebsiteCommandDebounce;
                return GuardLiveView.Standard(true, d.Active, "Locked", d.Status,
                [
                    new("Website lock", d.Active ? $"{d.SecondsRemaining}s" : "Ready", "Two-minute wait after a website action."),
                ], busyTone: GuardTone.Warning);
            }),

        new GuardInfo(
            "Super Defender", GuardCategory.System,
            "Detects repeated phone/Home Assistant thermostat changes and tightens correction timing without cutting thermostat Wi-Fi.",
            "Home Assistant context on climate state changes: user_id, parent_id, and context id.",
            "Changes with user_id count as Home Assistant user or phone changes. Changes with parent_id count as automation/script changes. Repeated remote-style changes inside the configured window arm Super Defender for the hold minutes. While active and the room still needs cooling, it can bypass subtle quiet waits. Wi-Fi blocking is intentionally manual only because cutting the thermostat off can also remove monitoring and recovery.",
            "Shows source attribution, arms a strict response window, and can bypass quiet timing while cooling is needed.",
            ["SuperDefenderModeEnabled", "SuperDefenderRemoteChangeThreshold", "SuperDefenderWindowMinutes", "SuperDefenderHoldMinutes", "SuperDefenderSafetyBandCelsius", "SuperDefenderBypassQuietTiming"],
            s =>
            {
                var d = s.SuperDefender;
                var tone = d.BypassingQuietTiming ? GuardTone.Alert : d.Active ? GuardTone.Warning : GuardTone.Calm;
                var label = !d.Enabled ? "Off" : d.BypassingQuietTiming ? "Strict" : d.Active ? "Armed" : "Watching";
                return new GuardLiveView(d.Enabled, d.Active || d.BypassingQuietTiming, label, tone, d.Status,
                [
                    new("Remote changes", d.Enabled ? d.RecentRemoteChangeCount.ToString() : "Off", "Phone/Home Assistant-style changes inside the window."),
                    new("Strict wait", d.Active ? $"{d.SecondsRemaining}s" : "Ready", "Time left in the strict window."),
                    new("Last source", d.LastChangeSource, "Best source classification from Home Assistant context."),
                    new("Wi-Fi block", "Manual only", d.NetworkLockdownStatus),
                ]);
            }),

        new GuardInfo(
            "Emergency Protocols", GuardCategory.System,
            "One-tap stand-down modes for too-cold, someone-upset, and suspicion situations.",
            "The chosen protocol and its remaining window.",
            "Too cold (30 min) pauses the defender and turns the thermostat off. Someone upset (45 min) and Suspicion quiet (90 min) keep reading the thermostat 24/7 but send no corrective commands until the window ends. Emergency actions bypass the website debounce.",
            "Suppresses corrective commands for the protocol window.",
            ["(run from the Controls page)"],
            s =>
            {
                var e = s.Emergency;
                return GuardLiveView.Standard(true, e.Active, "Active", e.Status,
                [
                    new("Protocol", e.Active ? e.Protocol : "None", "Which emergency mode is active."),
                    new("Wait", e.Active ? $"{e.SecondsRemaining}s" : "Ready", "Time left in the emergency window."),
                ], busyTone: GuardTone.Warning);
            }),

        new GuardInfo(
            "Cooling Failure Watch", GuardCategory.System,
            "Raises a repeating mega-alert when cool mode is demanded but the AC is not really cooling, and escalates to a full-site OMEGA alert when a rising room confirms it.",
            "Real Home Assistant data only: hvac_mode, hvac_action, the setpoint, and room-temperature history.",
            "MEGA: it alerts if the entity is in cool, the room is clearly above the setpoint, and the action stays idle for several minutes (possible breaker/equipment), or if the action says cooling but the room does not drop over the retained window (possible compressor/airflow). OMEGA: while the idle/breaker mega alert is up, if the room has also risen at least 0.4 C over the last 5 minutes — what a dead breaker looks like — it escalates to a full-site OMEGA alert. Requiring a real, sustained rise (and only on the idle branch) keeps false positives down. Alerts repeat about once a minute.",
            "Surfaces a red alert, an event log entry, and (on OMEGA) a site-wide overlay; it never changes thermostat commands.",
            ["(automatic monitoring)"],
            s =>
            {
                var c = s.CoolingFailure;
                var tone = c.Alerting ? GuardTone.Alert : GuardTone.Calm;
                var label = c.OmegaAlerting ? "OMEGA" : c.Alerting ? "Alerting" : "Watching";
                return new GuardLiveView(true, c.Alerting, label, tone, c.Status,
                [
                    new("Active for", c.Alerting ? $"{c.SecondsActive}s" : "Ready", "How long the alert has been raised."),
                    new("Alerts", c.AlertCount.ToString(), "How many times it has fired."),
                    new("Room rise", c.RoomRiseCelsius is { } r ? $"{r:+0.0;-0.0;0.0} C" : "--", "Room change over the OMEGA confirmation window."),
                ]);
            }),

        // ───────────────── Guide-only (no live card) ─────────────────
        new GuardInfo(
            "Dynamic Cooldown", GuardCategory.System,
            "A frequency-based quiet period after a manual thermostat change.",
            "How many wall touches happened recently inside the touch-frequency window.",
            "cooldown = min(MaxCooldownSeconds, BaseCooldownSeconds × recentTouchCount) + a small random quiet delay. More repeated changes mean longer cooldowns.",
            "Holds the next correction until the cooldown elapses.",
            ["BaseCooldownSeconds", "MaxCooldownSeconds", "TouchFrequencyWindowMinutes"]),

        new GuardInfo(
            "Fan Energy Saver", GuardCategory.System,
            "Optionally moves the fan to an energy-saving mode when the room is near target.",
            "Room temperature versus target and the thermostat's available fan modes.",
            "When enabled and the room is within the threshold of target, if the configured fan mode exists on the device it calls climate.set_fan_mode.",
            "Sets the fan to the saver mode; otherwise leaves the fan alone.",
            ["FanEnergySaverEnabled", "FanEnergySaverThresholdCelsius", "FanEnergySaverMode"]),

        new GuardInfo(
            "Upstairs Comfort Guard", GuardCategory.System,
            "Prioritizes cooling when upstairs rooms get hot while someone is home.",
            "The hottest configured (or auto-discovered) upstairs temperature sensor and optional presence entities.",
            "If the hottest upstairs room exceeds the comfort maximum, it lowers the target toward the comfort target and adds the cooling boost. Severe upstairs heat bypasses cooldown so comfort wins. When presence is required and nobody is detected, it assumes home rather than under-cooling.",
            "Lowers the effective target and can bypass quiet timing.",
            ["UpstairsComfortEnabled", "UpstairsTemperatureEntityIds", "UpstairsMaxComfortCelsius", "UpstairsComfortTargetCelsius", "UpstairsComfortBoostCelsius", "HomePresenceRequired", "PresenceEntityIds"]),

        new GuardInfo(
            "Schedule & Weather Rules", GuardCategory.System,
            "Time-of-day target rules, each gated by a weather activation condition.",
            "The active schedule entry for the current day/time and the weather rule.",
            "When the custom schedule is on, the matching rule supplies the target. Weather rules (always, room-above-outdoor, room-below-outdoor, outdoor-above-target, outdoor-below-target) decide whether corrective action is allowed. The defender still reads Home Assistant 24/7 even when a rule blocks correction.",
            "Sets the target and whether corrective action runs.",
            ["ScheduleEnabled", "WeatherActivationMode", "(per-rule Days / Start / End / Target / Weather)"]),
    ];

    /// <summary>Catalog entries that expose a live card (used by the Defense board).</summary>
    public static IEnumerable<GuardInfo> Live => All.Where(g => g.Project is not null);
}
