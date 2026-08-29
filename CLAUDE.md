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
| Open the editor | `pwsh tools/editor.ps1` |
| Export a build | `pwsh tools/export.ps1 -Preset "Windows Desktop" -Release` |
| Clickable build (exe + dataset) | `pwsh tools/package.ps1` |
| Install export templates | `pwsh tools/install-export-templates.ps1` |

## Layout

```
libs/EndlessSky.Data    parser: DataFile, DataNode, DataWriter   (no Godot)
libs/EndlessSky.Sim     Point, Angle, Ship, StarSystem, GameData (no Godot)
src/game/               Godot views, camera, world, starfield
scenes/flight.tscn      main scene (Milestone 1 flight slice)
tests/sim/              NUnit over the two libraries; runs with no engine
tests/godot/            gdUnit4 suites that need a real engine process
external/endless-sky/   upstream reference checkout (gitignored)
tools/                  build / test / run / editor / export scripts
```

Two test tiers, deliberately: `sim` is plain NUnit on the bare .NET host and
finishes in under a second, which is where behavioural parity gets pinned down.
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

Export templates are per-user by design and stay at
`%APPDATA%\Godot\export_templates\`.

## Gotchas

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
  `InputEvent`s, so gdUnit4 refuses to start without it. Add
  `--remote-debug tcp://127.0.0.1:0` too: the never-bound port is refused
  instantly, which stops a parse error from dropping Godot into its interactive
  `debug>` prompt and hanging CI.
- **GDScript cannot name a C# class at parse time** unless the assembly is built
  — it is a hard parse error that kills the whole script. Load it dynamically
  and check `can_instantiate()`.
- **An export is not a runnable game on its own.** The dataset is read from
  disk with `System.IO`, not through `res://`, so it can never come out of the
  `.pck` however the export is configured — an exported build with no data
  beside it boots to "Endless Sky data not found" and idles. `EsData` resolves
  `external/endless-sky/data` relative to `res://`, which in an export
  globalizes to the executable's own directory. `tools/package.ps1` does the
  export and the copy together; the output folder is movable, the exe alone is not.
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
