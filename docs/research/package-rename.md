# Renaming a cooked package to another slot

2026-10-25. Question from a Reddit modder (u/Impressive-Team-3212): he built a Backlash arena
from the SmackDown arena's packages (`Arena_005_*`) and wants it to replace the All Stars arena
(`Arena_003_*`). Renamed files are refused by the launcher (modding ticket 20,
`launcher/src/Launcher.Core/PackageOrigin.cs`), because a renamed package hangs the game.

## What the packages hold

His five arena sublevels (`_Cin_Entrance`, `_Cin_Ring`, `_Cin_Ropes`, `_Entrance`, `_Ring`) are
LZO-chunked big-endian packages (licensee version 398). Their name tables carry their own
package name exactly once: the top-level `Package` export (`Arena_005_Cin_Entrance`). Every
other package they reference is shared and keeps its name: `Env_*`, `MAT_*`, `Core`, `Engine`,
`EngineMaterials`. Nothing else in them names `Arena_005`.

## The rename

Prototype (`scripts/ue3rename.py`, reading through `scripts/ue3names.py`): expand the chunks, rewrite the name-table entry equal to
the old file name to the new one (same length, `Arena_005` -> `Arena_003`, so no offset
moves), and re-pack each chunk as literal-only LZO1X blocks. The summary keeps its layout, and
only the chunk table's compressed offsets and sizes change. The files come out about 2.3x larger
(no compression) and end at their last chunk, without the disc's 64 KB padding. Each one
expands back to the edited bytes.

**Owner check, 2026-10-25:** the launcher accepted the five renamed packages, plus his own
`Shared_Assets.xxx`, as a mod. The Backlash arena loaded in place of the All Stars arena, and
entrance and match played with no hang.

## Limits

- **Same length only.** A different length shifts every offset after the name table. That
  means the export serial offsets, plus bulk-data offsets inside objects, which are not worth
  rewriting.
- **Self-name only.** A package that references another package of the same arena set by name
  would need that reference renamed too. None of his do. A launcher feature should check for it
  and refuse when an `Arena_005*` name other than the self-name is present.
- **Only the sublevels he changed are replaced.** The rest of `Arena_003_*` (level, lighting,
  FX, audio) stays the All Stars arena's own.
