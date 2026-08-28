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

## Hull scale comes from hardpoints, not mass

Upstream has no `length` field. A ship's size *is* the size of its sprite, and the
only sprite-scale measurement present in the data files is the hardpoint offsets,
which are sprite pixel coordinates. So a hull is sized from its own hardpoints:

    length = max(along span, across span / 0.85, fitted mass curve)
    beam   = clamp(across span, 0.34 x length, 0.85 x length)

Spans include a 1.15x margin so a mount at the extreme still sits on the hull
rather than half off the tip.

The first attempt derived length from mass as a cube root, reasoning that mass
tracks volume. Measured against the fleet that is simply wrong: regressing
log(mass) on log(hardpoint span) over the 318 ships carrying both gives

    span ~ mass^0.47      (r = 0.84)

not `mass^0.33`. Ships are shells, and larger hulls are proportionally hollower.
The cube root therefore under-sized big ships badly, and because mount positions
were already at true sprite scale, **287 of the 339 armed ships wore their guns
outside their own hull** — by up to 4.5x on a Deep River Transport. The hulls were
too small; the mounts had been right all along.

The mass curve survives only as a floor, for hulls whose mounts all sit near the
centre and for the unarmed ships that have no hardpoints to measure. Keeping the
whole fleet on one curve is what stops a fighter and a World-Ship from being drawn
to two incompatible scales.

One consequence worth stating: the resulting fleet spans roughly 190x in length,
not the ~19x a cube root gives. That is the spread upstream's sprites actually
have. A Korath World-Ship really is orders of magnitude longer than an interceptor.

## Lighting the hull, and one bug worth remembering

Godot treats **clockwise** winding as front-facing, so a front face's outward
normal is `(c-a) x (b-a)` — the negation of the counter-clockwise convention.

Getting this backwards does not make anything vanish, because culling keys off
winding rather than off the normal attribute. The geometry draws; every outward
face just carries an inward normal, `N.L` goes negative across the entire lit side,
and hulls render as flat black silhouettes. Turning rim lighting on inverts the
symptom rather than fixing it: Godot's rim term is not scaled by `N.L`, so it lights
the hull uniformly and the same bug now reads as a flat *white* blob. Both
"blobs" were one defect.

Two guards in `tests/godot/ShipMeshBuilderTest.cs` hold the line, since a mesh
defect is invisible to every simulation test: face normals must point away from the
hull centre, and the dorsal surface must sit above the ventral in value. The
simulation suite passed 373/373 throughout the entire episode.

## Open items

- Faction design language needs a ship → government association the data does not
  provide directly; fleets and shipyards are the route.
- Hull *beam* is still inferred, not measured: mounts cluster near the centreline
  and so understate true width. The 0.34 floor is a fleet median, not a per-ship
  fact.
- Windows, damage geometry and silhouette composition are specified here but
  generating them is presentation work in `src/game`, which this document does not
  cover.
