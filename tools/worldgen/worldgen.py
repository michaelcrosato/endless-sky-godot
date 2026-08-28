"""Generates the Reach: an original universe in Endless Sky data format.

Run it:

    python tools/worldgen/worldgen.py [--systems 1000] [--jobs 1000] [--out universe]

Everything it writes is regenerable from the seed, so the output is a build
artifact with a source rather than a pile of hand-written text nobody dares
touch. Change a race's design language here and its whole territory changes with
it — ships, worlds, prices and all.

The generator does not validate the result. That is deliberate: the check that
matters is whether the ENGINE can read it, so validation lives in the test suite
where the real parser does the reading.
"""

import argparse
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import emit                      # noqa: E402
import galaxy as galaxy_module   # noqa: E402
import jobs as jobs_module       # noqa: E402
import ships as ships_module     # noqa: E402
from races import RACES          # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate the Reach.")
    parser.add_argument("--systems", type=int, default=1000)
    parser.add_argument("--jobs", type=int, default=1000)
    parser.add_argument("--seed", type=int, default=20260828)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    root = args.out or os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "..", "universe")
    root = os.path.abspath(root)

    print(f"[worldgen] seed {args.seed} -> {root}")

    systems = galaxy_module.build(seed=args.seed, total_systems=args.systems)
    outfits = ships_module.build_outfits()
    fleet = ships_module.build_ships(outfits, seed=args.seed ^ 0x5EED)
    work = jobs_module.build(seed=args.seed + 7, total=args.jobs)

    _assign_fleets(systems, args.seed)

    written = {
        "governments": emit.governments(root),
        "commodities": emit.commodities(root),
        "minables": emit.minables(root),
        "outfits": emit.outfits(root, outfits),
        "ships": emit.ships(root, fleet),
        "shops": emit.shops(root, fleet, outfits),
        "fleets": emit.fleets(root, fleet),
        "systems": emit.systems(root, systems),
        "planets": emit.planets(root, systems),
        "jobs": emit.missions(root, work, fleet),
        "start": emit.start(root, systems),
    }

    worlds = sum(len(w.moons) + 1 for s in systems for w in s.worlds)
    inhabited = sum(1 for s in systems for w in s.worlds
                    if w.inhabited) + sum(
        1 for s in systems for w in s.worlds for m in w.moons if m.inhabited)
    factions = sum(len(r.factions) for r in RACES)

    print(f"[worldgen] {len(systems)} systems, {worlds} worlds "
          f"({inhabited} inhabited)")
    print(f"[worldgen] {len(RACES)} races, {factions} factions")
    print(f"[worldgen] {len(fleet)} ships across {len(ships_module.CLASSES)} classes")
    print(f"[worldgen] {len(outfits)} outfits")
    print(f"[worldgen] {len(work)} jobs across {len(jobs_module.ARCHETYPES)} archetypes")
    print(f"[worldgen] files: {', '.join(sorted(written))}")
    return 0


def _assign_fleets(systems, seed: int) -> None:
    """Gives each system the traffic its owner would actually send.

    A system with no fleet entry is a system that stays empty however long the
    player waits in it, so every inhabited system gets at least one.
    """
    rng = random.Random(seed ^ 0xF1EE7)

    for system in systems:
        if system.race is None:
            # Frontier space: somebody passes through, but rarely.
            if rng.random() < 0.55:
                race = rng.choice(RACES)
                faction = rng.choice(race.factions)
                system.fleets.append((f"{faction.name} Patrol", rng.randrange(2500, 9000, 500)))
            continue

        for faction in system.race.factions:
            # The owner's own traffic is common; its rivals' is not.
            common = faction.name == system.government
            if not common and rng.random() > 0.45:
                continue

            period = rng.randrange(600, 2200, 100) if common else rng.randrange(2500, 8000, 500)
            system.fleets.append((f"{faction.name} Patrol", period))


if __name__ == "__main__":
    raise SystemExit(main())
