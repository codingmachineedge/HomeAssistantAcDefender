---
layout: doc
title: "Visibility Guard"
description: "Slows the next safe nudge when a wall touch lands right after a defender command (someone likely noticed)."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Visibility Guard

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Slows the next safe nudge when a wall touch lands right after a defender command (someone likely noticed).</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#visibility-guard">See it on the logic page</a></p>
  </div>
  <div class="motion-stage category-wall" aria-hidden="true">
  <div class="motion-track motion-track-a"></div>
  <div class="motion-track motion-track-b"></div>
  <div class="motion-node motion-node-input"><span>1</span><strong>Watch</strong></div>
  <div class="motion-node motion-node-decision"><span>2</span><strong>Decide</strong></div>
  <div class="motion-node motion-node-output"><span>3</span><strong>Act</strong></div>
  <div class="thermostat-mini"><i></i></div>
</div>
</div>

<img class="article-visual" src="images/algorithms/article-visibility-guard.svg" alt="Unique generated explanatory visual for Visibility Guard">

## The short version

Slows the next safe nudge when a wall touch lands right after a defender command (someone likely noticed).

## What it watches

Wall touches that occur within the after-command window, counted as &#x27;notices&#x27; over the notice window.

## How it decides

Each notice adds pressure (0–100). When notices reach the trigger, the next safe correction waits a variable hold between the min and max hold minutes, scaled by pressure. A room over the safety band clears the hold.

## What it changes

Delays the next safe correction so the AC&#x27;s reaction looks less mechanical.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>VisibilityGuardEnabled</code></li><li><code>VisibilityGuardTriggerNotices</code></li><li><code>VisibilityGuardNoticeWindowMinutes</code></li><li><code>VisibilityGuardAfterCommandSeconds</code></li><li><code>VisibilityGuardMinimumHoldMinutes</code></li><li><code>VisibilityGuardMaximumHoldMinutes</code></li><li><code>VisibilityGuardSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
