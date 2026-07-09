---
layout: doc
title: "Schedule & Weather Rules"
description: "Time-of-day target rules, each gated by a weather activation condition."
---

<p class="article-kicker">Safety, Energy, and System algorithm</p>

# Schedule &amp; Weather Rules

<div class="algorithm-article-hero category-system">
  <div>
    <p class="lede">Time-of-day target rules, each gated by a weather activation condition.</p>
    <p>These algorithms keep the product honest: real Home Assistant commands, real errors, real weather or usage data, and safety-first fallbacks whenever comfort or equipment protection matters.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#schedule-and-weather-rules">See it on the logic page</a></p>
  </div>
  <div class="motion-stage category-system" aria-hidden="true">
  <div class="motion-track motion-track-a"></div>
  <div class="motion-track motion-track-b"></div>
  <div class="motion-node motion-node-input"><span>1</span><strong>Watch</strong></div>
  <div class="motion-node motion-node-decision"><span>2</span><strong>Decide</strong></div>
  <div class="motion-node motion-node-output"><span>3</span><strong>Act</strong></div>
  <div class="thermostat-mini"><i></i></div>
</div>
</div>

<img class="article-visual" src="images/algorithms/article-schedule-and-weather-rules.svg" alt="Unique generated explanatory visual for Schedule &amp; Weather Rules">

## The short version

Time-of-day target rules, each gated by a weather activation condition.

## What it watches

The active schedule entry for the current day/time and the weather rule.

## How it decides

When the custom schedule is on, the matching rule supplies the target. Weather rules (always, room-above-outdoor, room-below-outdoor, outdoor-above-target, outdoor-below-target) decide whether corrective action is allowed. The defender still reads Home Assistant 24/7 even when a rule blocks correction.

## What it changes

Sets the target and whether corrective action runs.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>ScheduleEnabled</code></li><li><code>WeatherActivationMode</code></li><li><code>(per-rule Days / Start / End / Target / Weather)</code></li></ul>

## Where to see it

- **Defense page:** guide-only reference entry.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
