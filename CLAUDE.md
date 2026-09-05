# Endless Sky 3D (Godot)

Reimplementation of Endless Sky on **Godot 4.7.2 (.NET)**: original data and
simulation behaviour, low-poly 3D presentation. Read `starthere.txt` — the master
directive — before making design decisions.

A sibling effort targets Unity in `C:\dev\unity-cc-t`. **Do not edit it**; it is
owned by a different session. This repo is the Godot arm of that comparison.

## Ground rules from the directive

1. **Upstream is the source of truth.** When unsure how something should behave,
   read `external/endless-sky/source` and reproduce the observable behaviour.
   Do not invent rules.
2. **Never delete a system because it is hard.** Mark it incomplete and keep going.
3. **Do not mix rendering with simulation.** Enforced by the compiler: the data
   and simulation layers are separate projects that never reference GodotSharp,
   and `tests/sim/ArchitectureTests.cs` fails if that is ever undone.
4. **The builder does not grade its own output.** Changes get built, run and
   reviewed against upstream before being called done.
5. **Gameplay stays planar** until parity is reached. Ships may bank and pitch
   visually, but those rotations never feed back into simulation values.

## Commands

| Task | Command |
|---|---|
| Build (import + compile) | `pwsh tools/build.ps1` |
| Full clean rebuild | `pwsh tools/build.ps1 -Clean` |
| All tests | `pwsh tools/test.ps1` |
| Simulation tests only | `pwsh tools/test.ps1 -Suite sim` |
| One simulation fixture | `pwsh tools/test.ps1 -Suite sim -Filter "FullyQualifiedName~ShipPhysics"` |
| Presentation tests | `pwsh tools/test.ps1 -Suite godot` |
| Run the game | `pwsh tools/run.ps1` |
| Headless smoke | `pwsh tools/run.ps1 -Headless` |
| Mission/combat smoke | `pwsh tools/run.ps1 -Headless -Frames 20000 -UserArgs '--mission-smoke'` |
| Save round-trip smoke | `pwsh tools/run.ps1 -Headless -Frames 400 -UserArgs '--save-smoke'` |
| Landing smoke (select + fly + land) | `pwsh tools/run.ps1 -Headless -Frames 20000 -UserArgs '--land-smoke'` |
| Tutorial smoke (land → job → jump → deliver) | `pwsh tools/run.ps1 -Headless -Frames 20000 -UserArgs '--tutorial-smoke'` |
| Regenerate the universe | `python tools/worldgen/worldgen.py` |
| Open the editor | `pwsh tools/editor.ps1` |
| Export a build | `pwsh tools/export.ps1 -Preset "Windows Desktop" -Release` |
| Clickable build (exe + dataset) | `pwsh tools/package.ps1` |
| Relocated package smoke | `pwsh tools/smoke-package.ps1` (add `-Preset Linux` on Linux) |
| Install export templates | `pwsh tools/install-export-templates.ps1` |

## Layout

```
libs/EndlessSky.Data    parser: DataFile, DataNode, DataWriter   (no Godot)
libs/EndlessSky.Sim     Point, Angle, Ship, StarSystem, GameData (no Godot)
src/game/               Godot views, camera, world, starfield
scenes/flight.tscn      main scene
tests/sim/              NUnit over the two libraries; runs with no engine
tests/godot/            gdUnit4 suites that need a real engine process
tools/worldgen/         Python generator for The Reach; the SOURCE of universe/
universe/               its output, committed: the galaxy the game actually plays
external/endless-sky/   upstream reference checkout (gitignored)
tools/                  build / test / run / editor / export scripts
```

Two test tiers, deliberately: `sim` is plain NUnit on the bare .NET host and
also loads both content datasets, which is where behavioural parity gets pinned down.
`godot` spawns an engine and is reserved for things that genuinely need one.

`EndlessSky.csproj` is the Godot project. It references the two libraries as
projects, and `Compile Remove`s `libs/**`, `external/**` and `tests/sim/**` —
Godot's SDK globs every `.cs` under the project root, so without those removals
the libraries compile twice and NUnit attributes leak into the engine assembly.

## Engine binaries

Godot is installed **machine-wide** under `C:\Program Files\WinGet\Packages\`,
with aliases in `C:\Program Files\WinGet\Links` (system PATH). Nothing
engine-related lives in this repo.

Two builds are installed and **their winget aliases collide** — `godot.exe`
points at whichever package was installed or upgraded last. Use the shims in
`~/.local/bin`:

- `godot4` — standard build, GDScript only
- `godot4-mono` — **.NET build; this project requires it**

`tools/_env.ps1` resolves the .NET binary by globbing the winget package roots,
machine scope before user scope, so a version bump needs no edits. Override with
`$env:GODOT_BIN` (must be a `*_console.exe`; the plain `.exe` detaches from the
terminal and swallows stdout).

Reinstall machine-scope, from an elevated shell:

```powershell
winget install --id GodotEngine.GodotEngine.Mono --exact --scope machine
```

Without `--scope machine` winget installs to `%LOCALAPPDATA%`, whose aliases
take PATH precedence and silently shadow the Program Files ones.

Export templates are per-user by design: `%APPDATA%\Godot\export_templates\` on
Windows, and `$XDG_DATA_HOME/godot/export_templates/` (default
`~/.local/share/godot/export_templates/`) on Linux. The installer and exporter share
this lookup. Linux scripts require `GODOT_BIN` to name the .NET editor executable.

## Which dataset the game loads

**The game plays `universe/`, not upstream.** `EsData` resolves, in order:
`$ENDLESS_SKY_DATA`, then `universe/` (The Reach — this project's own generated
galaxy, 1000 systems, committed), then `external/endless-sky/data`, then
`../es-upstream/data`. The upstream clone stays on disk because the parity suites
read it, and `tools/package.ps1` ships `universe/` beside the exe.

So: **the game plays our content and the tests check theirs.** That split is
deliberate, and it is worth holding in mind when a change looks correct under
test and wrong in the game — the two are reading different files. Point
`ENDLESS_SKY_DATA` at `external/endless-sky/data` to play upstream's galaxy.

`universe/` is generated, not hand-written: `python tools/worldgen/worldgen.py`
reproduces it byte for byte from its seed, and CI regenerates and diffs it, so
edit the generator rather than the output.

## Gotchas

- **`.gitignore`'s `bin/` matches ANY directory of that name**, including the one
  gdUnit4 vendors its command-line runner in. `!/addons/gdUnit4/bin/` un-excludes
  it; without that a fresh clone cannot run the in-engine suite at all and dies
  on "Can't load script: res://addons/gdUnit4/bin/GdUnitCmdTool.gd".
- **`reports/.gdignore` and `build/.gdignore` must stay.** gdUnit4 writes HTML
  reports containing PNGs into `res://reports/`. Without `.gdignore`, Godot
  imports them on the next boot, and the extra cold-start time makes the
  in-engine test runner miss its connect timeout — so the suites fail
  intermittently depending on run order. `.gdignore` stops the importer from
  scanning a directory; writes still work. `libs/` carries one for the same
  reason: those `.cs` files are compiled by MSBuild, not loaded as scripts.
- **`EndlessSky.sln` is required and must be classic `.sln`.** Godot's .NET
  export plugin looks for it by name; without it the export silently ships no
  assembly and the game crashes on the first C# call. .NET 10's `dotnet new sln`
  defaults to `.slnx`, which Godot does not recognise — pass `--format sln`.
- **Godot exports build under `ExportDebug` / `ExportRelease`,** not `Release`.
  Test packages in `EndlessSky.csproj` are gated to `Configuration == Debug` so
  the adapter, gdUnit4 API and Roslyn stay out of shipped builds.
- **gdUnit4's CLI needs `--ignoreHeadlessMode`.** Headless Godot delivers no
  `InputEvent`s, so gdUnit4 refuses to start without it. Omit the interactive
  debugger (`-d`) and use `--ignore-error-breaks` in automation. The former
  workaround, `--remote-debug tcp://127.0.0.1:0`, logged engine errors on every
  successful run. A malformed-script probe confirms parse errors exit nonzero
  without the debugger.
- **GDScript cannot name a C# class at parse time** unless the assembly is built
  — it is a hard parse error that kills the whole script. Load it dynamically
  and check `can_instantiate()`.
- **An export is not a runnable game on its own.** The dataset is read from
  disk with `System.IO`, not through `res://`, so it can never come out of the
  `.pck` however the export is configured — an exported build with no data
  beside it boots to "Endless Sky data not found" and idles. `EsData` resolves
  its candidates relative to `res://`, which in an export globalizes to the
  executable's own directory. `tools/package.ps1` does the export and the copy
  together; the output folder is movable, the exe alone is not.
- **Release exports have no console wrapper** (`debug/export_console_wrapper=1`
  is debug-only), so redirect stdout to inspect their output.
- `Godot.Environment` shadows `System.Environment` in any file with
  `using Godot;` — qualify it.
- `gdunit4_testadapter_v5/` is regenerated on every in-engine test run. It is
  gitignored; do not edit or commit it.

## Versions

Godot 4.7.2-stable (.NET) · .NET SDK 10.0.400, targeting `net8.0` ·
gdUnit4 6.2.1 · gdUnit4.api 5.1.0-rc5 · gdUnit4.test.adapter 3.1.1 ·
Microsoft.NET.Test.Sdk 18.9.0 · NUnit 3.14.0 (sim suite).

The three gdUnit4/TestPlatform packages are interlocked — the adapter resolves
that exact API version and TestPlatform 18.x. Bump them together or not at all.
The sim suite is on the NUnit 3.x line because the ported tests use the classic
`Assert` API that NUnit 4 moved to `ClassicAssert`.
