---
layout: doc
title: "Wall Settling"
description: "Waits for someone who is still tapping the wall thermostat to stop before correcting."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Wall Settling

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Waits for someone who is still tapping the wall thermostat to stop before correcting.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#wall-settling">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-wall-settling.svg" alt="Unique generated explanatory visual for Wall Settling">

## The short version

Waits for someone who is still tapping the wall thermostat to stop before correcting.

## What it watches

Recent touches inside the settling window and the room temperature.

## How it decides

With enough recent touches it holds for the base settle seconds plus extra pressure seconds (more touches = longer), measured from the latest touch. A room over the safety band clears it.

## What it changes

Holds the correction until the wall stops changing.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>WallSettlingGuardEnabled</code></li><li><code>WallSettlingMinimumTouches</code></li><li><code>WallSettlingWindowMinutes</code></li><li><code>WallSettlingBaseSeconds</code></li><li><code>WallSettlingPressureExtraSeconds</code></li><li><code>WallSettlingSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
