# Endless Sky 3D — Godot

A reimplementation of [Endless Sky](https://github.com/endless-sky/endless-sky)
on **Godot 4.7.2 (.NET)**: the original game's data, rules and feel, rendered as
a polished low-poly 3D space game instead of 2D sprites.

This is not an Endless Sky-inspired game. Upstream is the behavioural reference,
and the goal is to consume its content files directly rather than re-authoring
them. `starthere.txt` is the master directive; `docs/` holds the upstream facts
that the simulation is checked against.

## Requirements

- Godot 4.7.2 **.NET build** — `winget install --id GodotEngine.GodotEngine.Mono --exact --scope machine`
  (elevated shell, so it installs to `C:\Program Files` rather than per-user AppData)
- .NET SDK 8.0 or newer (`net8.0` is the target framework)
- PowerShell 7 (`pwsh`)

## Quick start

```powershell
pwsh tools/get-data.ps1       # fetch the upstream reference into external/
pwsh tools/build.ps1          # import assets, compile C#
pwsh tools/test.ps1           # run every suite
pwsh tools/run.ps1            # fly
```

## Architecture

The directive's hard rule is that rendering must not be mixed into gameplay.
That is enforced by the compiler rather than by convention:

| Layer | Project | Sees Godot? |
|---|---|---|
| Data — Endless Sky file parser | `libs/EndlessSky.Data` | **no** |
| Simulation — physics, ships, systems | `libs/EndlessSky.Sim` | **no** |
| Presentation — views, camera, world | `src/game` (`EndlessSky.csproj`) | yes |

Neither library references `GodotSharp`, so a stray `using Godot` in the
simulation is a build error, and `tests/sim/ArchitectureTests.cs` fails if
someone re-opens that door.

The payoff is test speed: the data and simulation suites are plain NUnit on the
bare .NET host and finish in under a second, with no engine to boot.

```
libs/EndlessSky.Data    DataFile, DataNode, DataWriter
libs/EndlessSky.Sim     Point, Angle, Ship, StarSystem, GameData
src/game/               WorldSpace, FlightWorld, ShipView, CameraRig, Starfield
scenes/flight.tscn      main scene
tests/sim/              NUnit, engine-free
tests/godot/            gdUnit4, needs a real engine process
external/endless-sky/   upstream reference (gitignored; see tools/get-data.ps1)
```

## Testing

```powershell
pwsh tools/test.ps1                                              # everything
pwsh tools/test.ps1 -Suite sim                                   # engine-free only
pwsh tools/test.ps1 -Suite sim -Filter "FullyQualifiedName~ShipPhysics"
pwsh tools/test.ps1 -Suite godot                                 # in-engine only
```

## Status

Milestone 1 (flight) is in progress: the data parser reads real upstream content,
the simulation reproduces Endless Sky's ship physics, and one star system renders
in 3D with a controllable ship. See `docs/` for what is verified against upstream
and what is still outstanding.

## Contributing

`CLAUDE.md` documents the toolchain and the non-obvious traps — the `.gdignore`
files, the required classic `.sln`, Godot's export configurations, and the
engine-binary setup. Read it before changing build or test plumbing.

## Licence

GPL-3.0, matching upstream Endless Sky. See `LICENSE`.
