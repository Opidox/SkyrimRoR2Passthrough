# Skyrim × Risk of Rain 2 — Passthrough

Play the **Risk of Rain 2 survivors inside Skyrim**.

Risk of Rain 2 runs in the background and does the actual playing: your survivor, its skills, items,
enemies, drones and effects. Skyrim shows it all in its own world: the survivor's model and animations,
first-person hands, projectiles, lasers, allies, a RoR2-style HUD, and Skyrim's terrain copied back into
RoR2 so both games agree on where the ground and the walls are.

> **Status:** playable but experimental (version 0.74.0). Expect bugs and the occasional crash.
> In-game texts follow Skyrim's language (English, French, German, Italian, Spanish, Polish, Russian, Japanese,
> Chinese). The source code comments are in French.

## How it works

The mod is made of two plugins that talk to each other through Windows shared memory:

| Part | Folder | Language | Loaded by |
|---|---|---|---|
| Risk of Rain 2 plugin | `SkyrimRoR2/` | C# (netstandard2.1) | BepInEx (r2modman profile) |
| Skyrim plugin | `skse/` | C++ (CommonLibSSE-NG) | SKSE |
| Shared bridge layout | `Shared/` | C# + C++ header | both (must stay in sync) |
| Bridge debug viewer | `BridgeViewer/` | C# console | standalone |
| Helper scripts | `tools/` | Python / PowerShell | build time |

Main bridges: player input and camera (Skyrim → RoR2), survivor and first-person model streaming,
sprites for particles/trails/lasers, allies and projectiles as converted NIF models with animated bones,
HUD data, terrain and solid objects. RoR2 models and textures are converted to NIF/DDS on the fly;
newly converted models become visible in Skyrim from the next launch.

## Requirements

- Skyrim Special Edition / Anniversary Edition with **SKSE** (Mod Organizer 2 recommended)
- Risk of Rain 2 and **r2modman** with **BepInEx**. The DLCs are optional: without them, their survivors and items
  are simply not offered. Only survivors unlocked in your RoR2 profile can be played in Skyrim.
- Skyrim **1.6.1170** or **1.7.104** (both tested).
- Both games running at the same time on the same PC
- On a fresh install, launch Skyrim **once without mods** first (it creates its settings files), then install SKSE
  and the mod.

## Installation (players)

1. **Risk of Rain 2**: in r2modman, use a profile named exactly **`Passthrough`** — Skyrim starts RoR2 with
   this profile. The easiest way is to import the ready-made profile (BepInEx and the mods it needs):
   *Profiles* > *Import / Update* > *Import new profile* > *From code*, enter this code and name the profile `Passthrough`:
   ```
   01a13fd7-86a5-fe65-01d1-5b27a2b82b96
   ```
   Then add the RoR2 part of the mod (*Settings* > *Import local mod* > the RoR2 zip from the release).
   Using another profile name? Put its folder in `SkyrimRoR2.ini`, `[Lancement] ProfilR2modman = ...`.
2. **Skyrim**: launch it once without mods, install SKSE, then install the Skyrim zip from the release with
   Mod Organizer 2 and enable `SkyrimRoR2.esp`.
3. Launch Skyrim through SKSE: it starts RoR2 by itself in the background.

## Building

### Risk of Rain 2 plugin (C#)

1. Install the .NET SDK.
2. If Risk of Rain 2 is not in the default Steam folder, or your r2modman profile is not called
   `Passthrough`, copy `LocalPaths.props.example` to `LocalPaths.props` and put your own folders in it
   (this file is ignored by git). Then build:
   ```
   dotnet build SkyrimRoR2 -c Release
   ```
3. The DLL is copied automatically into `BepInEx\plugins\SkyrimRoR2` of that profile.

### Skyrim plugin (C++)

1. Install Visual Studio with *Desktop development with C++* and *C++ CMake tools for Windows*, plus Git.
2. Run `build-skyrim-plugin.bat`. The first time it downloads CommonLibSSE-NG and vcpkg (15–30 minutes).
3. The result is `dist\SkyrimRoR2-Skyrim.zip` (DLL, `SkyrimRoR2.ini`, `SkyrimRoR2.esp`), ready to install
   with your mod manager. If the mod is already installed in MO2, it is updated in place and your
   `.ini` settings are kept.

## Languages

The mod reads Skyrim's language (`sLanguage`) and picks `SKSE\Plugins\SkyrimRoR2_Lang\<language>.txt`.
RoR2 names (items, survivors, skills, skins) are taken from RoR2 in the same language when RoR2 has it.
To force a language, set `[Langue] Langue = english` (or `french`, `german`...) in `SkyrimRoR2.ini`.

Translations are generated from `tools/lang_data*.py` with `python tools/make_lang.py`, which also checks that
every text used in the code has a line and that each translation keeps the same `{}` placeholders.
Corrections are welcome — most non-French/English lines were machine-assisted and never checked by a native speaker.

## Settings

- Skyrim side: `SKSE\Plugins\SkyrimRoR2.ini` (commented, reloaded when you load a save). Set
  `[Test]` to 1 to unlock every survivor, variant and skin for testing.
- RoR2 side: the BepInEx config, editable from r2modman (sections *Monde*, *Effets*, etc.).

## Known issues

- Occasional Skyrim crashes during fights (under investigation — a Crash Logger SSE log helps).
- Artificer's ice wall is drawn too dark; some survivor effects (Acolyte, Chef) are still missing.
- Opening the Steam overlay while the game is paused can freeze input.
- Co-op is untested.

## Authors

- **Opidox** — idea, design, testing
- **Claude** (Anthropic) — code

## License

MIT — see [LICENSE](LICENSE). Copy it, change it, reuse it in your own mods: just keep the authors notice.

Risk of Rain 2 belongs to Hopoo Games / Gearbox, Skyrim to Bethesda. This is an unofficial fan mod; no
game assets are included in this repository — models and textures are converted on your own PC from
your own copy of the game.
