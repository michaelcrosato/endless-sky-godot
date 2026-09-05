# Repository audit — September 2026

This audit is active. The earlier report in local `reports/endless-sky-repo-audit.html`
was used to locate candidates; findings are checked against current code and runtime
behavior before changing them. `reports/` is ignored and its logs are local evidence,
not artifacts available from a clone. CI runs the reproducible checks below.

## Completed and verified

| Area | Change | Evidence |
|---|---|---|
| Reproducibility | Pin upstream to `tools/upstream-ref.txt`; share fetch/test commands between local runs and CI. Resolve Godot only for commands that use it. | Fresh sparse fetch, repeat-fetch check, full sim suite with an invalid `GODOT_BIN`; commit `dc05648` passed both GitHub CI jobs. |
| Navigation and onboarding | Review and retain the existing landing selector/autopilot, jump braking, radar, planet labels, tutorial and mission hand-in changes. Generated starts avoid hostile patrols and drives declare jump speed. | Jump/landing/tutorial regressions; real landing and tutorial smoke; generator output matches all 11 committed files byte for byte. |
| Save state | Preserve each ship's name, crew, cargo, position, velocity, facing, system, shields, hull, energy, fuel, heat and overheating state. Read older saves with default values and fleet cargo. | Four new regressions cover distinct active/parked ships, overheating hysteresis, old saves and outfit-dependent capacity. |
| Save files | Write and flush a temporary sibling before replacing a save; remove temporary files after failure. Save smoke uses its own temporary slot. | Two Godot tests check replacement and failure cleanup; runtime save smoke checks serialized state before and after restoring damaged resources. |
| Load integration | Stop combat rebuilding from refilling the flagship's battery; free the previous effects node when rebuilding. | The stronger save smoke initially failed on `energy 5 -> energy 620`, then passed after the fix. |
| Landed saves | Reopen the saved port against the restored player and clear obsolete navigation commands. | The runtime save check failed before the fix; now it verifies both flight and landed round trips and departure after loading. |
| Integer persistence | Read credit balances and condition counters without converting through floating point. | Regressions cover positive/negative values above 2^53, both signed 64-bit limits, and legacy exponent notation. |
| Engine test lifecycle | Build planet labels in `_Ready`, exercise them in the scene tree, and defer disposal. Remove the invalid remote-debug port workaround. | Godot tests run without text-server errors or resource leaks; a malformed script exits 1 without an interactive prompt. |
| Quality gates | Run startup, landing, tutorial delivery, save/load and combat-resolution smokes in CI. Require completion markers, successful exit and no engine errors. | An unfinished landing run fails even with engine exit 0; missing startup data fails with exit 1. |
| Cleanup | Share planet-to-system lookup and braking logic; remove redundant helpers and the unused save-path property. Enable existing nullable annotations in test sources. | Full regression suite and build. |
| Presentation | Keep the bottom control legend inside the viewport; allow the tutorial's final confirmation to display and dismissal to hide it immediately. Give dismissal F3 so it does not also open F2 graphics options. | Real rendered flight capture and control binding review. |
| Packaging | Build in a fresh staging folder, replace the output only on success, guard replacement paths, and share preset lookup. Fail when the export artifact is missing or empty. | The old script retained an obsolete-data sentinel. The new release has exactly the 11 source data files; a copy outside the repository loaded its own dataset and passed flight/landed save smoke. Engine-free script regressions cover replacement, fallback data, export failure, preset selection, traversal and links. |
| Required fixtures | Missing upstream/generated data and missing named parity content fail instead of skipping. Generated data stays within its checkout; explicit upstream overrides must be valid. Replace the always-passing coverage report with reviewed limits on unhandled content. | A fresh worktree previously reported success with all eight upstream tests skipped. It now fails all eight when data is missing; removing its generated systems file also fails instead of borrowing the parent checkout's data. |
| Fresh checkout | Fetch the pinned reference and import/build without using the original worktree's engine cache or data. | An isolated detached worktree passed 795 simulation tests, 15 engine tests, packaging regressions and all five smoke contracts; Debug build had zero warnings/errors. |

Latest local validation: **795 simulation tests, 15 engine tests, zero failures or
skips**, and Debug/Release builds with zero warnings or errors. All five runtime
smokes passed their documented contract. The combat smoke ended in player defeat;
the limitation below still applies.

Run validation from the repository root:

```powershell
pwsh tools/get-data.ps1
pwsh tools/build.ps1
pwsh tools/test.ps1
pwsh tools/smoke.ps1 -NoBuild
pwsh tests/tools/PackageTests.ps1
python tools/worldgen/worldgen.py --out build/universe-check
```

The generator check must compare file lists and bytes against `universe/`; successful
generation alone is not proof of reproducibility. `tools/smoke.ps1 -Scenario land
-Frames 1 -NoBuild` is a failure probe, not a passing smoke command.

## Remaining work

The audit is not complete merely because these checks pass. The following areas
still contain meaningful work and need further implementation and verification:

- **Combat and boarding:** `--mission-smoke` currently treats player defeat as a
  resolved fight. It verifies combat machinery, not a won bounty or its payment.
  Add a winning combat-to-hand-in scenario and wire boarding/capture into play.
- **Persistence:** mission NPC state/history, changed universe data, reputation,
  transient jumps, and per-ship weapon mount assignments still need round-trip
  coverage. Inspect escort reconstruction and access to save/load while landed as well.
- **Gameplay rules:** mission NPC spawn/despawn gates, landing permissions, the
  opening debt/conversation flow and turret firing arcs remain incomplete.
- **Simulation boundary:** commodity transactions and much of the session's
  orchestration still live in the presentation layer. Move rules into the engine-free
  layer with behavioral coverage. Preserve the actual player-facing flow.
- **Coverage:** strengthen remaining assertions that only prove a call does not
  throw, and reduce the reviewed backlog of unhandled upstream node types.
- **Delivery:** verify a Linux release export and review dependency/CI
  reproducibility. Fresh checkout validation, Windows release packaging and
  relocated startup are verified above.
- **UX and content:** inspect controls and tutorial recovery when a job is abandoned,
  UI layout at smaller windows, input remapping, audio, and representative Reach
  content for currently unused event/conversation/wormhole systems.

`docs/MILESTONES.md` and `rg -n INCOMPLETE libs src` retain the broader parity
inventory. Do not delete unfinished systems to make the audit appear complete.
