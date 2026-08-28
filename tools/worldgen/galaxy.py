"""Builds the systems, worlds and trade of the Reach.

Layout is territorial rather than random: each race holds a region of the map,
its systems cluster around a capital, and the space between regions is frontier
that belongs to nobody. That is what makes travel mean something — crossing from
Voth space into Nyx space should be a thing you notice, and it cannot be if
systems are scattered uniformly and coloured at random.

Connectivity is guaranteed rather than hoped for. Links come from a nearest
neighbour pass, then a spanning tree stitches every component together, because a
galaxy with an unreachable pocket is a galaxy with content nobody can ever see.
"""

import math
import random
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Set, Tuple

from races import RACES, Race

# World types the renderer knows how to colour. Kept here so the generator and
# the view layer share one vocabulary; adding a type means adding it to both.
WORLD_SPRITES = {
    "earthlike": "planet/earthlike",
    "ocean": "planet/ocean",
    "desert": "planet/desert",
    "rock": "planet/rock",
    "ice": "planet/ice",
    "gas": "planet/gas",
    "cloud": "planet/cloud",
    "storm": "planet/storm",
    "lava": "planet/lava",
    "ash": "planet/ash",
    "forest": "planet/forest",
    "fungal": "planet/fungal",
    "swamp": "planet/swamp",
    "crystal": "planet/crystal",
    "shard": "planet/shard",
    "machine": "planet/machine",
    "relic": "planet/relic",
    "hive": "planet/hive",
    "industrial": "planet/industrial",
    "fortress": "planet/fortress",
    "derelict": "planet/derelict",
    "dense": "planet/dense",
    "void": "planet/void",
    "cathedral": "planet/cathedral",
}

STAR_SPRITES = {
    "b": "star/b2",
    "a": "star/a0",
    "f": "star/f5",
    "g": "star/g5",
    "k": "star/k3",
    "m": "star/m4",
    "brown": "star/brown",
    "neutron": "star/neutron",
}

# Habitable zone distance by star class: hotter stars push it out. Used for
# planet placement so an ice world is not found hugging a blue giant.
STAR_HEAT = {
    "b": 3000.0, "a": 2200.0, "f": 1500.0, "g": 1100.0,
    "k": 800.0, "m": 500.0, "brown": 220.0, "neutron": 300.0,
}

COMMODITIES = [
    "Food", "Clothing", "Metal", "Plastic", "Equipment",
    "Medical", "Industrial", "Electronics", "Heavy Metals", "Luxury Goods",
]

# Base price and how far a world's character can move it.
COMMODITY_BASE = {
    "Food": 220, "Clothing": 260, "Metal": 420, "Plastic": 380,
    "Equipment": 550, "Medical": 620, "Industrial": 760,
    "Electronics": 880, "Heavy Metals": 1150, "Luxury Goods": 1250,
}

MINABLES = ["iron", "copper", "silver", "gold", "titanium", "uranium",
            "silicon", "platinum", "iridium", "neodymium"]


@dataclass
class World:
    name: str
    kind: str
    government: str
    attributes: List[str]
    inhabited: bool
    spaceport: str
    distance: float
    period: float
    shipyard: Optional[str] = None
    outfitter: Optional[str] = None
    security: float = 0.25
    moons: List["World"] = field(default_factory=list)


@dataclass
class System:
    name: str
    x: float
    y: float
    race: Optional[Race]
    government: str
    star_classes: List[str]
    habitable: float
    worlds: List[World]
    asteroids: List[Tuple[str, int, float]]
    minables: List[Tuple[str, int, float]]
    trade: Dict[str, int]
    links: Set[str] = field(default_factory=set)
    fleets: List[Tuple[str, int]] = field(default_factory=list)
    arrival: float = 0.0


class NameForge:
    """Race-flavoured names that never repeat."""

    def __init__(self, rng: random.Random):
        self.rng = rng
        self.used: Set[str] = set()

    def make(self, race: Optional[Race], kind: str = "system") -> str:
        for _ in range(400):
            name = self._attempt(race, kind)
            if name not in self.used:
                self.used.add(name)
                return name

        # Fall back to numbering rather than looping forever; a duplicate name
        # would collide in the loader's dictionary and silently drop a system.
        base = self._attempt(race, kind)
        index = 2
        while f"{base} {index}" in self.used:
            index += 1
        name = f"{base} {index}"
        self.used.add(name)
        return name

    def _attempt(self, race: Optional[Race], kind: str) -> str:
        rng = self.rng
        if race is None:
            stem = rng.choice(FRONTIER_STEMS)
            if rng.random() < 0.35:
                return f"{stem} {rng.choice(GREEK)}"
            return f"{stem}-{rng.randint(2, 989)}"

        parts = rng.randint(2, 3)
        word = "".join(rng.choice(race.syllables) for _ in range(parts))
        word = word.capitalize()
        if rng.random() < 0.55:
            word += rng.choice(race.suffixes)
        if kind == "system" and rng.random() < 0.18:
            word += f" {rng.choice(ROMAN)}"
        return word


FRONTIER_STEMS = [
    "Coldwater", "Longpass", "Ashfall", "Tinderbox", "Fallow", "Waystone",
    "Gallows", "Harrow", "Marrow", "Nettle", "Quarry", "Rookery", "Saltmarsh",
    "Tallow", "Umber", "Vantage", "Windlass", "Yardarm", "Bellrock", "Cinderway",
    "Drybank", "Emberlee", "Foxhole", "Gravewatch", "Hollowmere", "Ironbind",
]

GREEK = ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta",
         "Iota", "Kappa", "Lambda", "Sigma", "Omega"]

ROMAN = ["II", "III", "IV", "V", "VI", "VII", "IX", "XI"]


def build(seed: int = 20260828, total_systems: int = 1000) -> List[System]:
    rng = random.Random(seed)
    forge = NameForge(rng)

    systems: List[System] = []
    per_race = int(total_systems * 0.78) // len(RACES)
    frontier_count = total_systems - per_race * len(RACES)

    # Territories on a disc, evenly spaced in angle so no two races overlap
    # entirely, with a jittered radius so the map is not a perfect ring.
    centres: List[Tuple[float, float]] = []
    for index in range(len(RACES)):
        angle = (index / len(RACES)) * math.tau + rng.uniform(-0.08, 0.08)
        radius = rng.uniform(520.0, 1250.0)
        centres.append((math.cos(angle) * radius, math.sin(angle) * radius))

    for race, (cx, cy) in zip(RACES, centres):
        spread = 150.0 + per_race * 3.2
        for _ in range(per_race):
            # Gaussian around the capital: dense core, thinning marches.
            x = rng.gauss(cx, spread * 0.42)
            y = rng.gauss(cy, spread * 0.42)
            systems.append(_make_system(rng, forge, race, x, y))

    for _ in range(frontier_count):
        angle = rng.uniform(0, math.tau)
        radius = rng.uniform(0.0, 1500.0)
        x = math.cos(angle) * radius
        y = math.sin(angle) * radius

        nearest = min(range(len(RACES)),
                      key=lambda i: math.hypot(centres[i][0] - x, centres[i][1] - y))
        systems.append(_make_system(rng, forge, None, x, y, claimant=RACES[nearest]))

    _link(systems, rng)
    return systems


def _make_system(rng: random.Random, forge: NameForge, race: Optional[Race],
                 x: float, y: float, claimant: Optional[Race] = None) -> System:
    name = forge.make(race, "system")

    star_pool = race.stars if race else ("g", "k", "m", "f", "a", "brown")
    stars = [rng.choice(star_pool)]
    if rng.random() < 0.16:
        stars.append(rng.choice(star_pool))

    habitable = STAR_HEAT[stars[0]] * rng.uniform(0.85, 1.15)

    government = ""
    if race is not None:
        # A race's own space is mostly its dominant faction, with pockets held by
        # the others — which is what gives a territory internal politics.
        weights = [(f, 3.0 if f.role == "navy" else 1.0) for f in race.factions]
        government = _weighted(rng, weights).name
    elif claimant is not None and rng.random() < 0.30:
        # Frontier space that is claimed is claimed by whoever is NEAREST. Drawing
        # a government at random scattered single factions across the whole map,
        # which leaves them holding no territory in any meaningful sense.
        government = rng.choice(claimant.factions).name

    worlds = _make_worlds(rng, forge, race, habitable, government)
    asteroids, minables = _make_rocks(rng)
    trade = _make_trade(rng, worlds)

    system = System(
        name=name, x=round(x, 3), y=round(y, 3), race=race, government=government,
        star_classes=stars, habitable=round(habitable, 2), worlds=worlds,
        asteroids=asteroids, minables=minables, trade=trade,
    )

    # Busy, well-defended systems hold arrivals further out.
    if any(w.inhabited for w in worlds) and rng.random() < 0.22:
        system.arrival = float(rng.randrange(1000, 6000, 500))

    return system


def _make_worlds(rng, forge, race, habitable, government) -> List[World]:
    count = rng.choices([0, 1, 2, 3, 4, 5, 6], weights=[6, 14, 22, 24, 18, 11, 5])[0]
    pool = list(race.worlds) if race else [
        "rock", "ice", "gas", "desert", "ocean", "earthlike", "cloud", "derelict"]

    worlds: List[World] = []
    distance = rng.uniform(280.0, 620.0)

    for index in range(count):
        distance *= rng.uniform(1.35, 1.95)
        kind = rng.choice(pool)

        # Cold out there, hot close in — the star's habitable distance decides.
        if distance > habitable * 2.2 and rng.random() < 0.65:
            kind = rng.choice(["ice", "gas", "rock", "void"])
        elif distance < habitable * 0.45 and rng.random() < 0.6:
            kind = rng.choice(["lava", "ash", "rock", "dense"])

        inhabited = (
            race is not None
            and kind in ("earthlike", "ocean", "forest", "industrial", "hive",
                         "crystal", "machine", "fortress", "cathedral", "dense",
                         "gas", "cloud", "fungal", "swamp", "relic")
            and habitable * 0.4 < distance < habitable * 3.0
            and rng.random() < 0.55
        )

        world = World(
            name=forge.make(race, "planet"),
            kind=kind,
            government=government if inhabited else "",
            attributes=_world_attributes(rng, kind, inhabited, race),
            inhabited=inhabited,
            spaceport=_spaceport_text(rng, race) if inhabited else "",
            distance=round(distance, 2),
            period=round(math.sqrt(distance ** 3) / rng.uniform(180.0, 260.0), 3),
            security=round(rng.uniform(0.05, 0.9), 2),
        )

        # inhabited is only ever true for a race's own space, but say so rather
        # than relying on it.
        if inhabited and race is not None:
            tier = rng.random()
            if tier < 0.42:
                world.shipyard = f"{race.key} shipyard"
            if tier < 0.62:
                world.outfitter = f"{race.key} outfitter"

        # Moons: a reason for a system to have more than one landing.
        if kind in ("gas", "storm", "cloud", "dense") and rng.random() < 0.55:
            for _ in range(rng.randint(1, 3)):
                moon_kind = rng.choice(["rock", "ice", "industrial", "derelict"])
                moon_inhabited = race is not None and rng.random() < 0.30
                world.moons.append(World(
                    name=forge.make(race, "planet"),
                    kind=moon_kind,
                    government=government if moon_inhabited else "",
                    attributes=_world_attributes(rng, moon_kind, moon_inhabited, race),
                    inhabited=moon_inhabited,
                    spaceport=_spaceport_text(rng, race) if moon_inhabited else "",
                    distance=round(rng.uniform(90.0, 240.0), 2),
                    period=round(rng.uniform(9.0, 40.0), 3),
                    security=round(rng.uniform(0.05, 0.8), 2),
                    outfitter=(f"{race.key} outfitter"
                               if moon_inhabited and race is not None and rng.random() < 0.3
                               else None),
                ))

        worlds.append(world)

    return worlds


TRAIT_BY_KIND = {
    "earthlike": ["farming", "temperate"],
    "ocean": ["fishing", "temperate"],
    "desert": ["mining", "arid"],
    "rock": ["mining", "barren"],
    "ice": ["frozen", "mining"],
    "gas": ["gas giant", "refinery"],
    "cloud": ["gas giant", "skyhold"],
    "storm": ["gas giant", "hazardous"],
    "lava": ["volcanic", "hazardous", "mining"],
    "ash": ["volcanic", "barren"],
    "forest": ["farming", "forested"],
    "fungal": ["fungal", "farming"],
    "swamp": ["swamp", "humid"],
    "crystal": ["crystalline", "research"],
    "shard": ["crystalline", "barren"],
    "machine": ["machine", "research", "manufacturing"],
    "relic": ["ancient", "research"],
    "hive": ["hive", "crowded"],
    "industrial": ["manufacturing", "urban"],
    "fortress": ["military", "fortified"],
    "derelict": ["derelict", "salvage"],
    "dense": ["high gravity", "mining"],
    "void": ["void", "barren"],
    "cathedral": ["shrine", "pilgrimage"],
}


def _world_attributes(rng, kind, inhabited, race) -> List[str]:
    traits = list(TRAIT_BY_KIND.get(kind, ["barren"]))
    if not inhabited:
        traits.append("uninhabited")
        return traits

    traits.append(race.key if race else "independent")
    for extra in ("rich", "poor", "dissident", "tourism", "shipping",
                  "military", "research", "frontier", "core"):
        if rng.random() < 0.13:
            traits.append(extra)

    return traits


def _spaceport_text(rng, race) -> str:
    opener = rng.choice([
        "The port is cut into the rock itself",
        "Landing pads ring a shallow crater",
        "The field is old, and patched in a dozen places",
        "A single tower handles every approach",
        "Gantries reach out over open water",
        "The terminal is warm, loud and crowded",
        "Freight moves through here faster than people do",
    ])
    closer = rng.choice([
        "and nobody asks where you came from.",
        "and the fees are posted where you cannot miss them.",
        "and there is always a queue.",
        "and the air smells of hot metal.",
        "and half the berths are empty.",
        "and someone is always watching the arrivals board.",
    ])
    who = f"{race.name} colours fly over the gate. " if race else ""
    return f"{opener}, {closer} {who}".strip()


def _make_rocks(rng):
    asteroids = []
    for size in ("small", "medium", "large"):
        for material in ("rock", "metal"):
            if rng.random() < 0.55:
                asteroids.append((f"{size} {material}",
                                  rng.randint(1, 90),
                                  round(rng.uniform(0.9, 5.2), 4)))

    minables = []
    for _ in range(rng.randint(0, 4)):
        minables.append((rng.choice(MINABLES),
                         rng.randint(1, 22),
                         round(rng.uniform(1.0, 4.0), 5)))

    return asteroids, minables


def _inhabited_bodies(worlds: List[World]) -> List[World]:
    """Every settled body, moons included.

    Counting only top-level worlds left systems whose sole settlement was a moon
    with no market and no traffic — inhabited by the loader's reckoning and dead
    to the player.
    """
    found: List[World] = []
    for world in worlds:
        if world.inhabited:
            found.append(world)
        found.extend(m for m in world.moons if m.inhabited)
    return found


def _make_trade(rng, worlds) -> Dict[str, int]:
    if not _inhabited_bodies(worlds):
        return {}

    traits = {t for w in worlds for t in w.attributes}
    traits |= {t for w in worlds for m in w.moons for t in m.attributes}
    prices = {}
    for commodity in COMMODITIES:
        base = COMMODITY_BASE[commodity]
        factor = rng.uniform(0.72, 1.28)

        # A world's character moves what it sells cheaply and what it needs.
        if commodity == "Food" and {"farming", "fishing"} & traits:
            factor *= 0.62
        if commodity == "Food" and {"barren", "void", "volcanic"} & traits:
            factor *= 1.45
        if commodity in ("Metal", "Heavy Metals") and "mining" in traits:
            factor *= 0.6
        if commodity in ("Industrial", "Equipment") and "manufacturing" in traits:
            factor *= 0.68
        if commodity == "Electronics" and "research" in traits:
            factor *= 0.7
        if commodity == "Luxury Goods" and "rich" in traits:
            factor *= 1.35
        if commodity == "Luxury Goods" and "poor" in traits:
            factor *= 0.8
        if commodity == "Medical" and "hazardous" in traits:
            factor *= 1.4

        prices[commodity] = max(40, int(base * factor))

    return prices


def _weighted(rng, pairs):
    total = sum(weight for _, weight in pairs)
    roll = rng.uniform(0, total)
    for value, weight in pairs:
        roll -= weight
        if roll <= 0:
            return value
    return pairs[-1][0]


def _link(systems: List[System], rng: random.Random) -> None:
    """Nearest-neighbour links, then a spanning pass so nothing is stranded."""
    count = len(systems)

    def distance(a: System, b: System) -> float:
        return math.hypot(a.x - b.x, a.y - b.y)

    # Nearest few, which produces dense cores and sparse frontier naturally.
    for i, system in enumerate(systems):
        nearest = sorted(
            ((distance(system, other), j) for j, other in enumerate(systems) if j != i),
            key=lambda pair: pair[0])[:6]

        wanted = rng.choices([1, 2, 3, 4], weights=[16, 40, 30, 14])[0]
        for dist, j in nearest[:wanted]:
            # A link across half the map is not a link, it is a wormhole.
            if dist > 260.0:
                continue
            system.links.add(systems[j].name)
            systems[j].links.add(system.name)

    # Stitch components together: walk each unreached component and join it to
    # the nearest reached system. A galaxy with an island has content in it that
    # no player can ever get to.
    by_name = {s.name: s for s in systems}
    seen: Set[str] = set()
    order: List[System] = []

    def flood(start: System):
        stack = [start]
        component = []
        while stack:
            node = stack.pop()
            if node.name in seen:
                continue
            seen.add(node.name)
            component.append(node)
            for link in node.links:
                if link not in seen:
                    stack.append(by_name[link])
        return component

    components = []
    for system in systems:
        if system.name not in seen:
            components.append(flood(system))

    components.sort(key=len, reverse=True)
    main = components[0]
    order.extend(main)

    for component in components[1:]:
        best = None
        for node in component:
            for other in order:
                d = distance(node, other)
                if best is None or d < best[0]:
                    best = (d, node, other)

        if best is None:
            raise RuntimeError("a component had no reachable neighbour to stitch to")

        _, node, other = best
        node.links.add(other.name)
        other.links.add(node.name)
        order.extend(component)

    assert len(order) == count, "every system must end up in the connected graph"
