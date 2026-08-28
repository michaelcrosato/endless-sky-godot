"""The twenty peoples of the Reach, and the factions inside each.

Every race carries a design language rather than just a name, because the
generator downstream reads these fields to decide what its ships look like, what
its worlds are called, and who it shoots at. A race defined as only a string
produces fifty systems that differ by label alone.

Fields that drive generation:

    hull        how its ships are built: multipliers on hull, shields, speed,
                cost. This is what makes a Voth cruiser feel different from a
                Lumen one rather than being the same ship with another badge.
    palette     the star and world types common in its space, which is what
                makes its territory recognisable on sight.
    syllables   name generation, so a race's worlds sound like each other and
                unlike anyone else's.
    temperament how it treats its neighbours, used to build the attitude matrix.
"""

from dataclasses import dataclass
from typing import List, Tuple


@dataclass(frozen=True)
class Faction:
    """One government within a race."""

    name: str
    role: str            # navy | trade | fringe | zealot | corporate
    aggression: float    # 0 peaceful .. 1 hostile to everyone
    crew_attack: float
    crew_defense: float


@dataclass(frozen=True)
class Race:
    key: str
    name: str
    blurb: str
    # Ship design language: multipliers applied to the class baseline.
    hull: float
    shields: float
    speed: float
    agility: float
    cost: float
    # What its space looks like.
    stars: Tuple[str, ...]
    worlds: Tuple[str, ...]
    # How its worlds and ships are named.
    syllables: Tuple[str, ...]
    suffixes: Tuple[str, ...]
    ship_words: Tuple[str, ...]
    temperament: str     # insular | expansionist | mercantile | ancient | predatory
    factions: Tuple[Faction, ...]


def _f(name, role, aggression, atk=1.0, dfn=2.0):
    return Faction(name, role, aggression, atk, dfn)


RACES: List[Race] = [
    Race(
        key="terran",
        name="Terran Concord",
        blurb="Descendants of the colony fleets, holding the old trade lanes.",
        hull=1.0, shields=1.0, speed=1.0, agility=1.0, cost=1.0,
        stars=("g", "k", "f"),
        worlds=("earthlike", "ocean", "desert", "rock", "ice"),
        syllables=("nov", "cal", "har", "bre", "min", "sol", "ver", "dun"),
        suffixes=("ia", "on", "us", "ar", "eth", "held"),
        ship_words=("Lancer", "Warden", "Drover", "Kestrel", "Pilgrim"),
        temperament="mercantile",
        factions=(
            _f("Concord Navy", "navy", 0.35, 1.25, 2.3),
            _f("Concord Merchant Guild", "trade", 0.05),
            _f("Free Terran Worlds", "fringe", 0.30, 1.1, 2.0),
        ),
    ),
    Race(
        key="vashari",
        name="Vashari Accord",
        blurb="Crystalline minds who grow their hulls rather than build them.",
        hull=0.85, shields=1.45, speed=1.05, agility=1.1, cost=1.35,
        stars=("a", "b", "f"),
        worlds=("crystal", "ice", "shard", "rock"),
        syllables=("vash", "sel", "ith", "ael", "cry", "sen", "lyr"),
        suffixes=("ar", "iel", "esh", "ai", "un"),
        ship_words=("Facet", "Prism", "Refrain", "Lattice", "Chord"),
        temperament="insular",
        factions=(
            _f("Vashari Choir", "navy", 0.40, 1.0, 2.6),
            _f("Vashari Lapidary", "trade", 0.10),
        ),
    ),
    Race(
        key="keltoth",
        name="Kel'Toth Swarm",
        blurb="Hive builders who field many small ships and few large ones.",
        hull=0.65, shields=0.6, speed=1.35, agility=1.45, cost=0.55,
        stars=("m", "k"),
        worlds=("hive", "rock", "desert", "fungal"),
        syllables=("kel", "toth", "zik", "mur", "chak", "ssa", "vek"),
        suffixes=("ka", "ith", "uz", "ar", "on"),
        ship_words=("Drone", "Nymph", "Swarm", "Chitin", "Brood"),
        temperament="expansionist",
        factions=(
            _f("Kel'Toth Brood", "navy", 0.65, 1.4, 1.6),
            _f("Kel'Toth Drift", "fringe", 0.45, 1.2, 1.5),
        ),
    ),
    Race(
        key="morrow",
        name="Morrow Combine",
        blurb="A shipping cartel that outgrew the world it started on.",
        hull=1.25, shields=0.95, speed=0.8, agility=0.7, cost=0.9,
        stars=("g", "k"),
        worlds=("industrial", "rock", "ocean", "earthlike"),
        syllables=("mor", "brack", "hald", "wen", "stro", "gild"),
        suffixes=("row", "ford", "stead", "gate", "holm"),
        ship_words=("Ledger", "Consign", "Bulwark", "Tariff", "Freight"),
        temperament="mercantile",
        factions=(
            _f("Morrow Combine", "corporate", 0.15),
            _f("Combine Security", "navy", 0.40, 1.2, 2.2),
            _f("Morrow Debtors", "fringe", 0.35, 1.0, 1.8),
        ),
    ),
    Race(
        key="ixil",
        name="Ixil Ascendancy",
        blurb="Gas-giant dwellers whose cities float and never land.",
        hull=0.9, shields=1.2, speed=1.1, agility=0.95, cost=1.2,
        stars=("f", "g"),
        worlds=("gas", "cloud", "storm"),
        syllables=("ix", "il", "phaen", "aur", "tel", "ombr"),
        suffixes=("is", "ae", "or", "ynn"),
        ship_words=("Zephyr", "Mantle", "Cirrus", "Updraft", "Halo"),
        temperament="insular",
        factions=(
            _f("Ixil Ascendancy", "navy", 0.30, 1.1, 2.4),
            _f("Ixil Skyholds", "trade", 0.08),
        ),
    ),
    Race(
        key="sarn",
        name="Sarn Dominion",
        blurb="A war state that never demobilised after its founding.",
        hull=1.35, shields=1.15, speed=0.9, agility=0.85, cost=1.1,
        stars=("k", "m", "g"),
        worlds=("fortress", "rock", "desert", "industrial"),
        syllables=("sarn", "dro", "kesh", "var", "tul", "grim"),
        suffixes=("ax", "on", "ur", "eth", "kar"),
        ship_words=("Edict", "Legion", "Rampart", "Vow", "Scourge"),
        temperament="predatory",
        factions=(
            _f("Sarn Dominion", "navy", 0.70, 1.45, 2.5),
            _f("Sarn Marches", "fringe", 0.50, 1.2, 2.0),
            _f("Sarn Exiles", "fringe", 0.40, 1.15, 1.9),
        ),
    ),
    Race(
        key="ythera",
        name="Ythera Bloom",
        blurb="Their ships are grown, and heal between engagements.",
        hull=1.1, shields=1.25, speed=0.95, agility=1.0, cost=1.25,
        stars=("g", "k"),
        worlds=("forest", "fungal", "ocean", "earthlike"),
        syllables=("yth", "era", "sylv", "moro", "vine", "thal"),
        suffixes=("a", "en", "iss", "orr"),
        ship_words=("Frond", "Seedling", "Bramble", "Canopy", "Spore"),
        temperament="insular",
        factions=(
            _f("Ythera Bloom", "navy", 0.25, 1.0, 2.2),
            _f("Ythera Rootward", "trade", 0.05),
        ),
    ),
    Race(
        key="drell",
        name="Drell Wandering",
        blurb="Nomads with no homeworld and a fleet for a capital.",
        hull=0.95, shields=1.0, speed=1.25, agility=1.15, cost=1.0,
        stars=("m", "k", "brown"),
        worlds=("rock", "ice", "derelict"),
        syllables=("drell", "vann", "osk", "mira", "sund", "hael"),
        suffixes=("ur", "isk", "aan", "ol"),
        ship_words=("Wanderer", "Hearth", "Longboat", "Cradle", "Farsight"),
        temperament="mercantile",
        factions=(
            _f("Drell Caravan", "trade", 0.10),
            _f("Drell Outriders", "navy", 0.40, 1.2, 2.0),
        ),
    ),
    Race(
        key="orokh",
        name="Orokh Forges",
        blurb="Volcanic worlds, and weapons that run hot on purpose.",
        hull=1.3, shields=0.8, speed=0.95, agility=0.9, cost=0.95,
        stars=("m", "k"),
        worlds=("lava", "rock", "industrial", "ash"),
        syllables=("orokh", "vurm", "kald", "brenn", "slag", "tor"),
        suffixes=("ak", "un", "orr", "eth"),
        ship_words=("Cinder", "Bellows", "Anvil", "Ember", "Crucible"),
        temperament="expansionist",
        factions=(
            _f("Orokh Forgemasters", "corporate", 0.25),
            _f("Orokh Ashguard", "navy", 0.55, 1.35, 2.2),
        ),
    ),
    Race(
        key="lumen",
        name="Lumen Filament",
        blurb="Fast, fragile, and gone before the shot lands.",
        hull=0.6, shields=1.1, speed=1.5, agility=1.4, cost=1.3,
        stars=("a", "b"),
        worlds=("crystal", "ice", "cloud"),
        syllables=("lum", "aeth", "sira", "vel", "ori", "phos"),
        suffixes=("ae", "is", "el", "une"),
        ship_words=("Glimmer", "Arc", "Filament", "Corona", "Spark"),
        temperament="insular",
        factions=(
            _f("Lumen Filament", "navy", 0.30, 0.9, 2.4),
            _f("Lumen Diaspora", "fringe", 0.20),
        ),
    ),
    Race(
        key="grask",
        name="Grask Reclaimers",
        blurb="Nothing is scrap until they have already sold it twice.",
        hull=1.05, shields=0.7, speed=1.0, agility=1.0, cost=0.6,
        stars=("m", "k", "brown"),
        worlds=("derelict", "rock", "ash", "industrial"),
        syllables=("grask", "hulk", "rud", "skav", "murn", "clag"),
        suffixes=("ok", "ish", "ar", "um"),
        ship_words=("Picker", "Sifter", "Hulk", "Ragman", "Tallow"),
        temperament="predatory",
        factions=(
            _f("Grask Reclaimers", "trade", 0.30, 1.1, 1.7),
            _f("Grask Wreckers", "fringe", 0.75, 1.3, 1.6),
        ),
    ),
    Race(
        key="tessarai",
        name="Tessarai Enumeration",
        blurb="Machine intelligences that count everything, twice.",
        hull=1.0, shields=1.3, speed=1.05, agility=1.05, cost=1.4,
        stars=("a", "f", "g"),
        worlds=("machine", "crystal", "rock"),
        syllables=("tess", "arai", "quon", "ekt", "vor", "nul"),
        suffixes=("ix", "um", "ath", "eon"),
        ship_words=("Axiom", "Corollary", "Proof", "Remainder", "Set"),
        temperament="ancient",
        factions=(
            _f("Tessarai Enumeration", "navy", 0.35, 1.0, 2.8),
            _f("Tessarai Remainder", "fringe", 0.45, 1.1, 2.2),
        ),
    ),
    Race(
        key="voth",
        name="Voth Gravitum",
        blurb="Born under crushing gravity; their armour reflects it.",
        hull=1.55, shields=0.9, speed=0.7, agility=0.6, cost=1.05,
        stars=("k", "m"),
        worlds=("dense", "rock", "industrial"),
        syllables=("voth", "grum", "dhal", "kron", "bael", "murg"),
        suffixes=("un", "oth", "ak", "eim"),
        ship_words=("Weight", "Deadfall", "Pillar", "Ingot", "Keel"),
        temperament="insular",
        factions=(
            _f("Voth Gravitum", "navy", 0.45, 1.5, 2.6),
            _f("Voth Underclans", "fringe", 0.40, 1.3, 2.1),
        ),
    ),
    Race(
        key="ceph",
        name="Ceph Tidal",
        blurb="Ocean worlds, pressure hulls, and very long memories.",
        hull=1.2, shields=1.1, speed=0.9, agility=1.1, cost=1.1,
        stars=("g", "k"),
        worlds=("ocean", "ice", "cloud"),
        syllables=("ceph", "thal", "nerei", "abys", "mor", "pel"),
        suffixes=("ys", "ara", "oon", "eth"),
        ship_words=("Tide", "Fathom", "Nautilus", "Current", "Trench"),
        temperament="insular",
        factions=(
            _f("Ceph Tidal", "navy", 0.30, 1.15, 2.3),
            _f("Ceph Shoalkeepers", "trade", 0.08),
        ),
    ),
    Race(
        key="karn",
        name="Karn Free Companies",
        blurb="Mercenaries who will fight anyone, including each other.",
        hull=1.1, shields=1.0, speed=1.1, agility=1.15, cost=0.85,
        stars=("g", "k", "m"),
        worlds=("rock", "desert", "industrial", "fortress"),
        syllables=("karn", "vex", "hald", "rusk", "brann", "tegg"),
        suffixes=("ar", "isk", "on", "ord"),
        ship_words=("Contract", "Retainer", "Blade", "Wager", "Fee"),
        temperament="predatory",
        factions=(
            _f("Karn Free Companies", "navy", 0.55, 1.4, 2.0),
            _f("Karn Broken Lances", "fringe", 0.65, 1.35, 1.8),
        ),
    ),
    Race(
        key="silane",
        name="Silane Exchange",
        blurb="They do not own territory. They own the routes through it.",
        hull=0.95, shields=1.05, speed=1.15, agility=1.0, cost=1.15,
        stars=("f", "g"),
        worlds=("earthlike", "ocean", "cloud", "industrial"),
        syllables=("sil", "ane", "corr", "veyd", "mari", "tess"),
        suffixes=("a", "eu", "ine", "oss"),
        ship_words=("Broker", "Consul", "Envoy", "Margin", "Bourse"),
        temperament="mercantile",
        factions=(
            _f("Silane Exchange", "corporate", 0.10),
            _f("Silane Factors", "trade", 0.05),
        ),
    ),
    Race(
        key="aurei",
        name="Aurei Remnant",
        blurb="What is left of something that was very large, very long ago.",
        hull=1.4, shields=1.6, speed=1.2, agility=1.0, cost=2.2,
        stars=("b", "a", "neutron"),
        worlds=("relic", "crystal", "machine", "shard"),
        syllables=("aur", "ei", "solm", "vast", "ohm", "sere"),
        suffixes=("um", "is", "ael", "orr"),
        ship_words=("Vestige", "Aeon", "Codex", "Reliquary", "Testament"),
        temperament="ancient",
        factions=(
            _f("Aurei Remnant", "navy", 0.50, 1.6, 3.0),
            _f("Aurei Caretakers", "zealot", 0.35, 1.3, 2.6),
        ),
    ),
    Race(
        key="bracken",
        name="Bracken Verge",
        blurb="Plant-minds that measure war in growing seasons.",
        hull=1.15, shields=1.2, speed=0.85, agility=0.9, cost=1.0,
        stars=("g", "k", "m"),
        worlds=("forest", "fungal", "earthlike", "swamp"),
        syllables=("brack", "thorn", "vine", "sedge", "moss", "burr"),
        suffixes=("en", "wold", "hollow", "thicket"),
        ship_words=("Bramble", "Creeper", "Thicket", "Rootfast", "Bloom"),
        temperament="insular",
        factions=(
            _f("Bracken Verge", "navy", 0.30, 1.1, 2.3),
            _f("Bracken Coppice", "trade", 0.10),
        ),
    ),
    Race(
        key="nyx",
        name="Nyx Umbral",
        blurb="Void dwellers. You generally see them once.",
        hull=0.8, shields=1.15, speed=1.3, agility=1.3, cost=1.45,
        stars=("brown", "neutron", "m"),
        worlds=("void", "ice", "derelict", "shard"),
        syllables=("nyx", "umbr", "sela", "quiet", "vane", "hush"),
        suffixes=("al", "eth", "ir", "oon"),
        ship_words=("Whisper", "Pall", "Nocturne", "Veil", "Absence"),
        temperament="predatory",
        factions=(
            _f("Nyx Umbral", "navy", 0.60, 1.3, 2.4),
            _f("Nyx Quietus", "zealot", 0.70, 1.4, 2.2),
        ),
    ),
    Race(
        key="choir",
        name="Hollow Choir",
        blurb="A faith with a fleet, and a plan for everyone else's worlds.",
        hull=1.2, shields=1.3, speed=0.95, agility=0.95, cost=1.2,
        stars=("a", "f", "g"),
        worlds=("cathedral", "rock", "desert", "relic"),
        syllables=("hol", "psalm", "kyri", "vesp", "matin", "cant"),
        suffixes=("um", "ine", "oss", "iel"),
        ship_words=("Antiphon", "Censer", "Litany", "Reliquary", "Chant"),
        temperament="expansionist",
        factions=(
            _f("Hollow Choir", "zealot", 0.65, 1.35, 2.5),
            _f("Choir Mendicants", "trade", 0.20),
            _f("Choir Apostate", "fringe", 0.45, 1.2, 2.0),
        ),
    ),
]


def all_factions() -> List[Tuple[Race, Faction]]:
    return [(race, faction) for race in RACES for faction in race.factions]


def attitude(a_race: Race, a: Faction, b_race: Race, b: Faction) -> float:
    """How faction A regards faction B, on upstream's scale where < 0 is hostile.

    Built from temperament rather than hand-written, so forty-odd governments
    stay internally consistent: a predatory race dislikes everyone a little, an
    insular one dislikes outsiders specifically, and a trade faction gets on with
    almost anybody because that is its whole business.
    """
    if a is b:
        return 0.0

    same_race = a_race.key == b_race.key
    if same_race:
        # Inside a race, the fringe resents the navy and the navy returns it.
        if {a.role, b.role} == {"navy", "fringe"}:
            return -0.6
        if {a.role, b.role} == {"zealot", "fringe"}:
            return -0.8
        return 0.5

    # Traders are nearly everyone's friend; that is the point of them.
    if a.role in ("trade", "corporate") and b.role in ("trade", "corporate"):
        return 0.3

    base = {
        "insular": -0.3,
        "expansionist": -0.5,
        "mercantile": 0.1,
        "ancient": -0.2,
        "predatory": -0.7,
    }[a_race.temperament]

    # A faction's own aggression pushes it further either way.
    return round(base - (a.aggression - 0.35), 3)
