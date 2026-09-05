# Milestone tracker

Ground truth for what is done, in flight, and untouched, against the master
directive (`starthere.txt`). Per the directive's non-negotiable rule: nothing
here is quietly dropped — incomplete systems stay listed as incomplete.

The tables below record the August milestone review. See
[the September repository audit](repository-audit.md) for the current fixes,
validation results and remaining work; milestone labels are not proof that every
player-facing path or saved state is complete.

| Milestone | Status | Evidence |
|---|---|---|
| **M1 Flight** | **Done through gauntlet round 1** | Sim port verified exact by the gameplay critic (epoch math, quantized angles, coasting rule); visual critic's six corrections landed (key light, framing, bloom, silhouette, plume, HUD) plus the retrograde-brake input translation with hand-derived tests. Evidence: `reports/m1_flight_v3.png` — note that `reports/` is gitignored, so the gauntlet captures live only on the machine that took them and are not evidence anyone else can check. |
| M2 Combat | **Sim complete; views wired; gauntlet run** | Weapons/damage/projectiles/governments/firing/collision/targeting-AI in `libs/EndlessSky.Sim` (shields-block-entirely, 0.25 hull epsilon, valueless flags pinned by tests); `CombatEffects`/`ProjectileView`/`ExplosionView`/`ShieldImpactView` + the `--combat-demo` hostile drone driven by `ShipAi`. Combat gauntlet round (bolt/flash captures) pending. |
| M3 Travel | **Sim complete; view wired** | `Ship.Travel.cs` ports IsReadyToJump/DoHyperspaceLogic (hyperdrive path) with hand-derived tests (exact 100-frame phases, fuel drain, 4-jump tank); FlightWorld: J-key best-aligned-link targeting, brake-and-face autopilot, arrival advances the date and rebuilds the system. Full protocol: docs/upstream-reference.md §jump. |
| M4 Landing economy | **Playable; the opening debt is not built** | Commodity/TradeData/CargoHold/Outfitting, `Trading` (buy/sell ships and outfits with upstream's `Depreciation`), a moving economy (`StepEconomy`), and a landed screen with trade, shipyard, outfitter and job counters. |
| M5 Missions | **Done** | Parsing, conditions, conversations (inline and top-level), events (416, incl. universe patching), NPC entities (1,186 across 587 missions), the full accept/carry/complete/fail lifecycle with deadlines, and text substitution so jobs read as prose rather than templates. |
| M6 Fleet gameplay | **Done in the sim; boarding is unreachable from the cockpit** | Multiple owned ships, escorts, fleet commands (escort/gather/hold/attack on upstream's `MoveTo` + `StoppingPoint`), salaries, cargo distribution, boarding, capturing, parking and flagship selection. |
| M7 Content compatibility | **Ahead of schedule** | The loader already ingests the FULL upstream dataset (902 ships / 920 outfits / 694 systems, zero parse diagnostics) — M7's "progressively larger portions" started at 100% for parsing; behavior coverage still tracks the other milestones. `GameData.UnhandledNodes` counts what the model doesn't yet understand. |
| M8 Visual production | Done | Hulls generated per ship from ShipAppearance; faction plating from fleets/shipyards. See `docs/art-direction.md`. |
| M9 Full gauntlet | **Running — see below** | Scenario suite across all nine dimensions. Four combat-breaking defects found and fixed in the first pass; the directive's own wording ("continue correcting discrepancies") makes this milestone open-ended by design. See `docs/m9-gauntlet.md`. |

## Directive audit

Every item named in `starthere.txt` was checked against the codebase rather than
against memory, using `GameData.UnhandledNodes` — the loader's own tally of content
it skips — to find gaps objectively. What that turned up:

| Named in the directive | Was | Now |
|---|---|---|
| M5 "events" | Not parsed at all | `GameEvent`, 416 loaded, 294 patch the universe |
| M5 "NPC mission entities" | Not parsed at all | `MissionNpc`, 1,186 across 587 missions |
| M2 "factions" | `Government` implemented, never fed | 126 governments loaded; ships built flying their flag |
| M5 "conversations" | Inline only | Top-level too; all 38 named references resolve |
| M4 "ship purchasing" | No transaction path | `Trading` + `Depreciation` |
| M6 "fleet commands" | Escorts listed, inert | `FleetOrders` on upstream's `MoveTo`/`StoppingPoint` |
| Philosophy "energy", "heat" | Manoeuvring was free | Costed and throttled; generation added |
| Philosophy "jump drives" | Not implemented | Range from the drive, fuel charged by destination |
| M3 travel (wormholes) | Not parsed | 18 wormholes, cycle-linked, traversed by landing |
| M5 "mission completion" | No lifecycle | Accept, carry, complete, fail, deadlines |
| M4 "ship purchasing" | Unreachable in game | Shipyard and outfitter counters on the landed screen |
| M2 "NPC ships" | Systems were empty | Fleets spawn and fly; 599 systems declare traffic |
| Rendering "asteroid fields" | Not parsed | 71,984 rocks across 669 systems, instanced |
| M7 "do not hard-code content" | Start was 4 constants | Loaded from `starts.txt`, conditions included |
| Progression (save/load) | None | Round-trips through the data format, AND reachable: Save game in the pause menu, Continue on the main menu |

Still unparsed, and listed rather than dropped: `phrase` (867, procedural naming),
`effect` (309), `news` (219), `color`/`swizzle`/`interface`/`tip`/`help`
(presentation), `person` (16), `hazard` (30), `formation` (14) and `galaxy` (26).
None is named in a milestone checklist. The ones that were — wormholes, minables
and asteroids, governments, events, conversations, starts — are now in.

## Known gaps inside M1 (deliberate, tracked, not deleted)

- No afterburner handling; no Stop-autopilot retrograde turn — the
  `Command.Stop` cheat is ported but unbound by default. Energy and heat costs on
  thrust and turn are now in, with generation to match.
- Player starts in space beside New Boston; upstream starts landed with a
  launch. Landing/launch is M4 surface.
- Stellar objects reposition on date change, as upstream does, and the date now
  actually advances: a jump costs a day on the player's own calendar, which is
  what drives deadlines, salaries, depreciation and mission expiry.
- Key bindings are hardcoded polls (W/A/S/D + arrows); Godot InputMap
  remapping is UI-milestone work.

## Gauntlet pass: combat and mission NPCs (2026-08-28)

The universe generator had produced a thousand jobs, and 429 of them placed NPCs
that nothing ever built. Chasing that down found the larger problem underneath.

**Found by running the game, not by testing it.** All 657 sim tests passed
throughout; none of these were visible from inside the simulation layer.

| Defect | Effect on the player | Fix |
|---|---|---|
| The projectile field was created only behind `--combat-demo` | A normal game had **no combat at all** — nothing could shoot or be shot | `BuildCombat` runs in every flight; `StepCombat` steps the field each frame |
| `BuildMounts()` was never called on the player's ship | The player's hull carried **no hardpoints at all**, so it could never fire or be armed at an outfitter | Built in `BuildCombat`, and again for any hull bought at a shipyard |
| The player had no government | Hostility is reputation-driven for the player; with no flag, raiders ignored a defenceless freighter | A `Player` government, marked `IsPlayer`, plus `"player reputation"` on every generated faction |
| `npc` blocks were parsed but never instantiated | Every bounty, escort and salvage job could be accepted and never finished | `NpcSpawner` + `NpcInstance`, built at accept time as upstream does |
| Nothing reported combat back to the mission log | A bounty target could be destroyed and the job stayed open | `MissionLog.ReportShipEvent`, wired from `HitReport` |
| `ship "Model" 3` in a generated npc block | The count was read as the ship's NAME, placing one hull where three were meant | One `ship` line per hull |
| `system destination` read as a system literally named "destination" | Bounties were placed where the job was taken, not where its text pointed | `IsAtDestination` on the template — but see the 2026-08-28 audit below: the resolution half was not wired until later, so the symptom survived this fix |
| Bounty targets drawn from any faction of an enemy race | Peoples with no raiders (the Orokh field only a navy and a corporation) produced bounties on friendly ships that never fought back | Targets are picked faction-first, from `fringe`/`zealot` only |
| Objectives were satisfied in aggregate | A bounty on three raiders paid out on the first kill | Per-ship events, as upstream: every hull must meet the objective |
| Escorts could not travel | An `accompany` objective failed the moment the player jumped | `MissionLog.CarryAccompanying`, called on arrival |
| Weapons aimed at where a target *was* | Two evenly matched hulls traded shots forever and never landed one | `ShipAi.RendezvousTime`/`AimPoint`, ported from upstream `AI::RendezvousTime` |
| `AI::MoveToAttack`'s second thrust clause was missing | Ships that passed each other coasted apart forever, full energy, out of range, closing on nothing | Thrust also when velocity carries the ship away from its target |
| Generated engines were far weaker than upstream's | Median turn **0.30°/frame against upstream's 2.68**; 83 of 100 hulls turned slower than 1°/frame, so nothing could come about in a dogfight | Engine output rescaled; the fleet now measures 2.69°/frame and 0.097px/f² against upstream's 2.68 and 0.098 |

Verified in a real engine process, not only under test: `pwsh tools/run.ps1
-Headless -Frames 20000 -UserArgs '--mission-smoke'` takes a bounty, flies to
where the job points, fights it, and reports whether the objective was met.
Before this pass the fight never resolved; it now ends in 500–2800 frames, and
the player — in the cheapest hull the starting world sells — wins some of them.

### Still incomplete here (tracked, not deleted)

- **Nothing boards a disabled hull.** Upstream stops attacking a crippled ship
  and boards it, to capture or to strip it. `CaptureOdds` exists in the sim and
  is not wired to anything, so a crippled ship is neither captured nor finished,
  and a fight between two ships that both end up disabled simply stops. The
  smoke run names this explicitly when it happens rather than reporting a stall.
- **Mission NPCs never despawn**, and the template's `to spawn` condition gate is
  parsed but not consulted.
- **NPC ships do not jump under their own power.** Escorts are carried with the
  flagship, which is the same observable outcome for an `accompany` objective but
  is not the upstream mechanism (escort-personality AI flying its own jump).
- **Boarding, capture and plunder are unreachable from the cockpit**, so a
  `board` objective — every salvage job — can only be satisfied programmatically.

## The gauntlet (per directive)

IMPLEMENT → BUILD → RUN → TEST → CAPTURE → CRITIQUE → FIX → REPEAT, with
independent critic contexts; the builder never grades its own output.

- BUILD/TEST: `pwsh tools/build.ps1` · `pwsh tools/test.ps1` (suites: `sim`,
  `godot`).
- RUN: `pwsh tools/run.ps1 [-Headless]`.
- CAPTURE: `<godot_console> --path . "--" --capture=reports/<name>.png
  --capture-frames=N --autopilot` (quote the `--`: PowerShell eats a bare one).
- CRITIQUE: one visual critic (screenshot vs. the directive's readability/
  composition/lighting bar) and one gameplay critic (implementation vs.
  upstream source; `docs/upstream-reference.md` is the distilled ground
  truth) in fresh contexts; their top findings become the next FIX pass.

## Full repository audit (2026-08-28)

Seventeen agents over eight dimensions, every finding sent to an adversarial
verifier told to refute it: 72 raised, 5 refuted, 67 confirmed. **All 67 are
now resolved** — 64 fixed test-first across 29 commits, 3 recorded in the source
as deliberate divergences rather than changed. The report is at
<https://claude.ai/code/artifact/65b6e714-bb73-4d7d-b33f-b2ed92301e3e>.

**The pattern was worth more than any single finding.** Correct, tested code in
`libs/` was repeatedly wired to nothing. `SaveGame`, `MissionLog.Step`,
`CaptureOdds`, `ConversationRunner`, `TradeData.StepEconomy`,
`Government.Offend`, `GameEvent.Apply`, `TextSubstitution.DescriptionOf`,
`Minable` and `StartScenario.MortgagePrincipal` all had zero callers outside
tests. The suite was green because it tests the library, and the library had run
ahead of the game. That is why a tracker can read "Done" over a subsystem the
player cannot reach — and why the statuses above now distinguish the two.

### Fixed in this pass

| Was | Now |
|---|---|
| CI red for three pushes: the `sim` job never fetched the dataset, so 14 tests threw and 80 skipped | Data fetched in the job that needs it, with a fail-fast check so a green tick cannot mean "the parity suites skipped" |
| Ammunition lived in a private ledger no production code wrote to, so every launcher and torpedo tube in the dataset was inert | Ammunition IS outfits, as upstream reads it; firing removes the round and its mass |
| `BuildShip` never built hardpoints, so every hull it made carried its guns as inventory | Hardpoints first, as in `Ship::FinishLoading` |
| Disabled ships repaired hull, regenerated shields and made power | Generation is gated on `!isDisabled`, as upstream gates it |
| Heat accumulated and did nothing | Overheating disables a ship, with upstream's 0.9 hysteresis and opt-in hull burn |
| Mission progress used invented condition keys, so all 1,966 `": done"` gates in the dataset read 0 forever | Upstream's six counters, keyed on the mission's true name |
| An inline `not` in a LocationFilter built an empty exclusion, so the whole filter matched nothing | Both shapes of `not` read, as upstream reads them |
| No day ever passed: jumping advanced a render-side counter, not the player's date | One calendar; `MissionLog.Step` runs on it |
| No death: a destroyed flagship hid its mesh and the game carried on | Losing is a state the game can be in |
| No save and no load in any menu | Save game in the pause menu, Continue on the main menu, verified by `--save-smoke` |
| Bounties spawned where the job was taken, and any job could be handed in anywhere | An accepted mission fixes its destination once and uses it for both |
| Buying and selling were asymmetric: every sale paid the 25% floor | Purchase dates recorded, so a same-day resale is break-even |
| An outfit could always be sold, even one holding the rest of the loadout | Uninstall is gated on `CanAdd(outfit, -1)` |
| Landing repaired nothing and refuelled only the flagship | `PlayerState.TakeOff` services the fleet, skipping parked and crippled hulls |
| Reputation never moved | `Politics` ports upstream's propagation across every government |
| `universe/jobs.txt` could not be regenerated — salted `hash()` | CRC32; CI regenerates and diffs on every push |
| No mission ever spoke: the `on offer` trigger was never fired | 1,392 upstream missions show their dialogue, and the answer decides |
| 416 events were parsed and nothing fired one | A dated queue on the player, fired by the day tick and saved with the game |
| 260 `fail` clauses did nothing | A bare `fail` ends the mission; `fail "<name>"` ends another |
| Turrets fired along the hull, like fixed guns | Each traverses at its own rate, aimed by the AI at the lead point |
| A jump drive flew the hyperdrive's protocol | Its own: no facing gate, a random bearing close in, no deceleration run |
| A hold order accelerated the escort away | Thrust waits until the ship is pointed retrograde, as upstream's does |
| Cluster rounds landed as one shot | The parent's impulse is redirected along each child's heading, plus its own spread |
| Systems appended on redefinition | Upstream's replace-on-first-occurrence, with `add` and `remove` |
| Plugins could not override a definition | The root `overwrite` node resets the next one |
| The landing rule lived in the view layer with invented constants | `Ship.CanLandOn`, upstream's speed and the object's own radius |
| `random` was always 0, so `random < N` was always true | Registered, in upstream's [0, 100), with `roll:` alongside |
| Conversation options ignored their `to display` gates | Hidden options stay hidden |
| A browned-out ship coasted forever | `NeedsEnergy` widens the drag-only branch, as upstream's does |

### Still incomplete, and now listed where a reader will find it

The source is the inventory: `grep -rn "INCOMPLETE" libs/ src/` returns 40-odd
entries, each naming what is missing and the upstream function it belongs to.
The ones a player would notice first:

- **Nothing boards a disabled hull.** `CaptureOdds` exists and is not wired, so
  a crippled ship is neither captured nor finished and a fight between two
  ships that both end up disabled simply stops. Every salvage job's `board`
  objective is unreachable from the cockpit.
- **The opening conversation still does not grant a ship.** Missions show their
  offer dialogue now, but `StartScenario.MortgagePrincipal` is parsed and never
  applied: the player starts with 480,000 credits and no debt where upstream
  gives both, and picks a starting hull by other means because upstream's
  opening conversation is what sells it to them on credit.
- **The Reach defines no events, conversations or wormholes at all**, so those
  three subsystems have no content to act on in the shipped game whatever the
  engine supports. They are exercised against upstream's dataset, by tests.
- **Mission NPCs never despawn**, and the template's `to spawn` gate is parsed
  and not consulted.
- **NPC ships do not jump under their own power.** Escorts are carried with the
  flagship, which is the same observable outcome for an `accompany` objective
  but is not upstream's mechanism.
- **Turret firing ARCS are not modelled**: every turret traverses, but as though
  it were omnidirectional, so one mounted behind a hull can still bear forward.
- **Planet::CanLand's licence, government-access and "requires" gates** are not
  modelled, so any world with a landing site accepts anyone.
- **No audio at all**, and no input remapping — every control is a hard-coded
  key poll rather than an `InputMap` action.
