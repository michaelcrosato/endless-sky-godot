# Parity audit findings

Upstream behaviours this port got wrong, found by independent critics reading the
C++ against our C# with every claim adversarially verified. Recorded because most
of them are invisible in the data and several were asserted as *correct* by our
own tests before the audit.

Round 1 confirmed 51 of 54 claims across 8 subsystems. This is a companion to
`upstream-reference.md`, kept separate so the two do not collide.

## The ones that were actively wrong in play

**Ships were indestructible by gunfire.** `disabled damage` defaults to *hull
damage*, not zero — upstream fills it in after the parse loop
(`if(!disabledDamageSet) damage[DISABLED_DAMAGE] = damage[HULL_DAMAGE]`). No
vanilla weapon declares it, so reading the raw attribute left it at 0 for every
weapon in the game. Once a ship was disabled, `HullUntilDisabled` is ~0, the
overshoot is paid at the disabled rate, and a 10,000-damage hit did *nothing*.

**Cluster weapons healed their targets.** A weapon's damage includes its
submunitions' damage (`Weapon::TotalDamage`). The Korath Minelayer declares
−3200 shield damage on its carrier shell; only the 11 mines it releases make that
a net +650. Reading the carrier's own attribute inverted the sign of the weapon.

**49 ships never fought to the death.** `"never disabled"` is a **bare quoted
flag on the ship node**, not a numeric attribute, so it was never read. Those
hulls went derelict at 10–50% hull and became boardable — something upstream
never offers for them.

**Shots shredded neutral traffic.** `CollisionSet::Line` skips any body whose
government is *not an enemy* of the shooter's — not merely same-government. It
also *always* collides with the body the shot was aimed at, even a friendly one.

**No government could ever turn on the player.** `Politics::IsEnemy` has two
distinct paths: between ordinary governments it is
`a.AttitudeToward(b) < 0 || b.AttitudeToward(a) < 0` — **either** side's dislike
suffices — and when the player is involved it is reputation-driven with bribes
and provocation. With neither, hostility was one-directional and blind to
reputation.

**NPCs rammed instead of fighting.** Thrusting whenever the target is ahead ends
every pursuit in a collision. The deeper reason is that *coasting is lossless*
here, so ceasing thrust does not slow a ship — standing off requires actively
braking, which is what upstream's slowdown-distance calculation is for.

**Conversations ended as "no outcome" where upstream returns DECLINE.** Any jump
landing outside the node list maps to `Endpoint::DECLINE`. A caller treating
"none" as acceptance hands out missions the player never agreed to.

**Pressing the jump key spun the ship in circles forever.** Reported from play,
round 2. Two independent defects, either of which alone would have been survivable:

- No drive in `universe/` stated `"jump speed"`, so `JumpSpeedLimit` read 0 for
  every ship in the played galaxy. Upstream's velocity gate is a strict
  `|v| > jump speed`, so a zero limit means *only an exact standstill is ever
  legal* — not a slow jump, no jump at all. Upstream states .2 on the Hyperdrive
  and .3 on the Jump Drive; a drive that omits the number is not a slower drive,
  it is a broken one.
- The autopilot's brake was an invention living in the flight scene, not a port of
  `AI::Stop`. It never raised `Command.Stop` (upstream's dead-stop cheat) and had
  no `VELOCITY_ZERO` floor, so it retro-thrust at an exact zero that thrust
  overshoots and drag only ever approaches. Once velocity decayed far enough that
  `(-v).Unit()` returned the zero vector, `TurnToward`'s documented zero-vector
  case took over and turned the ship at full rate, one direction, every frame:
  64.7 full circles in the 5,000-frame reproduction.

Neither half was reachable by the suite. The rule was on the view side of a
boundary the architecture test only checks the *direction* of, and every fixture
drive in the suite stated a jump speed while no drive in the galaxy did — so the
one number that made the jump impossible was the one number no test ever read.
`ShipAi.PrepareForHyperspace`/`Stop` now hold the rule, and
`UniverseTests.EveryDriveStatesTheSpeedItWillJumpAt` reads the number.

**A new game opened under fire.** Reported from play, round 2. `emit.start()` chose
the player's home world on connectivity and a shipyard alone — the docstring said
"a quiet, inhabited world" and nothing in the filter meant it. The best-connected
inhabited system belonged to a `zealot` faction, which the generator writes at
`"player reputation" -50`; since `Government::IsEnemy`'s player branch makes the
player an enemy exactly while standing is negative, a new pilot was born an enemy
of the government owning the space they were sitting in. Its patrols spawned
hostile, as did a second faction's at -1000, and the game began with the player
being shot at before they had touched a key.

The fix is structural rather than a nudge to the filter: the standing a faction
starts the player at is now decided in ONE function, which both the government
writer and the start chooser consult. A generator free to write -50 in one place
and pick a home in another will eventually disagree with itself again.

**A job could be taken but never handed in.** Found while building the tutorial's
last step. `MissionLog.Complete` existed, was correct, and was covered by the
simulation suite — and nothing in the game ever called it. The only caller anywhere
outside `tests/` was the test suite itself. A player could accept work, carry it to
the right world, land beside the person waiting for it, and find no key that would
finish the job; the JOBS counter answered "already carrying that one" and no payment
ever arrived. `B` on a carried mission now hands it in, and says *where* it hands in
when the world is wrong, because "cannot complete" on a world that looks right is
indistinguishable from a broken button.

The general shape is worth naming: a green simulation suite proves a rule is right,
never that anything invokes it. Both defects in this section found the same way —
by trying to play the sequence rather than by testing the pieces.

## Traps in our own structure

**Ship-level booleans must not live in `Attributes`.** `ShipDefinition.InheritFrom`
copies a base hull's attributes only when the variant's own bag is **empty**, so
writing a flag there silently strips every derelict variant of its hull, mass,
drag and thrust. Flags live in their own set and are inherited separately. This
bit us *while fixing* the `never disabled` bug.

**Shops are not flat lists.** A `shipyard`/`outfitter` node carries `to sell`
conditions, a `location` filter and a `stock` block. Treating every child's first
token as an item name invents goods called "to", "location" and "stock" while
silently dropping the real ones nested under `stock`.

**Most mission dialogue is inline.** A conversation can be referenced by name or
defined in place as a bare `conversation` block with children. Reading only the
named form left 51 of 1,597 conversations reaching the runner.

## Invariants we asserted that upstream contradicts

These were *our* assumptions, not upstream rules. Each cost a red test before it
was understood.

- **Damage can be negative.** The Korath Minelayer's carrier shell is −3200/−2400
  by design.
- **A moving projectile need not have a lifetime.** Submunition carriers burst on
  frame one; the Ion Hail Turret has velocity and no lifetime at all.
- **Commodity price bands are not clamps.** The low/high on a commodity is the
  range the galaxy *generates* within; hand-authored systems sit outside it (Anax
  pays 600 for Heavy Metals against a 610–1310 band). 4,778 of 4,800 quotes fall
  inside, 22 do not.
- **A bare token in an action block is not an increment.** Treating it as one
  leaked `conversation`, `dialog` and `payment` keys into the condition store.

## Values that are easy to get subtly wrong

| Thing | Correct | Wrong version's symptom |
|---|---|---|
| Hull damage clamp | `hull + 0.25 − minimumHull` | nothing can ever be disabled |
| `MaxHeat` | `100 × (cargo + mass + heat capacity)` | zero for every ship without a heatsink |
| Burst reload default | `1` | every burst collapses to one shot |
| Hardpoint offsets | halved (`point * .5`) | every mount sits twice as far out |
| Projectile spawn | `−0.5 × ship velocity` | shots detach from a moving hull |
| Planet security default | `0.25` | every undeclared world is a free port |
| `RequiredCrew` | `max(1, …)`, or 0 for automata | drones need phantom crew |
| Salaries | `100 × (crew − 1)`, extras only on the flagship | overcharges, and a solo captain pays 100/day |
| Division by zero (conditions) | saturates to int64 max | flips comparisons content relies on |
| Modulo by zero (conditions) | returns the dividend | same |
| `and` vs `or` precedence | equal | mixed inline expressions re-associate |
| `port` node | an alias for `spaceport` | 19 worlds cannot refuel |
| `"jump speed"` on a drive | .2 hyperdrive, .3 jump drive | omitted reads 0, and only a dead stop may jump |
| `AI::Stop` at maxSpeed 0 | floors at `VELOCITY_ZERO` (.001) | braking never terminates; the ship spins |

## Method

Independent critics, one per subsystem, told to read the upstream C++ *first* and
derive intended behaviour from it before opening our code — and explicitly told
not to trust our comments, several of which asserted upstream behaviour and were
wrong. Every claim then went to two more agents whose default position was that
the claim is false, one checking whether it misread upstream and one whether it
misread us. A claim survived only if neither could refute it.

The value was not in finding bugs the author suspected. It was in finding the
three that the author's own tests certified as correct — which no amount of
self-review reaches, because the tests encode the same misreading as the code.
