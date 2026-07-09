---
layout: doc
title: "Fan Energy Saver"
description: "Optionally moves the fan to an energy-saving mode when the room is near target."
---

<p class="article-kicker">Safety, Energy, and System algorithm</p>

# Fan Energy Saver

<div class="algorithm-article-hero category-system">
  <div>
    <p class="lede">Optionally moves the fan to an energy-saving mode when the room is near target.</p>
    <p>These algorithms keep the product honest: real Home Assistant commands, real errors, real weather or usage data, and safety-first fallbacks whenever comfort or equipment protection matters.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#fan-energy-saver">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-fan-energy-saver.svg" alt="Unique generated explanatory visual for Fan Energy Saver">

## The short version

Optionally moves the fan to an energy-saving mode when the room is near target.

## What it watches

Room temperature versus target and the thermostat&#x27;s available fan modes.

## How it decides

When enabled and the room is within the threshold of target, if the configured fan mode exists on the device it calls climate.set_fan_mode.

## What it changes

Sets the fan to the saver mode; otherwise leaves the fan alone.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>FanEnergySaverEnabled</code></li><li><code>FanEnergySaverThresholdCelsius</code></li><li><code>FanEnergySaverMode</code></li></ul>

## Where to see it

- **Defense page:** guide-only reference entry.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
