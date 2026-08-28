# gd-cc-t — Godot engine test bench

A Godot 4.7.2 (.NET) project used to exercise engine features. GDScript and C#
both run here, and both are unit-tested headlessly.

## Commands

All scripts are PowerShell 7 and resolve the engine themselves — no need to set
anything first.

| Task | Command |
|---|---|
| Build (import + compile C#) | `pwsh tools/build.ps1` |
| Full clean rebuild | `pwsh tools/build.ps1 -Clean` |
| Run all tests | `pwsh tools/test.ps1` |
| GDScript tests only | `pwsh tools/test.ps1 -Suite gd` |
| C# tests only | `pwsh tools/test.ps1 -Suite cs` |
| One GDScript suite | `pwsh tools/test.ps1 -Suite gd -Path tests/gd/health_test.gd` |
| Filter C# tests | `pwsh tools/test.ps1 -Suite cs -Filter "FullyQualifiedName~Inventory"` |
| Run the game | `pwsh tools/run.ps1` |
| Headless smoke test | `pwsh tools/run.ps1 -Headless` |
| Open the editor | `pwsh tools/editor.ps1` |
| Export a build | `pwsh tools/export.ps1 -Preset "Windows Desktop" -Release` |
| Install export templates | `pwsh tools/install-export-templates.ps1` |

`tools/test.ps1` exits non-zero if any suite fails, so it is safe as a gate.

## Engine binaries

Godot is installed **machine-wide** via winget, under
`C:\Program Files\WinGet\Packages\`, with aliases in
`C:\Program Files\WinGet\Links` (on the system PATH). Nothing engine-related
lives in this repo.

Two builds are installed, and **their winget aliases collide** -- `godot.exe`
points at whichever package was installed or upgraded last. Use these shims
(in `~/.local/bin`) instead:

- `godot4` -- standard build, GDScript only
- `godot4-mono` -- **.NET build; this project requires it** (it has C# support)

`tools/_env.ps1` resolves the .NET binary by globbing the winget package roots,
machine scope before user scope, so a version bump or a reinstall needs no
edits. Override with `$env:GODOT_BIN` (must be a `*_console.exe`; the plain
`.exe` detaches from the terminal and swallows all stdout).

To reinstall or add a build, keep it machine-scope:

```powershell
winget install --id GodotEngine.GodotEngine.Mono --exact --scope machine
```

`--scope machine` needs an elevated shell. Without it winget installs to
`%LOCALAPPDATA%\Microsoft\WinGet\Packages` instead, whose aliases take PATH
precedence over the Program Files ones and will silently shadow them.

Export templates are per-user by design and stay at
`%APPDATA%\Godot\export_templates\` -- that is the only path Godot searches.

## Layout

```
scripts/  scenes/     GDScript + scenes
src/                  C# sources
tests/gd/             GDScript tests (gdUnit4 6.2.1, addons/gdUnit4)
tests/cs/             C# tests (gdUnit4Net, run by dotnet test)
tools/                build / test / run / export scripts
reports/  build/      generated; each holds a .gdignore (see below)
```

## Testing model

Keep game logic in **engine-independent classes** and put a thin Godot-facing
wrapper around it. `Inventory` (plain C# class) versus `InventoryNode`
(`RefCounted`) is the reference pattern.

This is not a style preference. Constructing any `GodotObject` subclass outside
a running engine dereferences uninitialised native interop and dies with
`AccessViolationException`. Tests that must touch Godot types need
`[RequireGodotRuntime]`, which makes gdUnit4 spawn a real engine process for
them — far slower than the plain .NET host used for everything else.

GDScript tests extend `GdUnitTestSuite`; C# suites are `[TestSuite]` classes
with `[TestCase]` methods.

## Gotchas

- **`reports/.gdignore` and `build/.gdignore` must stay.** gdUnit4 writes HTML
  reports containing PNGs into `res://reports/`. Without `.gdignore`, Godot
  imports them on the next boot, and the extra cold-start time makes the C#
  `[RequireGodotRuntime]` runner miss its connect timeout — so `-Suite gd`
  followed by `-Suite cs` fails intermittently. `.gdignore` stops the importer
  from scanning the directory; writes still work.
- **`GdCcT.sln` is required and must be classic `.sln`.** Godot's .NET export
  plugin looks for it by name; without it the export silently ships no assembly
  and the game crashes on first C# call. .NET 10's `dotnet new sln` defaults to
  `.slnx`, which Godot does not recognise — pass `--format sln`.
- **Godot exports build under `ExportDebug` / `ExportRelease`,** not `Release`.
  Test packages in `GdCcT.csproj` are gated to `Configuration == Debug` so the
  adapter, gdUnit4 API and Roslyn stay out of shipped builds.
- **gdUnit4's GDScript CLI needs `--ignoreHeadlessMode`.** Headless Godot never
  delivers `InputEvent`s, so gdUnit4 refuses to start without it. Fine here — no
  suite drives simulated input. Add `--remote-debug tcp://127.0.0.1:0` too: the
  never-bound port is refused instantly, which stops a parse error from dropping
  Godot into its interactive `debug>` prompt and hanging CI.
- **GDScript cannot name a C# class at parse time** unless the assembly is
  already built — it is a hard parse error that kills the whole script. Load it
  dynamically and check `can_instantiate()` first (see `scripts/main.gd`).
- **Release exports have no console wrapper** (`debug/export_console_wrapper=1`
  is debug-only), so redirect stdout to inspect their output.
- `gdunit4_testadapter_v5/` is regenerated by the C# adapter on every
  `dotnet test`. It is gitignored; do not edit or commit it.

## Versions

Godot 4.7.2-stable (.NET) · .NET SDK 10.0.400, targeting `net8.0` ·
gdUnit4 6.2.1 · gdUnit4.api 5.1.0-rc5 · gdUnit4.test.adapter 3.1.1 ·
Microsoft.NET.Test.Sdk 18.9.0.

Those last three are interlocked — the adapter resolves that exact API version
and TestPlatform 18.x. Bump them together or not at all.
