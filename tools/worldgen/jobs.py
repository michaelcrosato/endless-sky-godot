"""A thousand jobs, in seven kinds.

Jobs are written as templates rather than as fixed itineraries: a job says
"somewhere within four jumps that mines metal", and the engine picks a real world
when it is offered. That is what lets a thousand jobs cover a thousand systems
without any of them naming a place that the player cannot get to.

The seven archetypes exist because they ask different things of a player. Cargo
wants hold space, passengers want bunks, bounties want guns, escort wants you to
keep something else alive, salvage wants you to go somewhere unpleasant, courier
wants speed, and supply wants you to have been mining. A job board of one
archetype in seven costumes is a board with one job on it.
"""

import random
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

from races import RACES, Race

ARCHETYPES = ["cargo", "passengers", "bounty", "escort", "salvage", "courier", "supply"]


@dataclass
class Job:
    name: str
    display: str
    description: str
    archetype: str
    source_filter: Dict[str, List[str]]
    destination_filter: Dict[str, object]
    cargo: Optional[Tuple[str, int]] = None
    passengers: int = 0
    deadline: int = 0
    payment: int = 0
    npc: Optional[Dict[str, object]] = None
    repeat: bool = True


CARGO_KINDS = [
    ("Food", "grain", "perishable"), ("Metal", "ore", "heavy"),
    ("Industrial", "machine parts", "bulky"), ("Medical", "medical supplies", "urgent"),
    ("Electronics", "components", "fragile"), ("Plastic", "polymer stock", "bulky"),
    ("Equipment", "tooling", "heavy"), ("Luxury Goods", "luxuries", "valuable"),
    ("Heavy Metals", "refined metals", "heavy"), ("Clothing", "textiles", "light"),
]

PASSENGER_KINDS = [
    ("migrants", "families looking for somewhere the rent is lower"),
    ("pilgrims", "travellers who will not explain the destination"),
    ("surveyors", "a survey team and rather more equipment than people"),
    ("students", "a class, a chaperone, and a great deal of noise"),
    ("diplomats", "quiet people with heavy luggage"),
    ("labourers", "contract workers rotating out"),
    ("refugees", "people leaving somewhere in a hurry"),
    ("medics", "a relief team, and they are in a hurry"),
]

SALVAGE_KINDS = [
    ("a drifting hulk", "derelict"), ("an abandoned station", "derelict"),
    ("a lost survey probe", "void"), ("a sealed vault", "relic"),
    ("a wrecked freighter", "salvage"),
]


def build(seed: int = 5150, total: int = 1000) -> List[Job]:
    rng = random.Random(seed)
    jobs: List[Job] = []
    used: set = set()

    # Even across archetypes, so no kind of work is a rarity.
    per_kind = total // len(ARCHETYPES)
    remainder = total - per_kind * len(ARCHETYPES)

    for index, archetype in enumerate(ARCHETYPES):
        count = per_kind + (1 if index < remainder else 0)
        builder = {
            "cargo": _cargo, "passengers": _passengers, "bounty": _bounty,
            "escort": _escort, "salvage": _salvage, "courier": _courier,
            "supply": _supply,
        }[archetype]

        for _ in range(count):
            race = rng.choice(RACES)
            job = builder(rng, race)

            # Names are the loader's key, so a collision silently drops a job.
            base = job.name
            suffix = 2
            while job.name in used:
                job.name = f"{base} {suffix}"
                suffix += 1

            used.add(job.name)
            jobs.append(job)

    return jobs


def _source(race: Race, *traits: str) -> Dict[str, List[str]]:
    """Where a job is posted: a race's space, optionally narrowed by world type."""
    source: Dict[str, List[str]] = {"government": [f.name for f in race.factions]}
    if traits:
        source["attributes"] = list(traits)
    return source


def _pay(rng, base: int, distance_hint: int = 3) -> int:
    return int(base * rng.uniform(0.8, 1.35) * (0.7 + 0.42 * distance_hint))


def _cargo(rng, race: Race) -> Job:
    commodity, plain, character = rng.choice(CARGO_KINDS)
    tons = rng.choice([5, 10, 15, 20, 25, 30, 40, 50, 60, 80])
    reach = rng.randint(1, 6)

    urgency = "" if character != "urgent" else " They needed it yesterday."
    return Job(
        name=f"{race.key} cargo {plain} {tons} {rng.randrange(10000)}",
        display=f"Deliver {plain} to <planet>",
        description=(
            f"A {race.name} shipper wants <tons> of {plain} taken from <origin> "
            f"to <destination>. Payment is <payment> on delivery.{urgency}"),
        archetype="cargo",
        source_filter=_source(race),
        destination_filter={"distance": (1, reach)},
        cargo=(commodity, tons),
        deadline=rng.randint(8, 40) if character == "urgent" else rng.randint(20, 90),
        payment=_pay(rng, 9_000 + tons * 320, reach),
    )


def _passengers(rng, race: Race) -> Job:
    kind, blurb = rng.choice(PASSENGER_KINDS)
    count = rng.choice([1, 2, 3, 4, 6, 8, 12, 16, 24])
    reach = rng.randint(1, 6)

    return Job(
        name=f"{race.key} passage {kind} {count} {rng.randrange(10000)}",
        display=f"Carry {kind} to <planet>",
        description=(
            f"<fare> travelling from <origin> to <destination>: {blurb}. "
            f"They will pay <payment> when they arrive."),
        archetype="passengers",
        source_filter=_source(race),
        destination_filter={"distance": (1, reach)},
        passengers=count,
        deadline=rng.randint(15, 70),
        payment=_pay(rng, 7_500 + count * 2_100, reach),
    )


def _bounty(rng, race: Race) -> Job:
    # Bounties are posted against somebody, so pick a plausible enemy.
    enemies = [r for r in RACES if r.key != race.key and
               r.temperament in ("predatory", "expansionist")]
    target_race = rng.choice(enemies or [r for r in RACES if r.key != race.key])
    target = rng.choice([f for f in target_race.factions if f.role in ("fringe", "navy", "zealot")])
    count = rng.choice([1, 1, 1, 2, 2, 3])

    return Job(
        name=f"{race.key} bounty {target.name} {rng.randrange(10000)}",
        display=f"Bounty: {target.name}",
        description=(
            f"{race.name} authorities are paying for {target.name} hulls destroyed "
            f"near <destination>. <payment> on proof, and they are not fussy about "
            f"the proof."),
        archetype="bounty",
        source_filter=_source(race),
        destination_filter={"distance": (1, rng.randint(2, 5))},
        deadline=rng.randint(25, 90),
        payment=_pay(rng, 42_000 * count, 4),
        npc={"objective": "kill", "government": target.name,
             "personality": ["heroic", "vindictive"], "count": count,
             "race": target_race.key},
    )


def _escort(rng, race: Race) -> Job:
    friendly = rng.choice([f for f in race.factions if f.role in ("trade", "corporate")] or
                          list(race.factions))

    return Job(
        name=f"{race.key} escort {rng.randrange(10000)}",
        display="Escort a convoy to <planet>",
        description=(
            f"A {friendly.name} convoy is running from <origin> to <destination> "
            f"and would rather not do it alone. <payment> if it arrives intact."),
        archetype="escort",
        source_filter=_source(race),
        destination_filter={"distance": (2, rng.randint(3, 7))},
        deadline=rng.randint(20, 60),
        payment=_pay(rng, 38_000, 5),
        npc={"objective": "save", "government": friendly.name,
             "personality": ["timid", "escort"], "count": rng.randint(1, 3),
             "race": race.key, "accompany": True},
    )


def _salvage(rng, race: Race) -> Job:
    what, _trait = rng.choice(SALVAGE_KINDS)

    return Job(
        name=f"{race.key} salvage {rng.randrange(10000)}",
        display=f"Recover {what}",
        description=(
            f"{what.capitalize()} has been logged out past <destination>. "
            f"{race.name} salvage rights are already sold; the buyer wants it "
            f"boarded and catalogued. <payment>, and no questions about the crew."),
        archetype="salvage",
        source_filter=_source(race),
        destination_filter={"distance": (1, rng.randint(2, 6))},
        deadline=rng.randint(30, 100),
        payment=_pay(rng, 33_000, 4),
        npc={"objective": "board", "government": "Derelict",
             "personality": ["derelict", "uninterested"], "count": 1,
             "race": rng.choice(RACES).key},
    )


def _courier(rng, race: Race) -> Job:
    what = rng.choice([
        "a sealed data core", "an unregistered ledger", "a diplomatic packet",
        "a set of survey plates", "a court summons", "an encrypted key",
    ])
    reach = rng.randint(2, 7)

    return Job(
        name=f"{race.key} courier {rng.randrange(10000)}",
        display="Courier run to <planet>",
        description=(
            f"{what.capitalize()} must reach <destination> by <date>. It masses "
            f"almost nothing and the fee is <payment>, which should tell you "
            f"something about what is in it."),
        archetype="courier",
        source_filter=_source(race),
        destination_filter={"distance": (2, reach)},
        cargo=("Electronics", 1),
        deadline=rng.randint(6, 24),
        payment=_pay(rng, 26_000, reach),
    )


def _supply(rng, race: Race) -> Job:
    ore = rng.choice(["iron", "copper", "silver", "gold", "titanium",
                      "uranium", "silicon", "platinum", "iridium", "neodymium"])
    tons = rng.choice([10, 15, 20, 30, 40, 60])

    return Job(
        name=f"{race.key} supply {ore} {rng.randrange(10000)}",
        display=f"Mining supply run to <planet>",
        description=(
            f"A {race.name} refinery at <destination> is short of {ore} feedstock "
            f"and is buying <tons> at <payment>. The belts near here are the "
            f"usual source."),
        archetype="supply",
        source_filter=_source(race, "mining"),
        destination_filter={"attributes": ["manufacturing", "mining"],
                            "distance": (1, rng.randint(2, 5))},
        cargo=("Heavy Metals", tons),
        deadline=rng.randint(20, 70),
        payment=_pay(rng, 12_000 + tons * 420, 3),
    )
