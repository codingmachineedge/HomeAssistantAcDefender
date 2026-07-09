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
            "Once recent touches reach the trigger count and the room is inside the walkback safety band, each command moves only about the walkback step (plus a tiny jitter) toward target. A warm room that needs direct cooling skips walkback and still commands the configured warm-room approach below the current room temperature (0.5 C by default).",
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
            "Human Nudge", GuardCategory.WallTouch,
            "Makes the final safe setpoint command look like a normal thermostat step instead of a precise bot number.",
            "Recent wall touches, the candidate defender command, the current thermostat setpoint, and room temperature.",
            "After repeated touches and while the room is inside the safe band, it snaps only safe follow-up commands to the configured human step size. Direct warm-room cooling, upstairs heat, or quiet-timing bypasses skip this shaper.",
            "Rewrites the outgoing safe setpoint to a normal one-step-looking value.",
            ["HumanNudgeEnabled", "HumanNudgeTriggerTouches", "HumanNudgeStepCelsius", "HumanNudgeSafetyBandCelsius"],
            s =>
            {
                var h = s.HumanNudge;
                return GuardLiveView.Standard(h.Enabled, h.Active, "Shaping", h.Status,
                [
                    new("Step", h.Enabled ? $"{h.StepCelsius:0.0} C" : "Off", "Normal thermostat step size to imitate."),
                    new("Touches", h.Enabled ? h.RecentTouchCount.ToString() : "Off", "Recent external thermostat changes."),
                    new("Last nudge", h.LastSetPointCelsius is { } sp ? $"{sp:0.0} C" : h.Enabled ? "Ready" : "Off", "Last command after human nudge shaping."),
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
            "Command Camouflage", GuardCategory.WallTouch,
            "Gives a recent helper command time to look normal before another safe correction appears.",
            "The last real helper setpoint command, recent helper-command pressure, recent wall-touch pressure, and the room temperature.",
            "After a setpoint command, it waits at least the minimum gap plus pressure-scaled extra seconds before another safe correction. Higher recent touch or command pressure makes the gap longer. A room over the safety band or any comfort/safety bypass clears it immediately.",
            "Holds the next safe correction until the recent command has enough spacing.",
            ["CommandCamouflageEnabled", "CommandCamouflageMinimumGapSeconds", "CommandCamouflagePressureExtraSeconds", "CommandCamouflageSafetyBandCelsius"],
            s =>
            {
                var c = s.CommandCamouflage;
                return GuardLiveView.Standard(c.Enabled, c.Holding, "Covering", c.Status,
                [
                    new("Cover wait", OffWait(c.Enabled, c.Holding, c.SecondsRemaining), "Time left before another safe command looks spaced out."),
                    new("Pressure", Score(c.Enabled, c.Pressure), "How strongly recent touches or helper commands extend the gap."),
                    new("Commands", c.Enabled ? c.RecentCommandCount.ToString() : "Off", "Recent helper setpoint commands in the rolling budget window."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Stealth Governor", GuardCategory.WallTouch,
            "Runs a whole-system low-profile hold when wall touches, noticed corrections, remote changes, and helper commands make the defender look too busy.",
            "Recent wall-touch pressure, noticed-correction pressure, Home Assistant remote-change pressure, helper command count, and room temperature.",
            "It computes a 0-100 pressure score. If the score reaches the trigger and the room is inside the safety band, it holds the next safe correction for a min-to-max low-profile window scaled by the score. Direct comfort needs, upstairs heat, or a quiet-timing bypass clear it.",
            "Holds only safe corrections until the low-profile window ends.",
            ["StealthGovernorEnabled", "StealthGovernorTriggerScore", "StealthGovernorMinimumHoldMinutes", "StealthGovernorMaximumHoldMinutes", "StealthGovernorSafetyBandCelsius"],
            s =>
            {
                var g = s.StealthGovernor;
                return GuardLiveView.Standard(g.Enabled, g.Holding, "Low profile", g.Status,
                [
                    new("Pressure", g.Enabled ? $"{g.Score}/{g.TriggerScore}" : "Off", "Overall stealth pressure versus trigger."),
                    new("Low wait", OffWait(g.Enabled, g.Holding, g.SecondsRemaining), "Time left in low-profile hold."),
                    new("Touches", g.Enabled ? g.RecentTouchCount.ToString() : "Off", "Recent external thermostat changes."),
                    new("Commands", g.Enabled ? g.RecentCommandCount.ToString() : "Off", "Recent helper setpoint commands."),
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
            "Tug-of-War Truce", GuardCategory.WallTouch,
            "Calls a temporary truce when the real thermostat bounces up and down, so answer-back commands do not look like a duel.",
            "The real external thermostat audit log: previous setpoint, new setpoint, timestamp, and source classification.",
            "Inside the configured flip window it converts each external setpoint change into up/down/flat, counts direction flips, and compares that count to the flip trigger. If the flip trigger is met and the room is still inside the safety band, it holds only safe answer-back corrections for the truce minutes. A warm room, severe upstairs heat, matching setpoint, cooler-intent fast lane, or Super Defender strict bypass clears it.",
            "Holds safe corrections until the truce window ends, then lets the normal defender chain continue.",
            ["TugOfWarTruceEnabled", "TugOfWarTruceMinimumFlips", "TugOfWarTruceWindowMinutes", "TugOfWarTruceHoldMinutes", "TugOfWarTruceSafetyBandCelsius"],
            s =>
            {
                var t = s.TugOfWarTruce;
                return GuardLiveView.Standard(t.Enabled, t.Holding, "Truce", t.Status,
                [
                    new("Truce wait", OffWait(t.Enabled, t.Holding, t.SecondsRemaining), "Time left before answer-back commands can resume."),
                    new("Flips", t.Enabled ? $"{t.FlipCount}/{t.TriggerFlips}" : "Off", "Up/down direction flips inside the window."),
                    new("Pattern", t.Enabled ? t.DirectionPattern : "Off", "Compact recent setpoint direction pattern."),
                ], busyTone: GuardTone.Warning);
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
            "If repeated touches move the wall cooler by at least the cool threshold and the room is above target, it clears quiet waits (cooldown, grace, conflict quiet, cadence, repeat quiet, sensor rhythm, runway, and more) for the hold minutes. It never lowers the website target — warm-room cooling still starts at the current room temperature minus the configured WarmRoomApproachCelsius (0.5 °C by default), rather than subtracting the approach from the wall setpoint, and continues toward—but never below—the website target. A room over the safety band hands control back to normal safety rules.",
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
            "Setpoint Stillness", GuardCategory.Sensor,
            "Waits until the wall setpoint stops moving before a safe correction answers back.",
            "Real Home Assistant climate readings, the current reported setpoint, recent wall touches, and room temperature.",
            "After repeated external touches, while the room is still inside the safe band, it requires a few consecutive real Home Assistant readings at the same wall setpoint before allowing a safe correction. If the room gets too warm, a cooler-intent fast lane is active, the expected setpoint is already reached, or the max hold expires, it steps aside.",
            "Delays only safe corrections until the wall setpoint looks settled.",
            ["SetpointStillnessGuardEnabled", "SetpointStillnessTriggerTouches", "SetpointStillnessRequiredSamples", "SetpointStillnessMaxHoldSeconds", "SetpointStillnessToleranceCelsius", "SetpointStillnessSafetyBandCelsius"],
            s =>
            {
                var p = s.SetpointStillness;
                return GuardLiveView.Standard(p.Enabled, p.Holding, "Holding", p.Status,
                [
                    new("Stillness wait", OffWait(p.Enabled, p.Holding, p.SecondsRemaining), "Time left waiting for matching setpoint readings."),
                    new("Stable reads", p.Enabled ? $"{p.StableSampleCount}/{p.RequiredStableSamples}" : "Off", "Consecutive real readings at the same setpoint."),
                    new("Wall setpoint", p.CurrentSetPointCelsius is { } sp ? $"{sp:0.0} C" : p.Enabled ? "--" : "Off", "Current reported thermostat setpoint."),
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
            "HVAC Alibi", GuardCategory.Sensor,
            "Waits for a real HVAC action transition so a safe correction lands near a normal thermostat event.",
            "The current Home Assistant hvac_action, the last action transition, recent wall touches, and room temperature.",
            "After repeated wall touches, while the room is still inside the safety band, it can hold a safe correction until hvac_action changes (for example idle to cooling or cooling to idle). A recent transition can also clear the hold. Direct comfort needs, upstairs heat, or a too-warm room bypass the wait immediately.",
            "Delays only safe corrections until a real HVAC action transition or the max hold expires.",
            ["HvacActionAlibiEnabled", "HvacActionAlibiTriggerTouches", "HvacActionAlibiTransitionWindowSeconds", "HvacActionAlibiMaxHoldMinutes", "HvacActionAlibiSafetyBandCelsius"],
            s =>
            {
                var a = s.HvacActionAlibi;
                return GuardLiveView.Standard(a.Enabled, a.Waiting, "Waiting", a.Status,
                [
                    new("Alibi wait", OffWait(a.Enabled, a.Waiting, a.SecondsRemaining), "Time left waiting for a real hvac_action transition."),
                    new("Action", a.Enabled ? a.CurrentAction : "Off", "Current Home Assistant hvac_action."),
                    new("Touches", a.Enabled ? a.RecentTouchCount.ToString() : "Off", "Recent external thermostat changes."),
                    new("Last transition", a.LastTransitionAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : a.Enabled ? "--" : "Off", "Most recent real action transition time."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Telemetry Alibi", GuardCategory.Sensor,
            "Waits for a normal Home Assistant/weather/usage update before a safe correction, so the nudge is not an isolated event.",
            "Recent wall touches, real Home Assistant reading beats, weather samples, Alectra Hui usage updates, and room temperature.",
            "After repeated wall touches, while the room is still inside the safety band, it starts a short quiet hold and then waits for the next enabled real telemetry signal. A too-warm room, direct comfort need, matching setpoint, disabled signal source, or max wait clears the hold.",
            "Delays only safe corrections until a normal house telemetry update can act as cover.",
            ["TelemetryAlibiEnabled", "TelemetryAlibiTriggerTouches", "TelemetryAlibiMinimumHoldSeconds", "TelemetryAlibiMaxHoldMinutes", "TelemetryAlibiSafetyBandCelsius", "TelemetryAlibiUseWeather", "TelemetryAlibiUseSensorBeat", "TelemetryAlibiUsePeakPower"],
            s =>
            {
                var a = s.TelemetryAlibi;
                return GuardLiveView.Standard(a.Enabled, a.Waiting, "Waiting", a.Status,
                [
                    new("Telemetry wait", OffWait(a.Enabled, a.Waiting, a.SecondsRemaining), "Time left waiting for a normal telemetry cover signal."),
                    new("Touches", a.Enabled ? a.RecentTouchCount.ToString() : "Off", "Recent external thermostat changes."),
                    new("Signal", a.Enabled ? a.LastSignal : "Off", "Newest enabled telemetry signal."),
                    new("Signal time", a.LastSignalAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : a.Enabled ? "--" : "Off", "When the newest telemetry signal arrived."),
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
            "Rival Schedule Watch", GuardCategory.System,
            "Knows the AC vendor app's own temperature schedule (SLEEP / DEEP SLEEP / GOOD MORNING) and defends my temp when a scheduled block pushes the wall warmer while everyone sleeps.",
            "The configured rival AC-app schedule blocks (start time + low/high setpoints per weekday), the live wall setpoint, Home Assistant change context, and the local clock.",
            "The blocks are configuration (appsettings/environment), never code. A setpoint change that is not from a Home Assistant user and lands on the active block's low/high number is attributed to the AC app schedule instead of a human wall touch — so it starts no cooldown, no comfort grace, no touch counters, no peace offering, and teaches nothing to comfort memory/compromise (otherwise the schedule would train the defender to like the rival's warm blocks). While the wall sits at a scheduled setpoint above my temp and the room is warm, quiet waits are bypassed: a schedule is a machine running while the household sleeps, so nobody is watching the correction. My temp is never changed by the rival schedule, and extreme heat still defers to normal comfort safety. The vendor app's Fan schedule tab is reserved in configuration but not enforced yet.",
            "Attributes schedule pushes in the audit log, announces block boundaries as events, and answers a scheduled warm push back toward my temp without human-style delays.",
            ["RivalScheduleWatchEnabled", "RivalScheduleSetpointToleranceCelsius", "RivalScheduleBypassQuietTiming", "RivalScheduleSafetyBandCelsius", "RivalScheduleBlocks", "RivalFanScheduleBlocks"],
            s =>
            {
                var d = s.RivalSchedule;
                if (d is null)
                {
                    return GuardLiveView.Standard(false, false, "Off", "Rival schedule watch is not configured.",
                    [
                        new("Blocks", "0", "Configured AC-app temperature schedule blocks."),
                    ]);
                }

                // Historical matches remain audit evidence; only the current cycle's real bypass
                // verdict can put this card in the engaged roster.
                return GuardLiveView.Standard(d.Enabled, d.Active, "Answering", d.Status,
                [
                    new("Blocks", d.BlockCount.ToString(), "Configured AC-app temperature schedule blocks."),
                    new("Active block", d.ActiveBlockName, d.ActiveLowSetPointCelsius is { } low && d.ActiveHighSetPointCelsius is { } high
                        ? $"Scheduled setpoints {low:0.0}/{high:0.0} C."
                        : "No rival block is in force right now."),
                    new("Next block", d.NextBlockStartsAt is { } startsAt
                        ? $"{d.NextBlockName} at {startsAt.ToLocalTime():HH:mm}"
                        : d.NextBlockName, "The next boundary in the rival app's schedule."),
                    new("Schedule pushes", d.MatchCount.ToString(), "Wall changes attributed to the AC app schedule instead of a human."),
                    new("Last push", d.LastMatchAt is { } lastAt
                        ? $"{d.LastMatchBlockName} → {d.LastMatchSetPointCelsius:0.0} C at {lastAt.ToLocalTime():HH:mm:ss}"
                        : "none", "Most recent setpoint attributed to the rival schedule."),
                ], busyTone: GuardTone.Warning);
            }),

        new GuardInfo(
            "Cool-Outdoor Shutdown (Open-Window Armistice)", GuardCategory.System,
            "When it is genuinely cool outside and the forecast says it stays cool, the defender turns the AC fully off — and turns it back on by itself when the weather or the room demands it.",
            "The real outdoor temperature, the hourly Home Assistant forecast over the gate hours, the room temperature, the thermostat mode, and the minimum-off dwell clock.",
            "Below the shutdown threshold, and only when the forecast peak over the gate hours stays under threshold+margin (no off/on flapping before a hot afternoon), it sends ONE off command per cool episode and stands guard. It restores cool mode on its own once outdoor warms past threshold+margin (after the minimum off dwell) — or immediately, dwell ignored, if the room crosses the safety band. Someone turning the AC back on mid-episode wins for the rest of that episode; an AC already off by hand is adopted without a command. Unknown outdoor or a missing forecast means it does nothing new; safety bands always win. While it holds the AC off, the quiet minutes bank food rations.",
            "Sends climate.set_hvac_mode = off once per cool episode, then a tagged automatic restore.",
            ["CoolOutdoorShutdownEnabled", "CoolOutdoorShutdownBelowCelsius", "CoolOutdoorRestoreMarginCelsius", "CoolOutdoorMinimumOffMinutes", "CoolOutdoorForecastGateEnabled", "CoolOutdoorForecastGateHours", "ForecastRefreshMinutes"],
            s =>
            {
                if (s.CoolOutdoorShutdown is not { } c)
                {
                    return GuardLiveView.Standard(false, false, "Off", "Cool-Outdoor Shutdown is not available yet.",
                    [
                        new("Outdoor now", "--", "The live outdoor temperature."),
                    ]);
                }

                var coolOutdoorTone = !c.Enabled ? GuardTone.Off
                    : c.HumanOverride ? GuardTone.Calm
                    : c.Holding ? GuardTone.Holding
                    : GuardTone.Info;
                var coolOutdoorLabel = !c.Enabled ? "Off"
                    : c.HumanOverride ? "Human override"
                    : c.Holding ? "AC OFF"
                    : "Watching";
                return new GuardLiveView(c.Enabled, c.Holding, coolOutdoorLabel, coolOutdoorTone, c.Status,
                [
                    new("Outdoor now", c.OutdoorCelsius is { } outdoorNow ? $"{outdoorNow:0.0} C" : "--", "The live outdoor temperature."),
                    new("Shutdown below", $"{c.ThresholdCelsius:0.0} C", "Outdoor temperatures under this may turn the AC fully off."),
                    new("Restores at", $"{c.RestoreAtCelsius:0.0} C", "Outdoor warming past this brings cool mode back (after the off dwell)."),
                    new("Forecast peak", c.ForecastMaxCelsius is { } forecastPeak ? $"{forecastPeak:0.0} C" : "no forecast", "The hottest forecast temperature inside the gate hours."),
                    new("Forecast gate", !c.ForecastGateEnabled ? "Off" : c.ForecastGatePassed ? "Pass" : "Blocking", "The forecast must agree it stays cool before a shutdown."),
                    new("Off dwell", c.Holding && c.OffDwellSecondsRemaining > 0
                        ? $"{c.OffDwellSecondsRemaining / 60}m {c.OffDwellSecondsRemaining % 60}s"
                        : "—", "Minimum time the AC stays off before a weather restore."),
                ]);
            }),

        new GuardInfo(
            "Siesta Watch (mess hall)", GuardCategory.System,
            "Lets the whole guard force nap on command; while they sleep the AC eases off and the money it would have spent is banked as food rations.",
            "The siesta timer, the room temperature against the wake band, the budget safety maximum, and the thermostat mode.",
            "A siesta starts from the dashboard (1h/2h/4h) and parks the thermostat — or turns it off — exactly once; a human changing it back mid-nap is respected, the accrual just pauses while the unit cools. The guards wake on the timer, immediately when the room passes target + wake band or the budget safety maximum, on cancel, or when an emergency fires or the master switch pauses the defender. Rations already earned are always kept.",
            "Holds the whole correction pipeline while the nap timer runs; sends one park/off command at the start.",
            ["SiestaEnabled", "SiestaThermostatAction", "SiestaWakeBandCelsius", "SiestaMaxMinutes"],
            s =>
            {
                if (s.Siesta is not { } nap)
                {
                    return GuardLiveView.Standard(false, false, "Off", "Siesta Watch is not available yet.",
                    [
                        new("Nap ends", "—", "When the guards wake up."),
                    ]);
                }

                var siestaTone = !nap.Enabled ? GuardTone.Off
                    : nap.Active ? GuardTone.Info
                    : GuardTone.Calm;
                var siestaLabel = !nap.Enabled ? "Off"
                    : nap.Active ? "Sleeping"
                    : "On duty";
                return new GuardLiveView(nap.Enabled, nap.Active, siestaLabel, siestaTone, nap.Status,
                [
                    new("Nap ends", nap.Active && nap.Until is { } wake
                        ? $"{wake.ToLocalTime():HH:mm} ({nap.SecondsRemaining / 60} min left)"
                        : "—", "When the guards wake up."),
                    new("Reason", nap.Active ? nap.Reason : "—", "Manual button or the cool-outdoor shutdown."),
                    new("Rations this nap", $"${nap.FoodEarnedThisSiestaCad:0.00}", "Dollars banked so far during this siesta."),
                    new("Start action", nap.ThermostatAction, "What happens to the AC when a nap starts: park the setpoint or turn it off."),
                ]);
            }),

        new GuardInfo(
            "Field Kitchen (food rations)", GuardCategory.System,
            "Banks unspent AC dollars during siestas and cool-outdoor shutdowns, and spends them on forecast-hot days so the monthly budget eases exactly when cooling matters most.",
            "The pantry balance and cap, the trailing-week compressor duty cycle, the Alectra TOU rate in force, the hourly forecast over the release lookahead, and the AC's real per-slice estimated cost.",
            "While the guards nap, every quiet minute banks the money the AC would probably have spent — its usual share of run-time from the last week × its assumed power draw × the Alectra rate right now. On a forecast-hot day the pantry pays the AC's bill: every dollar the AC actually spends during the hot window comes out of the food balance instead of counting against the monthly budget (up to the per-day cap, only while over pace). A slice where the compressor actually cools earns nothing, and no usage history means no accrual — the pantry never invents savings. Rations can also summon the WinForge reactor's AI operator — one ration per hour.",
            "Adjusts the monthly budget's over/under bookkeeping; moves no real money and sends no thermostat commands.",
            ["FoodRationsEnabled", "FoodBalanceMaxCad", "FoodReleaseHotThresholdCelsius", "FoodReleaseLookaheadHours", "FoodReleaseMaxPerDayCad", "ReactorPowerEnabled", "FoodRationSizeCad"],
            s =>
            {
                if (s.FoodRations is not { } pantry)
                {
                    return GuardLiveView.Standard(false, false, "Off", "The field kitchen is not available yet.",
                    [
                        new("Pantry", "--", "Live ration evidence is unavailable."),
                    ]);
                }

                var pantryTone = !pantry.Enabled ? GuardTone.Off
                    : pantry.HotWindowActive && pantry.BalanceCad > 0 ? GuardTone.Success
                    : pantry.BalanceCad > 0 ? GuardTone.Info
                    : GuardTone.Calm;
                var pantryLabel = !pantry.Enabled ? "Off"
                    : pantry.HotWindowActive && pantry.BalanceCad > 0 ? "Paying the bill"
                    : pantry.BalanceCad > 0 ? "Stocked"
                    : "Empty";
                return new GuardLiveView(pantry.Enabled, pantry.HotWindowActive && pantry.BalanceCad > 0, pantryLabel, pantryTone, pantry.Status,
                [
                    new("Pantry", $"${pantry.BalanceCad:0.00} / ${pantry.BalanceMaxCad:0.00}", "Banked ration dollars against the cap."),
                    new("Earned today", $"${pantry.EarnedTodayCad:0.00}", "Rations banked so far today."),
                    new("Released this month", $"${pantry.ReleasedThisMonthCad:0.00}", "Hot-window dollars the pantry already paid off the budget."),
                    new("Hot window", pantry.HotWindowActive
                        ? (pantry.ForecastMaxCelsius is { } hotMax ? $"Yes ({hotMax:0.0} C peak)" : "Yes (live outdoor)")
                        : "No", $"Releases run when the forecast peaks at or above {pantry.HotThresholdCelsius:0.0} C."),
                    new("Duty cycle", $"{pantry.DutyCyclePercent:0.0}%", "The AC's usual share of run-time from the last week — the accrual honesty factor."),
                    new("Rations", pantry.RationsAvailable.ToString(), $"Spendable units of ${pantry.RationSizeCad:0.00}; one ration powers the WinForge reactor for an hour."),
                    new("Reactor", pantry.ReactorPowerActive && pantry.ReactorPowerUntil is { } poweredUntil
                        ? $"Powered until {poweredUntil.ToLocalTime():HH:mm}"
                        : "Unpowered", "The WinForge Web reactor's AI-operator voucher."),
                ]);
            }),

        new GuardInfo(
            "Desired-State Enforcer", GuardCategory.System,
            "Makes the owner's chosen AC state win automatically: if someone else turns the unit off or moves the setpoint, it restores the exact desired state and keeps it there.",
            "Home Assistant HVAC mode, the live setpoint vs the owner's target, context.user_id attribution, recent override/assert counts, and the learned interference probability.",
            "When a change is attributed to someone other than the owner (or has no owner user_id) it debounces, then either lets the human-like stealth pipeline ease the setpoint back (smart-stealth mode) or snaps to the exact target (hard mode). Cooldown, device-reject backoff, and a rate limit stop it thrashing; repeated overrides escalate it to firm mode and an optional notification. Owner changes are respected. It clamps to the device min/max and never acts while Home Assistant is unreachable.",
            "Restores the desired mode/setpoint, escalates on repeated interference, and notifies — using the trained interference/cadence models to pace itself.",
            ["EnforcerModeEnabled", "EnforcerTargetTemperatureCelsius", "EnforcerEnforceMode", "EnforcerEnforceSetpoint", "EnforcerStealthShaping", "EnforcerRespectOwner", "EnforcerOwnerUserIds", "EnforcerDebounceSeconds", "EnforcerCooldownSeconds", "EnforcerRateWindowMinutes", "EnforcerMaxAssertsPerWindow", "EnforcerEscalateAfterOverrides", "EnforcerBackoffBaseSeconds", "EnforcerBackoffMaxSeconds", "EnforcerScheduleEnabled", "EnforcerStartTime", "EnforcerEndTime", "EnforcerRequirePresence", "EnforcerNotifyEnabled", "EnforcerUseLearning"],
            s =>
            {
                var e = s.Enforcer;
                var tone = !e.Enabled ? GuardTone.Off
                    : e.Escalated ? GuardTone.Alert
                    : e.Active ? GuardTone.Warning
                    : GuardTone.Calm;
                var label = !e.Enabled ? "Off"
                    : e.Escalated ? "Escalated"
                    : e.Active ? (e.Stealth ? "Enforcing (stealth)" : "Enforcing")
                    : "Watching";
                return new GuardLiveView(e.Enabled, e.Active || e.Escalated, label, tone, e.Status,
                [
                    new("Desired", e.Enabled ? $"{e.DesiredTargetCelsius:0.0} C" : "Off", "Exact temperature the owner wants held."),
                    new("Overrides", e.Enabled ? e.RecentOverrideCount.ToString() : "Off", "Unwanted external overrides inside the window."),
                    new("Asserts", e.Enabled ? e.RecentAssertCount.ToString() : "Off", "Firm enforce commands inside the window."),
                    new("Last changed by", e.Enabled ? e.LastChangeUser : "Off", "Home Assistant user_id attribution for the last change."),
                    new("Interference model", e.LearningActive ? $"{e.InterferenceProbability:0.00}" : "Learning", "Trained probability that interference is happening now."),
                ]);
            }),

        new GuardInfo(
            "Remote Settling Guard", GuardCategory.System,
            "Gives repeated phone/Home Assistant or automation thermostat changes a quiet settling window before a safe answer-back.",
            "Home Assistant change source attribution, recent remote-style change count, room temperature, and the expected setpoint.",
            "When Home Assistant context shows repeated user/phone or automation changes inside the configured window, and the room is still inside the safety band, it holds only safe corrections for the quiet hold minutes. A too-warm room, cooler intent, matching setpoint, disabled setting, or expired hold releases it immediately.",
            "Delays only safe corrections after remote-style thermostat changes so the response does not look instant.",
            ["RemoteSettlingGuardEnabled", "RemoteSettlingTriggerChanges", "RemoteSettlingWindowMinutes", "RemoteSettlingHoldMinutes", "RemoteSettlingSafetyBandCelsius"],
            s =>
            {
                var r = s.RemoteSettling;
                return GuardLiveView.Standard(r.Enabled, r.Holding, "Holding", r.Status,
                [
                    new("Remote wait", OffWait(r.Enabled, r.Holding, r.SecondsRemaining), "Quiet hold after Home Assistant-side changes."),
                    new("Remote changes", r.Enabled ? $"{r.RecentRemoteChangeCount}/{r.TriggerRemoteChangeCount}" : "Off", "Phone/Home Assistant changes inside the window."),
                    new("Last source", r.Enabled ? r.LastChangeSource : "Off", "Last external source classification from Home Assistant context."),
                ], busyTone: GuardTone.Holding);
            }),

        new GuardInfo(
            "Alectra Peak Power Saver", GuardCategory.System,
            "Makes the defender more chill and resource-saving when Alectra Hui reports on-peak, high price, or high power use.",
            "Alectra Hui current TOU period, current price, current power, and current plan sensors from Home Assistant.",
            "When enabled, On-peak TOU, price above the c/kWh threshold, or current power above the kW threshold arms a short saver window. During that window it holds only safe cooling commands that would demand more cooling, and it can set the configured fan saver mode if the room is still inside the safety band. If the room or upstairs gets too hot, or the command would save energy by warming the setpoint, it steps aside.",
            "Holds safe cooling during expensive/high-load periods and prefers the saver fan mode.",
            ["PeakPowerSaverEnabled", "PeakPowerSaverOnPeakEnabled", "PeakPowerSaverHighPowerEnabled", "PeakPowerSaverPowerThresholdKilowatts", "PeakPowerSaverPriceThresholdCentsPerKwh", "PeakPowerSaverHoldMinutes", "PeakPowerSaverSafetyBandCelsius", "PeakPowerSaverFanSaverEnabled", "PeakPowerSaverFanMode"],
            s =>
            {
                var p = s.PeakPowerSaver;
                var tone = !p.Enabled ? GuardTone.Off
                    : p.Holding ? GuardTone.Holding
                    : p.Active ? GuardTone.Warning : GuardTone.Calm;
                var label = !p.Enabled ? "Off" : p.Holding ? "Holding" : p.Active ? "Peak" : "Watching";
                return new GuardLiveView(p.Enabled, p.Active || p.Holding, label, tone, p.Status,
                [
                    new("Power", p.CurrentPowerKilowatts is { } kw ? $"{kw:0.00} kW" : "--", $"Threshold {p.PowerThresholdKilowatts:0.0} kW."),
                    new("Price", p.CurrentPriceCentsPerKwh is { } cents ? $"{cents:0.0} c/kWh" : "--", $"Threshold {p.PriceThresholdCentsPerKwh:0.0} c/kWh."),
                    new("TOU", p.TouPeriod, "Alectra Hui current TOU period."),
                    new("Saver wait", p.Holding ? $"{p.SecondsRemaining}s" : p.Active ? "Armed" : "Ready", "Safe cooling hold window."),
                ]);
            }),

        new GuardInfo(
            "Front-door Guard Post", GuardCategory.System,
            "Pauses the defender and can turn the thermostat off when a real front-door person detector trips.",
            "Configured or auto-discovered Home Assistant front-door person sensors.",
            "The worker reads the configured entities, or auto-discovers likely front-door/porch/entry person sensors. If any detector reports a person, the defender pauses immediately, holds the guard window, and sends thermostat OFF if that setting is enabled. The source is recorded as the front-door guard post so it does not look like a wall touch.",
            "Runs the kill switch, hides the live boards while paused, and records the source.",
            ["FrontDoorKillSwitchEnabled", "FrontDoorPersonEntityIds", "FrontDoorKillSwitchHoldMinutes", "FrontDoorKillSwitchRefreshSeconds", "FrontDoorKillSwitchTurnsThermostatOff"],
            s =>
            {
                var f = s.FrontDoorKillSwitch;
                var tone = !f.Enabled ? GuardTone.Off
                    : f.PersonDetected ? GuardTone.Alert
                    : f.Active ? GuardTone.Warning : GuardTone.Calm;
                var label = !f.Enabled ? "Off" : f.PersonDetected ? "Person" : f.Active ? "Holding" : "Watching";
                return new GuardLiveView(f.Enabled, f.Active || f.PersonDetected, label, tone, f.Status,
                [
                    new("Detectors", f.Enabled ? f.DetectorCount.ToString() : "Off", f.EntityIds),
                    new("Last sentry", f.LastDetectedBy, "The detector that last triggered the guard post."),
                    new("Guard hold", f.Active ? $"{f.SecondsRemaining}s" : "Ready", "Time left before the guard window clears."),
                    new("Thermostat off", f.ThermostatOffCommanded ? "Sent" : "Ready", "Whether the off command was recently sent."),
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
            "Raises a repeating mega-alert when cool mode is demanded but the AC is not really cooling, escalates to a full-site OMEGA alert when a rising room confirms it, then turns the AC off until the room warms 0.5 C.",
            "Real Home Assistant data only: hvac_mode, hvac_action, the setpoint, and room-temperature history.",
            "MEGA: it alerts if the entity is in cool, the room is clearly above the setpoint, and the action stays idle for about 30 minutes (possible breaker/equipment), or if the action says cooling but the room does not drop over the retained window (possible compressor/airflow). OMEGA: while the idle/breaker mega alert is up, if the room has also risen at least 0.4 C over the last 5 minutes — what a dead breaker looks like — it escalates to a full-site OMEGA alert. Requiring a real, sustained rise (and only on the idle branch) keeps false positives down. Alerts repeat about once a minute.",
            "Surfaces a red alert, an event log entry, and (on OMEGA) a site-wide overlay. It also turns the AC fully off (a failing unit is only wasting power) and holds it off until the real room temperature rises 0.5 C above the reading captured at shutdown, then restores cool. A human turning the AC back on is always respected.",
            ["CoolingFailureWatchEnabled"],
            s =>
            {
                var c = s.CoolingFailure;
                var alerting = c.Enabled && (c.Alerting || c.OmegaAlerting);
                var tone = !c.Enabled ? GuardTone.Off : alerting ? GuardTone.Alert : GuardTone.Calm;
                var label = !c.Enabled ? "Off" : c.OmegaAlerting ? "OMEGA" : c.Alerting ? "Alerting" : "Watching";
                var status = c.Enabled ? c.Status : "Cooling-failure monitoring is disabled.";
                return new GuardLiveView(c.Enabled, alerting, label, tone, status,
                [
                    new("Active for", !c.Enabled ? "Off" : alerting ? $"{c.SecondsActive}s" : "Ready", "How long the alert has been raised."),
                    new("Alerts", c.Enabled ? c.AlertCount.ToString() : "Off", "How many times it has fired."),
                    new("Room rise", !c.Enabled ? "Off" : c.RoomRiseCelsius is { } r ? $"{r:+0.0;-0.0;0.0} C" : "--", "Room change over the OMEGA confirmation window."),
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

        // ─────────────── The truce family (added after a thermostat was detached) ───────────────
        new GuardInfo(
            "Repeated-Raise Surrender", GuardCategory.WallTouch,
            "If a person re-raises the setpoint to about the same value 3+ times in 30 minutes, the defender adopts their number for 4 hours — the human wins the argument.",
            "Recent external RAISES (times and values, pruned to a 30-minute window).",
            "Three or more raises landing within 0.7 C of each other mean the person really wants that temperature. The defender adopts it (capped at 27 C) as the effective target for 4 hours — deliberately with NO 'unless the room is too warm' escape, because that escape hatch is what turned dawn disagreements into a detached thermostat. My temp stays the hard floor, emergencies still win, and a deliberate website target clears the surrender.",
            "Raises the effective target to the human's number for 4 hours and logs the surrender.",
            ["(always on — fixed: 3 raises / 30 min window / 4 h hold / 27 C cap)"]),

        new GuardInfo(
            "Tamper Truce", GuardCategory.System,
            "If the thermostat vanishes right after a correction exchange, assume a frustrated person detached it — stand down 2 hours instead of escalating.",
            "Home Assistant reachability, the last defender command time, and recent human touches.",
            "A thermostat that becomes unreachable within 20 minutes of a defender command AND 45 minutes of a human touch looks exactly like someone pulling the unit off the wall (it really happened, twice). This is the ULTRA OMEGA ALERT — one tier above MEGA (not cooling) and OMEGA (breaker off). Instead of fighting harder, the defender enters a 2-hour emergency quiet named 'Tamper truce' and says why. Normal outages without a preceding exchange are unaffected.",
            "Raises the ULTRA OMEGA ALERT, activates a 2-hour stand-down, and records the tamper-truce event.",
            ["(always on — fixed: 20 min command window / 45 min touch window / 2 h truce)"]),

        new GuardInfo(
            "Wake-Up Truce (door sensor)", GuardCategory.Sensor,
            "A bedroom door opening at dawn means that person is awake — adopt the warm truce temperature before they ever touch the thermostat.",
            "The configured bedroom door sensor (closed-to-open transitions) during the dawn window.",
            "When the door sensor flips from closed to open between the window start and end (default 04:00-09:00), the defender immediately adopts the truce temperature (default 25 C, never below my temp, capped at 27 C) for the hold period (default 2 h) using the same surrender machinery. The person wakes to a defender that already agrees with them.",
            "Adopts the truce target for the hold period and logs a friendly good-morning event.",
            ["WakeTruceDoorSensorEntityId", "WakeTruceWindowStart", "WakeTruceWindowEnd", "WakeTruceTargetCelsius", "WakeTruceHoldMinutes"]),
    ];

    /// <summary>Catalog entries that expose a live card (used by the Defense board).</summary>
    public static IEnumerable<GuardInfo> Live => All.Where(g => g.Project is not null);
}
