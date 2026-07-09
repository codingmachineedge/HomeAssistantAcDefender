---
layout: doc
title: "Command Camouflage"
description: "Gives a recent helper command time to look normal before another safe correction appears."
---

<p class="article-kicker">Wall-Touch Response algorithm</p>

# Command Camouflage

<div class="algorithm-article-hero category-wall">
  <div>
    <p class="lede">Gives a recent helper command time to look normal before another safe correction appears.</p>
    <p>These algorithms exist for the exact household fight AC Defender is built for: someone keeps raising the thermostat, but the room still needs to come back to your temperature without starting a visible duel.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#command-camouflage">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-command-camouflage.svg" alt="Unique generated explanatory visual for Command Camouflage">

## The short version

Gives a recent helper command time to look normal before another safe correction appears.

## What it watches

The last real helper setpoint command, recent helper-command pressure, recent wall-touch pressure, and the room temperature.

## How it decides

After a setpoint command, it waits at least the minimum gap plus pressure-scaled extra seconds before another safe correction. Higher recent touch or command pressure makes the gap longer. A room over the safety band or any comfort/safety bypass clears it immediately.

## What it changes

Holds the next safe correction until the recent command has enough spacing.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>CommandCamouflageEnabled</code></li><li><code>CommandCamouflageMinimumGapSeconds</code></li><li><code>CommandCamouflagePressureExtraSeconds</code></li><li><code>CommandCamouflageSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
