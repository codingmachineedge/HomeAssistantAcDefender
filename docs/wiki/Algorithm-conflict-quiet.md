---
layout: doc
title: "Conflict Quiet"
description: "Stands the defender down during an obvious tug-of-war over the thermostat."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Conflict Quiet

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Stands the defender down during an obvious tug-of-war over the thermostat.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#conflict-quiet">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-conflict-quiet.svg" alt="Unique generated explanatory visual for Conflict Quiet">

## The short version

Stands the defender down during an obvious tug-of-war over the thermostat.

## What it watches

Recent wall touches within the touch window and how far the room is above target.

## How it decides

When touches reach the conflict threshold, it stops sending visible corrections for the stand-down minutes — but only while the room stays within target + comfort band. A warmer room, severe upstairs heat, or a crossed safety override ends it.

## What it changes

Suppresses corrections for the stand-down period.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>ConflictQuietModeEnabled</code></li><li><code>ConflictQuietTouchThreshold</code></li><li><code>ConflictQuietMinutes</code></li><li><code>ConflictQuietComfortBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
