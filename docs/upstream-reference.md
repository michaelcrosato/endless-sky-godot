# Upstream Endless Sky reference notes

Extracted from the upstream source (`external/endless-sky`, sparse clone:
`source/` + `data/`; HEAD `4e55639`). These are the facts the reimplementation
must reproduce. The ported code under the sim library and its NUnit suite
already encode all of them — change those only with this page (and upstream)
in hand.

## The ten rules a careless port gets wrong

1. **Coasting has no drag.** Drag is applied only inside the acceleration
   block (`Ship.cpp:4960` — `if(acceleration)`), so an undisturbed ship
   coasts forever. Only disabled/energy-starved ships decay:
   `velocity *= 1 - dragForce`.
2. **Indentation depth = raw count of leading whitespace code points.** One
   tab == one space == one level; depth jumps collapse to "one deeper";
   tabs-vs-spaces is fixed by the first indent seen per file (mismatch is a
   warning, not an error).
3. **No escape sequences in the data format.** `"…"` and `` `…` `` are the
   only quoting, mutually exclusive; an unterminated quote runs to EOL with a
   warning.
4. `#` comments only at line start or after inter-token whitespace —
   `foo#bar` is one token; `#` inside quotes is literal.
5. **Angles are 65536-step fixed point** (`llround(deg * 65536/360) & 0xFFFF`)
   with a precomputed unit table; unit vector = `(sin θ, −cos θ)`; screen
   coords, +Y down, clockwise positive; 0° points up.
6. `turn` is not degrees: **`turn / inertialMass` = degrees per frame** at
   60 fps (Shuttle: 552/192 = 2.875°/f = 172.5°/s).
7. `MaxVelocity()` divides by mass-clamped `Drag()`; the integrator uses
   `DragForce()` clamped to 1. They differ for very light or draggy hulls.
8. **Turn resolves before thrust within a frame** — thrust uses the updated
   facing.
9. A ship with no `thrust` but an afterburner uses the afterburner for
   ordinary forward flight; a reverse command with no reverse thruster is
   ignored outright (not even drag applies).
10. **Stellar positions update only when the in-game date changes.** Implicit
    orbital period = `sqrt(distance³ / M)` days (M = summed star masses, or
    the parent's mass for moons; lone star defaults to 10). Angle =
    `days_since_epoch * 360/period + offset` degrees; position = unit ×
    distance (+ parent position).

## Per-frame integration (60 fps fixed)

```
dragForce = min(1, drag / (dragReduction · inertialMass))
a_fwd     = thrust / inertialMass · accelMult          (px/frame²)
turnRate  = turn / inertialMass · turnMult             (deg/frame)
v_max     = thrust / min(drag, inertialMass)           (px/frame)

per frame (healthy ship):
  facing += clamp(turnCmd) · turnRate                  (turn first)
  accel   = facing.unit · thrustCmd · a_fwd
  if accel ≠ 0:
      d = accel − v · dragForce · accelMult
      d *= 0.5 · (accel.unit · d.unit + 1)             (opposing-drag softener)
      v += d
  position += v                                        (always, even coasting)
```

`inertialMass = (hull+outfits+cargo mass) / (1 + "inertia reduction")`.

## Stock Shuttle (the M1 reference ship)

Hull `data/human/ships.txt:3650`: mass 80, drag 1.8, no thrust/turn of its
own. Stock outfits: nGVF-AA Fuel Cell (20t), LP036a Battery (10t), D14-RN
Shield Generator (15t), X2700 Ion Thruster (27t, **thrust 24.075**), X2200 Ion
Steering (20t, **turn 552**), Hyperdrive (20t) → total mass **192**.

| quantity | value |
|---|---|
| acceleration | 24.075/192 = **0.12539 px/f²** (451 px/s²) |
| max velocity | 24.075/1.8 = **13.375 px/f** (802.5 px/s) |
| turn rate | 552/192 = **2.875°/f** (172.5°/s) |
| reverse | none (commands ignored) |

## Vanilla start

`data/starts.txt:18`: system **Rutilicus**, planet **New Boston**, date
**16 Nov 3013**, 480,000 credits against a 480,000 mortgage; no starting ship
(the player buys at New Boston's "Basic Ships": Shuttle / Sparrow / Star
Barge). Rutilicus (`data/map systems.txt:28569`): government Republic, star
`star/g5` (mass 3906.25), New Boston `planet/cloud6` at distance 513.86
(derived period ≈ 186.4 days), plus rock/gas companions and two moon systems;
asteroid belt at 1771.

## Data layout

Everything under `data/` is one merged namespace: definitions of the same
(type, name) in different files **add together** (that is how `ships.txt` and
`variants.txt` compose, and the groundwork for plugin overrides). Key files:
`map systems.txt`, `map planets.txt`, `stars.txt` (star/planet masses),
`governments.txt`, `starts.txt`, `human/ships.txt`, `human/engines.txt`,
`human/outfits.txt`, `human/power.txt`, `human/weapons.txt`,
`human/sales.txt`, `commodities.txt`, `gamerules.txt`.

## Combat rules that contradict first assumptions (M2 findings)

1. **Shields block hull damage entirely, not proportionally.** Hull damage
   scales by `(1 − shieldFraction)` and is zero while any shields remain;
   bleed-through happens only in the frame a shot overruns the shields, and
   only for the excess.
2. **Hull damage is clamped to `(hull + 0.25 − minimumHull)`**
   (`Entity::HullLevelUntilDisabled`). The 0.25 epsilon is load-bearing:
   disabled is `hull < minimumHull` strictly, so without it no weapon lacking
   explicit `"disabled damage"` could ever disable anything.
3. **`"homing"` and `"stream"` are valueless flags** — bare lines inside the
   weapon block, set on key presence (a following number is deprecated legacy
   syntax). A parser that only records key/number pairs reads all 68 upstream
   homing weapons as straight-firing guns.
4. **Damage can be negative, and a submunition carrier can have velocity with
   no lifetime.** The Korath Minelayer's carrier shell carries −3200 shield /
   −2400 hull (hitting it early does less than the cloud it splits into); the
   Ion Hail Turret's carrier bursts on frame one.
5. A missile exactly antiparallel to its target computes
   `desiredTurn = asin(0) = 0` and flies straight — upstream does the same;
   pinned by test so nobody "fixes" it into divergence.

- **Disabled ships stay valid targets** — only destroyed ones are dropped;
  upstream keeps shooting a crippled ship until boarded or finished.
- Weapon range = `velocity × lifetime` (or `"range override"`); upstream
  actually uses a *weighted* velocity accounting for projectile acceleration
  and drag, and extends lifetime by the longest-lived submunition — a naive
  range reads accelerating/cluster weapons short.
- Shots inherit the firing ship's velocity; a cluster carrier with velocity
  but no lifetime bursts at the muzzle (Ion Hail), not downrange.

## The jump protocol (M3 ground truth, from Ship.cpp/AI.cpp/Engine.cpp)

Constants: `HYPER_C = 100` frames each way, `HYPER_A = 2` px/f²,
`HYPER_D = 1000` px. Hyperdrive arrival offset = C²·A/2 + D = **11,000 px**.

**IsReadyToJump** (Ship.cpp:2467): not disabled, no WAIT, `hyperspaceCount==0`,
target+current system set; `fuelCost != 0 && fuel >= fuelCost`; position past
the system's departure distance (vanilla: 0 — no gate); speed
`|v| <= jump speed` (Hyperdrive attribute, 0.2 — a scram drive replaces this
with a lateral-deviation-only test and NO speed cap); and for non-jump-drives
the facing must be within ONE TURN STEP of `Angle(target.MapPos −
current.MapPos)` — turn by ±TurnRate toward it and require crossing over or
landing exactly (jump drives skip facing entirely).

**The J key** (AI.cpp:4729): with no travel plan, target = the LINKED system
best aligned with current facing (max dot of facing·direction); then the
autopilot latches and every frame runs PrepareForHyperspace — brake to jump
speed (AI::Stop) + TurnToward(direction) — plus `command |= JUMP` until
IsReadyToJump passes at the commit point (DoInitializeMovement). Any manual
input cancels the autopilot. A jump drive skips the TurnToward entirely and only
stops (AI.cpp:2784), because it tears its hole where the ship already is.

**AI::Stop's zero-speed floor** (AI.cpp:2666) is load-bearing, not a rounding
courtesy: asked to stop at 0 it settles for `VELOCITY_ZERO = .001` **and** raises
`Command::STOP`, upstream's cheat that snaps the along-facing velocity component
to zero once one frame of braking would cover it. Chasing a literal zero instead
never terminates — thrust overshoots it and drag only decays toward it — and the
degenerate `TurnToward(zero vector)` that follows returns −1, i.e. full-rate
rotation forever. Note this only ever *arises* when a drive fails to state
`"jump speed"`; every upstream drive does (Hyperdrive .2, Jump Drive .3), and
IsReadyToJump's own velocity gate has no epsilon, so a drive missing the number
cannot jump at all. It is a content bug that presents as a control bug.

**Sequence** (DoHyperspaceLogic, Ship.cpp:4596): commit frame still moves
normally; then each outbound frame: `acceleration = 0`,
`fuel −= cost/100`, `velocity += 2·facingUnit`, `position += velocity`,
facing frozen, untargetable from count ≥ 70. At count == 100: switch system,
teleport to `target − 11000·facingUnit (+ extra + escortOffset)`, snap
`velocity = |v|·facingUnit`; then inbound frames decelerate
`velocity −= 2·facingUnit` until `v·facing <= exitV`, then
`velocity = facing·exitV`, count = 0. `exitV = max(HYPER_A, MaxVelocity)`
capped by the quadratic `(.5/accel − .25)·v² + (150/turnRate)·v = HYPER_D`.
Jump-drive arrival instead teleports to a random angle at
`300·(rand+1) + extra` px with velocity untouched.

**Fuel** (Outfit.cpp:424, ShipJumpNavigation.cpp): `"jump fuel"` is a legacy
alias normalized into `"hyperdrive fuel"` (default 100; scram 150; jump drive
200). Cost = min across drive outfits, NOT summed; flat per ship for
hyperdrives (mass cost only if the drive declares `"jump mass cost"`).
Hyperdrive requires `from.Links` to contain `to`. Stock Shuttle: fuel 400 →
4 jumps.

**On system entry** (Engine::EnterSystem, Engine.cpp:1494): the date advances
by exactly **+1 day** per jump, `GameData::SetDate` re-places every stellar
object for the new day, and the economy steps.

**Arrival-distance gotcha:** modern gamerules set `habitable based arrival
distance` true, adding `clamp(habitable, 500, 5000)` px and aiming at the
SYSTEM CENTER; the classic "11,000 px out aimed at the target planet" is the
`extraArrivalDistance == 0` branch. Pick one and document it.

## Finding somewhere to land (AI.cpp:4590, 2592, 2604, 3669)

**The L key** builds the list of landable objects in the system — the test is
`HasValidPlanet() && GetPlanet()->IsAccessible(ship)`, so stars and unnamed bodies
are excluded — then picks one of three ways:

1. **Hovering.** A ship inside an object's radius and slower than
   `MIN_LANDING_VELOCITY / 60` (= 80/60 px/f, AI.h:255) is *considering* that world,
   and it wins outright. This is what stops the selection overriding a player who has
   already flown themselves somewhere.
2. **Cycling.** With a target set and the key pressed again inside the repeat
   cooldown (`Engine.cpp:2241` raises `WAIT`), step to the next landable — EXCEPT
   when already inside the current target's radius, where the target stands, so the
   last press before touchdown does not throw the approach away.
3. **Fresh.** Nearest, with everything that cannot recharge fuel pushed **+10,000**
   down the ranking. This is the load-bearing detail: systems are full of bare rocks
   nearer than the port, and plain "nearest" picks one every time.

**Then it flies there:** `autoPilot |= LAND`, and each frame `MoveToPlanet` →
`MoveTo(target->Position(), Point(), target->Radius(), 1.)`. `MoveTo` steers at
`target − StoppingPoint(...)`, NOT at the target: the stopping point is where the
ship would come to rest if it turned and braked now, and substituting it is what
makes the approach converge instead of overshooting and looping forever. Arrival is
`dp.Length() < radius && speed < slow` — the same two conditions as `Ship::CanLand`.
Any manual input cancels the autopilot (AI.cpp:558).

**Planet labels** (PlanetLabel.cpp:138-160) draw name + government beside each world
in flight, coloured from the government and dimmed when `!planet.CanLand()`; a
wormhole takes its link colour instead. Upstream's radar is ship-centred at a fixed
scale, which works because its main view is already a wide 2D one.

Landing (M4): `pos = 0.97·pos + 0.03·target` per frame while zoom shrinks.
