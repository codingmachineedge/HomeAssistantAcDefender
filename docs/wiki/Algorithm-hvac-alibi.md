---
layout: doc
title: "HVAC Alibi"
description: "Waits for a real HVAC action transition so a safe correction lands near a normal thermostat event."
---

<p class="article-kicker">Sensor Timing algorithm</p>

# HVAC Alibi

<div class="algorithm-article-hero category-sensor">
  <div>
    <p class="lede">Waits for a real HVAC action transition so a safe correction lands near a normal thermostat event.</p>
    <p>These algorithms make corrections land near real house signals instead of on a robotic beat, while still stepping aside when room comfort needs direct cooling.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#hvac-alibi">See it on the logic page</a></p>
  </div>
  <div class="motion-stage category-sensor" aria-hidden="true">
  <div class="motion-track motion-track-a"></div>
  <div class="motion-track motion-track-b"></div>
  <div class="motion-node motion-node-input"><span>1</span><strong>Watch</strong></div>
  <div class="motion-node motion-node-decision"><span>2</span><strong>Decide</strong></div>
  <div class="motion-node motion-node-output"><span>3</span><strong>Act</strong></div>
  <div class="thermostat-mini"><i></i></div>
</div>
</div>

<img class="article-visual" src="images/algorithms/article-hvac-alibi.svg" alt="Unique generated explanatory visual for HVAC Alibi">

## The short version

Waits for a real HVAC action transition so a safe correction lands near a normal thermostat event.

## What it watches

The current Home Assistant hvac_action, the last action transition, recent wall touches, and room temperature.

## How it decides

After repeated wall touches, while the room is still inside the safety band, it can hold a safe correction until hvac_action changes (for example idle to cooling or cooling to idle). A recent transition can also clear the hold. Direct comfort needs, upstairs heat, or a too-warm room bypass the wait immediately.

## What it changes

Delays only safe corrections until a real HVAC action transition or the max hold expires.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>HvacActionAlibiEnabled</code></li><li><code>HvacActionAlibiTriggerTouches</code></li><li><code>HvacActionAlibiTransitionWindowSeconds</code></li><li><code>HvacActionAlibiMaxHoldMinutes</code></li><li><code>HvacActionAlibiSafetyBandCelsius</code></li></ul>

## Where to see it

- **Defense page:** live card with state, verdict, evidence, and metrics.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
