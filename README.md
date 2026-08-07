# CS:WARFRONT

A faction-warfare mod for **Cities: Skylines** (2015). Turn your city into a battlefield: found rival military factions, build bases and fortifications, raise land / sea / air forces fed by your city's economy, and fight over territory — complete with supply lines, freight rail logistics, helicopters and external invasion events.

**Steam Workshop:** https://steamcommunity.com/sharedfiles/filedetails/?id=3774864056

## Features

- **Factions & bases** — designate any building asset as an Army / Naval / Air / Missile base per faction. Bases produce units, project territory, and can be captured.
- **Combat units** — infantry, mechanized infantry, APCs, tanks, artillery, AA, drones, destroyers, carriers, fighters, bombers, attack & transport helicopters, in five tech tiers.
- **City-driven economy** — residential zones near bases provide Manpower, commercial/office provide Funds, industry provides Production. Units cost Manpower + Production (Funds can substitute).
- **Supply system** — units consume ammo in combat and must be resupplied via base zones, supply trucks, transport helicopters, supply depots and freight trains.
- **Fortifications** — bunkers, artillery positions, supply depots, and faction-neutral trenches with a two-click line placement tool.
- **Military rail** — cargo stations snapped to your train tracks form routes; articulated freight trains haul supplies and ground units along the actual track geometry.
- **Invasion events** — hostile raider waves spawn at the map edge and march on the city's bases; defenders sortie to intercept.
- **Player command layer** — click / box selection, movement and rally orders, hold orders, missile strikes, and a one-stop Military Construction panel.

## Requirements

- Cities: Skylines (the 2015 original, Steam app 255710)
- The mod targets **.NET Framework 3.5 / C# 7.3** (the game runs on Unity 5.6)

## Building

```powershell
./build.ps1
```

`build.ps1` locates MSBuild via `vswhere`, compiles `src/CSWarfront/CSWarfront.sln`, and deploys the DLL plus `Models/` and `Sounds/` to `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\CSWarfront`. Game DLL references are resolved from the standard Steam install path (see the `.csproj`).

Tests (pure-logic core, runs on .NET 8):

```powershell
dotnet test tests/CSWarfront.Core.Tests/CSWarfront.Core.Tests.csproj
```

## Project layout

| Path | Contents |
|---|---|
| `src/CSWarfront/Core/` | Simulation logic: deterministic, engine-independent, fully unit-tested. No UnityEngine or CS API references. |
| `src/CSWarfront/Game/` | Cities: Skylines integration: threading, building/net readers, unit visuals, UI panels, audio. |
| `tests/CSWarfront.Core.Tests/` | xUnit tests for the core (~870 tests). |
| `tools/` | Blender export pipeline for the built-in unit/building models (`models.blend` → OBJ/FBX). |
| `docs/` | Design documents and TODO. |

### Design principles

- **Determinism** — the core never uses `System.Random`; randomness is derived from stable hashes so simulation results are reproducible.
- **Thread discipline** — simulation state is touched only on the game's simulation thread; Unity objects only on the main thread; one lock, snapshot-then-render.
- **Testability** — anything that can be pure logic lives in `Core/` behind small interfaces (height/water samplers, road graphs) and is covered by tests.

## Contributing & localization

Contributions are welcome. All source code and inline documentation are in English.

The mod's UI text is fully localizable. Every player-facing string lives in
[`Locales/en.txt`](Locales/en.txt) (auto-generated from the defaults in
`src/CSWarfront/Game/Localization/WarfrontStrings*.cs`). To add a language:

1. Copy `Locales/en.txt` to `Locales/<code>.txt`, where `<code>` is the language code the
   game reports (e.g. `de`, `fr`, `es`, `zh`, `ja`). The game's current language is picked
   up automatically at startup.
2. Translate the values (right-hand side only). Keep the `{0}`/`{1}` placeholders and the
   `\n` line-break escapes. Any key you leave out simply falls back to English, so partial
   translations are fine.
3. Test locally by dropping the file into the mod folder's `Locales/` directory, then open
   a pull request adding it here.

## License

[MIT](LICENSE). Sound effects and 3D models bundled under `src/CSWarfront/Sounds` and `src/CSWarfront/Models` are part of the project.
