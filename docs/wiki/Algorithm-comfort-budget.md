---
layout: doc
title: "Comfort Budget"
description: "Caps how many safe corrections happen inside a rolling window so the AC is not constantly nudged."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Comfort Budget

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Caps how many safe corrections happen inside a rolling window so the AC is not constantly nudged.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#comfort-budget">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-comfort-budget.svg" alt="Unique generated explanatory visual for Comfort Budget">

## The short version

Caps how many safe corrections happen inside a rolling window so the AC is not constantly nudged.

## What it watches

The count of recent automatic setpoint commands in the budget window.

## How it decides

If the number of commands in the window reaches the max, it rests until the oldest command ages out of the window. A room over the safety band clears the budget.

## What it changes

Holds new safe corrections until the budget frees up.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>ComfortBudgetEnabled</code></li><li><code>ComfortBudgetWindowMinutes</code></li><li><code>ComfortBudgetMaxCommands</code></li><li><code>ComfortBudgetSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
