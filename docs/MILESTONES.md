# Milestone tracker

Ground truth for what is done, in flight, and untouched, against the master
directive (`starthere.txt`). Per the directive's non-negotiable rule: nothing
here is quietly dropped — incomplete systems stay listed as incomplete.

| Milestone | Status | Evidence |
|---|---|---|
| **M1 Flight** | **Done through gauntlet round 1** | Sim port verified exact by the gameplay critic (epoch math, quantized angles, coasting rule); visual critic's six corrections landed (key light, framing, bloom, silhouette, plume, HUD) plus the retrograde-brake input translation with hand-derived tests. Evidence: `reports/m1_flight_v3.png`. |
| M2 Combat | **Sim complete; views wired; gauntlet run** | Weapons/damage/projectiles/governments/firing/collision/targeting-AI in `libs/EndlessSky.Sim` (shields-block-entirely, 0.25 hull epsilon, valueless flags pinned by tests); `CombatEffects`/`ProjectileView`/`ExplosionView`/`ShieldImpactView` + the `--combat-demo` hostile drone driven by `ShipAi`. Combat gauntlet round (bolt/flash captures) pending. |
| M3 Travel | **Sim green; view wired, verification pending** | `Ship.Travel.cs` ports IsReadyToJump/DoHyperspaceLogic (hyperdrive path) with hand-derived tests (exact 100-frame phases, fuel drain, 4-jump tank); FlightWorld: J-key best-aligned-link targeting, brake-and-face autopilot, arrival advances the date and rebuilds the system. Full protocol: docs/upstream-reference.md §jump. |
| M4 Landing economy | **Sim complete; shop UI pending** | Commodity/TradeData/CargoHold/Outfitting, plus `Trading` (buy/sell ships and outfits, with upstream's `Depreciation`) and the landing flow. Shop UI not started. |
| M5 Missions | **Sim complete except live tracking** | Parsing, conditions, conversations (inline and top-level), events (416, incl. universe patching), availability, completion actions, and NPC entities (1,186 across 587 missions). Accepting and tracking a mission in a running game is not wired. |
| M6 Fleet gameplay | **Sim complete** | Multiple owned ships, escorts, fleet commands (escort/gather/hold/attack on upstream's `MoveTo` + `StoppingPoint`), salaries, cargo distribution, boarding, capturing, parking and flagship selection. |
| M7 Content compatibility | **Ahead of schedule** | The loader already ingests the FULL upstream dataset (902 ships / 920 outfits / 694 systems, zero parse diagnostics) — M7's "progressively larger portions" started at 100% for parsing; behavior coverage still tracks the other milestones. `GameData.UnhandledNodes` counts what the model doesn't yet understand. |
| M8 Visual production | Done | Hulls generated per ship from ShipAppearance; faction plating from fleets/shipyards. See `docs/art-direction.md`. |
| M9 Full gauntlet | Running | Scenario suite across all nine dimensions; four combat-breaking defects found and fixed. See `docs/m9-gauntlet.md`. |

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

Still unparsed, and deliberately so for now: `phrase` (867, procedural naming),
`effect` (309) and `color`/`swizzle`/`interface`/`tip`/`help` (presentation),
`news` (219), `person` (16), `wormhole` (18), `minable` (34) and `hazard` (30).
These are listed here rather than dropped; none is named in a milestone
checklist, though asteroid fields appear under Rendering and wormholes bear on
travel.

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
