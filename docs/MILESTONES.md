# Milestone tracker

Ground truth for what is done, in flight, and untouched, against the master
directive (`starthere.txt`). Per the directive's non-negotiable rule: nothing
here is quietly dropped — incomplete systems stay listed as incomplete.

| Milestone | Status | Evidence |
|---|---|---|
| **M1 Flight** | **Done through gauntlet round 1** | Sim port verified exact by the gameplay critic (epoch math, quantized angles, coasting rule); visual critic's six corrections landed (key light, framing, bloom, silhouette, plume, HUD) plus the retrograde-brake input translation with hand-derived tests. Evidence: `reports/m1_flight_v3.png`. |
| M2 Combat | **Sim complete; views wired; gauntlet pending** | Weapons/damage/projectiles/governments/firing/collision/targeting-AI in `libs/EndlessSky.Sim` (shields-block-entirely, 0.25 hull epsilon, valueless flags pinned by tests); `CombatEffects`/`ProjectileView`/`ExplosionView`/`ShieldImpactView` + the `--combat-demo` hostile drone driven by `ShipAi`. Combat gauntlet round (bolt/flash captures) pending. |
| M3 Travel | **Sim green; view wired, verification pending** | `Ship.Travel.cs` ports IsReadyToJump/DoHyperspaceLogic (hyperdrive path) with hand-derived tests (exact 100-frame phases, fuel drain, 4-jump tank); FlightWorld: J-key best-aligned-link targeting, brake-and-face autopilot, arrival advances the date and rebuilds the system. Full protocol: docs/upstream-reference.md §jump. |
| M4 Landing economy | **Economy sim underway** | Commodity/TradeData/CargoHold/Outfitting in libs (peer lane); landing flow and shop UI not started. |
| M5 Missions | **Condition parsing in flight** (peer lane) | — |
| M6 Fleet gameplay | Not started | — |
| M7 Content compatibility | **Ahead of schedule** | The loader already ingests the FULL upstream dataset (902 ships / 920 outfits / 694 systems, zero parse diagnostics) — M7's "progressively larger portions" started at 100% for parsing; behavior coverage still tracks the other milestones. `GameData.UnhandledNodes` counts what the model doesn't yet understand. |
| M8 Visual production | Done | Hulls generated per ship from ShipAppearance; faction plating from fleets/shipyards. See `docs/art-direction.md`. |
| M9 Full gauntlet | Running | Scenario suite across all nine dimensions; four combat-breaking defects found and fixed. See `docs/m9-gauntlet.md`. |

## Known gaps inside M1 (deliberate, tracked, not deleted)

- Flight only: no energy/heat costs on thrust or turn yet (upstream throttles
  by available energy via `FractionalUsage`); no afterburner handling; no
  Stop-autopilot retrograde turn — the `Command.Stop` cheat is ported but
  unbound by default.
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
