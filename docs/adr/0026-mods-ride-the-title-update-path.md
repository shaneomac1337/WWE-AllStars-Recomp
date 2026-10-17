# ADR-0026: Mods ride the game's title-update path, through an update folder the launcher builds

Status: accepted (2026-10-17, the owner's decision in the modding grilling).

## Context

Players want mods: attire and arena swaps, config tuning, and new wrestlers
(`docs/research/modding.md`, `.scratch/modding/spec.md`). The game is UE3 and
reads disc files through its own table of contents. A file dropped next to the
disc stays invisible, and a replacement of another size is read at the old size
(research §2.2).

The game already has two ways to load files that replace or add to the disc's:

- `UPDATE:\PatchTOC.txt`, which TU2 uses to replace six packages and the
  `Coalesced` bundles;
- DLC phases, which Pack 1 uses to add wrestlers.

The alternatives were:

- a `game:` overlay device in C++ that also rewrites `Xbox360TOC.txt`
  (research §3.2);
- hooking the game's archive lookup (`FileSize`, `CreateFileReader`;
  research §3.7).

## Decision

- Package and config mods go through `UPDATE:\`. At Play, the launcher builds
  one update folder from TU2's files and the enabled mods. It writes a merged
  `PatchTOC.txt` and merged `Coalesced` bundles, and passes that folder as
  `--update_data_root`.
- With no enabled mod, the game starts on TU2's own folder, unchanged. TU2's
  folder is never written.
- Content mods (new wrestlers) go through the game's DLC path, offline only.
  The game installs only the DLC content with the highest `PhaseID`, so at
  Play the launcher builds one combined content from the installed official
  packs and the content mods, with a `PhaseID` above the packs', a generated
  `DLCTOC.txt` and the roster entries written into its
  `CombinedGlobalCharacters.xxx` (research, "Content mods: DLC phases and
  roster slots"). The player's pack folders are never written, and the
  combined content is removed when no content mod is on or online play is
  on.
- The game's code is not changed for mods. A hook over the archive lookup
  comes back only if the first spike shows that `PatchTOC.txt` can't
  override a package TU2 never touched.

## Consequences

- Mods need no C++ build or release, so the image check, Native, Emulated and
  online play are unaffected. All the mod logic lives in the launcher core
  and is tested there with xUnit.
- Mods can replace packages and edit configs only. Movies, XML and splash
  screens aren't reachable through `UPDATE:\`; reaching them later means
  adding the `game:` overlay, which this ADR doesn't rule out.
- A mod package must replace a package name the disc or TU2 already has.
  Adding new content belongs to content mods.
- Mods only work when started from the launcher. Starting the exe on its own
  runs the unmodded game.
