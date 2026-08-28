# Milestone tracker

Ground truth for what is done, in flight, and untouched, against the master
directive (`starthere.txt`). Per the directive's non-negotiable rule: nothing
here is quietly dropped — incomplete systems stay listed as incomplete.

| Milestone | Status | Evidence |
|---|---|---|
| **M1 Flight** | **Done through gauntlet round 1** | Sim port verified exact by the gameplay critic (epoch math, quantized angles, coasting rule); visual critic's six corrections landed (key light, framing, bloom, silhouette, plume, HUD) plus the retrograde-brake input translation with hand-derived tests. Evidence: `reports/m1_flight_v3.png`. |
| M2 Combat | **Sim complete; views wired; gauntlet run** | Weapons/damage/projectiles/governments/firing/collision/targeting-AI in `libs/EndlessSky.Sim` (shields-block-entirely, 0.25 hull epsilon, valueless flags pinned by tests); `CombatEffects`/`ProjectileView`/`ExplosionView`/`ShieldImpactView` + the `--combat-demo` hostile drone driven by `ShipAi`. Combat gauntlet round (bolt/flash captures) pending. |
| M3 Travel | **Sim complete; view wired** | `Ship.Travel.cs` ports IsReadyToJump/DoHyperspaceLogic (hyperdrive path) with hand-derived tests (exact 100-frame phases, fuel drain, 4-jump tank); FlightWorld: J-key best-aligned-link targeting, brake-and-face autopilot, arrival advances the date and rebuilds the system. Full protocol: docs/upstream-reference.md §jump. |
| M4 Landing economy | **Done** | Commodity/TradeData/CargoHold/Outfitting, `Trading` (buy/sell ships and outfits with upstream's `Depreciation`), a moving economy (`StepEconomy`), and a landed screen with trade, shipyard, outfitter and job counters. |
| M5 Missions | **Done** | Parsing, conditions, conversations (inline and top-level), events (416, incl. universe patching), NPC entities (1,186 across 587 missions), the full accept/carry/complete/fail lifecycle with deadlines, and text substitution so jobs read as prose rather than templates. |
| M6 Fleet gameplay | **Done** | Multiple owned ships, escorts, fleet commands (escort/gather/hold/attack on upstream's `MoveTo` + `StoppingPoint`), salaries, cargo distribution, boarding, capturing, parking and flagship selection. |
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
| Progression (save/load) | None | Whole game round-trips through the data format |

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
- Stellar objects are date-static (upstream repositions only on date change —
  faithful — but we never advance the date yet).
- Key bindings are hardcoded polls (W/A/S/D + arrows); Godot InputMap
  remapping is UI-milestone work.

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
