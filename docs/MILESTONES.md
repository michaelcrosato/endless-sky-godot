# Milestone tracker

Ground truth for what is done, in flight, and untouched, against the master
directive (`starthere.txt`). Per the directive's non-negotiable rule: nothing
here is quietly dropped — incomplete systems stay listed as incomplete.

| Milestone | Status | Evidence |
|---|---|---|
| **M1 Flight** | **Working; in gauntlet** | 81/81 tests (`tools/test.ps1`); headless boot prints the `[flight]` line with derived Shuttle constants matching upstream (mass 192, vmax 13.375 px/f, turn 2.875°/f); autopilot capture `reports/m1_flight.png` shows banked powered flight near New Boston. First visual + gameplay critic pass running; corrections land before M1 is called done. |
| M2 Combat | **Sim core + effect views built; firing/collision in flight** | Weapons/damage/projectiles/governments in `libs/EndlessSky.Sim` (shields-block-entirely, 0.25 hull epsilon, valueless flags all pinned by tests); `CombatEffects`/`ProjectileView`/`ExplosionView`/`ShieldImpactView` in `src/game`; hardpoint firing loop + collision under construction. |
| M3 Travel | Not started | Hyperspace constants already extracted (docs/upstream-reference.md §Hyperspace). |
| M4 Landing economy | Not started | — |
| M5 Missions | Not started | — |
| M6 Fleet gameplay | Not started | — |
| M7 Content compatibility | **Ahead of schedule** | The loader already ingests the FULL upstream dataset (902 ships / 920 outfits / 694 systems, zero parse diagnostics) — M7's "progressively larger portions" started at 100% for parsing; behavior coverage still tracks the other milestones. `GameData.UnhandledNodes` counts what the model doesn't yet understand. |
| M8 Visual production | Not started | M1 uses procedural prototype assets by design. |
| M9 Full gauntlet | Not started | Per-milestone gauntlet loops run now; the full comparative gauntlet needs M2–M6. |

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
