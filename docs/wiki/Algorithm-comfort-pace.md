---
layout: doc
title: "Comfort Pace"
description: "The high-frequency planner: under heavy wall fighting it waits for a calm weather, sensor, or clock-aligned slot."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Comfort Pace

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">The high-frequency planner: under heavy wall fighting it waits for a calm weather, sensor, or clock-aligned slot.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#comfort-pace">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-comfort-pace.svg" alt="Unique generated explanatory visual for Comfort Pace">

## The short version

The high-frequency planner: under heavy wall fighting it waits for a calm weather, sensor, or clock-aligned slot.

## What it watches

Touch pressure, command pressure, real outdoor-weather movement, the learned Home Assistant sensor beat, and 5/10-minute clock boundaries.

## How it decides

When touches reach the trigger and the room is inside the safety band, it computes a base delay between the min and max pace minutes (scaling with pressure) and then snaps it to the nearest calm slot — a weather update, the sensor beat, or a clock boundary — recording why. Too-warm rooms clear it instantly.

## What it changes

Delays the safe correction to the chosen calm climate slot.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>NaturalChangePlannerEnabled</code></li><li><code>NaturalChangePlannerTriggerTouches</code></li><li><code>NaturalChangePlannerMinimumMinutes</code></li><li><code>NaturalChangePlannerMaximumMinutes</code></li><li><code>NaturalChangePlannerJitterMinutes</code></li><li><code>NaturalChangePlannerPreferWeatherSlots</code></li><li><code>NaturalChangePlannerPreferSensorBeat</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
