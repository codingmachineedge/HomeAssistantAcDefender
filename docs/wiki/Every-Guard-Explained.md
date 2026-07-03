# Every Guard, Explained Simply

Every single algorithm in AC Defender, described so simply a five-year-old could follow.
Each one also has an exact technical write-up in [Defender Logic](Defender-Logic.md) and a
live card on the Defense page.

The one rule behind all of them: **the room belongs to *my temp* — the number you picked.
Everything else is just *how politely* we get back to it.**

---

## 🧊 The basics

**The 24/7 watcher.** A little robot looks at the real thermostat every few seconds, all
day and all night. It never sleeps. If the thermostat is not where it should be, the robot
decides what to do — and every other guard on this page is just a rule about *when* and
*how gently* it acts.

**Warm-room cooling (the sneaky walk).** When the room is too warm, the robot doesn't slam
the thermostat down. It sets it just a half-step below the room temperature, waits for the
room to actually cool, then takes another half-step. Like walking downstairs one stair at a
time instead of jumping — nobody notices, and it never goes below your number.

**Gentle stepping.** Even the "do it now" buttons move the wall a half-degree at a time.
The wall never teleports; it walks.

**Quiet levels.** The more people fiddle with the thermostat, the quieter the robot gets —
Calm, Light, Quiet, Extra quiet, Softest. Like lowering your voice when someone is grumpy.

---

## ✋ When a person touches the thermostat

**Dynamic Cooldown.** Someone changed the thermostat? Wait a bit before fixing it. If they
keep changing it, wait longer each time. Nobody likes being corrected instantly.

**Manual Comfort Grace.** If someone set a temperature and the room still feels fine,
leave it alone for a while. Their choice gets a turn — until the room actually gets warm.

**Wall Settling.** If fingers are still tapping the thermostat, wait for the tapping to
stop before doing anything. Don't grab the toy while someone's still playing with it.

**Conflict Quiet.** If the thermostat got touched a LOT, someone might be fighting with
it. Stand completely still for a while — as long as the room stays safe.

**Tug-of-War Truce.** Up, down, up, down — that looks like an argument. Call a truce and
stop answering back for a bit, so it doesn't turn into a real argument.

**Touch Intent.** Look at the last few touches: do they all say "warmer, please"? Then
that person really means it — give their wish extra time before undoing it.

**Cooler Intent Fast Lane.** But if the touches all say "colder, please" — great, we
agree! Skip all the polite waiting and cool right away.

**Comfort Envelope.** If someone's choice is only a *tiny* bit different from ours, don't
fight over crumbs. Accept it for a while.

**Comfort Compromise.** If someone keeps asking for the same thing, meet them halfway for
an afternoon, then slowly drift back to your number. Like letting a friend pick the game
for one round.

**Comfort Memory.** If people always want it a bit warmer at the same time every day,
remember that and be a bit softer at that hour. The memory fades away on its own.

---

## 🥷 Looking natural (the stealth team)

These guards make the robot's fixes look like a person did them — because a fix nobody
notices is a fix nobody fights.

**Natural Walkback.** After lots of touches, use extra-small nudges with a tiny bit of
randomness, so no two fixes look identical.

**Touch Signature.** Watch how big a step real people make on this thermostat, and make
our nudges the same size. Blend in.

**Human Nudge.** People set numbers like 23.5, not 22.8. Round our fix to a
normal-looking number.

**Visibility Guard.** If someone touched the wall right after we fixed it, they probably
*saw* the fix. Lay low for a while.

**Routine Timing.** Land fixes on natural-feeling times (like "every 15 minutes"), not
the exact second the math finished.

**Comfort Budget.** Only so many fixes per hour. Even robots shouldn't nag.

**Command Camouflage.** Leave a believable gap after our own last command, so two robot
commands never appear back-to-back.

**Stealth Governor.** Add up ALL the recent activity into one "how suspicious would
another fix look right now?" score. Too high? Wait.

**Natural Cadence.** Pick a slightly random future moment for the next fix, so fixes
never come on a robotic drumbeat.

**Comfort Pace.** When things are busy, wait for a *naturally believable* moment — right
after the weather updates, or on a nice round clock time.

**Setpoint Echo.** After we send a command, wait until the thermostat repeats it back
before sending another. One instruction at a time.

**Repeat Quiet.** Don't send the exact same number twice in a row quickly — that's what a
stuck robot looks like.

**Setpoint Stillness.** Make sure the wall number has stopped wiggling (nobody mid-tap)
before answering.

**Sensor Rhythm.** The house's sensors report on a heartbeat. Time our fixes to land just
after a heartbeat, like we simply "noticed" the new reading.

**HVAC Alibi.** Wait for the AC itself to do something (start or stop cooling) and slip
our fix in right after — the AC's own activity is our cover story.

**Telemetry Alibi.** Or wait for any normal house signal — a weather update, a power
reading — and act right after that instead.

**Cooling Runway.** When the AC just started cooling, give it room to work before asking
for more. You don't push someone who's already running.

---

## 🌡️ Trusting the room itself

**Room Trend Guard.** If the room is already getting cooler by itself, watch instead of
acting. Don't fix what's fixing itself.

**Thermal Momentum.** If the room is cooling fast and will reach your number soon anyway,
just wait for it to arrive.

**Weather Drift Timing.** If it's getting cooler outside too, the room may follow. Give
the sky a few minutes before spending electricity.

**Outdoor Power Rule.** When it's genuinely cool outside, the AC mostly isn't needed:
below one temperature the robot goes silent, a bit above that it only fixes real problems.

---

## 🤖 When it's a machine, not a person

**Rival Schedule Watch.** The AC's own phone app has a bedtime schedule — SLEEP, then
DEEP SLEEP that quietly warms the room to 26° at 2 a.m. while everyone dreams. The robot
knows that schedule by heart. When the wall jumps to exactly a scheduled number and no
human did it, that's the *machine* — so no politeness, no learning from it, just walk the
wall right back to your number. Machines don't get feelings.

**Super Defender.** If someone keeps changing things from a phone over and over, get
strict: skip the polite waits while the room still needs cooling.

**Remote Settling Guard.** The gentler cousin: after a burst of phone changes, give the
phone-person a quiet window first — unless the room is getting hot.

**Desired-State Enforcer.** The "my house, my rules" mode: if someone who isn't the owner
turns the AC off or moves it, put it back — either sneakily (through all the stealth
guards) or exactly and immediately. It learns who tends to interfere and paces itself.

**Cool Mode Restore.** If anyone switches the AC away from cooling, switch it back —
after a polite pause if the room is fine, instantly if it's getting hot.

---

## 😢 Feelings detection (yes, really)

**Fully automated getting-yelled-at detection (Auto Brother-Mad).** An angry jump on the
thermostat, or a flurry of touches, means someone is about to yell. The robot sees it
coming and — before any tears — bows deeply: it says sorry in the log, stops ALL
corrections for two hours, and even nudges the AC warmer as a peace gift.

**Peace Offering.** When someone asks for warmer, give them a *little more* than they
asked. They see the house agreeing with them, not fighting.

**Anger Learning.** Remember which hours of the day make people grumpy, and be extra
hands-off at those hours forever after.

**Emergency buttons.** "Too cold" turns everything off. "Someone upset" and "Suspicion
quiet" make the robot watch silently without touching anything. The BROTHER MAD button
starts the full two-hour apology on demand.

---

## 🌙 Night & safety

**Night Shutdown.** On cool nights, turn the AC fully off during the night window and
don't touch it again. If someone turns it back on, respect that.

**Night Minimum.** During the night the robot never sets the thermostat below the night
floor, even if the daytime number is lower.

**Night Cooling Budget.** Even on a warm night the AC may only run so many minutes —
then it eases to a stop. It is never allowed to hum all night.

**Cooling Rest (on-forever protection).** If the AC has been cooling for hours and still
can't reach the number, the number is probably unreachable right now. Rest, then try
again. Nothing runs forever.

**Stand-down Parking.** When you turn the defender off, it parks the thermostat at a
lazy, high number first — so the unguarded AC barely runs while nobody's watching it.

**Upstairs Comfort Guard.** If bedrooms upstairs are hot *and* someone is home, the robot
skips its polite waits so cooling starts sooner. Upstairs never forces extra cold on its
own — it just makes the robot less patient.

**Front-door Guard Post.** If the doorbell camera sees a person at the door, pause
everything and (optionally) switch the AC off. Company first.

**Schedule & Weather Rules.** Your own timetable: "weekday evenings, 22°" — with weather
conditions like "only when it's hotter outside than inside."

**Website Debounce.** The site's own buttons can't be spammed: after one thermostat
button, the rest wait two minutes.

**Cooling Failure Watch (MEGA → OMEGA).** If the AC *says* it's cooling but the room
isn't moving — maybe a dead breaker or a broken compressor — sound the MEGA alarm. If the
room is actually getting *warmer*, that's OMEGA: the loudest alarm there is.

---

## 💵 Money guards

**Runtime counters.** Count every real minute the compressor runs — today, this month,
lifetime — seeded from the house's old logs so history counts too.

**AC cost estimate (no meter needed).** Multiply those minutes by the AC's size and by
the electricity price *at that exact hour* (cheap nights, pricey afternoons). That's the
dollars under the hours, and every day's box on the calendar.

**Usage calendar.** A month of little boxes, colored like airline ticket prices: green
days were cheap, red days were expensive. Click any day; total any range.

**Alectra Peak Power Saver.** When electricity is at its most expensive, don't ask the AC
for *extra* cold unless the room truly needs it.

**Monthly budget.** Tell it how much you want to spend this month. Spending too fast? The
room runs a whisker warmer (mostly during the expensive hours) until you're back on pace.
It never passes the safety temperature and never goes below your number. If the power
meter goes quiet, it switches to its own estimate instead of giving up.

---

*Want the exact math and settings for any of these? Every one has a precise entry in
[Defender Logic](Defender-Logic.md) and a live card with real evidence on the Defense page.*
