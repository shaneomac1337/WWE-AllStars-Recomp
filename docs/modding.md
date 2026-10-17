# Modding guide

How mods work in WWE All Stars Recomp: for players who install them and for authors who
make them. A working example is in [`example-mod/`](example-mod): it adds a wrestler and
makes one config edit, with comments in every file.

Mods hold the author's own files. Never put files from the disc, Title Update 2 or a DLC
pack in a mod you share.

## What a mod is

A mod is a folder with a `mod.toml` file in it. It can have three parts, each optional:

| Folder | What it does |
|---|---|
| `packages/` | Replaces game packages (`.xxx`): attires, arenas, UI. |
| `config/` | Edits the game's config and text (the Coalesced bundles). |
| `content/` | Adds content: a new wrestler in a free roster slot. |

Installed mods live in the `mods` folder beside `WWEAllStarsLauncher.exe`. The launcher
never changes your game files: at Play it builds what the mods need in its own folders and
starts the game on them. Turn every mod off and the game is exactly as it was.

## Installing

Choose MODS on the launcher's home screen.

- **Drop** a `.zip`, `.7z` or `.rar`, a folder, or single files onto the page, or use
  **Add mod…** (files) or **Add folder…**. An archive with several mods installs them all.
- **New mods start off** and are marked new. Turn a mod on with its toggle.
- **Updates.** A mod whose name matches an installed mod is an update: the launcher asks
  "Update X from 1.0 to 1.1?". The update keeps the mod's on/off setting and its place in
  the load order.
- **Loose game files.** A download with no `mod.toml`, where every file is a game package
  or one of the game's inner inis, can be installed as a mod in one step. The launcher asks
  for a name and writes the `mod.toml`. Tick "This mod only changes looks" for a mod that
  only changes looks, so it can load in online play. Read-me and picture files (`.txt`,
  `.md`, `.pdf`, `.jpg`, `.png` and the like) are left out.
- **By hand.** A mod folder copied into `mods` works too. It shows up off and new.
- **Refused.** An archive that unpacks to more than 8 GB, a file that is not a mod or a game
  file, and a mod with a problem (see below) are refused with the reason.

**Remove** moves a mod's folder to the Recycle Bin. **Open mods folder** opens `mods`.
Changes made while the game runs apply the next time you choose Play.

## Load order and conflicts

The list on the Mods page is the load order. When two mods in use replace the same thing,
the one lower in the list wins. **Up** and **Down** move a mod.

The page lists each conflict among the mods turned on: what clashes, which mods, and which
one wins.

- Two mods replacing the same package.
- Two content mods filling the same roster slot, or giving the same `CodeName`. The later
  mod's entry is used; the earlier one is left out.
- A content mod shipping a whole `CombinedGlobalCharacters.xxx` or
  `Shared_Characters_DLC.xxx`. There is one of each for the whole roster, so that file
  replaces the DLC packs' and the other content mods' copy, and must already carry their
  wrestlers. Roster entries from `roster.toml` files are still written into it.
- Two content mods shipping a file with the same name, or a content mod shipping a file a
  DLC pack has. The mod's file wins over the pack's.

Two mods in use each shipping a whole inner ini (see "Whole inis" below) is a conflict too;
the later one wins, and the load report says so. Key edits to the same ini don't conflict:
they are applied one after another, in load order.

The order and the on/off settings are kept in `mods/load-order.txt`, one mod per line
(`on | folder` or `off | folder`, `| new` on a mod not looked at yet). You can edit it by
hand.

## Online play

With online play on, only cosmetic mods load. Gameplay mods stay on in the list but are
left out of that Play, and the page and the Play area say how many. This keeps a mod from
causing a desync.

A mod is **cosmetic** only when its `mod.toml` says `kind = "cosmetic"` and it touches
nothing that can change a match. It counts as **gameplay**, whatever it says, when it:

- has any file in `config/`;
- has a `content/` part (new wrestlers are offline only);
- replaces a package on the gameplay list: `Core`, `Engine`, `Game`, `Game_*`,
  `GameCharacterData*`, `GameOnline`, `AI*`, `Scripts_*`, `Aimed_*`,
  `OnlineSubsystemLive`, `CombinedGlobalCharacters`, `CombinedStartup_*`, `CHR_Physics`,
  `Triggers_*`, `Trigs_*`, `AnimFSM_*`, `AnimSet_*`, `Env_Collision`, and the arena
  packages `Arena_NNN`, `Arena_NNN_M`, `Arena_NNN_Ring`, `Arena_NNN_Level` (NNN is the
  arena's three digits), `Arena_Apron` and `Arena_Global`. `*` stands for any rest of the
  name.

Attire packages (`CHR_*`) and the other arena packages (lights, audio, effects, crowd,
cinematics, entrance art) are cosmetic. A mod declared cosmetic that is gameplay says why on
the Mods page.

## The manifest: `mod.toml`

```toml
name = "Example Wrestler"
author = "Your name"
version = "1.0"
description = "What the mod does, in a sentence."
game = "TU2"
kind = "gameplay"
```

- `name` is required. The mod's folder is named after it: characters Windows can't use in a
  file name, and `|`, become `_`, and it is cut to 64 characters. Keep the name the same
  across versions, so a new version installs as an update.
- `author`, `version` and `description` are shown on the Mods page. They can be left out.
- `game` must be `"TU2"`: mods are made for Title Update 2, the version the port runs.
- `kind` is `"cosmetic"` or `"gameplay"`. Left out, it is `"gameplay"`.

The format is flat `key = value` lines. Strings are in double quotes; `#` starts a comment.
There is no id field and no dependencies.

## Packages: `packages/`

Put each replacement package straight in `packages/`, under the short name the game knows it
by, such as `packages/CHR_101_A.xxx`. No subfolders, and no disc paths.

A package is refused when it:

- has a name the game has no package for (a mod can only replace packages);
- has a file name with characters other than letters, digits, `_`, `-` and dots;
- is not a game package, or is damaged;
- is another package renamed. A cooked package names its objects after its own file, so a
  renamed copy loads and then hangs the game. Rebuild it under the right name instead.

You never edit a table of contents: the launcher writes the sizes the game needs, including
the uncompressed size of a compressed package. When your mod brings shaders the launcher
hasn't seen, it prepares them before that Play.

## Config: `config/`

`config/` edits the inner inis of Title Update 2's Coalesced bundles. Name each file after
the inner ini it edits.

- A file straight in `config/` edits `Coalesced.ini`: `config/Xe-Game.ini`,
  `config/Xe-Engine.ini`, `config/Xe-Input.ini`.
- A file in `config/<lang>/` edits that language's bundle, such as
  `config/int/CharacterNames.int` for English text. Title Update 2 ships `int`, `deu`,
  `fra`, `ita` and `spa`; another language folder is a problem.

### Edits

Each line under a `[Section]` is one edit, in UE3's own style:

```ini
[Section]
Key=Value     ; sets the key: the first line of it gets the value, other lines of it go
+Key=Value    ; adds this value, unless the key already has it
-Key=Value    ; removes every line of the key with this value
.Key=Value    ; adds this value, even if the key already has it
```

A key that isn't there yet goes at the end of its section, and a new section at the end of
the ini. Lines starting with `;` and lines before the first section are ignored. Everything
you don't edit stays as it was, so two config mods combine.

The example's `config/Xe-Game.ini` removes one value of `Movies` in `[VendorStartup]`,
which drops the THQ logo from the startup movies.

### Whole inis

A file that has no `+`, `-` or `.` lines and names every key of every section of the
original ini counts as the whole ini, and replaces it. Anything less is edits, so a small
file never wipes an ini by accident. Two mods in use replacing the same whole ini is a
conflict; the later one wins.

A file for an ini the bundle doesn't have is left out at Play, and the log says so.

## Content: `content/` (new wrestlers)

A content mod adds a wrestler in one of the roster's 11 free slots: **120, 125, 127, 128,
129, 130, 215, 217, 228, 229 and 230**. Content mods load offline only.

At Play the launcher builds one combined content from your DLC packs and every content mod
in use. Your DLC folders are only read, never changed, and the combined content is deleted
again when no content mod is in use, when online play is on, and when the game exits. No
DLC pack is needed for a content mod.

`content/` can use any folder layout: the files go into one folder by name, so two files
with the same name are a problem. File names use letters, digits, `_`, `-` and dots, and
a `.xxx` file must be a game package under its own name. The launcher writes the content's `DLCTOC.txt` and its
phase, so a mod never ships `DLCTOC.txt` or sets `[GameDLCManager] PhaseID`.

### The roster entry: `content/roster.toml`

The launcher writes your wrestler into the game's roster package at Play. You never ship or
edit that package for a new wrestler.

```toml
[120]
template = 101
CharacterName = "Example Hogan"
CodeName = "ExampleHogan"
```

- `[id]` starts an entry for a free slot. A file can fill several slots.
- `template` is the complete roster entry copied: 101–130 or 201–230, not a free slot.
  Everything the file doesn't set comes from it, so the example plays as a copy of Hulk
  Hogan (101) under a new name, in the DLC list of the select screen.
- Pick a template whose packages the player has. A wrestler of the main roster (Hulk
  Hogan, The Rock, John Cena and the others you can pick without DLC) has all of his on the
  disc, so a mod copying one needs no DLC pack. Left out, the template is 110, the Million
  Dollar Man, a DLC wrestler: the game plays him from Pack 1's packages (the Million Dollar
  Pack), so use a mod copying him with Pack 1 installed, or ship the packages in the mod.
  Another DLC wrestler's entry needs his pack the same way, and the unused entries (Ric
  Flair, Batista and the like) name packages no disc or pack has.
- `CharacterName` is required. The select screen shows it in capitals.
- `CodeName` is required: letters and digits, used by no other wrestler (case is ignored).
  The game finds the wrestler's name and the wrestler you pick by it, so a copied
  `CodeName` would show and play as the original.
- Strings are in double quotes (printable characters, no `"` or `\`), numbers are bare, `#`
  starts a comment.

Keys the file can set:

- text: `CharacterName`, `CodeName`, `CharacterCode`, `IntroCinemaLevelName`;
- whole numbers, 0 or more: `nHealth`, `nStun`, `nPower`, `nStamina`, `nGrappling`,
  `nAgility`; `ClassType` (0–255);
- numbers above 0: `Scale`, `broadPhaseRadius`;
- asset names, as `"Package.Object"`: `SkeletalMeshAsset`, `PhysicsMeshAsset`,
  `MeshTreeAsset`, `BruiseResources`, `AnimSetAsset`, `AnimSetFinisherAsset`,
  `AnimFSMAsset`, `AIFSMAsset`, `MomentumData`, `AudioAsset`, `AudioCommentaryAsset`,
  `AudioVictoryMusicAsset`, `AudioCrowdChantAsset`, `AudioGruntAsset`,
  `CostumesArray.<n>.Model` (n is 0–9) and `CharacterPortraits.<n>.PortraitTexture`
  (n is 0–3).

A wrestler with its own look ships its packages in `content/` and points the asset names
at them. No portrait is needed: DLC wrestlers show none in the select screen's DLC list.

### The wrestler's name

The launcher writes the name for you: `<CodeName>_Name` and `<CodeName>_Short`, both the
`CharacterName` in capitals, for every language Title Update 2 ships. To give other text,
ship your own name fragment, as the example does in `content/Example__CharacterNames.int`:

```ini
[CharacterNames]
ExampleHogan_Name=EXAMPLE HOGAN
ExampleHogan_Short=EXAMPLE
```

A language your fragment gives the `_Name` for gets no generated name.

### Fragments

A `.ini` or language file (`.int`, `.deu`, `.fra`, `.ita`, `.spa` and so on) in `content/`
is a fragment: the game merges it into the ini or text file named after its first `__`.
`<tag>__CharacterNames.int` merges into `CharacterNames.int`, `<tag>__Xe-Engine.ini` into
`Xe-Engine.ini`. The tag is yours. The launcher renames fragments so two mods never clash,
and a fragment for a file the game doesn't have is a problem. A language fragment only
takes effect in its own language.

`[AvailableCharacters]` lines of an `Xe-Game.ini` fragment are collected into the content's
one `Xe-Game.ini`. Each roster entry gets its `CharacterData_<id>=1` line there on its own.

### Roster problems

Each of these keeps the mod off, with the reason on the Mods page: a slot that isn't one of
the 11, a slot given twice, an unknown key, a key set twice, a bad value or template, a line
that isn't `[id]` or `key = value`, an entry with no `CharacterName` or no `CodeName`, a
`CodeName` that isn't letters and digits, used twice in the file or already in the roster,
and a file with no entry. Play itself stops, with the reason, when the template lacks a
property the file sets.

## When something is wrong

A mod with a problem is shown with the reason and stays off; it never stops the game from
starting. Problems include a missing `mod.toml`, a missing `name` or `game`, a `game` other
than `"TU2"`, a `kind` other than `"cosmetic"` or `"gameplay"`, and the package, config and
content problems above.

Each Play writes a load report to the launcher log and to `mods.log` beside the session log:
the mods used in order, the files each supplied, the config edits, the roster slots filled,
the conflicts, and the mods left out and why. "Open logs" finds it; it is the first thing
to look at, and to attach to a bug report.

## What doesn't work yet

- **A renamed package.** A package only works under its own name (see Packages).
- **No repacking tool.** The launcher doesn't build or edit packages, apart from writing
  roster entries. Use community tools to make packages; the format notes are in
  [`research/modding.md`](research/modding.md).
- **Only packages, config and content.** Movies, XML files and other loose disc files can't
  be replaced, and there is no texture replacement on the Native renderer.
- **No code mods**, plugins or memory patches.
