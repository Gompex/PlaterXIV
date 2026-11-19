<p align="center">
  <img src="docs/images/hero.png" alt="Plater for FF14 – enemy nameplates in combat" width="100%">
</p>

<h1 align="center">Plater for FF14</h1>

<p align="center">
  A Dalamud plugin that replaces FINAL FANTASY XIV's enemy nameplates with combat plates inspired by WoW's Plater and Threat Plates:<br>
  health, casts, your DoTs and threat, all on one plate.
</p>

<p align="center">
  <img alt="Dalamud API 15" src="https://img.shields.io/badge/Dalamud-API%2015-4FC9BC">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Licence MIT" src="https://img.shields.io/badge/licence-MIT-56636A">
  <img alt="Off in PvP" src="https://img.shields.io/badge/PvP-off-D55E00">
</p>

---

## What a plate shows

<p align="center">
  <img src="docs/images/anatomy.png" alt="Parts of one plate: DoTs, focus and level, name, target arrows, HP bar, percent, cast bar" width="100%">
</p>

- **HP bar** with exact percent. Signal splits it into 10% segments, like HealFrames.
- **Level** before the name, coloured by how hard the enemy is compared with you.
- **Cast bar** in amber when you can interrupt it, grey with a padlock when you can't, with a countdown.
- **Your DoTs only**, with timers that turn red near the end.
- **Aggro colours**, with separate tank and DPS/healer modes (see below).
- **Target and focus**: arrows and a light frame on your target, which is drawn on top of other plates; a diamond on your focus target. Other plates are dimmed and shrunk.
- **Rules per enemy**: highlight, tag, hide, resize, never dim, or turn off aggro colours for one enemy name.
- **Profiles per role and job**, switched automatically when you change job.

## Every state at a glance

<p align="center">
  <img src="docs/images/states.png" alt="Tank, DPS/healer, cast, DoT and highlight states" width="100%">
</p>

## Three skins

<p align="center">
  <img src="docs/images/skins.png" alt="Signal, Ledger and Heraldry skins side by side" width="100%">
</p>

Each profile keeps its own skin and its own colour overrides for every skin. **Signal** is the default and matches the Signal style from HealFrames.

## How the HP bar colour is chosen

```mermaid
flowchart TD
    A([Enemy plate]) --> M{Tank mode?}
    M -- yes --> T{Attacking me?}
    T -- yes --> HAVE[Green: you hold aggro]
    T -- no --> E{Attacking a party player?}
    E -- "yes, a tank" --> CO["Blue: co-tank has it (▸ PLD)"]
    E -- "yes, not a tank" --> LOST["Red: lost to someone (▸ WHM !)"]
    E -- no --> F{On my enmity list?}
    F -- "top of it" --> HAVE
    F -- lower --> LOST
    F -- no --> IDLE["Not pulled: calm bar, or the game's hostility colour"]
    M -- no --> D{Attacking me?}
    D -- yes --> PULLED["Red: AGGRO!"]
    D -- no --> G{On my enmity list?}
    G -- "threat ≥ warning %" --> WARN["Yellow: threat warning (92%)"]
    G -- "below it" --> SAFE["Calm bar, your threat % shown"]
    G -- no --> H{Is my party fighting it?}
    H -- yes --> SAFE
    H -- no --> IDLE
```

Tank or DPS/healer mode follows your job's role by default. You can also set either mode permanently. DPS and healers get the warning at 80% of the top threat by default.

## How a profile is picked

```mermaid
flowchart LR
    J([You change job]) --> A{Switch automatically on?}
    A -- no --> K[Keep the current profile]
    A -- yes --> B{Exception for this job?}
    B -- "yes (BLM → Savage BLM)" --> P[Use that profile]
    B -- no --> C{Profile set for the job's role?}
    C -- "yes (tank, healer, melee, physical or magical ranged)" --> P
    C -- no --> F[Use the first profile]
```

Rules are not part of any profile. They apply to every profile, and the first matching rule wins.

## Settings

Open with `/plater`. It's a native game window built with KamiToolKit, and a live preview updates as you change settings.

<table>
  <tr>
    <td width="50%"><img src="docs/images/set-plates.png" alt="Plates tab"><br><sub><b>Plates</b>: size, target emphasis, position</sub></td>
    <td width="50%"><img src="docs/images/set-skin.png" alt="Skin tab"><br><sub><b>Skin</b>: pick a skin and override its colours</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/images/set-rules.png" alt="Rules tab"><br><sub><b>Rules</b>: per-enemy highlight, tag, hide, size</sub></td>
    <td width="50%"><img src="docs/images/set-profiles.png" alt="Profiles tab"><br><sub><b>Profiles</b>: per role, with job exceptions</sub></td>
  </tr>
</table>

> The images on this page are rendered from the design mockups. Fonts and icons in game come from the game itself; DoT icons are drawn as plain squares here.

## Middle-click interrupt (optional, off by default)

Under **Aggro → Click** you can turn on *Middle-click a plate: cast my interrupt on it*. One click casts one action on that enemy: **Interject** for tanks, **Head Graze** for physical ranged. Nothing is queued, repeated or automated. Clicks over other game windows are ignored.

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add this URL, tick **Enabled** and save:
   ```
   https://raw.githubusercontent.com/Gompex/DalamudPlugins/main/repo.json
   ```
3. Open `/xlplugins`, search for **Plater for FF14** and install it.

## Commands

| Command | What it does |
| --- | --- |
| `/plater` | Open or close settings |
| `/plater debug` | Data probe window (enmity table, plate positions, timings) |
| `/plater on` · `/plater off` | Turn the plates on or off |
| `/plater native` | Show or hide the game's own enemy name and HP gauge |
| `/plater pos 0-2` | Position source: 0 root, 1 root screen (default), 2 number array |

## How it works

```mermaid
flowchart LR
    G[Game NamePlate addon<br/>PreDraw] --> S[PlateScanner<br/>50 pooled slots, no allocations]
    S --> H[NativePlateHider<br/>hides the game's enemy name and gauge]
    S --> R[PlateRenderer]
    R --> AG[AggroResolver]
    R --> RU[Rules and profiles]
    R --> N[PlateNode pool<br/>KamiToolKit native nodes on an overlay]
```

- The plates are native UI nodes, not ImGui, so they sit under the game's windows like the original nameplates.
- Each frame only writes what changed (percent, widths, colours, text), and plates are created a few per frame.
- Turned off in PvP.

## Build from source

```powershell
git clone --recurse-submodules https://github.com/Gompex/PlaterXIV.git
cd PlaterXIV; dotnet build .\PlaterXIV -c Release
```

Needs the .NET 10 SDK and XIVLauncher with Dalamud installed. KamiToolKit is a git submodule pinned to a tested commit. Releases are built by GitHub Actions when a version tag is pushed (`tools/Release.ps1 x.y.z`).

## Licence

[MIT](LICENSE). FINAL FANTASY XIV © SQUARE ENIX. This project is not affiliated with Square Enix.
