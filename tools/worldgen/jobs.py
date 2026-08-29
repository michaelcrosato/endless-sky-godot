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


#: Several phrasings per archetype. One template per kind of work makes a board
#: of a hundred jobs read as one job printed a hundred times — measured on the
#: first pass, a thousand jobs shared thirty-nine display strings, and "Escort a
#: convoy to <planet>" alone appeared a hundred and forty-three times.
CARGO_TITLES = [
    "Deliver {goods} to <planet>", "{tons} of {goods} for <planet>",
    "Freight run: {goods} to <planet>", "{goods} wanted on <planet>",
    "Haul {goods} out to <planet>", "Shipment of {goods} for <destination>",
    "Consignment: {goods} to <planet>", "Carry {goods} as far as <planet>",
]

CARGO_BODIES = [
    "A {race} shipper wants <tons> of {goods} taken from <origin> to <destination>. "
    "Payment is <payment> on delivery.",
    "<cargo>, loading now at <origin>, bound for <destination>. The broker pays "
    "<payment> and does not haggle.",
    "There is <cargo> sitting on a pad at <origin> that somebody on <planet> is "
    "already paying rent on. <payment> to move it.",
    "Standing contract: <cargo> from <origin> to <destination>, <payment>. The "
    "{race} name on the manifest is worth more than the fee.",
    "Nobody has explained why <cargo> has to reach <destination> rather than "
    "somewhere nearer. The fee is <payment> and the question is not encouraged.",
]

PASSAGE_TITLES = [
    "Carry {who} to <planet>", "Passage for {who} to <planet>",
    "{count} berths wanted for <planet>", "Take {who} as far as <planet>",
    "{who} looking for a ship to <planet>", "Charter: {who} to <destination>",
    "Berths to <planet> for {who}",
]

PASSAGE_BODIES = [
    "<fare> travelling from <origin> to <destination>: {blurb}. They will pay "
    "<payment> when they arrive.",
    "{blurb_cap}. They need <bunks> berths as far as <destination>, and they are "
    "paying <payment>.",
    "A group waiting at <origin> for anything headed toward <destination>. "
    "{blurb_cap}. <payment>, and they keep to themselves.",
    "<fare> for <destination>. {blurb_cap}. The fare is <payment>, half of it "
    "already lodged with the port.",
]

BOUNTY_TITLES = [
    "Bounty: {target}", "{target} raiders wanted", "Standing bounty on {target}",
    "Clear {target} hulls near <planet>", "Letters of marque: {target}",
    "{target} activity near <destination>", "Contract: {target}",
]

BOUNTY_BODIES = [
    "{race} authorities are paying for {target} hulls destroyed near "
    "<destination>. <payment> on proof, and they are not fussy about the proof.",
    "{count_word} {target} ship{plural} have been working the lanes around "
    "<destination>. {race} pays <payment> for the wreckage.",
    "The bounty on {target} near <destination> has been raised twice this month, "
    "which tells you how the first two attempts went. <payment>.",
    "{target} took a {race} hauler apart near <destination> last week. The owner, "
    "not the government, is paying the <payment>.",
]

ESCORT_TITLES = [
    "Escort a convoy to <planet>", "Guard {who} to <planet>",
    "Convoy protection: <origin> to <planet>", "Ride along to <destination>",
    "Armed escort wanted for <planet>", "See {who} safely to <planet>",
]

ESCORT_BODIES = [
    "A {friendly} convoy is running from <origin> to <destination> and would "
    "rather not do it alone. <payment> if it arrives intact.",
    "{friendly} lost two hulls on this route last month. They are paying "
    "<payment> for company as far as <destination>.",
    "Unarmed {friendly} shipping, <origin> to <destination>. The fee is <payment> "
    "and it is contingent on everyone arriving.",
    "The cargo is not the point; the crew is. {friendly} pays <payment> to see "
    "them reach <destination>.",
]

SALVAGE_TITLES = [
    "Recover {what}", "Salvage claim: {what}", "{what_cap} near <planet>",
    "Board {what} out past <planet>", "Survey {what}",
]

SALVAGE_BODIES = [
    "{what_cap} has been logged out past <destination>. {race} salvage rights are "
    "already sold; the buyer wants it boarded and catalogued. <payment>.",
    "Something is drifting near <destination> that answers no hail. {race} will "
    "pay <payment> to know what it is, and the rights are theirs either way.",
    "{what_cap}, transponder dead, near <destination>. Board it, log what is "
    "aboard, come back. <payment>, and no questions about the crew.",
]

COURIER_TITLES = [
    "Courier run to <planet>", "Urgent packet for <planet>",
    "{what_cap} to <destination>", "Fast delivery: <planet>",
    "Sealed run to <planet>", "Priority courier to <planet>",
]

COURIER_BODIES = [
    "{what_cap} must reach <destination> by <date>. It masses almost nothing and "
    "the fee is <payment>, which should tell you something about what is in it.",
    "A {race} factor is paying <payment> for {what} to be at <destination> before "
    "<date>. No hold space required, and no delays excused.",
    "{what_cap}, <origin> to <destination>, by <date>. <payment> on arrival and "
    "nothing at all if you are late.",
]

SUPPLY_TITLES = [
    "Mining supply run to <planet>", "{ore_cap} wanted at <planet>",
    "Feedstock for <planet>", "{tons} of {ore} for <destination>",
    "Refinery contract: <planet>",
]

SUPPLY_BODIES = [
    "A {race} refinery at <destination> is short of {ore} feedstock and is buying "
    "<tons> at <payment>. The belts near here are the usual source.",
    "<cargo> wanted at <destination>. The {race} refinery there has been running "
    "under capacity for a month. <payment>.",
    "Standing order at <destination> for {ore}: <tons>, <payment>. They will take "
    "it as often as you can bring it.",
]

COUNT_WORDS = {1: "A", 2: "Two", 3: "Three"}


def _pick(rng, templates, **fields):
    return rng.choice(templates).format(**fields)


def _cargo(rng, race: Race) -> Job:
    commodity, plain, character = rng.choice(CARGO_KINDS)
    tons = rng.choice([5, 10, 15, 20, 25, 30, 40, 50, 60, 80])
    reach = rng.randint(1, 6)

    fields = {"goods": plain, "tons": f"{tons} tons", "race": race.name}
    urgency = "" if character != "urgent" else " They needed it yesterday."

    return Job(
        name=f"{race.key} cargo {plain} {tons} {rng.randrange(100000)}",
        display=_pick(rng, CARGO_TITLES, **fields),
        description=_pick(rng, CARGO_BODIES, **fields) + urgency,
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

    fields = {"who": kind, "count": count, "blurb": blurb,
              "blurb_cap": blurb[0].upper() + blurb[1:], "race": race.name}

    return Job(
        name=f"{race.key} passage {kind} {count} {rng.randrange(100000)}",
        display=_pick(rng, PASSAGE_TITLES, **fields),
        description=_pick(rng, PASSAGE_BODIES, **fields),
        archetype="passengers",
        source_filter=_source(race),
        destination_filter={"distance": (1, reach)},
        passengers=count,
        deadline=rng.randint(15, 70),
        payment=_pay(rng, 7_500 + count * 2_100, reach),
    )


def _bounty(rng, race: Race) -> Job:
    # Pick the FACTION first, not the race. Choosing a race and then hoping it has
    # raiders in it lands on peoples who have none - the Orokh field only a navy and
    # a corporation - and the fallback then puts a bounty on somebody perfectly
    # friendly, who never fights back and can never be collected on.
    hostile = [(r, f) for r in RACES if r.key != race.key
               for f in r.factions if f.role in ("fringe", "zealot")]
    preying = [(r, f) for r, f in hostile
               if r.temperament in ("predatory", "expansionist")]
    target_race, target = rng.choice(preying or hostile)
    count = rng.choice([1, 1, 1, 2, 2, 3])

    fields = {"target": target.name, "race": race.name, "count": count,
              "count_word": COUNT_WORDS[count], "plural": "" if count == 1 else "s"}

    return Job(
        name=f"{race.key} bounty {target.name} {rng.randrange(100000)}",
        display=_pick(rng, BOUNTY_TITLES, **fields),
        description=_pick(rng, BOUNTY_BODIES, **fields),
        archetype="bounty",
        source_filter=_source(race),
        destination_filter={"distance": (1, rng.randint(2, 5))},
        deadline=rng.randint(25, 90),
        payment=_pay(rng, 42_000 * count, 4),
        npc={"objective": "kill", "government": target.name,
             "personality": ["heroic", "vindictive"], "count": count,
             "race": target_race.key, "at_destination": True},
    )


def _escort(rng, race: Race) -> Job:
    friendly = rng.choice([f for f in race.factions
                           if f.role in ("trade", "corporate")] or list(race.factions))
    who = rng.choice(["a convoy", "a hauler", "a survey team", "a bullion run",
                      "a relief flight", "a diplomatic barge"])

    fields = {"friendly": friendly.name, "who": who, "race": race.name}

    return Job(
        name=f"{race.key} escort {rng.randrange(100000)}",
        display=_pick(rng, ESCORT_TITLES, **fields),
        description=_pick(rng, ESCORT_BODIES, **fields),
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
    fields = {"what": what, "what_cap": what[0].upper() + what[1:], "race": race.name}

    return Job(
        name=f"{race.key} salvage {rng.randrange(100000)}",
        display=_pick(rng, SALVAGE_TITLES, **fields),
        description=_pick(rng, SALVAGE_BODIES, **fields),
        archetype="salvage",
        source_filter=_source(race),
        destination_filter={"distance": (1, rng.randint(2, 6))},
        deadline=rng.randint(30, 100),
        payment=_pay(rng, 33_000, 4),
        npc={"objective": "board", "government": "Derelict",
             "personality": ["derelict", "uninterested"], "count": 1,
             "race": rng.choice(RACES).key, "at_destination": True},
    )


def _courier(rng, race: Race) -> Job:
    what = rng.choice([
        "a sealed data core", "an unregistered ledger", "a diplomatic packet",
        "a set of survey plates", "a court summons", "an encrypted key",
        "a medical sample", "a signed writ", "a black-boxed recorder",
    ])
    reach = rng.randint(2, 7)
    fields = {"what": what, "what_cap": what[0].upper() + what[1:], "race": race.name}

    return Job(
        name=f"{race.key} courier {rng.randrange(100000)}",
        display=_pick(rng, COURIER_TITLES, **fields),
        description=_pick(rng, COURIER_BODIES, **fields),
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
    fields = {"ore": ore, "ore_cap": ore.capitalize(), "tons": f"{tons} tons",
              "race": race.name}

    return Job(
        name=f"{race.key} supply {ore} {rng.randrange(100000)}",
        display=_pick(rng, SUPPLY_TITLES, **fields),
        description=_pick(rng, SUPPLY_BODIES, **fields),
        archetype="supply",
        source_filter=_source(race, "mining"),
        destination_filter={"attributes": ["manufacturing", "mining"],
                            "distance": (1, rng.randint(2, 5))},
        cargo=("Heavy Metals", tons),
        deadline=rng.randint(20, 70),
        payment=_pay(rng, 12_000 + tons * 420, 3),
    )
