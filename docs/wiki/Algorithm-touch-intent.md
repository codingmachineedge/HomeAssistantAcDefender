---
layout: doc
title: "Touch Intent"
description: "Reads whether recent wall changes trend warmer, cooler, or mixed, and extends grace for a clear warmer pattern."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Touch Intent

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Reads whether recent wall changes trend warmer, cooler, or mixed, and extends grace for a clear warmer pattern.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#touch-intent">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-touch-intent.svg" alt="Unique generated explanatory visual for Touch Intent">

## The short version

Reads whether recent wall changes trend warmer, cooler, or mixed, and extends grace for a clear warmer pattern.

## What it watches

The net sum of recent wall setpoint changes inside the intent window.

## How it decides

If the net movement is at least the warm threshold and the room is inside the intent safety band, it adds the extra grace minutes to Manual Comfort Grace. Cooler or mixed patterns get no extra grace; a too-warm room steps it aside.

## What it changes

Lengthens Manual Comfort Grace when people clearly want warmer air.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>TouchIntentEnabled</code></li><li><code>TouchIntentMinimumTouches</code></li><li><code>TouchIntentWindowMinutes</code></li><li><code>TouchIntentNetWarmThresholdCelsius</code></li><li><code>TouchIntentExtraGraceMinutes</code></li><li><code>TouchIntentSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
