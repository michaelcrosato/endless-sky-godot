# gd-cc-t

A Godot **4.7.2 (.NET)** test bench. GDScript and C# live side by side, and both
are unit-tested headlessly — so engine behaviour can be probed from a terminal
or from CI without opening the editor.

## Requirements

- Godot 4.7.2 **.NET build** — `winget install --id GodotEngine.GodotEngine.Mono --exact --scope machine`
  (elevated shell, so it installs to `C:\Program Files` rather than per-user AppData)
- .NET SDK 8.0 or newer (`net8.0` is the target framework)
- PowerShell 7 (`pwsh`)

Export templates are only needed for `tools/export.ps1`:

```powershell
pwsh tools/install-export-templates.ps1
```

## Quick start

```powershell
pwsh tools/build.ps1          # import assets, compile C#
pwsh tools/test.ps1           # run every suite
pwsh tools/run.ps1 -Headless  # boot the main scene, print a report, exit
pwsh tools/editor.ps1         # open the Godot editor
```

Expected test output: **8 GDScript cases + 10 C# cases, all passing.**

## What's in here

| Path | Purpose |
|---|---|
| `scripts/health.gd` | `Health` — bounded hit-point pool (GDScript) |
| `src/Inventory.cs` | `Inventory` — stackable item bag, plain C#, no engine dependency |
| `src/InventoryNode.cs` | `RefCounted` wrapper exposing `Inventory` to the scene tree |
| `scenes/main.tscn` | Entry scene; doubles as a GDScript↔C# interop smoke test |
| `tests/gd/` | gdUnit4 suites (GDScript) |
| `tests/cs/` | gdUnit4Net suites (C#), run by `dotnet test` |
| `tools/` | Build, test, run, editor and export scripts |
| `.github/workflows/ci.yml` | Headless CI: both suites on every push, export on tag |

`Inventory` is a plain class and `InventoryNode` is the Godot-facing shim on top
of it. That split is the point of the layout: engine-free logic tests run on the
bare .NET host in milliseconds, and only the handful of tests that genuinely
need the scene tree pay for spawning an engine.

## Testing

```powershell
pwsh tools/test.ps1                    # everything
pwsh tools/test.ps1 -Suite gd          # GDScript only
pwsh tools/test.ps1 -Suite cs          # C# only
pwsh tools/test.ps1 -Suite cs -Filter "FullyQualifiedName~Inventory"
```

HTML and JUnit XML reports land in `reports/report_N/`.

## Exporting

```powershell
pwsh tools/export.ps1                                        # Windows, debug
pwsh tools/export.ps1 -Preset "Windows Desktop" -Release     # Windows, release
pwsh tools/export.ps1 -Preset Linux -Release
```

Builds are written to `build/`. Release exports exclude the test tooling.

## Notes for contributors

`CLAUDE.md` documents the engine-binary setup and the non-obvious traps — the
`.gdignore` files, the required `.sln`, Godot's export configurations, and why
Godot types cannot be constructed outside a running engine. Read it before
changing the build or test plumbing.
