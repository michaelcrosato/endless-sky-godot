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

## Hyperspace constants (for the travel milestone)

`HYPER_C = 100` frames, `HYPER_A = 2` px/f², `HYPER_D = 1000` px; arrival
offset = C²·A/2 + D = **11,000 px** short of the target point; drag does not
apply in hyperspace. Landing: `pos = 0.97·pos + 0.03·target` per frame while
zoom shrinks.
