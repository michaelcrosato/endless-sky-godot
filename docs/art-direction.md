# Art direction — Milestone 8

Common rules for a consistent low-poly art set, derived from the actual upstream
fleet rather than chosen by eye. Every number below comes from surveying the 902
ship definitions we load; `tests/sim/ArtDirectionSurvey.cs` regenerates it, and
`tests/sim/ShipAppearanceTests.cs` asserts the rules still hold as content grows.

The executable form of this document is `libs/EndlessSky.Sim/ShipAppearance.cs`,
which is engine-free: it decides *what* a hull should look like from data the
simulation already has. The presentation layer decides *how* to draw it.

## What the fleet actually looks like

| | |
|---|---|
| Ships with mass | 902 |
| Mass range | 1 → 67,400 (**67,400×**) |
| Mass percentiles | p05 40 · p25 220 · **p50 630** · p75 1,510 · p95 4,360 |
| Guns per hull | max 18, mean 2.8 |
| Turrets per hull | max 22, mean 2.7 |
| Engines per hull | max 48, mean 3.4 |

Categories, by population: Medium Warship (153), Heavy Warship (134), Light
Warship (106), Utility (95), Interceptor (89), Heavy Freighter (72), Transport
(63), Light Freighter (62), Fighter (43), Drone (23), Space Liner (21),
Unclassified (21), Superheavy (9).

## Scale

**Length scales with the cube root of mass.** This is the single most important
rule, and it follows from the survey rather than from taste: the fleet spans a
67,400× mass range. Scaling any linear dimension proportionally to mass would
make a Drone literally invisible beside a Superheavy — they could never share a
frame. Mass tracks volume, and volume is the cube of a linear dimension, so:

```
length = 60 units × ∛(mass / 630)
```

630 is the fleet median, anchored to 60 units so a typical hull sits at a
comfortable mid-size. In practice this maps the 67,400× mass range onto a **40.7×**
length range — 7.0u for the smallest drone to 284.8u for the largest warship,
median 60.0u. Dramatic, but drawable together.

Beam is 0.62 × length; upstream sprites average roughly 2:3 across the fleet.
Framing radius is half the length.

**Hardpoint coordinates in ship data are stored at double scale.** Upstream
halves them in the `Hardpoint` constructor. Anything placing geometry from the
data — mounts, engine flares, muzzle flashes — must halve them too, or every
fitting sits twice as far from the hull centre as it should.

## Polygon density

Budgets are tied to hull class, not to importance, so a swarm cannot out-cost the
capital ship it is attacking:

| Class | Triangles | Population |
|---|---|---|
| Drone | 150 | 27 |
| Fighter | 300 | 50 |
| Light | 700 | 269 |
| Medium | 1,500 | 278 |
| Heavy | 3,000 | 269 |
| Capital | 6,000 | 9 |

Classification uses the declared `category` where the data gives one, and falls
back to mass otherwise. Category has to win: a Utility hull can outweigh a
warship without being one.

## Silhouettes

The directive requires immediately recognisable silhouettes. With hundreds of
hulls per class that cannot come from per-ship authoring, so it comes from
composition rules keyed to data the ship already carries:

- **Engine count drives the stern.** 1 engine reads as a single nozzle; 2–4 as a
  cluster; the 48-engine outliers as a bank. Engine mounts are already positioned
  in the data.
- **Turret count drives the dorsal line.** Turrets sit on the widest part of the
  hull; a 22-turret warship reads as bristling because it genuinely is.
- **Gun ports drive the nose.** Forward-firing hulls taper to their gun line.
- **Freighters are volume, warships are surface.** Cargo space relative to mass
  distinguishes a hauler's slab-sided hull from a warship's faceted one.

## Materials and emissives

Three material families only — hull plate, dark recess, emissive. Anything more
and the fleet stops reading as one set.

- **Windows** come from `bunks`: one lit port per four berths, capped at 40 so a
  liner does not become a light grid. An `automaton` shows **none** — a drone
  reading as crewed is the fastest way to break the fleet's internal logic.
- **Engine glow** is thrust per unit mass, normalised so most ships sit near 1.0
  and outliers stand out. A tug with heavy engines on a light hull visibly burns
  harder than a laden freighter.
- Emissives are the only saturated colour on a hull. Plate is desaturated so
  weapon fire, shield impacts and engine wash carry the eye.

## Faction design language

**Not derivable from a ship definition** — upstream associates ships with
governments through fleets and shipyards, not on the hull. `ShipAppearance.Faction`
is therefore settable and defaults to null, and remains an open item.

The intended rules once it is wired: each faction gets one silhouette motif
(human blocky/utilitarian, Hai rounded, Korath asymmetric-industrial, Pug
organic), one plate hue, and one emissive hue. A hull should be identifiable as
its faction's at a glance, before any detail resolves.

## Damage states

Four bands, driven by hull fraction:

| State | Hull remaining | Reads as |
|---|---|---|
| 0 | > 75% | pristine |
| 1 | > 45% | scorched, some panels dark |
| 2 | > 15% | venting, emissives flickering |
| 3 | ≤ 15% | wreck: emissives dead, structure broken |

State 3 matters more than it looks. A disabled ship sits at its hull threshold,
which the simulation puts between 10% and 50% of maximum depending on size — so
"disabled" and "visibly wrecked" need to coincide, or the player cannot tell a
boardable derelict from a ship still fighting.

## Open items

- Faction design language needs a ship → government association the data does not
  provide directly; fleets and shipyards are the route.
- Windows, damage geometry and silhouette composition are specified here but
  generating them is presentation work in `src/game`, which this document does not
  cover.
