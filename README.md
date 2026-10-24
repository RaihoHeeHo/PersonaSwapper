# Persona Swapper

A native macOS (and Windows/Linux) tool for Persona 5 Royal that turns any model in the game into a persona:

1. **Source model** — any GMD in `BASE.CPK`: Shadows and bosses, other personas, party members, NPCs.
2. **Destination persona** — the persona whose slot the model takes over.
3. **Edit and preview** — persona data (name, arcana, level, stats, growth, flags, 16 skills/traits, 20 affinities),
   an animation table (one row per persona animation slot, filled from any GAP in the game), and a live 3D preview
   of the selected slot playing on the source model.
4. **Export** — writes a Reloaded-II mod (Persona Essentials) with the model, rebuilt animation packs and any edited tables.

## Run it

Requirements: the .NET 8 SDK (`brew install --cask dotnet-sdk`), and the `GFD-Studio-macOS` folder next to this one.

Double-click **Run Persona Swapper.command**, or from Terminal:

```sh
cd ~/Downloads/p5r-build/src/PersonaSwapper
dotnet run -c Release --project PersonaSwapper.App
```

The game folder defaults to `~/Library/Application Support/Steam/steamapps/common/P5R`, and exports go to
`Reloaded-II/Mods` inside it. Both can be changed at the top of the window.

## How the swap works

| Game files | What the tool writes |
|---|---|
| `MODEL/CHARACTER/PERSONA/<id>/PS<id>.GMD` (and `PSZ`/`PSZX` compendium models) | the source model |
| `BPS<id>_000.GAP` (battle animations, 11 slots) and `GT<id>.GAP` | the persona's own pack with each slot replaced as chosen |
| `BATTLE/TABLE/PERSONA.TBL`, `UNIT.TBL`, `NAME.TBL` | only if edited; Persona Essentials merges them with other mods |

Findings this is based on (checked against the game files):
- Persona folders are named by persona ID. All 302 personas use battle slots 0 and 5–10; slots 1–4 are unused.
  Slot 0 is the long idle loop and slot 5 the appearance.
- Personas and Shadows share one slot layout: in the base game, persona slot N is a copy of the matching Shadow's slot N.
  So a Shadow or persona source uses its own animations slot-for-slot with no retargeting.
- Other sources (party members, NPCs) have different skeletons. The "Bones" column shows how many of an animation's
  bones exist on the source model; low numbers mean that motion won't play properly on it.

## Projects

- `PersonaSwapper.Core` — CPK reading (CriFsV2Lib), table parsers, model catalog, swap builder, mod writer, pose/skinning.
- `PersonaSwapper.App` — the Avalonia GUI and OpenGL preview.
- `PersonaSwapper.Cli` (`pswap`) — test harness: catalog, persona dumps, table round-trips, slot statistics, swaps, PNG renders.
- `PersonaSwapper.Snapshot` — runs the GUI headlessly and saves screenshots.

## Not done yet

- Compendium lore text (`FACILITY/DATMYTH.BMD`).
- Battle model scale (`VISUAL.TBL`), for very large or small source models.
- Bone-name mapping for retargeting between different skeletons.

## Credits

Table layouts from the 010 Editor templates by TGE, Scarltz, Lipsum, DeathChaos and Sierra; GFDLibrary by TGE;
CriFsV2Lib by Sewer56 (LGPL-3.0).
