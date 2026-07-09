---
layout: doc
title: "Routine Timing"
description: "Lines safe corrections up with a normal-looking comfort-check rhythm instead of firing instantly."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Routine Timing

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Lines safe corrections up with a normal-looking comfort-check rhythm instead of firing instantly.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#routine-timing">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-routine-timing.svg" alt="Unique generated explanatory visual for Routine Timing">

## The short version

Lines safe corrections up with a normal-looking comfort-check rhythm instead of firing instantly.

## What it watches

Recent wall touches and the wall-clock minute.

## How it decides

After repeated touches and while the room is safe, the next correction waits until the next interval boundary (the routine minutes) plus a little random wiggle, capped at the max routine delay. Too-warm rooms clear it.

## What it changes

Delays the safe correction to the next tidy time slot.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>RoutineTimingEnabled</code></li><li><code>RoutineTimingTriggerTouches</code></li><li><code>RoutineTimingIntervalMinutes</code></li><li><code>RoutineTimingJitterMinutes</code></li><li><code>RoutineTimingMaxDelayMinutes</code></li><li><code>RoutineTimingSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
