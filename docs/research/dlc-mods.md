# Mods for DLC wrestlers: why a `packages/` mod of `CHR_219_A.xxx` does not load

Date: 2026-10-23. Question: a modder's texture mod of Cody Rhodes "American
Nightmare" (`CHR_219_A.xxx`, a Pack 1 wrestler) does not show in game, with
Pack 1 set up. Why, and what is the smallest fix? Nothing was built or run, and
no code changed. The mod zip was unpacked into the session scratchpad and read
with Python (`-I`).

Tags as in `docs/research/modding.md`: **[S]** this repo (file:line), **[B]**
the TU2 binary (`scripts/disasm.py` on `analysis/xex/tu2.pe`; Ghidra's
`WWEAllStarsTU2` was locked by another session), **[I]** inference, **[U]**
unverified. Every guest address is TU2.

## Summary

- **Root cause: a DLC package always beats `UPDATE:\`.** The game's package
  cache keeps DLC packages in a separate map, and its package lookup reads that
  map first. Pack 1 ships its own `CHR_219_A.xxx`, so the game loads
  `DLC0:\Phase1\CHR_219_A.xxx` and never opens the mod's copy in the `UPDATE:\`
  overlay. The `PatchTOC.txt` line the launcher writes does not matter [B].
- **The zip layout is fine.** The launcher wraps loose files recursively, so the
  top-level folder is harmless. `CHR_219_A.xxx` is on the disc too, so the name
  is accepted, and names are compared case-insensitively [S].
- **The file is fine.** It is a genuine `CHR_219_A` cooked from Pack 1's copy,
  not the disc's (§2) [B].
- **Workaround today, with no new release:** ship it as a content mod with the
  file in `content/` (§4). The combined content already lets a mod's file
  replace a pack's file. Content mods are offline only.
- **Smallest fix, launcher only:** at Play, send a `packages/` file that an
  installed official pack also ships into the combined content, not the
  `UPDATE:\` overlay (§4).

## 1. From the Mods tab to the game

- **Install.** When a drop has no `mod.toml`, `ModWrapping.Candidate` walks it
  recursively and maps every file whose short name is a known package to
  `packages/<name>` (`launcher/src/Launcher.Core/ModInstall.cs:284-298`,
  `:312-317`). Known packages are the disc's and TU2's `.xxx` names, compared
  case-insensitively (`ModLibrary.cs:366-379`). A zip with one top-level folder
  therefore wraps to `packages/CHR_219_A.xxx` [S].
- **Mod layout.** `mod.toml` holds `name`, `game = "TU2"` and `kind`
  (`ModLibrary.cs:299-333`). Below it sit three folders:
  - `packages/`: flat replacement packages (`ModLibrary.cs:164-222`);
  - `config/`: ini edits;
  - `content/`: DLC-style files, any layout (`ModContent.cs:69-136`) [S].
- **Play.** `PrepareForPlay` (`ModOverlay.cs:26-91`) builds two things:
  - **The `update:` overlay.** It hard-links TU2's files and each winning
    `packages/` file into `cache/mod-update/`, and writes `PatchTOC.txt` with
    one line per mod package (`ModOverlay.cs:113-144`, `:245-256`). The game
    gets this folder as `--update_data_root`, which the SDK mounts as
    `UPDATE:` [S].
  - **The combined content.** Only content mods trigger it
    (`ModOverlay.cs:42-49`). It is one synthetic DLC content in the DLC folder
    at the next `PhaseID`. It takes in every installed official pack's files,
    then the content mods' files, keyed by file name, so a mod's file replaces
    the pack's file of the same name (`ModContent.cs:196-272`, line 225 then
    line 253; stated at `ModContentConflicts.cs:14-15`). `BuildCombined`
    writes `DLCTOC.txt` from the real sizes (`ModContent.cs:314-337`) [S].
- **What the overlay covers.** Only the `UPDATE:` device. The base `game:`/`d:`
  device and the DLC devices (`DLC0:`, `DLC1:`…) are not overlaid
  (`docs/research/modding.md` §2.1, §3.1). No runtime C++ overlay exists [S].

## 2. Where the game loads `CHR_219_A.xxx` from

| Copy | Path | Size | Header (names, licensee word at 0x08) |
|---|---|---|---|
| Disc | `assets/Game/CookedXenon/CHR_219_A.xxx`, TOC line `1966080 0 0 ..\Game\CookedXenon\CHR_219_A.xxx 0` (`Xbox360TOC.txt:958`) | 1,966,080 | `0x155` names, `00003E5A` |
| Pack 1 | `.tools/xenia-canary/content/0000000000000000/54510866/00000002/F5B3E057…/Phase1/CHR_219_A.xxx`, `DLCTOC.txt` line `1998848 0 0 .\CHR_219_A.xxx 0` | 1,998,848 | `0x151` names, `00003D3A` |
| Mod | `cody rhodes American nightmare/CHR_219_A.xxx` (zip) | 2,069,428 | `0x151` names, `00003D3A` |

- All three start with `9E2A83C1 0025018E`: a big-endian, uncompressed-header
  UE3 v398/37 package, not whole-file LZO, so its TOC column 2 is 0. The mod
  shares Pack 1's header words and table counts, so it was cooked or edited
  from **Pack 1's** copy. It is the same format, with no rename [B].
- Pack 1 lists `Phase1\CHR_219_A.xxx` (its SHA-256 is in
  `.scratch/tu2-dlc/prototype/pack1-files.sha256`). Cody (219) is one of its
  five wrestlers (`docs/research/title-updates-and-dlc.md:151-153`) [S].
- At sign-in the DLC installer `0x82A32A88` passes every package of the content
  to the package cache, at `0x82A32AEC`–`0x82A32B00`
  (`GPackageFileCache` `0x836FD228`, vtable `+0x18`, `r5` = the user index) [B].

## 3. Root cause

**The package cache.** The object is built at `0x82608554` (constructor
`0x82607C38`), with its vtable at `0x8209EF14`. It holds two maps [B]:

| Slot | Address | Role (UE3 `FMapPackageFileCache`) | Evidence |
|---|---|---|---|
| `+0x08` | `0x82606590` | `CachePackage`: the main map at `this+4` | `addi r28,r30,4` at `0x826065F0` |
| `+0x0C` | `0x82605268` | `FindPackageFile` | reads `this+0x18` at `0x826053F0`–`0x826053F4`; only on a miss (`bne` at `0x82605408`) reads `this+4` at `0x8260542C`–`0x82605430` |
| `+0x18` | `0x82607088` | `CacheDownloadedPackage(path, user)`: takes the base name and sets it in the map at `this+0x18` | `addi r3,r30,24` and `bl 0x82606970` at `0x826070F0`–`0x826070F4`; `0x82606970` replaces an existing key in place |
| `+0x1C` | `0x826079D8` | `ClearDownloadedPackages(user)`: removes the `this+0x18` entries of one user | compare on `+0x1C` at `0x82607A28` |

So a package name that any DLC content ships resolves to the DLC path, whatever
the main map says. The main map holds the disc entries and the `UPDATE:\`
entries, including the mod's. This also explains how Pack 1's `CHR_110_A.xxx`
and `CombinedGlobalCharacters.xxx` replace the disc's copies
(`docs/research/modding.md` "What the DLC installer reads", step 3) [B].

**The candidates, each ruled in or out.**

| Candidate | Verdict | Evidence |
|---|---|---|
| The overlay covers only `UPDATE:`, and the DLC copy wins | **In: the root cause** | §3 table; the mod goes to `UPDATE:\` (`ModOverlay.cs:126`, `:138`) |
| Top-level folder in the zip | Out | recursive wrap (`ModInstall.cs:287`); install moves it to `packages/` |
| Needs a specific subfolder | Out for `packages/` (flat, by short name); the folder that works is `content/` | `ModLibrary.cs:168-169`, `ModContent.cs:74` |
| Case sensitivity | Out | `OrdinalIgnoreCase` throughout (`ModLibrary.cs:369`); the name matches exactly |
| Newer or different format | Out | same tag, version and licensee words as Pack 1's copy (§2) |
| Refused as a renamed package | Out [I] | the inner tables are Pack 1's `CHR_219_A`; not run through `PackageOrigin.Check` |

Without Pack 1 the mod would load from `UPDATE:\` over the disc copy. Cody is
not on the select screen without Pack 1, though (`[AvailableCharacters]`
gate), and the disc copy is an older cook (`0x155` names). That is probably why
the modder "had to set up DLC" [I].

## 4. Fix and packaging

### Tell the modder now (works on 1.4.0)

Make it a content mod. The combined content takes in Pack 1 and then lets the
mod's file replace Pack 1's `CHR_219_A.xxx`:

```
Cody Rhodes American Nightmare/        (zip this folder, or drop it on the Mods tab)
  mod.toml
  content/
    CHR_219_A.xxx
```

```toml
name = "Cody Rhodes American Nightmare"
author = "<you>"
version = "1.0"
game = "TU2"
kind = "cosmetic"
```

Limits:

- Any `content/` part makes the mod gameplay and offline only
  (`ModOnlineRule.cs:61`). With online play on, the launcher leaves it out
  (`ModOverlay.cs:32`).
- The launcher log shows a "content conflict" line: Pack 1 and the mod both
  ship `CHR_219_A.xxx`, and the mod wins (`ModContentConflicts.cs:64-66`).
- Pack 1 must be installed through the launcher's DLC page. The combined
  content is built from `DlcLibrary.Installed()` (`ModContent.cs:275-281`).

### Smallest code fix (launcher only, no C++)

In `PrepareForPlay` (`ModOverlay.cs:26-91`), split each mod's `Packages` by
`PackFileNames()` (`ModContentConflicts.cs:75`):

- A package an installed official pack also ships goes into the combined
  content, as a `content/` file would (`ModContent.cs:253`). It is left out of
  the `UPDATE:\` overlay and its `PatchTOC.txt` line.
- A mod that has such a package triggers `PrepareCombined`, even with no
  `content/`. The load report then lists the file under the mod, and
  `ContentConflicts` reports it against the pack.

A consequence to decide on: a combined content raises the DLC phase, which
looks like a matchmaking filter (`0x82B158E8`, modding research
"`InstalledPhases` and `PhaseID`") [B; purpose I]. Two options:

- treat such a package mod as offline only, as content mods are, which is
  simplest and matches the workaround;
- or, if cosmetic DLC retextures must work online, use a runtime fix instead.

**Alternative runtime fix (keeps online, needs a build and a release).** Hook
`CacheDownloadedPackage` `0x82607088` (`REX_HOOK`). When the update overlay
holds a mod file of the same base name, skip the downloaded registration, so
the `this+4` entry (`UPDATE:\` plus its PatchTOC size) is found. The DLCTOC
size line for the DLC path then goes unused. The launcher would have to pass
the list of mod package names, for example in a file next to the stamp [I].

### Tests (`launcher/tests/Launcher.Tests`)

- `ModContentTests`: a `packages/` mod whose file an installed fake pack ships.
  After `PrepareForPlay`, the combined content holds the mod's bytes, its
  `DLCTOC.txt` line has the mod's size, and the overlay `PatchTOC.txt` has no
  line for it. A `packages/` file no pack ships still goes to the overlay.
- `ModOnlineRuleTests`: such a mod is off for online play, if that option is
  chosen.
- `ModInstallTests`: a zip with a top-level folder holding one known package
  wraps to `packages/<name>`. This regression guard should already pass.

## 5. Open questions

- **[U] Not run in game.** One owner run with Pack 1, the workaround mod and
  `--log_file_opens=CHR_219` should show the game opening
  `DLC1:\CHR_219_A.xxx` from the combined content. That content's mount number
  depends on enumeration order.
- **[U] Package size.** The mod is 70,580 bytes bigger than Pack 1's copy and
  not 32 KiB aligned. That is fine if the modder's tool rewrote the offsets.
  Whether it renders correctly is untested.
- **DLC-only packages.** `CHR_116_A`, `CHR_116_A_C` and `CHR_110_A_C` are not
  on the disc or in TU2. `KnownGameFiles` (`ModLibrary.cs:374-377`) does not
  know them, so a loose-file wrap of them is refused ("replaces no package the
  game has"). The `content/` layout works for them. The launcher fix should
  also add installed packs' package names to the known set.
- `CHR_204_A_C..xxx` in Pack 1 (double dot) is the pack's own name. It is
  noted here only so that nobody "fixes" it.
