# Milestone 9 — Full Gauntlet

> Repeatedly play representative Endless Sky scenarios in both implementations.
> Compare: controls, economics, missions, combat, progression, difficulty, fleet
> behavior, travel, outfitting.

## How this is run, and what "both implementations" means here

Upstream ships a headless test mode (`endless-sky --test <name>`) and 22 authored
integration tests under `tests/integration`. That would be the ideal A/B rig, and
it is not available: the checkout is source only, upstream builds through CMake and
vcpkg, and neither `cmake` nor `ninja` is installed on this machine. Building it
would pull and compile SDL2, OpenAL, libmad, libpng and libjpeg first.

So the comparison is made two ways, and neither is a claim to have run the C++
build:

1. **Against upstream's source.** Every rule is read out of
   `external/endless-sky/source` and pinned by a test that cites the function it
   came from. This is what the 443-test `sim` suite is.
2. **Against upstream's own authored expectations.** Upstream's integration tests
   assert on autoconditions — `flagship landed`, `flagship planet: Mars` — and
   those assertions are mirrored in `tests/sim/PlayerStateTests.cs`. The parts of
   those tests that drive the real UI through keyboard input and menu navigation
   are **not** reproduced; a headless simulation cannot, and pretending otherwise
   would be the worse answer.

`tests/sim/GauntletScenarios.cs` is the milestone's own instrument: scenarios that
run end to end on the real dataset, one per dimension above. Every other test in
the suite pins one rule in isolation. These run many rules together and ask whether
the *result* is what a player would experience — which is the only way to catch the
defect class where every part is correct and the combination is not.

That distinction is not theoretical. The first run of this suite found four defects
that 428 existing passing tests could not see.

## What playing it found

### Weapons with a short lifetime dealt no damage at all

Upstream appends newly fired rounds to the projectile list and *then* runs
`DoCollisions` over all of them (`Engine.cpp:1909`, then `:1921`). A round is
always collision-tested against the ground it is about to cover, and only moves on
the following frame.

We moved first and tested the segment behind. But `Projectile.Step` marks an
expiring round dead and returns **without moving it**, so for a round that expired
that frame the segment collapsed to a single point and could never intersect
anything. Any weapon with a lifetime of 1 therefore did nothing at all.

The Beam Laser has a lifetime of exactly 1. It is the standard human starter
weapon and what a stock Sparrow carries into every early fight in the game.

The weapon was correct. The projectile was correct. The damage model was correct.
The engagement was not.

### Weapon outfits never reached their hardpoints

`AddOutfit` recorded an outfit and folded in its attributes. `BuildMounts` created
the hardpoints and left them empty. Nothing joined the two, so a ship handed its
stock loadout carried its guns as inventory with every hardpoint empty.

A stock Sparrow holds two Beam Lasers and could fire neither. Every NPC in the game
was harmless. Upstream loads the Armament as part of finishing a ship; both paths
now do, and the order they are called in no longer matters.

### Reload clocks only advanced if the caller remembered

`StepArmament` was the caller's job. The flight scene called it for the drone and
not for the player, so the player could fire each gun exactly once per session.
Upstream advances reloads inside `Ship::Move`; ours now does the same.

### Anti-missile mounts counted as armament

Upstream services anti-missile and tractor hardpoints on a separate path and never
fires them at ships (`Hardpoint::IsSpecial`). Counting them made a stock Star Barge
— which carries an anti-missile turret and nothing else — read as a warship: it
would hunt for targets it has no way to hurt and close to a standoff no weapon of
its could reach.

### Smaller things the same pass turned up

- `GameData` never parsed `mission` nodes, so the mission system, which is fully
  implemented, had no content to run.
- `ShouldFire` disagreed with `CanFire`: an unloaded missile pod answered "yes,
  fire" on every frame forever, and only the firing path silently declined.

## Dimension by dimension

| Dimension | State | Evidence and what is still missing |
|---|---|---|
| **Controls** | Verified | Acceleration equals thrust over mass; drag caps the ship at its rated top speed; a turn cannot exceed the rated turn rate in one frame; retrograde braking slows the ship from any heading. Manoeuvring now costs energy and heat and is throttled by what the ship can afford, and ships generate power, shields and hull back. No afterburner yet. |
| **Travel** | Verified | A jump costs fuel and arrives correctly; systems that set an arrival distance hold arrivals away from their worlds. Jump drives reach unlinked systems by map distance and are charged by destination; 18 wormholes connect where no drive can. Scram drives remain. |
| **Economics** | Verified | A real route turns a profit equal to the spread times the tonnage; salaries accrue; 480 systems quote prices, and those prices now MOVE — supply decays and drifts and price follows it, bounded at 100 credits either side of base. Ships and outfits can be bought and sold, with depreciation. |
| **Outfitting** | Verified | Every gun-port weapon in the game fits some hull in the game — the check that caught the derived-gun-ports defect. Installing consumes a port and adds mass; the outfitter names the limit that binds. |
| **Combat** | Verified | A stock Sparrow disables a stock Star Barge and stops at the disable threshold rather than destroying it. This is the scenario that found the collision, arming and reload defects. |
| **Fleet behaviour** | Verified | An armed ship closes and holds its standoff; an unarmed hauler picks no fights. Escorts take orders (escort, gather, hold, attack) on upstream's `MoveTo` and `StoppingPoint`, and systems spawn their own traffic — 599 declare it, and it flies in the running game. Carried fighters and named formations remain. |
| **Missions** | Verified | The full lifecycle: a real world offers 424 jobs, one can be taken, carried and handed in at its destination, and a deadline that passes fails it. Text substitution means the board reads as prose rather than templates. Waypoints, stopovers and timers remain. |
| **Progression** | Verified | Time and travel accumulate, autoconditions read live state, events change the galaxy (416 of them), the start comes from the data with its opening conditions, and the whole game round-trips through save and load byte-identically. No campaign scripting yet. |
| **Difficulty** | Partial | Cost and durability correlate at r > 0.5 across 200+ hulls, so the power curve has the right shape. Genuine difficulty balance needs the campaign and fleet spawning. |

## Fixes made during this milestone, beyond the four above

**Hyperspace arrival ignored the destination's arrival distance.** Upstream aims at
the system *centre* by default and only picks a planet when the destination sets no
extra arrival distance. Systems set one precisely to stop ships dropping in on top
of their inhabited worlds, so aiming at a planet anyway put arrivals exactly where
the setting exists to prevent. The planet chosen also has to have services, not
merely a name.

**Hull scale disagreed with hardpoint positions.** Sizing hulls from mass by a cube
root left 287 of the 339 armed ships wearing their guns outside their own geometry,
by up to 4.5×. Measured across the fleet, length goes as `mass^0.47`, not
`mass^0.33`. Hulls are now measured from their own hardpoints. See
`docs/art-direction.md`.

**Ships had no faction.** A ship definition never names a government, so every hull
was drawn in the same neutral plating. Resolved from the fleets that fly a hull and
the shipyards that stock it — 738 of 902 ships across 58 governments.

**Binary stars had no derived orbital period**, and `DataWriter` wrote round-trip
precision where upstream writes eight significant digits.

**Manoeuvring was free, and nothing regenerated.** Upstream charges thrust and
turning against energy and heat, throttling the command by what the ship can
afford. Adding that alone would have browned out every ship in the game
permanently, because energy, shields and hull only ever went down — nothing in the
simulation regenerated anything. Both halves are now in: see `Ship::DoGeneration`
and the movement costs in `Ship::Move`.

## Standing caveat

Upstream's own integration tests are the strongest available oracle and most of
them remain out of reach without a UI. Until the C++ build is available, "matches
upstream" in this repository means *matches upstream's source as read and cited*,
not *observed side by side*. That distinction is deliberate and should not be
quietly dropped.
