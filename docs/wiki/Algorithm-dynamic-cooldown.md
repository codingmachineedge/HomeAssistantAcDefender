---
layout: doc
title: "Dynamic Cooldown"
description: "A frequency-based quiet period after a manual thermostat change."
---

<p class="article-kicker">Safety, Energy, and System algorithm</p>

# Dynamic Cooldown

<div class="algorithm-article-hero category-system">
  <div>
    <p class="lede">A frequency-based quiet period after a manual thermostat change.</p>
    <p>These algorithms keep the product honest: real Home Assistant commands, real errors, real weather or usage data, and safety-first fallbacks whenever comfort or equipment protection matters.</p>
    <p><a class="mini-link" href="Algorithms.html">Back to all algorithms</a> <a class="mini-link" href="Defender-Logic.html#dynamic-cooldown">See it on the logic page</a></p>
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

<img class="article-visual" src="images/algorithms/article-dynamic-cooldown.svg" alt="Unique generated explanatory visual for Dynamic Cooldown">

## The short version

A frequency-based quiet period after a manual thermostat change.

## What it watches

How many wall touches happened recently inside the touch-frequency window.

## How it decides

cooldown = min(MaxCooldownSeconds, BaseCooldownSeconds × recentTouchCount) + a small random quiet delay. More repeated changes mean longer cooldowns.

## What it changes

Holds the next correction until the cooldown elapses.

## Safety boundaries

- Uses the real inputs listed above. It does not invent thermostat, weather, usage, or sensor state.
- Changes only the output listed above. Thermostat-affecting work goes through Home Assistant or returns a real error.
- The global AC Defender rules still apply: the website target remains the floor for cooling commands, the worker keeps refreshing real Home Assistant state 24/7, and comfort/safety rules are not bypassed by decorative timing.

## Settings

<ul class="settings-list"><li><code>BaseCooldownSeconds</code></li><li><code>MaxCooldownSeconds</code></li><li><code>TouchFrequencyWindowMinutes</code></li></ul>

## Where to see it

- **Defense page:** guide-only reference entry.
- **Guide page:** generated from the same guard catalog entry.
- **Source:** `Guards/GuardCatalog.cs` describes this page; the implementation is coordinated by `Services/DefenderStateStore.cs` and `Services/AcDefenderService.cs`.
