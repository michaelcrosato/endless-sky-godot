"""Hulls and equipment.

Twenty classes give the shape of a ship — how big, how many hardpoints, what it
is for. A race's design language then bends that baseline: a Voth frigate and a
Lumen frigate share a role and almost nothing else, because the race multipliers
push hull, shields, speed and price in different directions.

Every ship is built with a loadout it can actually carry. That is checked rather
than assumed: outfit space, weapon capacity and hardpoint counts are all spent
down as equipment is added, and the generator stops when a ship is full. A hull
that ships with more equipment than it has room for is a hull the outfitter can
never put back together.
"""

import random
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple

from races import RACES, Race


@dataclass(frozen=True)
class ShipClass:
    name: str            # the category, which the game groups ships by
    role: str
    mass: float
    hull: float
    shields: float
    outfit_space: float
    engine_capacity: float
    weapon_capacity: float
    cargo: float
    bunks: int
    crew: int
    guns: int
    turrets: int
    engines: int
    cost: int
    drag: float


# Twenty classes, ordered small to large within each family.
CLASSES: List[ShipClass] = [
    ShipClass("Scout",           "recon",    60,  260,  180,  110,  55,  10,  12,  2, 1, 1, 0, 1,   190_000, 1.1),
    ShipClass("Courier",         "civilian", 80,  320,  200,  130,  60,  12,  30,  3, 1, 1, 0, 1,   240_000, 1.4),
    ShipClass("Shuttle",         "civilian", 90,  380,  220,  135,  62,  14,  22,  6, 1, 1, 0, 2,   210_000, 1.6),
    ShipClass("Yacht",           "civilian", 140, 520,  420,  180,  80,  20,  30, 10, 2, 1, 1, 2,   640_000, 1.8),
    ShipClass("Interceptor",     "warship",  110, 420,  340,  165,  85,  40,   8,  2, 1, 3, 0, 2,   520_000, 1.2),
    ShipClass("Fighter",         "warship",  150, 560,  420,  195,  90,  55,  12,  3, 2, 3, 0, 2,   690_000, 1.5),
    ShipClass("Bomber",          "warship",  210, 720,  460,  235, 100,  85,  20,  4, 2, 4, 0, 2,   940_000, 2.0),
    ShipClass("Corvette",        "warship",  280, 980,  680,  300, 120,  95,  40,  8, 4, 4, 1, 3, 1_450_000, 2.4),
    ShipClass("Frigate",         "warship",  420, 1500, 1050, 400, 150, 140,  60, 14, 8, 5, 2, 3, 2_600_000, 3.0),
    ShipClass("Destroyer",       "warship",  680, 2400, 1700, 560, 190, 210,  80, 26, 16, 6, 3, 4, 4_900_000, 4.1),
    ShipClass("Cruiser",         "warship",  1100, 4200, 3000, 820, 250, 320, 130, 48, 30, 6, 5, 4, 9_800_000, 5.6),
    ShipClass("Battlecruiser",   "warship",  1700, 6600, 4800, 1150, 320, 470, 170, 72, 48, 8, 7, 5, 17_500_000, 7.2),
    ShipClass("Dreadnought",     "warship",  2600, 11000, 7600, 1650, 420, 700, 220, 110, 80, 10, 10, 6, 32_000_000, 9.4),
    ShipClass("Carrier",         "warship",  2300, 8200, 5600, 1500, 400, 380, 400, 140, 90, 4, 8, 6, 27_000_000, 8.8),
    ShipClass("Freighter",       "trader",   380, 1300, 700,  340, 140,  40, 250, 10, 4, 1, 1, 3, 1_150_000, 3.4),
    ShipClass("Heavy Freighter", "trader",   760, 2600, 1300, 560, 200,  70, 520, 18, 8, 2, 2, 4, 2_900_000, 5.2),
    ShipClass("Bulk Hauler",     "trader",   1500, 4600, 2100, 880, 280, 100, 1100, 30, 14, 2, 3, 5, 6_400_000, 7.8),
    ShipClass("Miner",           "utility",  520, 1900, 900,  430, 170,  90, 300, 12, 6, 2, 2, 3, 1_800_000, 4.4),
    ShipClass("Salvager",        "utility",  640, 2200, 1000, 500, 180, 110, 380, 16, 8, 2, 2, 4, 2_300_000, 4.9),
    ShipClass("Tender",          "utility",  900, 3000, 1900, 700, 230, 130, 260, 40, 24, 2, 3, 4, 4_200_000, 6.1),
]


@dataclass
class Outfit:
    name: str
    category: str
    cost: int
    attributes: Dict[str, float]
    weapon: Optional[Dict[str, Any]] = None
    description: str = ""


@dataclass
class Ship:
    name: str
    ship_class: ShipClass
    race: Race
    attributes: Dict[str, float]
    outfits: List[Tuple[str, int]]
    guns: List[Tuple[float, float]]
    turrets: List[Tuple[float, float]]
    engines: List[Tuple[float, float]]
    description: str


# --- Equipment ---------------------------------------------------------------

TIERS = [
    ("Mk I", 1.00, 1.00),
    ("Mk II", 1.35, 1.55),
    ("Mk III", 1.80, 2.45),
    ("Mk IV", 2.40, 3.90),
    ("Mk V", 3.20, 6.20),
]


def build_outfits() -> List[Outfit]:
    outfits: List[Outfit] = []

    for race in RACES:
        prefix = race.name.split()[0]

        # Power: the thing every other upgrade competes for.
        for tier, power, price in TIERS:
            outfits.append(Outfit(
                f"{prefix} Reactor {tier}", "Power",
                int(58_000 * price * race.cost),
                {"mass": 22 * power, "outfit space": -(18 * power),
                 "energy generation": round(1.35 * power * race.shields, 3),
                 "heat generation": round(2.1 * power, 3)},
                description=f"{race.name} power plant. Everything else on the ship "
                            f"is queuing for what this makes.",
            ))
            outfits.append(Outfit(
                f"{prefix} Cell {tier}", "Power",
                int(14_000 * price * race.cost),
                {"mass": 9 * power, "outfit space": -(7 * power),
                 "energy capacity": round(620 * power, 1)},
                description="Storage, for the moments when generation is not enough.",
            ))

        # Defence.
        for tier, power, price in TIERS:
            outfits.append(Outfit(
                f"{prefix} Shield Array {tier}", "Shields",
                int(46_000 * price * race.cost * race.shields),
                {"mass": 16 * power, "outfit space": -(15 * power),
                 "shields": round(340 * power * race.shields, 1),
                 "shield generation": round(0.30 * power * race.shields, 3),
                 "shield energy": round(0.28 * power, 3)},
                description=f"{race.blurb}",
            ))
            outfits.append(Outfit(
                f"{prefix} Plating {tier}", "Armour",
                int(28_000 * price * race.cost),
                {"mass": 40 * power, "outfit space": -(11 * power),
                 "hull": round(420 * power * race.hull, 1),
                 "drag": round(0.09 * power, 3)},
                description="Weight you carry so that damage is something you survive.",
            ))
            outfits.append(Outfit(
                f"{prefix} Repair Unit {tier}", "Systems",
                int(52_000 * price * race.cost),
                {"mass": 14 * power, "outfit space": -(12 * power),
                 "hull repair rate": round(0.55 * power, 3),
                 "hull energy": round(0.5 * power, 3)},
                description="Slow, steady, and worth more than it looks after a fight.",
            ))

        # Movement.
        for tier, power, price in TIERS:
            outfits.append(Outfit(
                f"{prefix} Thruster {tier}", "Engines",
                int(34_000 * price * race.cost),
                {"mass": 18 * power, "outfit space": -(16 * power),
                 "engine capacity": -(16 * power),
                 # Measured against upstream: generated hulls massed about the same
                 # as human ones but carried a quarter of the thrust and a ninth of
                 # the turn, so nothing could come about inside a dogfight and two
                 # ships that passed each other simply drifted apart. Output is now
                 # scaled to upstream's band; the energy and heat per unit of output
                 # match its engines rather than being made free.
                 "thrust": round(38.0 * power * race.speed, 3),
                 "thrusting energy": round(0.55 * power, 3),
                 "thrusting heat": round(1.15 * power, 3)},
                description="Forward. Everything else is a negotiation with momentum.",
            ))
            outfits.append(Outfit(
                f"{prefix} Steering {tier}", "Engines",
                int(26_000 * price * race.cost),
                {"mass": 13 * power, "outfit space": -(12 * power),
                 "engine capacity": -(12 * power),
                 "turn": round(1035 * power * race.agility, 2),
                 "turning energy": round(2.4 * power, 3),
                 "turning heat": round(3.9 * power, 3)},
                description="How quickly an argument can be pointed somewhere else.",
            ))

        # Heat, which is what limits everything above.
        for tier, power, price in TIERS[:4]:
            outfits.append(Outfit(
                f"{prefix} Radiator {tier}", "Systems",
                int(18_000 * price * race.cost),
                {"mass": 11 * power, "outfit space": -(9 * power),
                 "cooling": round(1.6 * power, 3)},
                description="Heat is the tax on every other system aboard.",
            ))

        # Travel.
        outfits.append(Outfit(
            f"{prefix} Hyperdrive", "Drives", int(180_000 * race.cost),
            {"mass": 30, "outfit space": -25, "hyperdrive": 1, "hyperdrive fuel": 100},
            description="Follows the lanes, like almost everyone else.",
        ))
        outfits.append(Outfit(
            f"{prefix} Fuel Tank", "Drives", int(22_000 * race.cost),
            {"mass": 12, "outfit space": -10, "fuel capacity": 300},
            description="Range, measured in jumps you do not have to plan around.",
        ))
        if race.temperament in ("ancient", "insular"):
            outfits.append(Outfit(
                f"{prefix} Jump Drive", "Drives", int(2_400_000 * race.cost),
                {"mass": 55, "outfit space": -45, "jump drive": 1,
                 "jump drive fuel": 200, "jump range": 100 + 40 * (race.cost > 1.2)},
                description="Ignores the lanes entirely. Goes where the links do not.",
            ))

        # Utility.
        outfits.append(Outfit(
            f"{prefix} Cargo Expansion", "Systems", int(28_000 * race.cost),
            {"mass": 4, "outfit space": -22, "cargo space": 30},
            description="Space is money, and this is the exchange rate.",
        ))
        outfits.append(Outfit(
            f"{prefix} Scanner", "Systems", int(46_000 * race.cost),
            {"mass": 6, "outfit space": -8, "cargo scan power": 22,
             "outfit scan power": 18},
            description="Tells you what someone else is carrying.",
        ))

        # Weapons: guns, turrets and missiles per race, three grades each.
        for grade, (label, power, price) in enumerate(TIERS[:3]):
            word = race.ship_words[grade % len(race.ship_words)]

            outfits.append(Outfit(
                f"{prefix} {word} Cannon {label}", "Guns",
                int(62_000 * price * race.cost),
                {"mass": 12 * power, "outfit space": -(11 * power),
                 "weapon capacity": -(11 * power), "gun ports": -1},
                weapon={"reload": max(4.0, 16 - 2.5 * grade),
                        "velocity": round(14 * power * race.speed, 2),
                        "lifetime": round(52 * power, 1),
                        "hull damage": round(11 * power * race.hull, 2),
                        "shield damage": round(13 * power * race.shields, 2),
                        "firing energy": round(2.4 * power, 3),
                        "firing heat": round(3.1 * power, 3),
                        "inaccuracy": round(max(0.4, 2.4 - 0.4 * grade), 2)},
                description=f"{race.name} pattern. {race.blurb}",
            ))

            outfits.append(Outfit(
                f"{prefix} {word} Turret {label}", "Turrets",
                int(120_000 * price * race.cost),
                {"mass": 26 * power, "outfit space": -(24 * power),
                 "weapon capacity": -(24 * power), "turret mounts": -1},
                weapon={"reload": max(6.0, 22 - 3.0 * grade),
                        "velocity": round(12 * power * race.speed, 2),
                        "lifetime": round(60 * power, 1),
                        "hull damage": round(21 * power * race.hull, 2),
                        "shield damage": round(24 * power * race.shields, 2),
                        "firing energy": round(4.8 * power, 3),
                        "firing heat": round(6.2 * power, 3),
                        "turret turn": round(2.4 + 0.6 * grade, 2)},
                description="Covers an arc rather than a direction.",
            ))

        # Missiles need ammunition, which is its own outfit and its own decision.
        missile = f"{prefix} {race.ship_words[-1]} Missile"
        outfits.append(Outfit(
            f"{missile} Rack", "Missiles", int(140_000 * race.cost),
            {"mass": 22, "outfit space": -20, "weapon capacity": -20, "gun ports": -1},
            weapon={"reload": 90, "velocity": 9.0, "lifetime": 320,
                    "hull damage": round(220 * race.hull, 1),
                    "shield damage": round(190 * race.shields, 1),
                    "homing": 1, "missile strength": 24,
                    "ammo": f"{missile}", "firing energy": 12.0},
            description="Patient, expensive, and hard to argue with.",
        ))
        outfits.append(Outfit(
            missile, "Ammunition", int(3_200 * race.cost),
            {"mass": 1.4, "outfit space": -1.4},
            description="One shot. Buy more than you think you need.",
        ))

        # Defensive point fire.
        outfits.append(Outfit(
            f"{prefix} Interceptor Battery", "Turrets", int(190_000 * race.cost),
            {"mass": 20, "outfit space": -18, "weapon capacity": -18, "turret mounts": -1},
            weapon={"reload": 8, "velocity": 26, "lifetime": 14,
                    "anti-missile": round(9 * race.shields, 1),
                    "firing energy": 3.0},
            description="Shoots at missiles, and at nothing else.",
        ))

    return outfits


# --- Hulls -------------------------------------------------------------------

def build_ships(outfits: List[Outfit], seed: int = 909090) -> List[Ship]:
    rng = random.Random(seed)
    by_name = {o.name: o for o in outfits}
    ships: List[Ship] = []
    used: set = set()

    # Five ships per class, each from a different race, so every class is
    # represented across the map rather than being one people's speciality.
    for index, ship_class in enumerate(CLASSES):
        for slot in range(5):
            race = RACES[(index * 5 + slot * 7) % len(RACES)]
            name = _ship_name(rng, race, ship_class, used)
            ships.append(_make_ship(rng, race, ship_class, name, by_name))

    return ships


def _ship_name(rng, race: Race, ship_class: ShipClass, used: set) -> str:
    for _ in range(200):
        word = rng.choice(race.ship_words)
        if rng.random() < 0.45:
            stem = "".join(rng.choice(race.syllables) for _ in range(2)).capitalize()
            name = f"{stem} {word}"
        else:
            name = f"{word} {rng.choice(['I', 'II', 'III', 'IV', 'V', 'VII', 'IX', 'X'])}"
        if name not in used:
            used.add(name)
            return name

    name = f"{race.key.capitalize()} {ship_class.name} {len(used)}"
    used.add(name)
    return name


def _make_ship(rng, race: Race, cls: ShipClass, name: str,
               by_name: Dict[str, Outfit]) -> Ship:
    prefix = race.name.split()[0]
    variance = rng.uniform(0.9, 1.12)

    attributes: Dict[str, float] = {
        "cost": int(cls.cost * race.cost * variance),
        "shields": round(cls.shields * race.shields * variance, 1),
        "hull": round(cls.hull * race.hull * variance, 1),
        "mass": round(cls.mass * (0.85 + 0.3 * race.hull), 1),
        "drag": round(cls.drag / max(0.6, race.speed), 3),
        "heat dissipation": round(rng.uniform(0.55, 0.95), 3),
        "fuel capacity": 300 + 100 * (cls.guns + cls.turrets),
        "cargo space": round(cls.cargo * rng.uniform(0.9, 1.1)),
        "outfit space": round(cls.outfit_space * rng.uniform(0.95, 1.08)),
        "weapon capacity": round(cls.weapon_capacity * rng.uniform(0.95, 1.1)),
        "engine capacity": round(cls.engine_capacity * rng.uniform(0.95, 1.08)),
        "bunks": cls.bunks,
        "required crew": cls.crew,
    }

    # Hardpoints, laid out along the hull. Sprite coordinates are double scale
    # upstream, and the appearance layer halves them, so these are written at
    # that same double scale.
    guns = _spread(rng, cls.guns, -34.0, -8.0, 20.0)
    turrets = _spread(rng, cls.turrets, -6.0, 26.0, 26.0)
    engines = _spread(rng, cls.engines, 26.0, 40.0, 16.0)

    loadout = _fit_loadout(rng, race, cls, prefix, attributes, by_name)

    description = (
        f"A {race.name} {cls.name.lower()}. {race.blurb} "
        f"{_class_note(cls)}"
    )

    return Ship(name, cls, race, attributes, loadout, guns, turrets, engines, description)


def _class_note(cls: ShipClass) -> str:
    return {
        "recon": "Built to see and not be seen.",
        "civilian": "Not a warship, and does not pretend to be.",
        "warship": "Every spare ton of it went into the fight.",
        "trader": "The hold is the point; everything else serves it.",
        "utility": "Purpose-built, and awkward at anything else.",
    }[cls.role]


def _spread(rng, count: int, near: float, far: float, width: float):
    """Hardpoints placed symmetrically, so a hull does not look lopsided."""
    points = []
    for i in range(count):
        along = near + (far - near) * (i / max(1, count - 1) if count > 1 else 0.5)
        if count == 1:
            points.append((0.0, round(along, 1)))
        else:
            side = width * (0.35 + 0.65 * (i % 2))
            points.append((round(side if i % 2 == 0 else -side, 1), round(along, 1)))
    return points


def _fit_loadout(rng, race: Race, cls: ShipClass, prefix: str,
                 attributes: Dict[str, float],
                 by_name: Dict[str, Outfit]) -> List[Tuple[str, int]]:
    """Equips a ship only as far as it will actually go.

    Space, weapon capacity and hardpoints are spent as items are added. A hull
    that ships carrying more than it can hold cannot be reassembled by the
    outfitter, and the player discovers that the first time they sell anything.
    """
    space = attributes["outfit space"]
    weapon_space = attributes["weapon capacity"]
    engine_space = attributes["engine capacity"]
    gun_ports = cls.guns
    turret_mounts = cls.turrets

    chosen: List[Tuple[str, int]] = []

    def take(item_name: str, count: int = 1) -> bool:
        nonlocal space, weapon_space, engine_space, gun_ports, turret_mounts
        item = by_name.get(item_name)
        if item is None:
            return False

        need_space = -item.attributes.get("outfit space", 0.0) * count
        need_weapon = -item.attributes.get("weapon capacity", 0.0) * count
        need_engine = -item.attributes.get("engine capacity", 0.0) * count
        need_guns = -item.attributes.get("gun ports", 0.0) * count
        need_turrets = -item.attributes.get("turret mounts", 0.0) * count

        if (need_space > space or need_weapon > weapon_space
                or need_engine > engine_space
                or need_guns > gun_ports or need_turrets > turret_mounts):
            return False

        space -= need_space
        weapon_space -= need_weapon
        engine_space -= need_engine
        gun_ports -= need_guns
        turret_mounts -= need_turrets
        chosen.append((item_name, count))
        return True

    # Grade of equipment scales with the hull it is going into.
    grade = min(4, max(0, int(cls.outfit_space / 320)))
    tier = TIERS[grade][0]
    lower = TIERS[max(0, grade - 1)][0]

    # Order matters: a ship that cannot move or jump is not a ship.
    take(f"{prefix} Hyperdrive")
    for candidate in (tier, lower, "Mk I"):
        if take(f"{prefix} Thruster {candidate}"):
            break
    for candidate in (tier, lower, "Mk I"):
        if take(f"{prefix} Steering {candidate}"):
            break
    for candidate in (tier, lower, "Mk I"):
        if take(f"{prefix} Reactor {candidate}"):
            break

    take(f"{prefix} Cell {lower}")
    if not take(f"{prefix} Shield Array {lower}"):
        take(f"{prefix} Shield Array Mk I")
    if not take(f"{prefix} Radiator {lower}"):
        take(f"{prefix} Radiator Mk I")
    take(f"{prefix} Fuel Tank")

    # Weapons, filling the hardpoints the hull actually has.
    word_index = 0
    while gun_ports > 0:
        word = race.ship_words[word_index % len(race.ship_words)]
        word_index += 1
        if not (take(f"{prefix} {word} Cannon {TIERS[min(2, grade)][0]}")
                or take(f"{prefix} {word} Cannon Mk I")):
            break

    while turret_mounts > 0:
        word = race.ship_words[word_index % len(race.ship_words)]
        word_index += 1
        if not (take(f"{prefix} {word} Turret {TIERS[min(2, grade)][0]}")
                or take(f"{prefix} {word} Turret Mk I")):
            break

    # Traders and utility hulls would rather carry than fight.
    if cls.role in ("trader", "utility"):
        for _ in range(3):
            take(f"{prefix} Cargo Expansion")

    if cls.role == "warship" and space > 40:
        if not take(f"{prefix} Plating {lower}"):
            take(f"{prefix} Plating Mk I")

    return chosen
