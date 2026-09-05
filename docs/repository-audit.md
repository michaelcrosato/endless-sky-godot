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
| Build isolation | Exclude generated build, distribution and report directories from C# compilation. | A nested validation checkout caused duplicate-source and missing-NUnit build failures. The same build now succeeds with the checkout still present. |
| Tutorial recovery | Return to finding work after losing a job or departing without one; refresh changed destinations and guide players back after leaving the delivery system. | All seven new recovery cases failed before the fix and now pass; the real tutorial still completes delivery and payment. |
| Mission combat | Keep disabled mission ships stepping so drift and heat continue. Require the bounty smoke to take an offered job, win through normal combat, land and collect payment. | The disabled-ship probe failed before the controller fix. A stock Prism X defeated the bounty target and collected 91,297 credits. The smoke positions travel legs; the separate tutorial smoke flies its route. |
| Combat scope and arrivals | Clear transient ships, shots and combat views on arrival and load without resetting the flagship's resources or armament. All pilots target the combat field's ships, with other systems excluded. Update the pilot's system and exploration history on real jumps. | Two target-selection regressions and runtime save, bounty-arrival and tutorial-arrival probes failed before the fixes. Reload clears a firing traffic ship and its view; arrival clears the previous fight; tutorial checks the pilot's location and visited system. |
| Mission persistence | Save actual NPC ships, their allegiance, condition, locations and individual objective events, plus mission passengers. Restore missions after pilot placement; recover missing targets in older saves without repeating acceptance. | All ten new cases failed before the fix. They cover partial bounties, escorts that jumped, mission failure, payload, legacy placement, empty instances, aggregate-only records and repeated saves. The real bounty now reloads during combat and after victory, then lands and collects payment. |
| Weapon duplication and cleanup | Let mount reconstruction arm existing outfits without installing additional copies. Share ship serialization and smoke diagnostics; remove the unused NPC placement flag and the load overload that attached missions to a different player. | The stronger bounty smoke caught a cannon count doubling from 2 to 4 on load. It now passes both reloads with the stock loadout; existing ship and save regressions still pass. |
| Combat visibility | Scale camera distance and zoom to the hull and viewport, and bound velocity look-ahead by the visible frame. Show shields, hull, energy, heat and disabled/overheated warnings in the flight HUD; advertise fire and zoom controls. | All three new engine regressions failed before the fix: large-hull framing, fast flight in every direction, and zoom after a flagship change. They now pass, including portrait framing. Rendered battles at 1920×1080 and 1280×720 show both ships, projectiles and readable telemetry. |
| Capture dimensions | Honor explicit engine window mode and resolution options ahead of saved preferences, and include image dimensions in capture logs. | A requested 1280×720 capture previously became 1920×1080 when settings loaded. The same command now produces a verified 1280×720 image without changing the saved preferences. |
| Port menus and input | Make save/load available through Esc both in flight and at a port. Share key sampling between the shell, port and mission offers so held keys and simultaneous transitions cannot trigger actions on an underlying screen. | Seven engine tests cover saving/loading, held aliases, shop isolation, offer acceptance/decline and simultaneous inputs. The real save smoke saves at port, loads there and from flight, and verifies the restored port uses the restored player. |
| Flagship departure | Remove the remaining duplicate weapon installation path when changing flagship at a port. | The save smoke selects a stock Kestrel I with spare gun mounts and requires its full outfit inventory to remain unchanged after departure. |
| Port readability | Keep the tutorial below the port footer and hide flight controls while landed. Shell menus draw above the port and hide tutorial hints; combined landed/menu captures open the menu after landing. | Rendered 1280×720 port and pause captures show readable credits, controls, Save game and Load game rows. |
| CI action runtime | Pin checkout, .NET setup, cache and artifact actions to reviewed commits that declare Node 24. Give the workflow read-only repository access and avoid persisting checkout credentials. | Earlier CI reported three Node 20 actions running under a forced Node 24 override. Release notes and runtime metadata were checked at each pinned commit; the workflow passes `actionlint`. |
| Commodity transactions | Move commodity buying, selling and market availability into `Trading`. Reject unavailable quotes and unaffordable purchases before moving cargo; charge only for actual quantities and keep arithmetic within the credit balance's range. | Both new engine cases failed before the fix: a zero-price purchase added five free tons, and a large debt wrapped into one affordable ton. They now pass. Twenty-four simulation cases cover partial fills, current prices, unavailable markets, quantities, cargo mass and 64-bit payments. |
| Cargo at port | Restrict commodity transactions and port cargo totals to active ships in the current system. Place purchased ships in that system immediately. | Buying and selling leave remote, parked and destroyed ships' cargo untouched; local escorts share the load. A newly bought ship can carry a purchase before its first departure. The rendered 1280×720 port remains readable. |
| Daily markets | Apply deferred sales, normally distributed production changes and exchanges along current system links. Match upstream's export snapshot and division by each source system's link count; buying does not cancel the sales queue. | Hand-calculated graph, sale/buyback, changed-link and random-stream cases failed before the fix and now pass. Reversing system declaration order preserves the result. |
| Market persistence | Save supply, displayed prices and pending trades; restore them when a valid pilot is accepted. Older saves reset to base prices. Share exact integer parsing for credits, conditions and pending quantities. | Sixteen new economy cases include fresh data, repeated reloads, upstream headers, invalid numbers and exact 64-bit queues. A price-boundary regression caught and fixed a one-credit change from supply rounding. The engine save scenario restores changed markets and rejects an invalid save without mutating the active game. |
| Economy fixture isolation | Give full-dataset economy and save/load tests their own universes. Exercise linked markets in both datasets and round-trip the upstream pilot into fresh data. | New isolation assertions failed when the 100-day walk changed shared quotes (251 to 283 upstream, 188 to 193 in The Reach). Both now pass and check all 4,800 upstream and 3,830 generated quotes, including unchanged shared supply. |
| Native export tools | Share Godot template lookup across hosts, honor Linux's XDG data directory, use native curl and temporary paths, validate downloaded template versions and remove owned download staging directories. | Linux PowerShell reproduced a null-path failure before the fix. CI then caught multiple curl candidates being invoked as one command; a regression reproduced it before selecting the first. Windows/Linux cases cover archive installation, rejected versions, download failure and cleanup, plus missing templates, release/debug arguments and failed/missing/empty exports. |
| Linux release and relocation | Add `smoke-package.ps1` and make the Linux release export and relocated save/bounty scenarios gate CI. | Windows and Linux release packages passed from temporary copies outside the repository with an unrelated working directory and no data override. The smoke requires the loaded dataset to be inside that copy. Linux runtime validation passed in WSL and native Ubuntu CI; graphical Linux rendering is not yet verified. |
| Fleet servicing | Limit port repairs and refuelling to ships in the current system; remote ships receive only their own regeneration. Ignore takeoff requests without a landed pilot, system and flagship. | Eight new cases failed before the fix. They now cover remote ships with no generator or each of four generator types, plus invalid takeoff states. The existing local-escort, parked and disabled checks remain in place. |
| Mission freight | Give each accepted job its own freight identity and require the whole load to fit in local holds. Freight uses space and mass but cannot be sold or substituted for another job. Completion, abort, expiry and failure release only that job's load; missing or destroyed carriers fail the job at the next mission check. | Twelve initial cases failed before separation, and four carrier-loss cases failed before lifecycle integration. Twenty-one simulation cases now cover those paths, remote freight, zero-ton parcels, legacy partial loads and repeated per-ship reloads. Two engine cases exercise acceptance and selling at the actual port counters. |

Latest local validation: **884 simulation tests, 29 engine tests, zero failures or
skips**, and Debug/Release builds with zero warnings or errors. All five runtime
smokes passed their documented contract, including winning and collecting a bounty.
The Windows and Linux release packages also passed save/load, both bounty reloads
and payment from relocated copies with no data override.

Older saves never recorded NPC ships or their history, so they recreate those
targets once at load; missing historical kills cannot be recovered. New saves
retain each NPC's mission template index and actual ships. Mission definitions are
still resolved by name; migration across changed definitions and full upstream
mission-save compatibility remain incomplete.

New saves retain mission UUIDs, cargo type, required tonnage and each ship's actual
freight, including zero-ton parcels. A reload cannot refill missing freight from
the player's own commodities. Older port saves mixed those goods together; they
reserve only existing cargo, in saved mission and ship order. The original owner
of overlapping loads cannot be recovered from that old format. A legacy partial
load does not qualify for full delivery payment.

Economy saves retain current displayed quotes even when supply's eight-digit
serialization crosses a price boundary. They do not retain the random generator's
state; future random production is not a replay of an abandoned session.

CI action pins correspond to [checkout v7.0.1](https://github.com/actions/checkout/releases/tag/v7.0.1),
[setup-dotnet v6.0.0](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0),
[cache v6.1.0](https://github.com/actions/cache/releases/tag/v6.1.0), and
[upload-artifact v7.0.1](https://github.com/actions/upload-artifact/releases/tag/v7.0.1).
Review the release notes and update both the commit and its version comment when
updating an action.

Run validation from the repository root:

```powershell
pwsh tools/get-data.ps1
pwsh tools/build.ps1
pwsh tools/test.ps1
pwsh tools/smoke.ps1 -NoBuild
pwsh tests/tools/PackageTests.ps1
pwsh tests/tools/ExportTests.ps1
pwsh tools/package.ps1
pwsh tools/smoke-package.ps1
python tools/worldgen/worldgen.py --out build/universe-check
```

The generator check must compare file lists and bytes against `universe/`; successful
generation alone is not proof of reproducibility. `tools/smoke.ps1 -Scenario land
-Frames 1 -NoBuild` is a failure probe, not a passing smoke command.
Use `-Preset Linux` for Linux packaging and package smokes; run the resulting
executable on Linux. `tools/smoke-package.ps1 -Frames 1` is also a failure probe.

## Remaining work

The audit is not complete merely because these checks pass. The following areas
still contain meaningful work and need further implementation and verification:

- **Combat and boarding:** capture odds exist, but boarding approach, crew combat,
  plunder, capture transfer and their UI still need implementation. Continue the
  broader combat parity review (targeting personalities, turrets and other weapons).
- **Persistence:** changed universe data, reputation, transient jumps, and per-ship
  weapon mount assignments still need round-trip coverage. Inspect escort
  reconstruction as well.
- **Gameplay rules:** mission NPC spawn/despawn gates, landing permissions, the
  opening debt/conversation flow and turret firing arcs remain incomplete.
- **Economy:** commodity cost basis, individual port services and per-ship landing
  clearance remain incomplete. Landed cargo pooling and redistribution when selling
  a loaded ship are also missing; removing its hold currently loses its freight.
  Applied changes to market definitions share the wider universe-persistence gap above.
- **Simulation boundary:** much of the session's orchestration still lives in the
  presentation layer. Move rules into the engine-free layer with behavioral coverage.
  Commodity transactions now use that layer; preserve the actual player-facing flow.
- **Coverage:** strengthen remaining assertions that only prove a call does not
  throw, and reduce the reviewed backlog of unhandled upstream node types.
- **Delivery:** review dependency/CI reproducibility and verify graphical Linux
  rendering. Fresh checkout validation, Windows/Linux release packaging and
  relocated headless runtime scenarios are verified above.
- **UX and content:** add target identification and target condition information;
  inspect windows below 1280×720, input remapping, audio, and Reach content for
  currently unused event/conversation/wormhole systems.

`docs/MILESTONES.md` and `rg -n INCOMPLETE libs src` retain the broader parity
inventory. Do not delete unfinished systems to make the audit appear complete.
