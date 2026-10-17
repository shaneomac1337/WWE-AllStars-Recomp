# Modding support: game data, file loading, approaches and a first milestone

Date: 2026-10-17. Question: what is the best way to add modding support to the
port, and what should be built first? The roadmap lists "Mod and asset hooks"
as post-v1 item 3 (`docs/ROADMAP.md:60`). Seven players asked for mods and a
bigger roster, one of them a PS3 All Stars modder who wants to port their
mods (`docs/research/community-feedback.md:56`, `:120-128`). This note lists
facts, options and open questions, and decides nothing. No source file,
asset, ISO or generated file was changed. Nothing was built, and the game was
not run.

Tags: **[S]** this repo or the SDK (file:line). **[B]** the TU2 binary, read
with Ghidra (project `WWEAllStarsTU2`, program `default.xexp`, opened
read-only; `xex_entry` = `0x8241A7E0` confirms TU2) or with
`scripts/disasm.py` and Python on `analysis/xex/tu2.pe`. **[P]** a primary
external source (linked). **[I]** my inference. **[U]** unverified. Every
guest address is TU2 (ADR-0022). `SDK:` is `.tools/rexglue-source`
(`f5337cdc` plus `patches/`). Repo HEAD is `1900c54`.

## Summary

- **The data is stock UE3 with light THQ changes.** `assets/` holds
  5,414 cooked packages (`.xxx`), 164 Bink videos, a plain-text
  `Coalesced.ini` bundle, three loose XML files and `Xbox360TOC.txt`. Nothing
  is encrypted or signed. The TOC's CRC column is 0 everywhere, and the game
  does not even parse it. Packages compress with LZO1X, which this repo
  already decodes (`launcher/src/Launcher.Core/Lzo1x.cs`) [S][B].
- **The game already has two override mechanisms of its own.** Both come
  from UE3 and both already run in this port:
  1. **`UPDATE:\` with `PatchTOC.txt`.** TU2 uses this to replace six
     packages and the Coalesced files. The port mounts it from `updatedata/`.
  2. **DLC phases** (`DLC0:\Phase1\DLCTOC.txt`, ini fragments, extra
     packages). Pack 1 uses this to add five wrestlers.

  Mods can ride on these, so the game's code needs no change for file
  replacement [S][B].
- **The disc TOC is authoritative for disc files.** `FileSize` returns the
  TOC size, or −1 when a file is not listed. `FindFiles` on disc paths walks
  the in-memory TOC and never touches the disc. A file dropped into
  `gamedata` therefore stays invisible unless the TOC lists it, and a file
  replaced at a different size is read at the wrong size, unless the TOC is
  rewritten too [B].
- **Recommended first milestone: a launcher-built mod overlay on `update:`.**
  At Play, the launcher builds one folder from three sources: TU2's files,
  every enabled mod's files, a merged `PatchTOC.txt` and a merged
  `Coalesced.*`. It then passes that folder as `--update_data_root`. This
  needs no C++ change, no SDK patch and no RE beyond one spike, which checks
  that a PatchTOC entry also wins for packages TU2 never touched. A
  `game:` overlay device in `src/` comes second, for movies, XML and splash
  files that live outside the package system [I].
- **Code mods stay compile-time.** ReXGlue's overrides (`REX_HOOK`,
  `[[midasm_hook]]`) are linked into the exe, and direct calls are plain C++
  calls. A runtime "patch TOML" can only safely write data, not code. A
  runtime plugin API on the scale of N64Recomp's would be a large project of
  its own [S][P].
- **Texture replacement at the GPU layer is feasible on Native.** Native
  already hashes each guest texture's data with XXH3 before upload
  (`src/native/textures.cpp:466-475`), which is the natural key for a
  dump-and-replace pack. Emulated would need a separate SDK patch [S].

## 1. Game data formats

### 1.1 What is in `assets/`

Counts and sizes were read on 2026-10-17. 5,634 files, 5.9 GB in total.

| Folder | Files | Size | What |
|---|---|---|---|
| `Game/CookedXenon/` | 1,340 `.xxx` + `changelist.txt` | 1,610 MB | Cooked packages: scripts, characters, arenas, UI, animation |
| `Game/CookedXenon/RLE/` | 2,116 `.xxx` | 405 MB | Packages (texture-like; `RLE_001_d`) |
| `Game/CookedXenon/CAPMESHES/` | 847 `.xxx` | 103 MB | Create-a-Superstar meshes |
| `Game/CookedXenon/CAPTEXTURES/` | 1,111 `.xxx` | 38 MB | Create-a-Superstar textures |
| `Game/Movies/` | 164 `.bik`, 27 `.txt` | 3,803 MB | Bink video; the `.txt` files are cue tables (`VERSION`, `SCALE`, time/name rows) |
| `Game/Config/Xenon/Cooked/` | `Coalesced.ini` | 0.2 MB | Bundle of the `Xe-*.ini` files |
| `Game/Localization/` | 7 `Coalesced.<lang>`, `Online/` 5 `.txt` | 1.2 MB | Localised text bundles |
| `Game/Content/XML/` | 3 `.xml` | 0.1 MB | CAP UI state and message boxes, plain XML |
| `Game/Xbox360TOC.txt` | 1 | 0.3 MB | 5,696 lines, one per file |
| `Splash/` | 4 `.bmp` | 7 MB | Boot splash |
| root | `default.xex`, `default.xexp`, `nxeart`, `AvatarAssetPack` (both `PIRS` packages), `$SystemUpdate/` | | Not game data |

### 1.2 Packages (`.xxx`)

- **Format.** UE3, big-endian, tag `9E2A83C1`, version `0x0025018E` (file
  version 398, licensee 37) on 5,382 files [B: header tally, Python]. The
  header layout is in `docs/research/shader-containers-from-disc.md:56-103`:
  two licensee fields at `0x0C` and `0x10`, then a stock UE3 v398 summary
  with a CompressionFlags/chunk table (2 = LZO). Name, import and export
  entries are stock (`:94-100`) [S].
- **Whole-file compression.** 31 top-level files start with `C1 83 2A 9E`
  (the tag, little-endian), for example `Core.xxx`, `Engine.xxx`, `Game.xxx`,
  `SD_*.xxx` and `CombinedGlobalCharacters.xxx`. Each is one chunk of 128 KiB
  LZO1X blocks. I decoded block 0 of `Core.xxx` and of `SD_UI.xxx` with a
  scratch LZO1X decoder. Each gave exactly `0x20000` bytes beginning with
  the package tag, and used every compressed byte [B]. The header's
  uncompressed size (offset 12, LE) equals the TOC's second column, for
  example 13,413,618 for `CombinedGlobalCharacters.xxx` [B].
- **Per-package chunk compression.** The summary is plain and the body is in
  LZO chunks. 380 of the 410 shader-cache packages use it
  (`shader-containers-from-disc.md:76-78`) [S]. A rough heuristic (a second
  tag within the first 512 bytes) flags 547 of the 5,382 packages [B].
- **Odd one out.** `AIFSM_Strategies.xxx` (16 KiB, entropy 7.99 bits/byte)
  has no package tag. It is probably not a package (`shader-containers-from-disc.md:59`) [S].
- **Tool support.** UE Viewer (umodel) supports "WWE All Stars" as
  `ArVer 398 / LicVer 37` under its `TNA_IMPACT` build option: skeletal
  meshes, textures and static meshes, but not animations
  ([GameDatabase.cpp L148, L834-836](https://github.com/gildor2/UEViewer/blob/a0bfb468d42be831b126632fd8a0ae6b3614f981/Unreal/GameDatabase.cpp),
  [table.ini L2772-2776](https://github.com/gildor2/UEViewer/blob/a0bfb468d42be831b126632fd8a0ae6b3614f981/Tools/CompatTable/table.ini);
  Gildor's [forum thread](https://www.gildor.org/smf/index.php/topic,787.0.html)
  dates public support to 2014-10-08) [P]. umodel only reads. No public
  package writer or repacker for this licensee version was found [P/U].

### 1.3 Other files

- **`Xbox360TOC.txt`.** Each line is
  `<size> <uncompressed size or 0> 0 <..\path> <crc>`. Every CRC is 0. The
  game parses only `%d %d %d %s` (format string at `0x8213F498`, read by
  `0x82767D08`) [B], so the CRC column is ignored.
- **`Coalesced.ini` / `Coalesced.<lang>`.** A big-endian count, then
  `{FString name, FString body}` pairs. Each body is the plain text of one
  ini: `Xe-Engine.ini`, `Xe-Game.ini`, `Xe-Input.ini` and so on. Not
  compressed or encrypted [B: parsed with Python]. `Xe-Engine.ini` sets
  `Paths=..\Game\CookedXenon`, `XmlDir=CookedXenon\` and `[DLCSettings]`.
  `Xe-Game.ini` holds gameplay tuning such as attack delay modifiers [B].
  TU2 ships its own full `Coalesced.*` (`updatedata/`, 184,629 bytes for
  `.ini`) [S].
- **Movies.** Bink (`.bik`) under a fixed `..\Game\Movies\` prefix (string
  `0x8220162C`, used at `0x82AFC63C` and `0x82BDA5E4`) [B].
- **Integrity checks.** I found no string that hints at package hashing
  (`.sha` matched nothing related) [B]. Treat "no integrity check" as [U]
  until a modified package is loaded.

## 2. How the game opens files

### 2.1 Host side (ReXGlue VFS)

- `Runtime::SetupVfs` mounts `game_data_root` as a read-only
  `HostPathDevice` at `\Device\Harddisk0\Partition1`. It links `game:` and
  `d:` to it and mounts `update_data_root` as `update:`
  (SDK:`src/system/runtime.cpp:294-337`) [S].
- `VirtualFileSystem::ResolvePath` resolves symlinks, then takes the
  **first** device whose mount path is a prefix of the path. It never falls
  through to another device (SDK:`src/filesystem/virtual_file_system.cpp:105-134`).
  `HostPathDevice::ResolvePath` walks a tree it built at mount time, then
  falls back to stat and a case-insensitive scan
  (SDK:`src/filesystem/devices/host_path_device.cpp:59-114`) [S].
- The app can change mounts after setup and before the game starts:
  `Runtime::file_system()` is public (SDK:`include/rex/runtime.h:118`),
  `UnregisterDevice` exists (SDK:`include/rex/filesystem/vfs.h:35`), and
  `Device` is a small interface (SDK:`include/rex/filesystem/device.h:23-45`).
  `OnPostLoadXexImage` and `OnPostSetup` run before the module launches
  (SDK:`src/ui/rex_app.cpp:299-308`; `src/wweallstars_app.h:119`, `:137`) [S].
- `--log_file_opens=<substring>` logs every `NtCreateFile` whose path
  contains the substring (SDK:`src/kernel/xboxkrnl/xboxkrnl_io.cpp:135-138`).
  It is the test tool for every step below [S].
- The launcher passes `--game_data_root`, `--update_data_root` and
  `--user_data_root` (`launcher/src/Launcher.Core/LaunchArguments.cs:15-18`).
  DLC goes unpacked to `userdata/0000000000000000/54510866/00000002/<name>/`
  with a `.header` (`launcher/src/Launcher.Core/DlcLibrary.cs:17-44`) [S].

### 2.2 Guest side (the game's UE3 `FFileManagerXenon`)

The global file manager object is `PTR_PTR_835304cc`, and its vtable is at
`0x820037E8` [B: Ghidra decompile, vtable read from `tu2.pe`].

| Slot | Address | Role (my naming) | Evidence |
|---|---|---|---|
| `+0x00` | `0x82764508` | `Init`: mounts the utility drive, reads `..\Game\Xbox360TOC.txt` (string `0x8213E618`) with CreateFile/GetFileSize/ReadFile, then calls `+0x6C` | [B] decompile |
| `+0x08` | `0x82764830` | `CreateFileReader`: normalises the path (`0x82763F18`), opens it with `CreateFileA`, and wraps it in a reader (`0x82763A90`, 0x49C bytes). It does **not** consult the TOC | [B] |
| `+0x10` | `0x82764C68` | `FileSize`: for read-only paths, TOC column 1 or −1; for writable paths, the real file size | [B] |
| `+0x14` | `0x82764E70` | `UncompressedFileSize`: TOC column 2, or −1 | [B] |
| `+0x34` | `0x82765B40` | `MakeDirectory` | [B] |
| `+0x3C` | `0x827672A8` | `FindFiles`: for `DLC*`, `save:`, `cache:` and `UPDATE:` it enumerates the real device (FindFirst `0x82D9DF08`, FindNext `0x82D9EC78`) and adds unknown names to the TOC map. For disc paths it walks only the TOC array (stride `0x1C`, at `this+8`) | [B] |
| `+0x6C` | `0x82767D08` | Parses a TOC text into the map at `this+8` (`0x82767188` inserts, `0x827426A0` looks up) | [B] |
| `+0x70` | `0x82766460` | Rewrites TOC lines: splits on `\r\n` (`0x8213EF70`) and replaces `.\` (`0x8213EF68`) with a prefix | [B], purpose partly [I] |

Helpers:

- **Path normaliser `0x82763F18`.** Paths that start with `DLC`, `save:`,
  `cache:` or `UPDATE:` pass through. `..\X` becomes `D:\X`; other relative
  paths become `D:` plus the path, and writable ones go to `save:\` or
  `cache:\`. `/` becomes `\` [B: strings at `0x82135EF4`, `0x82134F2C`,
  `0x8213E468`, `0x8213E458`, `0x8213E430`, `0x8213E428`, `0x8213E438`,
  `0x8213E448`].
- **`CreateFileA` at `0x82D9E288`.** The XAPI wrapper. It maps the creation
  disposition, calls `RtlInitAnsiString`, then makes the NtCreateFile call
  through a pointer [B].

The game's own patch path:

- A static initialiser at `0x83431CA0` sets the global FString `0x837349C4`
  to `UPDATE:\` (string `0x8213F538`) [B].
- At boot, `0x82609CA0` (called from `0x82607ED8` at `0x82608518`) builds
  `UPDATE:\PatchTOC.txt` (`0x8209FCA4`). When that file loads, it calls the
  file manager's `+0x70`, then `+0x6C`, which merges the patch TOC into the
  TOC map [B].
- `0x82749048` builds `UPDATE:\` + `Coalesced.ini` (`0x82138A78`) at
  `0x827490F0`, behind a flag at `0x8370BEA4` (`lwz` at `0x827490D8`) [B].
- In a real run, the game lists `UPDATE:\*`, opens `UPDATE:\Coalesced.ini`
  and `.int`, reads `PatchTOC.txt`, and opens TU2's six packages from
  `UPDATE:\` instead of the disc
  (`.scratch/tu2-dlc/issues/03-tu2-boots-and-plays.md:58-65`, `:113-116`) [S].
- TU2's `PatchTOC.txt` is 12 lines like `1409024 6464092 0 .\Engine.xxx 0`
  (`updatedata/PatchTOC.txt`): the same columns as the disc TOC, with `.\`
  flat names [S].

The game's DLC path:

- `0x82A32A88` reads `DLCTOC.txt` (`0x821A54CC`). It merges every `ini` it
  finds (it splits names on `__`, as in `AI__Xe-Game.ini`), reads
  `Engine.PackagesToFullyLoadForDLC` and `MapName`, and loads a `DLCDummy`
  [B: strings at `0x82A32BCC`-`0x82A32C0C`].
- Pack 1 holds `Phase1/` with 103 packages, `DLCTOC.txt` and ini fragments
  with `[GameDLCManager] +InstalledPhases=1` and `[AvailableCharacters]`
  bit values (`docs/research/title-updates-and-dlc.md:144-154`) [S].
- The license mask gates the roster
  (`.scratch/tu2-dlc/issues/03-tu2-boots-and-plays.md:79-95`) [S].

**What follows for mods.**

- A **replaced package** can go in `UPDATE:\` with a PatchTOC line, as TU2's
  six do.
- A **replaced file of any type** can go in an overlay over `game:`, but
  `Xbox360TOC.txt` must then carry its new size (and its uncompressed size,
  if the file is compressed as a whole). Otherwise `FileSize` returns the old
  size [B].
- **New packages and ini entries** can arrive as a DLC phase, which is the
  engine's own "add content" path [B][S].
- How the package cache picks `UPDATE:\Engine.xxx` over
  `D:\Game\CookedXenon\Engine.xxx` for the same short name was not traced.
  It works for TU2's six packages, and the spike (§2.3) shows it works for
  packages TU2 never touched [B].

### 2.3 Spike result (2026-10-17, ticket `modding/01`)

**Run.** The built `out/build/win-amd64-relwithdebinfo-vulkan/wweallstars.exe`,
started directly (session.ps1's arguments, a throwaway copy of the user
data), with `--update_data_root=<scratch copy of updatedata>` and
`--log_file_opens=xxx`, up to the title screen, then closed with the
window's close. No code or game data in the repo changed. Two packages TU2
does not ship, both opened at boot from `D:\` in a baseline run, were copied
unchanged into the scratch folder:

- `Front_End.xxx`: plain header, line `98304 0 0 .\Front_End.xxx 0`;
- `CombinedGlobalCharacters.xxx`: whole-file LZO, line
  `3244032 13413618 0 .\CombinedGlobalCharacters.xxx 0`. The second column
  is the header's uncompressed size (offset 12, LE), and it equals the disc
  TOC's (§1.2).

**Log (paths and counts only).**

| Run | Update folder | Package opens | `Front_End` | `CombinedGlobalCharacters` | Reached |
|---|---|---|---|---|---|
| Baseline | TU2 copy | 46 (6 from `UPDATE:\`) | `D:\Game\CookedXenon\` | `D:\Game\CookedXenon\` | title screen |
| Override | + both files, + both lines | 46 (8 from `UPDATE:\`) | `UPDATE:\Front_End.xxx` | `UPDATE:\CombinedGlobalCharacters.xxx` | title screen |
| Control A | + both files, TU2's `PatchTOC.txt` | 32, then nothing | not reached | `UPDATE:\CombinedGlobalCharacters.xxx` | stuck on the boot logo |
| Control B | + `Front_End.xxx` only, TU2's `PatchTOC.txt` | 46 (7 from `UPDATE:\`) | `UPDATE:\Front_End.xxx` | `D:\` | title screen |

In the override run neither package was opened from `D:\` at all; the open
order and count match the baseline.

**Verdict.** A `PatchTOC.txt` line makes the game load a package TU2 never
replaced from `UPDATE:\`. Milestone 1 (§5) needs no hook, so §3.7 stays
unused. Two more findings:

- The `UPDATE:\` name wins **even without a TOC line**: `FindFiles` lists
  `UPDATE:\` and adds unknown names to the TOC map (§2.2). A plain package
  then loads (control B), but a whole-file LZO package without its
  uncompressed size hangs the boot (control A). The overlay must write a
  line for every mod package, with the header's uncompressed size, as the
  spec says. It also means a stray `.xxx` in the update folder is live, so
  the overlay folder must hold only TU2's files and the winning mod files.
- A visible change (an attire swap) was not checked, since it needs the
  select screen; the owner check is in the ticket.

**Owner check, attire swap by rename (2026-10-17).** Hulk Hogan's attire B
packages copied under attire A's names, with PatchTOC lines: the game opens
both from `UPDATE:\`, then hangs on a black screen at his entrance with no
model. Cooked packages name their inner objects after the original file, so
a renamed package is not a valid replacement and must be refused (ticket 20)
[owner check, ticket 01].

## 3. Approaches, ranked

Effort assumes the existing launcher (Avalonia, ADR-0018) and test habits.

| # | Approach | Effort | Risk | What modders get |
|---|---|---|---|---|
| 1 | **(a) `update:` overlay built by the launcher**, with PatchTOC and Coalesced merging | S (days) | Low: the game's own TU path | Replace any package; ini tuning; text edits |
| 2 | **(a') `game:` overlay device in `src/`**, with a regenerated `Xbox360TOC.txt` | S–M | Low–medium: VFS code on the boot path | Replace movies, XML, splash; replace packages of any size |
| 3 | **(e) Native texture dump/replace**, keyed by XXH3 | M | Medium: streaming mips, tiling, Native only | Texture packs with no package tools |
| 4 | **(f) Roster/attire/arena swaps** over 1–2, plus a DLC-phase "mod content" mount for additions | M–L | Medium–high: needs RE of `GameDLCManager` and character data | Attire and arena swaps soon; new wrestlers later |
| 5 | **(b) Package repack tooling** (read, edit, write `.xxx`) | L | Medium: licensee quirks | Real model and texture edits |
| 6 | **(d) Data-only patch TOML** (Xenia-style) | S | Low for data; code addresses must be refused | Number tweaks in `.data` |
| 7 | **(c) Hook the archive lookup** (`FileSize`/`CreateFileReader`/`FindFiles`) | M | Medium: hooks in hot file IO | Nothing beyond 1+2, unless the spike fails |
| 8 | **(d) Runtime code mods** (plugin DLLs) | XL | High | Real code mods; a large API to keep stable |

### 3.1 (a) `update:` overlay, built by the launcher (recommended first)

- **How.** Mods live in `<root>/mods/<id>/` (or a zip) with a small manifest
  and files laid out as they would appear in `updatedata/`. At Play the
  launcher builds `<root>/cache/mod-update/` (or similar):
  - every TU2 file, as hardlinks (falling back to copies);
  - each enabled mod's files, in priority order;
  - `PatchTOC.txt`: TU2's 12 lines, plus one line per mod file, using the
    file's size and, for whole-file compressed packages, the uncompressed
    size from header offset 12 (LE);
  - `Coalesced.*`, rebuilt from TU2's bundle with each mod's ini edits
    applied (format in §1.3).

  It then passes `--update_data_root=<that folder>` in place of `updatedata`
  (`LaunchArguments.cs:16`) [I].
- **Why first.** It is the exact path TU2 takes (§2.2), so no game RE is
  needed. It touches no C++, so the image check, Native and online are all
  unaffected. It can be tested in the launcher's xUnit suite. The launcher
  already has STFS, LZO and TOC-like parsing (`Lzo1x.cs`, `StfsPackage.cs`,
  `DiscShaders.cs:245`) [S].
- **Risks and limits.**
  - Does a PatchTOC entry for a package TU2 never shipped (for example a
    `CHR_*` attire package) also win? TU2 shows it for six packages only [U].
  - PatchTOC names are flat (`.\Name`). UE3 package names are unique short
    names, so a flat folder should do [I].
  - Movies and XML use fixed `D:\Game\...` paths, so `update:` probably does
    not reach them [I]; that is approach (a').
- **Launcher.** A Mods page lists each mod: name, author, version, a toggle,
  its order, and conflicts (two mods shipping the same file name, where the
  higher priority wins and the page says so). It also offers "Open mods
  folder" and drag-and-drop of a zip, the same pattern as the DLC page
  (`.scratch/tu2-dlc/issues/05-dlc-from-the-launcher.md`). The SvR 2011 port
  has a close match: `.svrmod` zip, `manifest.txt` key=value, a `disabled`
  marker file, a launcher mods tab (`KaikoClanworth1/wwe-svr2011-pc`
  `docs/SVRMOD_FORMAT.md`, `port/launcher/mods_tab.c`, read at `745aaf2d`) [P].

### 3.2 (a') `game:` overlay device in `src/`

- **How.** In `OnPostSetup`, unregister `\Device\Harddisk0\Partition1` and
  register an `OverlayDevice` with the same mount path.
  - Its `ResolvePath` tries each mod root, then the base `HostPathDevice`.
  - It serves a generated `Game\Xbox360TOC.txt` whose size columns match
    the files that win.
  - Directory listings merge names; the game lists disc folders through the
    TOC, not the device, so this matters little (§2.2, `FindFiles`) [S][B][I].
- **What it adds.** Replacement of `.bik` (plus their `.txt` cue tables),
  XML, splash `.bmp`, and any package, at any size. It also covers mods that
  ship new disc files that something references (they appear through the
  TOC).
- **Risks.** The VFS is on the boot and streaming path, and the entry tree
  is built once at mount. It needs SDK-level care with `Entry` ownership.
  It is C++, so it needs a build and a release, unlike (a).
- **Prior art.** UnleashedRecomp puts the same idea in `FileSystem::ResolvePath`,
  calling `ModLoader::ResolvePath`: it searches each active mod's include
  directories in order, the first match wins, and results are cached by XXH3
  ([mod_loader.cpp L30-87](https://github.com/hedge-dev/UnleashedRecomp/blob/cf829a9eca8fb680fba4b0409ddeb6ca92f22e3c/UnleashedRecomp/mod/mod_loader.cpp),
  [file_system.cpp L364-380](https://github.com/hedge-dev/UnleashedRecomp/blob/cf829a9eca8fb680fba4b0409ddeb6ca92f22e3c/UnleashedRecomp/kernel/io/file_system.cpp)).
  It hooks the game's archive loader to merge AR/ARL file lists (L318-565),
  which we should not need because UE3's TOC plays that role [P][I].
  Xenia Canary has no overlay or mods folder; people edit an extracted game
  folder (`src/xenia/vfs/devices/`, `emulator.cc` L374-398 at
  [`dac2794c`](https://github.com/xenia-canary/xenia-canary/tree/dac2794ca72e20b2cb5e6ca9e765da8811a19d6d)) [P].

### 3.3 (e) Texture replacement on Native

- **Hook point.** `src/native/textures.cpp:466-475` hashes the guest base
  level (and mips) with XXH3 before deciding whether to upload, and
  `:560-580` copies the converted data into the upload buffer [S].
  - A replacement pack keyed on `{base-level hash, format, width, height}`
    would substitute pre-converted data (for example a DDS/KTX2 in the host
    format) at that point.
  - A "dump textures" developer mode writes each newly seen key once [I].
- **Gains.** It works without understanding packages, and higher-resolution
  replacements are possible because Native owns the host image size [I].
- **Risks.** UE3 streams mips, so the base-level data at an address changes
  as mips stream in, and keys must survive that. CAP textures are composed
  at runtime and render-target textures change every frame. It is Native
  only: Emulated would need its own SDK texture-cache patch.
  The owner's "never touch the GPU" rule is not affected; this is ordinary
  texture upload.
- **Launcher.** A "Texture packs" section, or texture packs treated as a mod
  type with a `textures/` folder [I].

### 3.4 (f) Roster, attire and arena mods

- **Swaps.** Arena, attire and texture swaps are plain package replacements,
  so they come for free with 3.1 and 3.2 once modders can produce packages.
  The wrestler ID map (`CHR_101` = Hulk Hogan and so on) is community
  knowledge ([ProWrestlingMods wiki](https://wiki.prowrestlingmods.io/books/wwe-all-stars-xbox-360)) [P].
- **Additions.** New wrestlers or slots need the DLC path. The game reads
  `[GameDLCManager] InstalledPhases` and `[AvailableCharacters]
  CharacterData_<id>=<bit>` from ini fragments, `DLCTOC.txt` and packages
  under `DLC0:\Phase1` (§2.2). A "mod content" folder installed like a DLC
  pack (`DlcLibrary.cs`) could carry a Phase 2 [I].
  - This needs RE of how phases and character bits map to select-screen
    slots, how many phases and slots exist, and what a `CharacterData` entry
    must contain.
  - It also needs authored packages, which today means approach (b).
- **Ini-only roster tweaks** (AI variables per character in `Xe-Game.ini`,
  `[AI.Variables.Character.*]`) come with 3.1's Coalesced merging [S].

### 3.5 (b) Package repacking

- **Reading.** Mostly solved: umodel extracts meshes and textures (§1.2),
  and this repo has a package-table reader and an LZO1X decoder
  (`shader-containers-from-disc.md`; `Lzo1x.cs`) [S][P].
- **Writing.** Not solved anywhere public [U]. A minimal writer that keeps
  the export table and only replaces one export's serial data (a texture's
  mip bulk data) is feasible. Whether the loader accepts uncompressed output
  (CompressionFlags 0, TOC column 2 = 0) is [U]; it would avoid writing an
  LZO compressor.
- **Recommendation.** Leave repacking to the community, and publish the
  format notes we already have [I].
- **PS3 mods.** The PS3 modder's mods would be PS3 cooks. Textures and
  shaders are platform-cooked in UE3, so they are probably not drop-in [U].

### 3.6 (d) Code mods and memory patches

- **What exists.**
  - Whole-function override: every guest function is a weak alias of
    `__imp__sub_X`, and `REX_HOOK`/`REX_HOOK_RAW` replace it at link time
    (SDK:`include/rex/hook.h:36-49`; used in `src/unlock_everything.cpp:131`,
    `:138`).
  - Mid-function hooks: `[[midasm_hook]]` in the manifest (42 of them, for
    example `wweallstars_manifest.toml:197-201`) [S].
  - XenonRecomp documents the same mid-asm syntax (`registers`, `return*`,
    `jump_address*`, `after_instruction`)
    ([README L207-238](https://github.com/hedge-dev/XenonRecomp/blob/ddd128bc/README.md)) [P].
  - All of it is compiled in.
- **Runtime limits.**
  - Generated code calls `bl` targets directly
    (`generated/default/wweallstars_recomp.251.cpp:18726`,
    `sub_82BE7560(ctx, base);`).
  - Only `bctrl` goes through `REX_CALL_INDIRECT_FUNC` and the function
    table that `FunctionDispatcher::SetFunction` can change at runtime
    (SDK:`src/system/function_dispatcher.cpp:252`).
  - So a runtime plugin could only replace virtual or indirect targets [S].
  - Writing PowerPC into `.text`, as Xenia patches do, has no effect: the
    code is precompiled [I].
- **Data-only patch TOML.** A small Xenia-compatible loader would apply
  `be8`/`be16`/`be32`/`be64`/`f32`/`f64`/`string`/`u16string`/`array` writes
  to `.data`/`.rdata` only, and refuse `.text` addresses.
  - Format and parser: [game-patches README](https://github.com/xenia-canary/game-patches/blob/main/.github/README.md);
    `src/xenia/patcher/patch_db.cc` L30-281.
  - Xenia matches files on the XXH3-64 of the module's code pages
    (`user_module.cc` L1215-1255 at `dac2794c`). Our image check already
    computes XXH3-64 of `.text` (`0x0289638F85BB6980`); whether the two
    hashes agree is [U].
  - The only WWE All Stars file in game-patches is a **code** patch, `li r3,1`
    at base `0x833A6C98`
    ([file](https://github.com/xenia-canary/game-patches/blob/main/patches/54510866%20-%20WWE%20All%20Stars.patch.toml)),
    so it would not apply as a data patch anyway [P].
  - Low payoff: UE3 keeps most tunables in ini files, which 3.1 already
    covers [I].
- **Runtime plugins.** N64Recomp's model is the reference:
  - `.nrm` zip mods with `mod.toml`, and `RECOMP_PATCH`/`RECOMP_HOOK`/
    `RECOMP_HOOK_RETURN` sections
    ([modding.h](https://github.com/Zelda64Recomp/MMRecompModTemplate/blob/0c3e82c919dc16993ccc32f1dacb8b74ad19f8ac/include/modding.h)).
  - The runtime recompiles the mod and patches function entries with a
    `movabs/jmp` (`N64ModernRuntime` `librecomp/src/mods.cpp` L558-593 at
    `cdf5abbd`).
  - An in-game mod menu with toggles (`Zelda64Recomp` `src/ui/ui_mod_menu.cpp`) [P].
  - For this port it would mean a stable C ABI over guest functions, PPC
    mod code or native DLLs, and entry patching of our x64 functions. That is
    a project on its own, and it conflicts with the image check and online
    determinism [I].

### 3.7 (c) Hooking the archive lookup

This is only needed if the spike in §5 shows that PatchTOC entries do not
override packages outside TU2's six. Then a `REX_HOOK_RAW` on `FileSize`
`0x82764C68`, `UncompressedFileSize` `0x82764E70` and `CreateFileReader`
`0x82764830` could redirect any disc path to `UPDATE:\` when a mod has it.
These are indirect (vtable) calls, so a `SetFunction` swap would also work
[B][S][I].

### 3.8 Cross-cutting: online play

- Mods that change gameplay (ini tuning, scripts in `Engine.xxx`/`Game.xxx`,
  character packages) can desync online play, since the game checks its
  simulation with its own checksums (`CONTEXT.md` "Desync") [S].
- Options:
  - turn mods off for online sessions;
  - send a hash of the active mod set in the lobby and only match equal sets;
  - mark mods as cosmetic (textures, movies) or gameplay.

  This is the owner's call (§6) [I].

## 4. Prior art, verified vs. unverified

| Project | Mod mechanism | Status |
|---|---|---|
| Xenia Canary | Patch TOML (be8…array) matched by title id plus XXH3-64 of the code pages; `allow_plugins` loads homebrew `.xex` plugins from `plugins\<TITLEID>\plugins.toml` (`plugin_loader.cc` L16-140); no file overlay | Verified (source at `dac2794c`) |
| UnleashedRecomp | Hedge Mod Manager `ModsDB.ini`/`mod.ini` `IncludeDir`s; first-match path override; archive file-list merge; no code mods (README L205-240) | Verified (`cf829a9e`) |
| Zelda64Recomp / N64Recomp | `.nrm` (zip), `mod.toml`, patch/hook/event macros, runtime entry patching, in-game mod menu with toggles and config options | Verified |
| XenonRecomp / ReXGlue | Compile-time function overrides and `[[midasm_hook]]`; no runtime mods, patch DB or overlay | Verified |
| wwe-svr2011-pc | `.svrmod` zip, `manifest.txt`, `disabled` marker, launcher mods tab, a Mod Maker | Verified (repo tree at `745aaf2d`) |
| DBZ Budokai 3 HD port | `mods/<mod>/manifest.txt`, `.disabled`, per-archive-entry overrides, launcher Mods tab | Verified (`MODDING_README.md` at `711e031e`) |
| UE Viewer | Reads WWE All Stars packages (no animations) | Verified (source and forum) |
| Smacktalks All Stars forum (5 modding threads, read 2026-10-17 in Chrome) | Texture ripping with UE Viewer; one PS3 attire texture-replacement mod (method not published); no Xbox 360 mod tools | Verified, see 4.1 |
| QuickBMS/Noesis scripts for All Stars | None found; the ZenHAX/XeNTaX hits are Yuke's titles | Not found |

### 4.1 Smacktalks community threads

Read in Chrome on 2026-10-17, behind a Cloudflare check, from the
[WWE All Stars forum](https://smacktalks.org/forums/forum/173-wwe-all-stars/).
WebFetch got HTTP 403 there earlier. Each thread has only its opening post.

- **Texture ripping guide** ([topic 82969](https://smacktalks.org/forums/topic/82969-how-to-rip-hq-textures-and-skeleton-from-wwe-all-stars-xbox-360-or-ps3/),
  Feb 2026). It uses UE Viewer on single `CHR_XXX_Y` packages from
  `Game/CookedXenon`: `XXX` is the superstar id and `Y` the attire. Textures
  are 512x512 by default. It also lists the id of every superstar slot,
  including "No Slot" gaps (109, 111, 120, 125, 127-129, 200, 209, 215, 217,
  223, 225). It says texture modding "seems doable" but gives no method.
  **[S]** The names match our disc: `assets/Game/CookedXenon/CHR_101_A.xxx`,
  `CHR_101_A_C.xxx` and so on. The disc also has single packages for some
  DLC ids (110, 117, 212, 219). **[I]** The unused ids are candidates for
  new-wrestler slots (RE target 3), if the roster tables have room for them.
- **PS3 attire texture-replacement mod** ([topic 83040](https://smacktalks.org/forums/topic/83040-wwe-all-stars-texture-mod/),
  May 2026). The author reports a working attire texture swap on PS3, links
  a Discord and Reddit screenshots, and asked Nexus Mods for a game page. The
  method and tool are not published. The three linked Reddit posts were
  read through chrome-devtools on 2026-10-17
  ([1tmim28](https://www.reddit.com/r/WWEGames/comments/1tmim28/wwe_all_stars_custom_texture_mod/),
  [1tnenrm](https://www.reddit.com/r/WWEGames/comments/1tnenrm/wwe_all_stars_mods/),
  [1toykfw](https://www.reddit.com/r/WWEGames/comments/1toykfw/wwe_all_stars/)) [P].
  - The mods are attire retextures and recolours of the **PS3 version
    running on RPCS3**. The author says they don't know about the Xbox 360
    version.
  - The author promised a PDF guide and shares the mod files only on their
    Discord. No tool, method or format is posted, and the comments add
    none. The third post is a gameplay clip with nothing on modding.
  - This is the only report of package write-back. It is a PS3 cook, so it
    does not show that Xbox 360 packages can be written [I]. Asking the
    author how they repacked is the cheapest way to answer open question 5.
- **Texture dump** ([topic 83036](https://smacktalks.org/forums/topic/83036-wwe-all-stars-textures-dlcs/),
  May 2026). An archive of every wrestler texture ripped from Xbox 360,
  DLC included. It's game content: not downloaded, not linked here.
- **"Modding Characters on Xbox 360"** ([topic 77979](https://smacktalks.org/forums/topic/77979-modding-characters-on-xbox-360/),
  2022). The question was never answered, so no Xbox 360 character tool
  existed then.
- **"PS3 DLC PKG?"** (topic 78776). It only asks for DLC files and has no
  technical content.

## Content mods: DLC phases and roster slots

Ticket `.scratch/modding/issues/02-re-dlc-phases-and-roster-slots.md`,
2026-10-17. Tools:

- **Ghidra:** the MCP on project `WWEAllStarsTU2`, program `default.xexp`
  (`xex_entry` = `0x8241A7E0`, so TU2). Decompiles, xrefs, and
  `ghidra_eval` for vtables and strings.
- **`scripts/disasm.py`:** for `0x829EB380`–`0x829EB6DC`, where Ghidra's
  decompiler dropped the two `SetArray` calls as "unreachable".
- **Python helpers in the session scratchpad, not in the repo:**
  - an STFS reader (`.scratch/tu2-dlc/prototype/stfs.py`) on the player's
    Pack 1;
  - a port of the launcher's LZO and package-chunk code
    (`Lzo1x.cs`, `DiscShaders.cs`);
  - a small UE3 export-table and tagged-property reader, run on the disc's
    and Pack 1's `CombinedGlobalCharacters.xxx` and on TU2's `UI_SHELL.xxx`.

Every address is TU2. The names below are saved in the Ghidra project. No
game file was changed. Nothing was run.

### What the DLC installer reads

The installer is a pipeline of four steps. The chain starts in
`OnlineSubsystemLive`, whose vtable is at `0x82131010` [B].

1. **Mount each content: `OnlineSubsystemLive_ReadDLCContentStep`
   `0x829E3740`.**
   - For each enumerated marketplace content it calls `XamContentCreate` with
     the root `DLC%d` (`DLC_MakeRootName` `0x82B4AD70`, string `0x82215170`).
     The counter `g_DLCRootCounter` (`0x8373D4FC`) makes the roots `DLC0`,
     `DLC1`, and so on.
   - It appends one `OnlineContent` entry (stride `0x38`):
     - `+0x04` the display name;
     - `+0x10` the root;
     - `+0x1C` every package (`.xxx`), found by a **recursive** FindFiles
       (`0x826017D8`) under `DLCn:\`;
     - `+0x28` every other file;
     - `+0x34` the license mask that `XamContentCreate` returned.
   - The folder layout inside a content does not matter: `Phase1\` is only
     Pack 1's choice [B].
   - Then it calls `XamEnumerate` for the next content. After the last one
     it calls vtable `+0x1A4`.
2. **Pick the phase: `OnlineSubsystemLive_OnDLCReadComplete` `0x829EB118`
   (vtable `+0x1A4`).** For every content of the user:
   - **Which files it parses.** Every file whose name *contains*
     `Xe-Game.ini` (string `0x821972F8`, substring test `0x825F96E8`) is
     loaded into a scratch config. So `AI__Xe-Game.ini` is parsed too.
   - **`PhaseID`.** `[GameDLCManager] PhaseID` (`0x82197320`/`0x82197310`)
     is read as an int. Its string is added to a list, and the content with
     the largest `PhaseID` is remembered. The test is strict (`<`) and starts
     at 0, so a content without a `PhaseID`, or with 0, is never chosen. On a
     tie, the first content enumerated wins.
   - **`[AvailableCharacters]`** (`0x82197340`). For each `key=value` line,
     the key (`CharacterData_<id>`) is added to a list when
     `atoi(value) & content+0x34` (the license mask) is not 0
     (`0x829EB3C4`–`0x829EB430`, disasm).
   - **Writing the lists.** After the loop, `GConfig->SetArray` (vtable
     `+0x34`) writes `[GameDLCManager] DLCAvailableCharacters`
     (`0x821972C8`) and `InstalledPhases` (`0x821972A8`) into the game ini
     (`0x8370B540`) (`0x829EB5FC`, `0x829EB630`, disasm). So the list holds
     the `PhaseID` of every content. Pack 1's own `+InstalledPhases=1` line
     is merged later (step 3) and only adds a value that is already there.
   - **Installing.** Then it calls vtable `+0xCC` **once**, with the
     highest-`PhaseID` content only (`0x829EB6B0`).
3. **Install that one content: `OnlineSubsystemLive_InstallDLC`
   `0x829835A8` (vtable `+0xCC`)** calls `DLC_InstallDownloadableContent`
   `0x82A32A88` (UE3's `InstallDownloadableContent`). It:
   - **Packages.** Passes every package path of the content to the package
     cache (`0x836FD228` vtable `+0x18`). So a DLC package takes over the disc
     package of the same short name. Pack 1 does this with
     `CombinedGlobalCharacters.xxx` and `CHR_110_A.xxx`, which are both on
     the disc too [B][S].
   - **`DLCTOC.txt`** (`0x821A54CC`). Loads it and passes it to the file
     manager's `+0x70`, with the content folder as the prefix for `.\`, then
     to `+0x6C`, which merges the lines into the TOC map. A DLC file is read
     at the size this TOC gives (§2.2), so the TOC must list every file with
     its real size and, for whole-file LZO, its uncompressed size [B].
   - **`.ini` and `.<language>` files** (`0x821A54E4`, `0x82616C90`).
     - It drops everything up to and including the **first** `__` (`wcsstr`
       `0x82C36090` on the string `"__"` at `0x821A55B0`), so
       `AI__Xe-Game.ini` targets `Xe-Game.ini` and `A__B__Xe-Game.ini`
       targets `B__Xe-Game.ini`. Any number of fragments can target the same
       ini this way.
     - It takes a file as an ini when its extension is `ini`, and as a
       language file only when its extension is the running language's
       (`0x82616C90`), so a `.deu` fragment merges only in German [B].
     - It registers the target with a dummy `GConfig->SetString("DLCDummy",
       …)` (`0x821A54EC`), finds the cached file (`+0x58`), and `Combine`s
       the fragment into it (`0x82748150`).
     - Ini files resolve under the config folder (`0x826031E0`), and
       language files under the localisation folder [B].
   - **`[Engine.PackagesToFullyLoadForDLC]`** (`0x821A5510`). On a game
     engine, it reads the `MapName`/`Package` pairs and the
     `GameType_PreLoadClass`/`GameType_PostLoadClass` pairs, and adds them to
     the fully-load lists (`0x828D6610`). Pack 1 ships none [B][S].
   - **Reload.** It then flushes the config and calls
     `GameDLCManager_Reload(1)` `0x82B22CB0`.
4. **The DLC manager.** `g_GameDLCManager` (`0x837349A4`, 0x2C bytes,
   `GameDLCManager_New` `0x827638C8`).
   - `GameDLCManager_ReadConfig` `0x82B57788` reads the two arrays back into
     `+0x04` (`DLCAvailableCharacters`) and `+0x10` (`InstalledPhases`).
   - `+0x20` holds the folder of the `DLCTOC.txt` being merged.
   - `GameDLCManager_GetHighestPhase` `0x82B57880` returns
     `max(atoi(InstalledPhases))`.

Pack 1's `Xe-Game.ini` also has `AllAvailable=1`. Neither the code
(ASCII or UTF-16) nor TU2's `Game.xxx` and `Engine.xxx` name tables contain
`AllAvailable`, so the key is inert [B][S].

### `InstalledPhases` and `PhaseID`: how many phases, and a second phase

- **`InstalledPhases` is a derived list.** The game rebuilds it from each
  content's `PhaseID` (step 2) and doesn't trust the fragments'
  `+InstalledPhases` lines. It has no fixed size. The enumerator asks for 51
  items per call (`.scratch/tu2-dlc/issues/03-tu2-boots-and-plays.md`) and
  calls `XamEnumerate` again until it is done [B][S].
- **Readers of the highest phase.**
  - `GameDLCManager_SnapshotPhase` `0x82B57900` and
    `GameDLCManager_PhaseChanged` `0x82B57930` notice new content. Their
    callers at `0x82B96F10` and `0x82B96FC8` rebuild front-end state.
  - `0x82B158E8` passes `0xFFFF << highestPhase & 0xFFFF` to the online
    session code (`0x82B4A520`). This looks like a matchmaking filter that
    keeps players with different DLC phases apart [B; purpose I]. So a
    higher phase changes what online play sees. That is one more reason
    content mods stay out of online play.
- **A second phase can't sit next to Pack 1's as a separate content.**
  Only the highest-`PhaseID` content is installed.
  - **Phase 2 beside Pack 1.** A mod content with `PhaseID=2` beside Pack 1
    gets its packages, TOC and inis installed. Pack 1's don't, yet Pack 1's
    wrestlers stay in `DLCAvailableCharacters`, so they would show up with
    their packages missing.
  - **Phase 1 or lower.** A mod content with `PhaseID=1`, or one that sits
    below Pack 1, is never installed.

  The working assumption in the spec ("an extra DLC phase … separate from
  the player's DLC") therefore doesn't hold as written. The content the
  game installs must be a **superset**: the official pack's files plus the
  mod's files, under one `PhaseID` above the pack's [B].

### `[AvailableCharacters]`, the license mask and select-screen slots

- **The bit value is only a license gate.** A key's value is ANDed with
  its own content's license mask. The bit does not pick a slot. Pack 1's
  five bits (1, 2, 4, 8, 16) are its five purchasable offers [B][S].
- **The license mask in our build.** `XamContentCreate` returns the mask.
  The port opens marketplace content with `0xFFFFFFFF` when
  `marketplace_full_license` is on, its default
  (`patches/rexglue-content.patch`). So any non-zero value works for a mod
  [S].
- **The slot is the character data object.** `CharSelect_PopulateWrestlers`
  `0x82BA0670` fills the select screen in three passes:
  - `0x82B939C0`, the base roster (its filter is in blocks the decompiler
    drops; not decoded) [B, filter U];
  - `0x82B92A20`, created superstars;
  - `CharSelect_AddDLCWrestlers` `0x82B932E8`.
- **How the DLC pass matches names.** For every name in
  `DLCAvailableCharacters`:
  - `Roster_FindCharacterByName` `0x82B0A0A8` looks the name up by object
    name in `g_CharacterRoster` (`0x836FBD58`) `+0xA4`.
    `Roster_CollectCharacterData` `0x82B0B688` fills that array with every
    loaded `GameCharacterData` object (a walk over `GObjObjects`). It is a
    growable `TArray`, with no fixed cap.
  - On a match, the pass calls ActionScript `_global.AddWrestlerToUI`
    (`0x82223CA0`) with the object's name strings and a byte at `+0xA7`.
  - A name with no loaded object is skipped silently [B].
- **The select-screen movie.** It is `fe_characterSelect`, in TU2's
  `UI_SHELL.xxx`.
  - **The grid.** It has 32 thumbnail cells, `c00`–`c151` (16 columns, 2
    rows). Two of them open the created-superstar and DLC sub-selections
    (`EnterDLCSelection`, `PickAltCharSelection`).
  - **DLC wrestlers** aren't grid cells. They sit in a pushed array, so
    their count isn't fixed by the grid [S, read from the movie's strings;
    I for the behaviour].
  - **DLC portraits** come from `Shared_Characters_DLC.xxx`
    (`Character_Art_DLC.swf`), which has one frame per DLC code name: 13
    names, more than Pack 1's five. A new wrestler needs a frame there too
    [S].

### The unused superstar ids, and what a character entry needs

The character data lives in **`CombinedGlobalCharacters.xxx`**, not in
`GameCharacterData.xxx`: the disc's `GameCharacterData*.xxx` are empty
32 KB packages, with 0 names and 0 exports. The objects are
`GameCharacterData.CharacterData_<id>`. Because UE3 stores the number as an
FName suffix, the string `CharacterData_110` never appears in the file.

Both the disc's and Pack 1's copies hold **60 entries**, ids 101–130 and
201–230, plus 5 `CharacterData_CAP_*` and 4 `CharacterData_OLCAP_*` [S].
Pack 1's copy is 13,899,615 bytes against the disc's 13,413,618. It
replaces the disc's copy through the package cache.

| Id group | Ids | State |
|---|---|---|
| Placeholder (`CharacterName` = `Undefined`, stub asset references) | 120, 125, 127, 128, 129, 130, 215, 217, 228, 229, 230 | 23–34 properties |
| Complete data, `bCT_DLC=1`, not in Pack 1 | 121, 122, 123, 124, 126, 210, 221, 226 | 33–36 properties; their art is in `Character_Art_DLC.swf` |
| Named, but on the community "No Slot" list | 109, 111, 209, 223, 225 | 25–31 properties |
| No object | 200 | none |

So the community's gaps can be used, and there are 11 true placeholders. An
id that has no object (200) or lies outside 101–130/201–230 would need a new
export, which is harder [S].

A character entry needs:

1. **The `GameCharacterData` export,** in a `CombinedGlobalCharacters.xxx`
   that the DLC content carries.
   - Properties seen: `CharacterName`, `CodeName`, `CharacterCode`
     (`CHR<id>`), `IntroCinemaLevelName` (`Intro_CHR<id>`), `ClassType`,
     the six stat ints, `Scale`, the `bLS_Legend`/`bCT_Normal`/`bCT_DLC`
     flags, `CostumesArray`, `CharacterPortraits`, and the `LegendId`
     struct.
   - About a dozen `SD_StringAssetReference` sub-objects, each a string
     `m_AssetName` such as `AnimSet_<Name>.AnimSet_<Name>_AnimSet`: skeletal
     mesh, physics mesh, mesh tree, bruises, anim set, finisher anim set,
     anim FSM, AI FSM, momentum, audio sets.
   - Filling a placeholder means rewriting these string properties, which
     changes the export's size. That needs a package writer (§3.5): no
     community tool does this for the Xbox 360.
   - Bool properties are 4 bytes in this package version (398), which a
     writer must respect [S].
2. **The packages the strings name.** Pack 1's per-wrestler set is the
   model: `CHR_<id>_A[_C]`, `AnimSet_<Name>[_FIN]`, `AnimFSM_<Name>[_0..3]`,
   `Audio_Set_CHR<id>` and the commentary, chant, victory and intro audio,
   `HUDPortraits<id>`, `Intro_CHR<id>`, `Cin_Intro_CHR<id>`, and
   `<id>_JumboTron.bik` [S].
3. **Ini fragments.**
   - **`Xe-Game.ini`:** `[AvailableCharacters] CharacterData_<id>=<bit>`
     and the content's `[GameDLCManager] PhaseID`.
   - **`AI__Xe-Game.ini`:** `[AI.Variables.Character.<Name>]`.
   - **`Xe-Engine.ini` entries** for packages loaded by name. TU2 registers
     every DLC wrestler's `AnimList_*`, `AnimSet_*_FIN`, chants and
     commentary there (`docs/research/title-updates-and-dlc.md`). A
     `<tag>__Xe-Engine.ini` fragment is merged by the same `Combine` path.
     Pack 1 itself ships only `Xe-Game.ini` fragments. Ticket 15 ran both
     untested paths in the game [R]: a mod's `T15__Xe-Engine.ini` with
     `[DLCFrontEndPackages] +package=CHR_110_A` made the game open
     `DLC1:\CHR_110_A.xxx` right after `Shared_Characters_DLC.xxx` at the
     main menu (that section is read by `0x82B95B08`/`0x82B94668` after the
     install), and a `T15__Screens.int` with `[MainMenu] Exhibition=T15
     FRAGMENT` showed "T15 FRAGMENT" as the main menu's first entry. The
     `.int` file is listed in `DLCTOC.txt`; the inis are not.
4. **A select-screen name and portrait.** Both are keyed by `CodeName`: a
   `[CharacterNames] <CodeName>_Name` text and a `<CodeName>_Large` movie
   in a loaded package such as `Shared_Characters_DLC.xxx` (ticket 18,
   below). There are no "frames" per code name.

### Does a content mod need an official pack?

- **No official pack is needed.** The installer accepts any content with a
  `PhaseID` ≥ 1. The disc's `CombinedGlobalCharacters.xxx` already has every
  slot, so a mod can derive its copy from the disc.
- **TU2 is needed:** it turns DLC loading on (`[DLCSettings]`) and holds the
  `Shared_Characters_DLC` front-end registration [B][S].
- **Pack 1's phase number doesn't have to be free.** It has to be
  *exceeded*. With Pack 1 installed, the mod content must use `PhaseID` ≥ 2
  and must also carry Pack 1's files, its `AvailableCharacters` lines and
  its AI fragment.
- **Pack 1 and a mod both ship `CombinedGlobalCharacters.xxx`.** The two
  copies must be merged at the export level: Pack 1's entries plus the
  mod's edited placeholder.

### Recommended content-mod layout

A content mod is workable, but only through **one synthetic DLC content**
that the launcher builds at Play. It can't be an extra phase beside the
player's packs.

- **In the mod: `content/`.** It holds the DLC-style files, in any folder
  layout:
  - the new packages;
  - its roster entries in `roster.toml`, which the launcher writes into
    `CombinedGlobalCharacters.xxx` (format notes below, ticket 16);
  - its `Shared_Characters_DLC.xxx` with the portrait movie, or (planned,
    ticket 21) a roster entry's portrait option (ticket 18);
  - its `<anything>__CharacterNames.int` with the wrestler's name (ticket 18);
  - ini fragments named `<anything>__Xe-Game.ini`,
    `<anything>__Xe-Engine.ini` and `<anything>__<File>.int`.

  A mod author never writes `DLCTOC.txt` or `PhaseID`.
- **Where it installs.** At Play, offline only, with at least one content
  mod on, the launcher builds `userdata/0000000000000000/54510866/00000002/<fixed
  synthetic name>/`, plus its `Headers/00000002/<name>.header`. This is the
  folder `DlcLibrary` already writes, with the same header and a full
  license mask. The folder holds:
  - hard links to every file of each installed official pack;
  - each content mod's files, in load order (later wins);
  - one `Xe-Game.ini` with `[GameDLCManager] PhaseID=<highest official phase
    + 1>` and the union of every `[AvailableCharacters]` line;
  - every other fragment renamed `<mod>__<ini>`, so that they all merge;
  - a generated `DLCTOC.txt` with sizes and the uncompressed size of
    whole-file LZO packages, as in the overlay's `PatchTOC.txt`.

  The player's real pack folders are never written. The game still mounts
  them: they add their characters, but they don't win the install. The
  launcher deletes the synthetic folder when no content mod is on, or when
  online play is on.
- **Conflicts.**
  - `CombinedGlobalCharacters.xxx` and `Shared_Characters_DLC.xxx` are one
    file each for the whole roster. Until an export-level merge exists, two
    content mods that both ship them conflict (later wins), and any mod that
    ships them must already include Pack 1's entries.
  - Two mods claiming the same `CharacterData_<id>` conflict too.
- **Open before ticket 10's code** (needs a run, or the owner):
  1. A test content with only `PhaseID=2` and the Pack 1 files hard-linked,
     to check that the synthetic content loads the five wrestlers as before.
     Run in Spike 11 (below): the game installs it in place of Pack 1. The
     select-screen look is left to the owner (ticket 13).
  2. One placeholder flipped through a patched `CombinedGlobalCharacters.xxx`
     plus a `CharacterData_<id>` line, to see whether it appears in the DLC
     sub-selection and what a missing portrait frame looks like.
  3. A package writer that can rewrite string properties in one export,
     plus the `Shared_Characters_DLC` movie edit. Without these, content
     mods depend on outside tools. Today no Xbox 360 tool for them is known
     (§4.1).

### Spike 11: a relink-only combined content (2026-10-17, ticket `modding/11`)

**Setup.** Everything sat in a scratch user-data root in the session
scratchpad; the repo's game data, `updatedata/` and the owner's DLC were only
read.

- Pack 1 was installed there by the real launcher code:
  `WWEAllStarsLauncher --root <scratch> --add-dlc dlc/F5B3…D754`. It gives
  111 files in `Phase1\` and a full-mask header.
- `.scratch/modding/prototype/spike11_combined.py` built the combined
  content beside it:
  - hard links to every Pack 1 file except `Xe-Game.ini` and `DLCTOC.txt`
    (`AI__Xe-Game.ini` kept under its name);
  - a new `Xe-Game.ini` with only `[GameDLCManager] PhaseID=2` and Pack 1's
    five `[AvailableCharacters]` lines;
  - a `DLCTOC.txt` generated from the files (`size unc 0 .\name 0`, CRLF).
    `unc` is the header's uncompressed size (offset 12, LE) for a `.xxx`
    whose first bytes are `C1 83 2A 9E` (only `CombinedGlobalCharacters.xxx`:
    `3342336 13899615`), and 0 otherwise;
  - a `.header` written like `DlcLibrary`'s (full licence mask).
- The game was the built `wweallstars.exe` with session.ps1's arguments,
  `--user_data_root=<scratch>` and `--log_file_opens=:` (every guest path).
  It ran about 80 s: title, START, A at the AUTOSAVE notice, main menu
  (the menu-loop movie opens). It was closed with the window's close.

**Runs.**

| Run | Contents in `00000002\` (listing order) | Mounted | Installed from (`DLCTOC.txt`, the inis, the 4 install-time packages) |
|---|---|---|---|
| Baseline | Pack 1 | `DLC0` = Pack 1 | `DLC0:\Phase1\` |
| Combined after Pack 1 | Pack 1, `ZZSPIKE11COMBINED` (`Phase1\` layout) | `DLC0` = Pack 1, `DLC1` = combined | `DLC1:\Phase1\` |
| Combined before Pack 1 | `00SPIKE11COMBINED` (flat, no `Phase1\`), Pack 1 | `DLC0` = combined, `DLC1` = Pack 1 | `DLC0:\` |
| After removal | Pack 1 | `DLC0` = Pack 1 | `DLC0:\Phase1\` |

The four install-time packages are `CombinedGlobalCharacters.xxx`,
`Shared_Characters_DLC.xxx`, `AnimSet_Base_Template_DLC.xxx` and
`AnimFModEventList.xxx`.

- **Pack 1's root.** The game reads only Pack 1's `Xe-Game.ini` and
  `AI__Xe-Game.ini` there: the `PhaseID` scan of step 2. It opens no
  `DLCTOC.txt` and no package from Pack 1's root.
- **The combined root.** The game opens its two inis twice (once in the
  scan, once at install), then `DLCTOC.txt` and the four packages. After
  that, the opens match the baseline's, apart from the randomly picked menu-loop movie.
- **The log.** It shows no TOC, package or file error. The content log is
  `XamContentCreate: root DLCn … license FFFFFFFF` for both contents.

**Answers.**

- **The game installs the combined content in place of Pack 1.** Pack 1's
  `CombinedGlobalCharacters.xxx` comes from the combined root, never from
  Pack 1's own. This confirms the design.
- **Sizes.** Every line of the generated TOC is the file's real size. 107 of
  its 108 lines are byte-identical to lines of Pack 1's own `DLCTOC.txt`,
  `CombinedGlobalCharacters.xxx`'s included. Pack 1's own TOC has 113 lines:
  - five `AnimFSM_<Name>.xxx` lines for files that aren't in the package;
  - one `CHR_204_A_C.xxx` line, while the package holds the file as
    `CHR_204_A_C..xxx`. Our extraction and Canary's agree on that name, so
    it is the package's own.

  The generated TOC lists the files that are there instead. Nothing the
  game opened is missing from it.
- **The folder name doesn't matter.** Neither does the listing order: the
  SDK lists `00000002\` alphabetically (`ContentManager::ListContent`), and
  the higher `PhaseID` wins in either order.
- **`Phase1\` is optional.** A flat content works the same. `.\` in
  `DLCTOC.txt` resolves against the folder that holds the TOC.
- **Not yet shown by the log: the five wrestlers on the select screen.** The
  roster list isn't logged. The character packages (`CHR_110_A.xxx` and
  the rest) open only when a wrestler is picked. Ticket 13's owner session
  covers this: it has the same combined content staged and asks "Are Pack
  1's wrestlers unchanged?". Ticket 11 has the steps for a standalone look.
- **Clean-up.** The combined content and its header were removed. Pack 1's
  folder and header hash the same before and after: 112 SHA-256 lines,
  `pack1-before/after.sha256` in the scratch folder.

### Spike 12: writing one roster entry (2026-10-17)

Ticket `.scratch/modding/issues/12-spike-roster-entry-writer.md`. A scratch
Python writer (session scratchpad, not in the repo) unpacked the disc's
`CombinedGlobalCharacters.xxx`, filled placeholder 120 from template 110 (every
string and sub-object copied, `CharacterName` changed to a test name, 120's
own `LegendId` kept), and wrote the package back. Runs used the built exe,
read-only, through a scratch update folder with a `PatchTOC.txt` line (as in
§2.3), up to the title screen, closed with the window's close.

**Package layout (the unpacked file).** Summary (105 bytes, nothing after the
empty chunk table), name table, import table, export table, then every export
body, back to back in export order to the end of the file. There are no gaps,
and `TotalHeaderSize` (offset 8) is the first body's offset. 5,821 names,
473 imports, 18,175 exports, one generation entry `{exports, names, 0}`.
Export entries are 68–104 bytes: 40 fixed bytes, then a component map
(count × 12), ExportFlags, a net-object count with its array, and a GUID.
There is no package-flags field after the GUID [B: Python walk].

**One roster entry is several exports.**

- `GameCharacterData.CharacterData_<id>`: the tagged properties.
- One `SD_StringAssetReference` export per asset reference, with the entry
  as Outer (`CharacterData_<id>.SD_StringAssetReference_<n>`). Each body is a
  single property, `m_AssetName` (`StrProperty`), then `None`. The entry
  refers to them by export index through `ObjectProperty` values, including
  inside the `CostumesArray[].Model` and `CharacterPortraits[].PortraitTexture`
  struct arrays.
- A complete DLC entry (110, 121) has 19 sub-objects. A placeholder has 14:
  it lacks the five audio ones (`AudioAsset`, `AudioCommentaryAsset`,
  `AudioVictoryMusicAsset`, `AudioCrowdChantAsset`, `AudioGruntAsset`), and
  the `CodeName`, `bCT_Normal` and `bCT_DLC` properties. Filling a
  placeholder therefore means **adding five exports**. The spike appended
  them to the export table, Outer = the entry, ObjectName =
  `SD_StringAssetReference` with an unused number suffix, flags copied from
  the template's sub-object [B].

**Strings, not names.** `CharacterName`, `CodeName`, `CharacterCode`,
`IntroCinemaLevelName` and every `m_AssetName` are `StrProperty`. `ClassType`
is a 1-byte `ByteProperty`, not an enum name. Every property name and type
name a complete entry uses is already in the table, and a new sub-object
reuses the `SD_StringAssetReference` name with a new number. So **filling a
placeholder adds no names**. If a later case needs one, appending it to the
end of the name table keeps every existing index valid and shifts the bodies
like any other growth [B].

**The fix-ups a size change needs.**

1. The grown export's `SerialSize` and every later export's `SerialOffset`.
2. Summary: `NameCount`/`NameOffset`, `ExportCount`/`ExportOffset`,
   `ImportOffset`, `TotalHeaderSize`, and the generation entry's export and
   name counts.
3. **Absolute file offsets inside export bodies.** These are found by a
   whole-file scan for i32 fields that point inside their own body, then
   checked by structure:
   - inline bulk data, `{flags, count, sizeOnDisk, offsetInFile}`, where
     `offsetInFile` equals the field's own position + 4: 108 fields, in 17
     `Texture2D`, 4 `SkeletalMesh`, 2 `StaticMesh` and 1 `FmodEventFile`;
   - the `SeekFreeShaderCache` export: each of the 772 shader records' end
     offset (`+0x18`, `shader-containers-from-disc.md`), and after the
     records, a count of material shader maps, each `{GUID, SkipOffset}`,
     with 28 skip offsets.

   The other hits of the scan are data that happens to match: anim-script
   strings, texture blocks, and runs of object indices in the
   `ObjectReferencer`. They point outside any structure, so the writer must
   not touch them [B].

The writer rebuilt the file in the original order, put the five new bodies
after the last one, and moved all 18,160 bodies by +340 bytes, the export
table's growth. It then fixed the 908 offsets. A check walk confirmed that
names and imports were unchanged and that every untouched body was identical
except at those offsets. The changed bodies were the entry and five rewritten
sub-objects, since nine sub-object strings already matched the template's [B].

**Runs** (title screen = the `START_LOOP_LOGO` movie opened and a screenshot
of the title screen):

| Case | TOC line | Result |
|---|---|---|
| Unedited round trip, uncompressed (byte-identical to the unpacked disc copy) | `13413618 0 0 .\CombinedGlobalCharacters.xxx 0` | title screen, 46 package opens, file `UPDATE:\` |
| Edited, all offsets fixed, uncompressed | `13414685 0 0 …` | title screen, same opens |
| Edited, all offsets fixed, whole-file LZO | `13468543 13414685 0 …` | title screen, same opens |
| Edited, shader cache fixed, bulk offsets not | `13414685 0 0 …` | title screen |
| Edited, bulk fixed, **shader cache not** | `13414685 0 0 …` | **stuck on the boot logo**, 32 opens, the last is `UPDATE:\CombinedGlobalCharacters.xxx` |

The sets of opened files are the same across the passing runs, except the
front-end music package, which the game picks at random. No new warning
appears, and the log has no error line. The failing control shows that the
game parses the package at boot and that a broken one hangs. It gives no log
line, so "reached the title screen" is the test [B].

**Answers.**

- **Strings or names:** strings throughout. No name-table change is needed.
- **Sub-objects:** separate exports. A filled placeholder rewrites the entry
  and up to 14 sub-object bodies, and adds five exports.
- **Compression:** not needed. The game loads the written package
  uncompressed with TOC uncompressed size 0. The whole-file LZO form also
  loads. The spike's LZO writer emits literal-only LZO1X blocks (valid
  streams, no compression), so the launcher needs no LZO compressor.
- **Smallest set:** the writer starts from a **complete template entry**
  (all its properties and its 19 sub-objects) and keeps the slot's own
  `LegendId` (`CharacterIdentifier` with one bool `b<id-100>`/`b<id-200>`,
  per-slot). The mod gives `CharacterName`, `CodeName` (the portrait frame
  key), `CharacterCode`, `IntroCinemaLevelName` and the asset names (costume
  model, mesh tree, anim set, finisher anim set, anim FSM, the five audio
  assets). It may give `ClassType`, the six stats and `Scale`. Bruise,
  physics, skeleton, AI FSM, momentum and the generic HUD portraits can come
  from the template. `bCT_DLC=1`/`bCT_Normal=0` come from a DLC template
  such as 110. The game does not derive `CharacterCode` or the intro level
  from the id for roster entries. `CHR%02d` and `Intro_CHR%02d`
  (`0x822042F0`, `0x82204300`) are only formatted by the created-superstar
  builders `0x82B09D68` and `0x82B0A848`, so the writer must store them
  [B: decompile].
- **Not checked here:** whether the filled entry appears and plays. That
  needs the select screen (ticket 13).

### Owner check 13: a filled placeholder on the select screen (2026-10-17)

Placeholder 120 filled from 110 in Pack 1's `CombinedGlobalCharacters.xxx`
(only `CharacterName` changed), inside a combined content at `PhaseID=2`, with
`CharacterData_120=1`. The DLC sub-list showed it as a sixth wrestler beside
Pack 1's five; it played an exhibition match; Pack 1 was unchanged. It was
labelled "Ted DiBiase" with his portrait, so the shown name and the portrait
do not come from `CharacterName` but from the fields cloned from 110
(`CodeName` and/or `CharacterCode`) [owner check, ticket 13].

### Ticket 18: a new wrestler's name and portrait (2026-10-17)

Ticket `.scratch/modding/issues/18-portrait-for-a-new-wrestler.md`. Method:
Ghidra on TU2 (`CharSelect_AddDLCWrestlers` and the SD_UI GFx glue), a
scratch AS2 disassembler over TU2's `UI_SHELL.xxx` and `fe_templates.xxx`
movies and Pack 1's `Shared_Characters_DLC.xxx`, and one headless run.
Scratch tools (not in the repo) are in the session scratchpad, `t18\`.

**One field keys both: `CodeName`.**

- **The DLC pass** (`0x82B932E8`) calls `_global.AddWrestlerToUI`, whose
  ActionScript signature is `(DisplayName, CodeName, FightStyle,
  NumCostumes, DefCostume, CreatedPlayer, Locked, Unselectable, IsDLC,
  HasLegendsBadge, HasSuperstarsBadge, HasTagTeamBadge)`
  (`fe_characterSelect`). It passes:
  - `DisplayName` = `Localize("CharacterNames", <CodeName> + "_Name",
    "CharacterNames")` (`0x82600608`, suffix `_Name` at `0x82200B48`,
    section and package `0x82200938`; disasm `0x82B93434`–`0x82B93490`);
  - `CodeName` = the entry's `FString` at `+0x124`;
  - `FightStyle` = the byte at `+0xA7` (`ClassType`);
  - the three badges from `0x82BE49E0(…, CodeName, 0|1|2)` [B].

  `+0x124` is `CodeName`: the `CharacterNames` keys are code names
  (`TedDibiase_Name = TED DIBIASE` in TU2's `Coalesced.int`, which explains
  ticket 13's label), and neither `CharacterName` ("Million Dollar Man")
  nor `CharacterCode` ("CHR110") is a key. `CharacterCode` is at `+0x168`
  (`CHR%02d`, `0x82B09D68`) [B][S].
- **The portrait.** Hovering a wrestler calls the portrait component's
  `SwitchCharacter(CodeName)` (`CharacterSelect_Large` in TU2's
  `fe_templates.xxx`), which sets the name with the same `Localize` and
  calls `loadMovie("../Shared_Characters/" + CodeName + "_Large.swf")`. The
  DLC sub-list (`CharacterSelect_DLC_Large`) is a text list of
  `DisplayName`s, with no thumbnails [S: AS2 disassembly].
- **How the movie is found.** The SD_UI file opener (`0x825C6ED8`, GFx state
  10, vtable `0x8208CF80`) turns the URL into `Shared_Characters.<CodeName>_Large`
  (`0x825C4D58`), then tries `FindObject` with the movie class, then
  `FindObject` with `ANY_PACKAGE`, then a package load. Pack 1's movies sit
  at the top of `Shared_Characters_DLC` (no `Shared_Characters` outer), so
  they are found by the any-package step, from any loaded package [B; I for
  the name match, shown by Pack 1 working].
- **Pack 1's `Shared_Characters_DLC.xxx`** (uncompressed, zero-padded to
  1 MB): 13 `<CodeName>_Large` `SD_UIMovieData` exports (one `RawData`
  byte array each, a ~200-byte GFx movie), `Character_Art_DLC` (12
  `DefineExternalImage` bitmaps on `Texture2D` exports, and 13 exported
  sprites with their own linkage names such as `DIBIASE`, `HAWK_`), the 13
  textures, and an `SD_UIGCReferencer` whose `ReferencedObjects` lists
  every export. Each `_Large` movie only imports one sprite from
  `../Shared_Characters_DLC/Character_Art_DLC.swf` and places it [S].
  `[DLCFrontEndPackages]` (TU2's `Xe-Engine.ini`) loads it: every value of
  the section is passed to `LoadPackage` (`0x82B94668` → `0x824F1A80`) [B].
- **`CodeName` must be unique.** `0x82B09BA0` returns the *first* roster
  entry whose `CodeName` matches (case-insensitive); 14 front-end functions
  call it, the select screen among them (the pick is sent back as
  `Character_<n>_Select` with the code name). Two entries with one code name
  are one wrestler to the UI: ticket 13's copy was a second "Ted DiBiase",
  and picking it may well have picked 110 [B].

**What this means for a new wrestler.** "Point the entry at an existing
frame" (reuse a code name) is not a route: the label and the badges follow
the code name too, and the pick resolves to the other entry. A new wrestler
needs its own `CodeName`, and then:

1. **A name:** `[CharacterNames] <CodeName>_Name=` (and `_Short=`) in a
   `<anything>__CharacterNames.int` fragment. Checked in the run below.
2. **A portrait:** a `<CodeName>_Large` movie in a package loaded at the
   front end. The smallest form is a clone of an existing `_Large` export
   under the new name: same `RawData`, so it shows that wrestler's art.
   The scratch writer (`t18\tools\portrait.py`, on spike 12's `upk.py`)
   appends the name `<CodeName>_Large`, appends the export (entry cloned,
   new name), appends its index to the referencer's `ReferencedObjects`,
   and re-lays the package: 27 bodies moved, 26 texture bulk offsets fixed,
   size unchanged inside the 1 MB padding. Untouched bodies are identical
   except those offsets. A custom picture needs a new `Texture2D` and a new
   sprite in `Character_Art_DLC` (or a self-contained movie): a GFx edit
   of the 2 KB movie plus a texture export, an estimated few days with a
   DXT encoder, so out of scope here.
3. **No portrait:** what the select screen shows for a code name with no
   `_Large` movie is left to the owner check in ticket 18.

**Headless run** (`t18\run.ps1 -Sanity 75`; scratch user data with Pack 1
hard-linked and a combined content `ZZTICKET18COMBINED` at `PhaseID=2`):
placeholder 120 filled from 110 as `CharacterName` "Spike Eighteen",
`CodeName` `SpikeEighteen`; 125 as "Spike No Portrait", `SpikeNoPortrait`
(Pack 1's 125 lacks seven sub-objects of 110's, one more than ticket 12's
five: its costume's `CineModel` and `MeshTree`); the edited
`Shared_Characters_DLC.xxx` with `SpikeEighteen_Large` cloned from
`TedDibiase_Large`; `T18__CharacterNames.int` with both names and
`T18__Screens.int` setting `[MainMenu] Exhibition=T18 INT FRAGMENT`.

- After START, the game opens `DLC1:\Phase1\T18__CharacterNames.int` and
  `T18__Screens.int`, then the edited `CombinedGlobalCharacters.xxx` and
  `Shared_Characters_DLC.xxx`. The log has no error.
- The main menu's first row reads **T18 INT FRAGMENT**: a `.int` fragment in
  a DLC content is combined into the game's localisation, and the main menu
  (built after the install at sign-in) uses it, as ticket 15 found. Here the
  fragments had no `DLCTOC.txt` line, so a `.int` doesn't need one
  [B: run, screenshot].
- The select screen is left to the owner check in ticket 18.

### Format notes: a new wrestler's name and portrait

For a mod author (and for ticket 16's roster-entry file):

- Give the entry its own **`CodeName`**: letters and digits, unique across
  the disc roster, the official packs and every other mod in use (the game
  takes the first match). It is the key for the name and the portrait. The
  launcher checks it (ticket 21): an entry with no `CodeName` (it would keep
  the template's), a bad one, one used twice in the file, or one the roster
  base already has (the highest pack's copy, else the disc's; case ignored)
  keeps the mod off. Two mods in use giving one `CodeName` in different
  slots are a conflict: the later mod's entry is written, the earlier left
  out.
- **The name** is written by the launcher: for each entry it adds
  `<CodeName>_Name` and `<CodeName>_Short`, both the `CharacterName` in
  capitals, in a `M<n>_Roster__CharacterNames.<lang>` fragment for every
  language TU2 ships (the installer merges only the running language's).
  To give other text, ship your own fragment, such as
  **`<anything>__CharacterNames.int`** in `content/`; a language whose
  fragment sets `<CodeName>_Name` gets no generated line:

  ```
  [CharacterNames]
  MyWrestler_Name=MY WRESTLER
  MyWrestler_Short=WRESTLER
  ```

  The select screen shows `_Name`; the game's own names are upper case.
- **No portrait is needed** (owner check 18): DLC wrestlers show no
  portrait image, and a code name with no `<CodeName>_Large` movie breaks
  nothing. The borrowed-portrait option once planned for ticket 21 was
  dropped. A mod can still ship a whole `Shared_Characters_DLC.xxx` (one
  file for the whole roster, ticket 17's conflict), to no visible effect.

### Format notes: the roster-entry file (ticket 16)

A content mod fills placeholder slots with one text file,
**`content/roster.toml`**. The launcher writes it into the combined content's
`CombinedGlobalCharacters.xxx` at Play (`RosterWriter`, a port of Spike 12's
writer that gives the same bytes for the same edit). Mod authors never ship
or edit that package for a new wrestler. The file itself is not copied into
the content.

```toml
# One section per placeholder slot the mod fills.
[120]
template = 110                    # the complete entry copied; 110 when left out
CharacterName = "Spike Twelve"    # required
CodeName = "SpikeTwelve"          # required, unique: the select screen's label and pick key (ticket 18)
CharacterCode = "CHR120"
IntroCinemaLevelName = "Intro_CHR120"
ClassType = 3
nHealth = 90
Scale = 1.02
CostumesArray.0.Model = "CHR_120_A.Meshes.CHR_120_A"
MeshTreeAsset = "CHR_120_A.MeshTree.Cap_MeshTree"
AnimSetAsset = "AnimSet_Spike_Twelve.AnimSet_Spike_Twelve_AnimSet"
AnimSetFinisherAsset = "AnimSet_Spike_Twelve_FIN.AnimSet_Spike_Twelve_FIN_AnimSet"
AnimFSMAsset = "AnimFSM_Spike_Twelve.AnimFSM_Spike_Twelve"
AudioAsset = "Audio_Set_CHR120.Audio_Set_CHR120"
```

- **Syntax.** `[<id>]` starts an entry, then `key = value` lines. A string
  is quoted (printable ASCII, no `"` or `\`); a number is bare. `#` starts a
  comment.
- **Slots.** Only the 11 placeholders are accepted: 120, 125, 127, 128, 129,
  130, 215, 217, 228, 229, 230.
- **`template`.** A complete entry, 101–130 or 201–230, not a placeholder.
  Its asset references come along (19 for 110 and 121, 23–33 for the main
  roster), so pick one whose packages the player has: a main-roster wrestler
  has them all on the disc; 110 is meant to play from Pack 1's (ticket 23, below). `bCT_DLC`
  doesn't decide the DLC list. Every property the file
  doesn't set is the template's, except the slot's own `LegendId`, which the
  writer keeps. Asset references the placeholder lacks (the five audio ones)
  are added as new sub-objects of the slot.
- **Keys.** The writer's property set is fixed, so a mod can't add a
  property. It can set:
  - strings: `CharacterName`, `CodeName`, `CharacterCode`,
    `IntroCinemaLevelName`;
  - whole numbers: `nHealth`, `nStun`, `nPower`, `nStamina`, `nGrappling`,
    `nAgility` (0 or more), `ClassType` (0–255);
  - numbers above 0: `Scale`, `broadPhaseRadius`;
  - asset names (`"Package.Object"`, with groups between):
    `SkeletalMeshAsset`, `PhysicsMeshAsset`, `MeshTreeAsset`,
    `BruiseResources`, `AnimSetAsset`, `AnimSetFinisherAsset`,
    `AnimFSMAsset`, `AIFSMAsset`, `MomentumData`, `AudioAsset`,
    `AudioCommentaryAsset`, `AudioVictoryMusicAsset`, `AudioCrowdChantAsset`,
    `AudioGruntAsset`, `CostumesArray.<n>.Model` and
    `CharacterPortraits.<0–3>.PortraitTexture`.

  Spike 12 settled what a new wrestler sets: the names, the codes, the intro
  level and the asset names. The game doesn't derive `CharacterCode` or the
  intro level from the id. The shown label and portrait follow `CodeName`, not
  `CharacterName` (owner check 13, ticket 18). A new wrestler needs its own
  `CodeName`; the launcher writes its name: "Format notes: a new wrestler's
  name and portrait" above.
- **Problems that keep the mod off.** Any other id, a slot given twice, an
  unknown key, a key set twice, a bad value, a bad template, a line that isn't
  `[id]` or `key = value`, an entry with no `CharacterName` or no `CodeName`,
  a `CodeName` that isn't letters and digits, is used twice in the file or is
  already in the roster base, a file with no entry. Play itself stops when the template lacks a property the file sets.
- **What Play does.** The base is the `CombinedGlobalCharacters.xxx` the
  combined content would carry anyway: the highest official pack's (Pack 1's
  has 36 more asset references than the disc's), a content mod's whole copy,
  or else the disc's. Every content mod's entries go into it in load order,
  and a later mod wins a slot. The package is written uncompressed (TOC
  uncompressed size 0), and each slot adds `CharacterData_<id>=1` to the
  generated `Xe-Game.ini`. The load report has one line per slot:
  `roster: CharacterData_120 filled by <mod> <version> (<folder>)`.
- **Checked in the game** (ticket 16): slot 120 written from Pack 1's copy by
  the real `PrepareForPlay` (13,902,582 bytes, uncompressed). The game mounted
  the combined content, opened the written package from it, and reached the
  main menu with no package error [B].

**Owner check 18 (2026-10-17).** Both test wrestlers listed under their own
names and played a match against each other; Pack 1 unchanged. DLC
wrestlers have no portrait image in the sub-list, and the picked wrestler's
3D render works, so a missing `_Large` movie breaks nothing: new wrestlers
need a unique `CodeName` and a name line, not a portrait [owner check].

### Ticket 23: a template from the disc (2026-10-17)

Can a content mod fill a DLC slot from a main-roster wrestler, whose packages
are all on the disc, so it needs no official pack?

- **Which templates the disc covers.** A scratch walk of the disc's
  `CombinedGlobalCharacters.xxx` (each complete entry's `m_AssetName`
  packages and intro level against `CookedXenon\*.xxx`): every wrestler of the
  main roster (101–108, 112–115, 118, 119, 201–208, 211, 213, 214, 216, 218,
  220, 222, 224) has all his packages on the disc. The only names missing are
  `Audio_SFX_Grunts_01`–`04`, which every entry names, 110 included, and which
  no disc file has; the game plays the disc roster without them.
  The disc's 110 entry names only packages the disc also has, but they are
  not Pack 1's: `AnimSet_Million_Dollar_Man.xxx` is 2,621,440 bytes on the
  disc and 3,866,624 in Pack 1, `Intro_CHR110.xxx` 2,818,048 and 4,194,304,
  and Pack 1 adds `CHR_110_A_C` and `HUDPortraits110`. The game never offers
  110 without Pack 1, so those disc copies are untested. 117, 210, 212 and 219
  lack some of theirs; 121–124, 221 and 226 lack most; 109, 111, 116, 209,
  223, 225 and 227 are unused entries whose packages no disc file has
  [B: scratch walk].
- **The DLC list ignores `bCT_DLC`.** `CharSelect_AddDLCWrestlers`
  (`0x82B932E8`) walks the `[AvailableCharacters]` names, finds each entry by
  name and calls `AddWrestlerToUI` with `IsDLC` fixed to 1 and a constant
  costume count; it reads no bCT flag. The main grid (`0x82B92A20`) walks its
  own code-name list (screen `+0x25C`), not the roster. The flags are the bits
  `0x80000000` (`bCT_Normal`) and `0x40000000` (`bCT_DLC`) of entry `+0x13C`;
  `0x82BD1E18` and `0x82BD1FC0` turn them into a type 0/1/2 that only the
  match-result and challenge tracking (`0x82BE5D30`, `0x82AE9738`) reads. A
  main-roster template brings no bCT property (the class default), as on the
  wrestler himself [B: decompile].
- **Packages are found by name.** An entry's asset names are
  `Package.Object` paths, and the package is looked up by file name across
  the mounted roots (disc, update and DLC alike, as Pack 1's wrestlers load
  their own packages with the disc's shared ones). A slot-120 copy of 101
  names `CHR_101_A` and `AnimSet_Hulk_Hogan`, which load from the disc as
  they do for Hogan [I].

**Answer: yes.** The example (`docs/example-mod`) now fills 120 from 101 (Hulk
Hogan) as "Example Hogan"; `ExampleModTests` writes it into the disc's roster
with no pack and checks that every package it names is a disc file (the grunt
sets aside). Left out, `template` stays 110, so a mod that relies on the
default should be used with Pack 1; the guide says so. Since a main-roster
template works, the mod library needs no "template not available" refusal. A match with the example and no pack
installed is ticket 23's owner check.

## Character and arena packages: cosmetic or gameplay?

Ticket 03, 2026-10-17. Method: a scratch Python reader (LZO1X as in
`Lzo1x.cs`, both compression layouts) listed the export classes of all 115
`CHR_*` and all 83 `Arena_*` packages, with 0 parse failures, plus the
gameplay-data packages. The executable was read in Ghidra
(`WWEAllStarsTU2`, `xex_entry` = `0x8241A7E0`) and in `tu2.pe`. Counts are
exports.

**Verdict.**
- **`CHR_*` attire packages are cosmetic.** They hold no hit volumes,
  physics bodies, animations or attack data. One residual risk is noted
  below.
- **Arena packages are mixed.** The level, ring, apron and streaming
  packages carry game logic and collision, so they are gameplay. Lights,
  audio, FX, crowd and cinematics are cosmetic.
- **The real gameplay data lives in `CombinedGlobalCharacters.xxx`, which the
  spec's gameplay list missed.** Several of the listed names are empty stubs
  on the disc.

**Character packages** [B]:
- **What the 115 packages hold.** Texture2D 415, SkeletalMeshSocket 376,
  MaterialInstanceConstant 214, SkeletalMesh 163, ObjectRedirector 155,
  SD_MeshTree nodes about 108 each, GameBruiseResource 107, ShaderCache 107,
  GameHairTasselList 106, GameCAPRecord 35, plus AnimTree, GameHairTasselInfo
  and SkelControlGameHairTassel 12 each.
- **What they do not hold.** There is no PhysicsAsset, RB_BodySetup, AnimSet
  or AnimSequence, and nothing imports one. The SkeletalMesh exports carry
  only `LODInfo`, `bUseBoneMapping`, `Sockets` and `SkelMirrorTable` as
  tagged properties. There is no `PhysicsAsset` reference.
- **Sockets.** All 376 are weapon grips on the right hand
  (`RightHand_Socket_1H`, `_2H`, `_2HChair`, `_2HLadder`, `_2HWide`,
  `_Belt`, `_Bell`). Most sit on the CAP T-pose helper mesh. The executable
  has no string for them, so they are data-driven. They place the held
  weapon.
- **Hair tassels.** Their `CollisionCapsules` are secondary motion
  (`SkelControlGameHairTassel`), not match collision [I].
- **`GameCAPRecord`.** It is a native field list. All 35 copies are
  identical (`WrestlingStyle`=103, `CharacterScale`=1.0, `IntroCinema`=101,
  `NickName`=501), so it is a bake default, not per-wrestler data. The
  executable constructs a `CapRecord` only in the CAP bake and save paths
  (`0x825D95F8`, `0x82BD43B0`, which writes `CAP_SAVE_REC` under
  `CookedXenon`). The only reader of the `CharacterScale` FName that was
  resolved is a CAP UI callback (`0x82AF1140`,
  `_global.CAPCallbacks.cbGetCurren…`). The CAP-to-character copy at
  `0x82B0A848` reads `WrestlingStyle` and `CharacterScale` from a created
  superstar's record. The search found no path that reads the record from an
  attire package, but it was not exhaustive. Since the values are the same
  for every wrestler, they cannot be what tells wrestlers apart.

**Where hit volumes and attack data come from** [B]:
- **Hit volumes are PhysicsAsset bodies.** The animation-script tooltips
  next to "Make hit volume active", "Attack Type to use." and "Capsule
  Length/Width Scale" read "Name of hit volume created in Phat tool" and
  "Name of collision volume created in Phat tool". Phat is UE3's
  PhysicsAsset editor (`tu2.pe` around `0x8207DBA8`).
  `UGameHitCollisionManager` and `HitCollisionSize` ("Sets size of Hit
  collision capsule") exist too.
- **There is one character PhysicsAsset for the whole game.** It is
  `CHR_Physics.Default_Physics` inside `CombinedGlobalCharacters.xxx`, with
  14 RB_BodySetups on the bones Head, Spine1, the arms, forearms, hands,
  up-legs, legs and feet. The same package holds 69 GameCharacterData, the
  Game_DamageSets, Game_Buffs and Game_AttackType_Triggers objects,
  `Triggers_*`, `AICore` and `AIFSM_MoveLists`. All are objects cooked
  in under their source package names.
- **Disc stubs.** `GameCharacterData*.xxx`, `Game_DamageSets`,
  `Game_AttackType_Triggers`, `Game_AttributeTables`, `Game_Buffs`,
  `Scripts_Attacks_Main`, `Scripts_Attacks_Weapon`, `CHR_Physics`,
  `Env_Collision`, `Env_Generic_Ring` and `Triggers_Grabs` are 32 KiB
  stubs on the disc, with 0 names and 0 exports.
- **Other gameplay packages.**
  - `AnimFSM_*` holds the per-wrestler attack and reaction state machines
    (`AnimFSM_Andre`: 30,756 exports, SD_AnimStateConcreteAttack 364,
    GameAnimFSMReversalButtonTrigger 400).
  - `CombinedStartup_int` holds input logic (SD_Input* ops, AnimFSM
    triggers).
  - `Trigs_Vols_*` holds trigger volumes and touch events (SD_TriggerVolume
    22 and SD_SeqEvent_Touch 21 in `Trigs_Vols_Std_In`).
  - `WPN_*` packages hold only textures.

**Arena packages** [B]:
- **Gameplay.**
  - Main `Arena_NNN`: Kismet with `SeqAct_SetGameState`,
    `SeqEvent_GameplayManager`, PlayerStart and PathNode.
  - `Arena_Apron`: 8 SD_TriggerVolume and 7 SD_SeqEvent_Touch, for example
    "Apron_Front_Release_Check".
  - `Arena_NNN_Ring`: an RB_BodySetup on the ring mat mesh, a
    GamePhysicalMaterial (`GroundEvents`) and StaticMeshPhysicsComponents.
  - `Arena_NNN_Level`: 492 static meshes with StaticMeshPhysicsComponent.
  - `Arena_NNN_M` and `Arena_Global`: level streaming
    (SD_StreamingGroupKismet).
- **Cosmetic.**
  - `_Audio`, `_Chr_Light` and `_LvL_Light` (lights and toggles);
  - `_FX` and `Arena_LensFlares` (particles);
  - `_Cin`, `_Cin_Entrance`, `_Cin_Ring` and `_Cin_Ropes`;
  - `_Entrance` (entrance-ramp art);
  - `Arena_Crowd` and `Arena_Cin_Crowd`.

  This assumes matches never use the ramp geometry or a cinematic copy of
  the ring [I].

**Residual risk for attire mods** [I]:
- The 14 hit bodies hang on bones by name. A replacement mesh with a
  different skeleton (renamed bones or another reference pose) could move
  or drop them, if the bone transforms come from the mesh's reference pose
  and not from the shared animations. This was not checked.
- The launcher cannot check a skeleton cheaply. A later check could compare
  a mod mesh's bone names with the original.
- The wrestler's own textures, materials and mesh shape are safe.

**Reader note** [B]:
- At file version 398, the export entry carries a ComponentMap after the
  40 fixed bytes: count, then 12 bytes per entry. Then come ExportFlags, the
  generation net-object count array and the GUID. There is no PackageFlags.
- The "longer tail" of some entries in
  `shader-containers-from-disc.md` is a non-empty ComponentMap.

## 5. Starting plan

### Milestone 1: package and ini mods through `update:` (launcher only)

1. **Spike (½ day, no code).** Copy one disc package that TU2 does not
   replace, for example an arena or attire `CHR_*` package, into a scratch
   copy of `updatedata/` with a small visible change (or simply a different
   package of the same type renamed, to see a swap). Append its PatchTOC
   line, then run headless with `--log_file_opens=UPDATE`. If the game opens
   it from `UPDATE:\` and shows the change, milestone 1 is safe. If the
   change is not visible, trace the package cache (RE target 1).
   Done 2026-10-17: the override works (§2.3); the visible swap waits on
   the owner check in ticket `modding/01`.
2. **Coalesced merge.** A reader and writer for the bundle format (§1.3),
   plus an ini-edit format for mods: whole-file replacement of one inner ini,
   or UE3-style `+Key=`/`-Key=` lines. Tested against TU2's bundle.
3. **Mod folder, manifest, overlay builder** in `Launcher.Core`, with xUnit
   tests: hardlink fallback, conflict report, PatchTOC sizes, the
   uncompressed size for whole-file LZO packages.
4. **Mods page** in `Launcher.App`: list, toggle, order, conflicts, open
   folder, drop a zip, and an online warning.

### Milestone 2: `game:` overlay plus TOC rewrite (C++), and a texture dump

The `OverlayDevice` (§3.2) covers movies, XML and splash. Then Native texture
dump/replace (§3.3) as a separate, experimental option.

### RE targets, in order

1. **How `UPDATE:\` beats the disc for one package name.**
   - Start at the boot function `0x82607ED8`, which calls the PatchTOC
     loader `0x82609CA0` at `0x82608518`.
   - Follow `FindFiles` `0x827672A8` callers through the file-manager vtable
     (`0x820037E8 + 0x3C`) to the package-file cache that maps short names to
     paths.
   - Also read what `+0x70` (`0x82766460`) does to the PatchTOC lines.
2. **The TOC map and size lookups,** to confirm the overlay's TOC rewrite:
   - the parser `0x82767D08`, its insert `0x82767188` and lookup `0x827426A0`;
   - `FileSize` `0x82764C68` and `UncompressedFileSize` `0x82764E70`;
   - `CreateFileReader` `0x82764830` and its reader `0x82763A90`, to check
     whether reads clamp to the TOC size.
3. **DLC phase and roster slots** (done: "Content mods: DLC phases and
   roster slots" above), for additions: the DLC installer
   `0x82A32A88` (`DLCTOC.txt`, ini merge, `PackagesToFullyLoadForDLC`), and
   the readers of `InstalledPhases` (`0x821972A8`) and `PhaseID`
   (`0x82197310`) through their xrefs.
4. Later: the `UPDATE:\Coalesced.ini` gate at `0x82749048` (flag
   `0x8370BEA4`), and the movie open path from `0x82AFC488` and `0x82BDA5E4`,
   to see whether Bink honours the TOC size.

## 6. Open questions for the owner

1. **Mod format.** A folder plus `mod.toml` (id, name, author, version,
   `game = "TU2"`), installed from a zip? Or reuse the SvR 2011 port's
   `.svrmod`/`manifest.txt` so the scene's tools carry over?
2. **Online.** Should mods be off for online play, matched by a mod-set
   hash, or allowed when cosmetic only?
3. **Scope of milestone 1.** Packages plus ini only (launcher), or the `game:`
   overlay (C++) in the same release?
4. **Texture packs.** Is a Native-only texture replacement worth building,
   given that Emulated would not show it?
5. **Repacking.** Do we write and ship a package tool (texture swap in
   place), or publish format notes and leave tools to the community?
6. **Roster additions.** Is "new wrestlers via a fake DLC phase" a goal, or
   are swaps enough? It decides whether RE target 3 is worth its cost.
7. **Distribution.** Mods carry game-derived assets. The repo never ships
   them and the launcher only installs what the player supplies, like DLC.
   Is that the policy?

### Owner answers (2026-10-17)

1. **Mod format:** a folder with a manifest, one folder per mod under
   `mods/`, with enable toggles in the launcher.
2. **Online:** visual-only mods are allowed online. Gameplay and config mods
   are turned off or flagged for online play.
6. **Roster additions:** adding new wrestlers is the goal, so RE target 3
   (the DLC installer `0x82A32A88`, `InstalledPhases`, `PhaseID`) is in
   scope, not deferred.

Questions 3, 4, 5 and 7 are still open.
