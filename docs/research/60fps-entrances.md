# 60 fps wrestler entrances

Date 2026-10-11. The owner wants 60 fps entrances as a setting that can be
turned on and off. This study feeds the spec. **Every guest address here is a
TU2 address** (ADR-0022). Older notes that this one builds on give base v5
addresses (`movement-judder.md`, `entrance-video-stutter.md`,
`.scratch/zero-stutter/issues/15-sixty-hz-cinematics.md`); they were converted
with `python scripts/tu_addrmap.py` (report only, table in §7). ReXGlue paths
(`SDK/`) are relative to `.tools/rexglue-source` (`f5337cdc` plus `patches/`).

Tools used:
- `python scripts/disasm.py` on `analysis/xex/tu2.pe` (TU2 memory image).
- Ghidra through the MCP server. The repo's project `ghidra/WWEAllStars` holds
  only `default_v5`, the **base v5** image, and was locked by a running Ghidra,
  so it was not used. Instead, `analysis/xex/tu2.pe` was imported as a raw
  binary (`PowerPC:BE:64:A2ALT-32addr`, rebased to `0x82000000`) into a new
  scratch project under the session scratchpad. That image is TU2. The
  decompiled `FEngineLoop::Tick` and `USeqAct_Interp::Play` below come from it.
- **Nothing was installed.** XEXLoaderWV was not needed: the import names
  (`VdSwap`, `XGetVideoMode`, …) are already in the generated code
  (`generated/default/wweallstars_init.cpp`, `wweallstars_funcs.h`).
- Small throwaway scanners in the scratchpad: data xrefs from `lis`+`d(rA)`
  pairs, and `bl` callers over `.text`.

A claim marked **confirmed** was read in disassembly, decompilation, source or
logs. **Hypothesis** means it is plausible but not measured.

## Answer

1. **The frame rate comes from the game's own fixed step. Entrances run at
   30 Hz on purpose (confirmed).** `FEngineLoop::Tick` (`0x82417780`) steps
   1/60 s per frame. It steps 1/30 s when the global `0x83709514` is set and
   two debug flags are clear. A QPC limiter at the end of the tick then waits
   out the step. While a Matinee whose THQ flag bit `0x40000` is set plays,
   `USeqAct_Interp::Play` (`0x82862CC0`) sets `0x83709514`, and the stop paths
   (`0x824D5F30`, `0x824D6858`) clear it. The tick log measured this on the
   route: 96 % of intro ticks and 91 % / 43 % of the two entrances were at
   1/30 s, and 0 in the match (`entrance-video-stutter.md`, last section).
   Gameplay is 60 Hz. No D3D presentation interval and no vblank wait are
   involved in normal play (§1.3).
2. **The simulation steps a fixed dt per tick and never uses measured time
   (confirmed).** So a 60 Hz tick plays the Matinee at the same speed, half as
   far per tick and twice as often. The risks are what does *not* scale with
   dt: work per tick has to fit 16.7 ms (today about 13 ms at 30 Hz), per-frame
   lerps, Bink's own 30 fps clock, and a helper tick that the game holds off
   during 30 Hz scenes (§2).
3. **Prior art.** wwe-svr2011-pc ships `unlock_30fps` (default on) for SvR 2011
   entrances. It never calls the game's "halve the rate" function. SVR2011-Recomp
   has no such feature. Xenia Canary's patch file for title `54510866` has no
   frame-rate patch (§3).
4. **The setting would follow `wwe_vibration_mode`'s path:** a cvar in
   `src/settings_cvars.cpp`, a row in `src/settings/registry.h`, the in-game F4
   page and `wweallstars.settings.toml`, the schema export, and the launcher
   only if it is a startup setting. It also needs the enhancement lists of the
   scene runner and the session report (§4).
5. **ReXGlue needs no change.** The presenter paces each guest swap to the
   guest vblank. A 60 Hz entrance is the same cadence as a match (§5).
6. **First experiment (about 30 lines, no codegen):** extend the existing tick
   hook in `src/game_tick_log.cpp`. It hides `0x83709514` from the tick's step
   choice only, puts it back before `GEngine->Tick`, and keeps the counter at
   `0x835E0088` armed. Then play the route with `--wwe_tick_log=true` (§6).

## 1. How the game caps its frame rate

### 1.1 The tick's step choice (confirmed, TU2)

`FEngineLoop::Tick` is `0x82417780`. Its only caller is the main loop, at
`0x82419E38`. The step choice is at `0x824178CC–0x8241792C`:

```
824178CC  lis r11,-0x7c8f ; lwz r11,-0x6aec(r11)   ; 0x83709514 half-rate flag
824178D4  cmpwi cr6,r11,0 ; beq 0x82417910          ; clear -> 1/60
824178DC  lwz r11,-0x6af0(r29) ; bne 0x82417910     ; 0x83709510 (-BENCHMARK) set -> 1/60
824178EC  lwz r11,-0x6ad0(r11) ; bne 0x82417910     ; 0x83709530 (-DUMPMOVIE) set -> 1/60
824178F8  li r11,5 ; lfs f31,0x2f38(r10)            ; f31 = [0x82002F38] = 0x3D088889 = 1/30
82417908  stw r11,0x88(r9) ; b 0x82417930           ; [0x835E0088] = 5
82417910  lwz r11,0x88(r10) ; lfs f31,0x1ccc(r9)    ; f31 = [0x82001CCC] = 0x3C888889 = 1/60
82417920  ... if ([0x835E0088] != 0) [0x835E0088]--
```

Ghidra's decompilation of the same block, from the TU2 import:

```c
if (((iRam83709514 == 0) || (iRam83709510 != 0)) || (iRam83709530 != 0)) {
  dVar9 = (double)fRam82001ccc;               // 1/60
  if (iRam835e0088 != 0) iRam835e0088 = iRam835e0088 + -1;
} else {
  dVar9 = (double)fRam82002f38;               // 1/30
  iRam835e0088 = 5;
}
...
*(float *)(iVar2 + 0x28) = (float)dVar9;                       // DeltaSeconds
dVar13 = (double)(float)((double)*(float *)(iVar2 + 0x2c) * dVar9);  // x dilation
(**(code **)(*piRam8371d1c0 + 0xcc))(dVar13);                  // GEngine->Tick(dt)
```

- The two float constants were read from the image: `0x82002F38` =
  `3D088889` (0.0333333) and `0x82001CCC` = `3C888889` (0.0166667).
- The two flags that force 1/60 are command-line switches, parsed in Init
  (`0x82414180`): `-BENCHMARK` sets `0x83709510` (`stw` at `0x824144F4`,
  string L"BENCHMARK" at `0x82001BC0`), and `-DUMPMOVIE` sets `0x83709530`
  (`0x824144DC`, L"DUMPMOVIE" at `0x82001BD4`). The console commands at
  `0x828726B0`/`0x828726F0` also write `0x83709530`.

### 1.2 The limiter (confirmed, TU2)

At the end of the tick, `0x82417D30–0x82417DAC`:

```
82417D48  stfd f0,-0x42c0(r24)          ; GCurrentTime 0x8370BD40 = QPC seconds
82417D60  fsub f10,f0,f11               ; now - frame start (this+0xC)
82417D64  fcmpu cr6,f10,f30 ; bge exit  ; f30 = dt x dilation
82417D70  bl 0x82608ef8                 ; appSleep(0.0), then read the QPC again, loop
```

Decompiled: `while (GCurrentTime - this->FrameStart < dt) appSleep(0);`. So the
30 Hz cadence comes **only** from the 1/30 step plus this limiter. There is
also an optional `GEngine->GetMaxTickRate()` sleep (`0x82417AE8–0x82417B44`,
vtable `+0xE0`), which `-BENCHMARK` skips. `src/game_tick_log.cpp:140-150`
already hooks this tick (`REX_HOOK_RAW(sub_82417780)`). It replaces the
limiter's spin with a sleep (`wwe_limiter_sleep`), and it logs the step and
`0x83709514` per tick (`wwe_tick_log`, `kHalfRateFlag` at line 55).

### 1.3 Presentation interval and VdSwap (confirmed; one hypothesis)

- The D3D present is `0x82DBDFF8`, the only caller of `VdSwap`
  (`src/d3d_census.cpp:602-604`).
- The **only** other reader of `0x83709514` that touches D3D is `0x82768088`:

  ```
  82768130  lwz r10,-0x6aec(r9)     ; half-rate flag
  82768138  cntlzw / rlwinm / xori / addi  -> r11 = flag ? 2 : 1
  82768150  stw r11,0xb4(r1)        ; params at r1+0x80, so field +0x34
  82768154  bl 0x82db94f8           ; r3 = [0x837349E0], r4 = params
  ```

  Field `+0x34` of `D3DPRESENT_PARAMETERS` is `PresentationInterval` (the
  layout the Xbox 360 shares with D3D9: width, height, … `FullScreen_RefreshRateInHz`
  `+0x30`, `PresentationInterval` `+0x34`; the width/height stores at `+0x80`/`+0x84`
  fit). **Hypothesis:** `0x82DB94F8` is `D3DDevice_Reset` or a similar call.
- `0x82768088` is called only from the console-command handler (`0x82871A38`,
  `0x82871A64`, `0x82871A90`), behind the commands L"LOCKSTEP",
  L"LOCKED" and L"UNLOCKED" (strings at `0x82158620`, `0x82158610`,
  `0x821585FC`). Normal play never runs it. The measured 60 presents/s in
  matches (`movement-judder.md` §1) show that the device presents at interval
  one. So a 60 Hz entrance needs **no** change to the presentation interval.
  This also means the SvR trap of "forcing the interval alone made entrances
  1.7x too fast" (§3) does not apply here.

### 1.4 Detecting "a cinematic is playing" (confirmed)

`0x83709514` is the game's own signal. Every store in `.text`:

| Address | Function | Store |
|---|---|---|
| `0x82862CFC` | `USeqAct_Interp::Play` `0x82862CC0` | `= 1` when `(this+0xF8) & 0x40000` |
| `0x824D5F5C` | `0x824D5F30` (Matinee stop) | `= 0` under the same bit (`rlwinm r10,r11,0,13,13`) |
| `0x824D6910` | `0x824D6858` (Matinee stop) | `= 0` under the same bit |

Ghidra (TU2) on Play: `if ((param_1[0x3e] & 0x40000U) != 0) { uRam83709514 = 1; }`
(`param_1[0x3e]` is `this+0xF8`). Play's callers are `0x824D60E8`,
`0x824D8860`, `0x827EFB68`, `0x8286761C` and `0x828678E4`.

Readers: the tick (`0x824178D0`), the console-only `0x82768130`, and
**`0x824F03E8`, the cinema-skip input**:

```
824F03DC  cmpwi cr6,r28,5 ; bne ...          ; key 5
824F03E8  lwz r11,-0x6aec(r11) ; beq ...     ; only while the half-rate flag is set
824F0410  ori r11,r11,1 ; stw -0x4200(r10)   ; bit 0 of 0x836FBE00: the FName is built (run once)
824F0418  addi r4,r11,0x30f0                 ; L"SkipCinema" (0x820630F0)
824F042C  bl 0x82752488                      ; FName(0x836FBDF8, L"SkipCinema")
824F0450  bl 0x824db1e0                      ; queue the named event SkipCinema
```

Correction (faster-starts ticket 02, 2026-10-24): `0x836FBE00` (not `0x8370BE00`;
`lis 0x8370` with `-0x4200`) is the run-once guard of the static FName at `0x836FBDF8`,
not a skip latch. Every press queues the event through `0x824DB1E0`; the world tick drains
the queue into the Kismet events that listen for it (`0x824DA880`).

So the flag means "a skippable THQ cinematic is playing". **The feature must
not clear `0x83709514`**, or the skip button would stop working during
entrances. It has to bypass only the tick's branch.

What the flag does not tell: whether the cinematic is an entrance, the arena
intro, or something else that THQ flagged. The data has entrance-specific
classes (`USeqAct_CreateEntrance_Cinema` at `0x82051BB8`;
`USD_CreateEntrance` at `0x82050E90`; `GetRingEntrancesSetting` at
`0x82216AA4`). The game, however, uses the one flag for every flagged Matinee.
"60 fps entrances" in practice means "60 fps flagged cinematics". On the
measured route those were the arena intro and both entrances (match: 0).
Which Matinees outside that route carry the bit is an open question.

## 2. Fixed dt, and what a 60 Hz cinematic could break

**The step is fixed (confirmed).** `DeltaSeconds` (`this+0x28`) is the
constant chosen above, times the time-dilation ramp (`this+0x2C`). The
measured time between ticks goes only into statistics
(`0x8370BD48`/`0x8370BD50`, `0x82417828–0x824178C8`). The config has no smoothed
frame rate (`movement-judder.md` §1, `Coalesced.ini [Engine.Engine]`). A tick at
1/60 therefore advances game time by exactly 1/60 s.

| Area | At 60 Hz during a flagged Matinee | Confidence |
|---|---|---|
| Matinee/interp speed | Same speed. `UpdateInterp`/`StepInterp` takes `dt`; half the advance per tick, twice the ticks | High (UE3 design; the step is confirmed) |
| Animation, particles, pyro | Advance by `dt`, the same as in matches, which already run at 1/60 | High |
| Music/crowd audio (FMOD, real time) | Stays in sync **as long as every tick fits 16.7 ms**. The game is lock-step with no catch-up, so a late tick slows game time against the real-time music | Confirmed mechanism; budget unmeasured |
| Work per tick | Measured at 30 Hz: 12.9 ms (intro), 11.9 / 8.3 ms (entrances) per tick, waits on the GPU included (`entrance-video-stutter.md`, tick log). At 60 Hz the per-tick cost may drop, because part of it is per second of game time. The budget is 16.7 ms | Unmeasured, **main risk** |
| Bink videos | The jumbotron (`NNN_JumboTron.bik`, 512x512) and arena (`ARENA_00x.bik`, 1280x720) videos are 30 fps files. At 60 Hz each video frame stays up for two ticks. The 720p decode may land on every other tick and make those ticks uneven | Hypothesis |
| Helper tick gated by `0x835E0088` | `0x82410988` calls `GEngine` vtable `+0xEC(dt)` under a lock only when `[0x835E0088] == 0`, GWorld (`0x837218A0`) is set and `GEngine+0x2BC` is non-zero (`0x824109C8–0x82410A44`). It is called from a timed loop in `0x8243B738` (`bl` at `0x8243B788`). The 1/30 path keeps the counter at 5 and the 1/60 path counts it down. **If the bypass takes the 1/60 path, the counter reaches 0 after 5 ticks and this helper can start running during the entrance.** The bypass must keep storing 5 | Confirmed code; effect unknown |
| Camera | `USD_CameraMode::MakeLerpRateFramerateIndependent` exists (L"…execMakeLerpRateFramerateIndependent" at `0x8200F468`). So some camera lerps are per frame by default and would converge faster at 60 Hz. Cinema cameras are probably keyed tracks (`UCinemaTrackMove`, …), which follow `dt` | Hypothesis |
| SD_Cinema frame rate | A separate THQ value: `SETCINEMAFRAMERATE` (console, `0x82872700`) calls `0x8256D230`, which stores a float at `0x836FC214`. Its static initialiser (`0x83418B40`) sets it to `[0x8352F758]` = **60.0**. So the cinema system's own rate is 60 already, which supports the "authored for 60, dropped to 30 for cost" reading | Confirmed value; its reader is not found |
| Physics | `NxTimeStep=0.02` fixed sub-steps (`Coalesced.ini`) take the same path as in matches | High |
| Scripting by frame count | Any Kismet/UnrealScript latent action that counts ticks instead of seconds would run twice as fast. Not found, not ruled out | Unknown |
| Native renderer | The 30 Hz readback spikes from `entrance-video-stutter.md` are off: `wwe_native_resolve_readback` defaults to false (`src/native/native_renderer.cpp:116`, checked at `:636`) | Confirmed |

## 3. Prior art

- **wwe-svr2011-pc** (KaikoClanworth1, `b4884ac`):
  [`port/src/menu_hooks.cpp` L444-504](https://github.com/KaikoClanworth1/wwe-svr2011-pc/blob/b4884ac95884406b9b51157479de5a228888fc83/port/src/menu_hooks.cpp#L444-L504).
  - `REXCVAR_DEFINE_BOOL(unlock_30fps, true, "GPU", "Run the game's 30 fps scenes (entrances, cutscenes) at 60 fps")`.
  - It hooks the halving function `sub_826E1C88` and never calls the original.
    It calls `SetSceneThirtyFps(true)` only when the setting is off.
    `sub_826E1D28` (the restore) then calls `SetSceneThirtyFps(false)` and
    `ApplyFrameRate`.
  - Its comment says: "Only forcing the present interval to 1 made entrances
    play ~1.7x too fast". SvR steps by a frame count (`0x82EDDBF0`, 2 at
    30 fps), which this game does not do (§1.3).
  - The setting is a plain cvar on its graphics page and is on by default. So
    it is the same idea as ours: skip the game's half-rate request at its
    source.
- **SVR2011-Recomp** (`D:\Projects\SVR2011-RECOMP`, HEAD `6a12d52`): no 60 fps
  entrance code. `grep 826E1C88|EDDBE8` over `src/` and the manifest finds
  nothing. `docs/performance.md:607-612` only records that entrances run at
  30 fps because `sub_826E1C88` halves the rate.
  `analysis/reddit-post-v0.3.1.md:19` lists "unlocking entrances, which the game
  caps at 30 FPS" as future work.
- **Xenia Canary game-patches**: title ID `54510866` (TQ-2150;
  `docs/research/title-update.md:53`).
  [`patches/54510866 - WWE All Stars.patch.toml`](https://github.com/xenia-canary/game-patches/blob/106bb0a40be27b5a2444162562975e4885e1ce74/patches/54510866%20-%20WWE%20All%20Stars.patch.toml)
  at `106bb0a` holds a single patch, "Skip Audio Loop Stuck Code" (`be32`
  `0x833a6c98` = `0x38600001`, base v5; TU2 `0x833A9B50`). **There is no 60 fps
  patch.** No `-BENCHMARK`-style frame-rate patch either. A search of the repo's
  issues and PRs for "WWE All Stars" finds only #66 (missing hashes). So there
  are no addresses to convert.

## 4. How a toggle flows today, and the path for this one

Example: `wwe_vibration_mode`, a live setting.

1. **Cvar.** It is defined in `src/settings_cvars.cpp:41`
   (`REXCVAR_DEFINE_INT32(wwe_vibration_mode, 2, "WWEAllStars", …)`), in the one
   file both the page and the tests link. It has no `.lifecycle(...)`, so it
   is live. Restart settings add `.lifecycle(rex::cvar::Lifecycle::kRequiresRestart)`
   (`settings_cvars.cpp:8-11`).
2. **Registry row.** `src/settings/registry.h:126-129`:
   `{"wwe_vibration_mode", "Vibration", "CONTROLS", {{"0","Off"},…}, "help"}`.
   This is the one list the page, the config file and the launcher read.
3. **In-game page (F4).** `src/settings/settings_page.cpp:125-137` `Apply`: a
   live row calls `rex::cvar::SetFlagByName`, and a restart row is kept in
   `g.pending`. Both are saved with `SaveSetting` to
   `wweallstars.settings.toml` in the user-data folder
   (`src/settings/config_file.h`, `SettingsPath`).
4. **Load at boot.** `src/wweallstars_app.h:107` `LoadSettingsFile(config_path_,
   user_data_folder_)` runs after the main config, so the page's file wins.
5. **Runtime read.** The hook reads the cvar on every use
   (`src/vibration/mode.cpp`, `REXCVAR_DECLARE(int32_t, wwe_vibration_mode)`).
   A midasm hook can return `false` to run the shipped code
   (`WweHitPulse`, `src/vibration/enhanced_hooks.cpp:114-119`; manifest entry
   with `jump_address_on_true`, `wweallstars_manifest.toml:197-201`).
6. **Launcher.** `--print-settings-schema` (`src/settings/schema.h`) is
   exported by `scripts/export-settings-schema.ps1` into the committed
   `launcher/src/Launcher.Core/SettingsSchema.json`, and a launcher test fails
   if the two drift apart. The launcher shows **only startup rows**:
   `SettingsSchema.Startup => Settings.Where(s => s.AppliesAtStartup)`
   (`SettingsSchema.cs:73`), used by `MainWindow.axaml.cs:1324`.
   `SettingsSchemaTests.cs:8-40` pins the exact startup list and asserts that
   live settings (`wwe_vibration_mode`, …) stay off it. `LaunchArgumentsTests.cs:13`
   lists the keys passed on.
7. **Enhancement bookkeeping.** `CONTEXT.md` ("Enhancement": scene runs always
   turn enhancements off). `scripts/scene_runner.py:56-66` `ENHANCEMENTS`
   forces each one off. `src/native/native_renderer.cpp:1515-1529`
   `kEnhancements` reports whether each ran, and the scene runner fails a run
   that had one on.

**Path for "60 fps entrances"** (the name is a placeholder, e.g. `wwe_cinematics_60`):

- A bool cvar in `src/settings_cvars.cpp`. It is live if the hook reads it per
  tick, which is cheap and lets the owner flip it in the middle of an entrance
  to compare.
- A registry row, probably on the GRAPHICS or DISPLAY tab, with Off/On choices
  and help text that says it goes beyond the Xbox 360.
- Being live, it appears on the F4 page only. To show it **in the launcher
  too**, either make it `kRequiresRestart`, or widen the launcher to show
  chosen live rows. That second option means changing the `Startup` filter
  and `SettingsSchemaTests`. Owner decision.
- Add it to `ENHANCEMENTS` (off value `false`) and to `kEnhancements`.
- The hook itself: a midasm hook (§6, E3) or the `game_tick_log.cpp` tick hook
  (§6, E1). Either one reads the cvar directly. The midasm hook needs a
  `wweallstars_manifest.toml` entry with the instruction evidence, then
  `build.ps1 -Action Codegen`. It needs no seed and no stub (the function
  exists).

## 5. Presentation in ReXGlue

- The guest swap goes through `VdSwap` (`SDK/src/kernel/xboxkrnl/xboxkrnl_video.cpp:440`,
  `REX_EXPORT` at `:584`) into the command processor.
  `Presenter::RefreshGuestOutput` then only marks a frame pending when
  `present_pace_to_guest_vblank` is on (`SDK/src/ui/presenter.cpp:668-683`;
  cvar `:94-97`, `kRequiresRestart`). The pacing thread presents it at guest
  vblank + `present_pacing_offset_us` (5000, `:106-110`).
- The guest vblank runs at `video_mode_refresh_rate` (60.0; `SDK/src/ui/window.cpp:63`,
  `xboxkrnl_video.cpp:77`). It follows the display when the display runs at a
  multiple of 60 Hz (`vblank_follows_display`,
  `SDK/src/graphics/graphics_system.cpp:41`, `:402-415`; ADR-0016).
- At 30 Hz each game frame covers two vblanks. The pacing thread counts the
  vblank without a new frame as "repeated" and presents nothing new. At 60 Hz
  there is one frame per vblank, exactly as in a match. **No presenter or
  vblank change is needed.**
- One side effect to watch, not to change: `scripts/analyze_hitches.py` judges
  the intro and entrances against the step it reads from the tick log (ticket
  15, choice A). With the setting on, those windows would be judged at 60 Hz
  (25 ms hitch bar), which is the right bar for the feature.

## 6. Rapid-prototype path

The owner asked for fast experiments rather than test-first. Each step answers
one question, and you can stop at any step.

**E0: what is flagged (log only, 10 minutes).** Add log lines to the existing
`--wwe_tick_log` output. Add `REX_HOOK_RAW(sub_82862CC0)` (Play) and the two
stop functions (`sub_824D5F30`, `sub_824D6858`). Each logs the Matinee
object, the `+0xF8` flags and the value of `0x83709514` after the call, plus
the guest present count. Play the route once (arena intro, two entrances,
match, a victory scene if there is one). This answers which cinematics the
setting would affect, and whether the flag ever stays set into gameplay. If
the hooks take too long to write, the tick log's per-second `at 1/30 s` count
already marks the windows.

**E1: the decisive one (tick hook, no codegen, about 30 lines).** It answers
whether the game *runs correctly* at 60 Hz in entrances. In
`src/game_tick_log.cpp`, behind a temporary cvar:

1. In `REX_HOOK_RAW(sub_82417780)`, before `__imp__sub_82417780`: if the cvar
   is on and `0x83709514 != 0`, remember that, set `t_hidden = true` and store
   0 to `0x83709514`.
2. Hook `sub_8241A050`, the QPC read, a 6-instruction leaf called 156 times.
   On the game thread, inside the tick, with `ctx.lr == 0x82417954` (the call
   at `0x82417950`, the first one after the step choice) and `t_hidden` set:
   restore `0x83709514 = 1`, store 5 to `0x835E0088`, and clear `t_hidden`.
   This happens before `GEngine->Tick` (`0x82417A78`), so
   `SkipCinema` and the Matinee's own Play/Stop see the true flag. All other
   calls run as shipped.
3. If the 1/60 path has already decremented the counter, step 2's store of 5
   keeps the helper tick at `0x82410988` disabled, as on the console.

Run: `--wwe_tick_log=true --<cvar>=true`, the route with entrances on, and the
owner watching. Read:
- the `game tick 1s:` line: `0 at 1/30 s` and about 60 ticks/s in the intro
  and entrances, plus `work mean` and `max`;
- the `game tick:` lines for slow ticks over 20 ms;
- the owner's eye on lip/music sync, pyro timing and the jumbotron.

Compare the same route with the cvar off. Flip it live through
`rex::cvar::SetFlagByName` (F4 once it has a row, or the console) for a
side-by-side within one entrance.

Outcomes:
- 60 ticks/s, work max under about 15 ms, and in sync by eye: the feature works.
- Ticks over 16.7 ms: the game slows against the music. Profile those ticks
  (Tracy, `entrance-video-stutter.md` item 2) before going further.
- Motion or effects twice too fast: something counts frames. Find it from what
  speeds up.

**E2: the skip still works (1 minute, same build).** With the setting on,
press the skip button during an entrance. If E1's restore happens in the right
place, it skips as it does with the setting off.

**E3: production form (after E1 succeeds).** Replace E1's two-hook trick with
one midasm hook:

- Site: `0x824178CC` (`lis r11,-0x7c8f`, the start of the flag test).
- `jump_address_on_true = 0x82417910` (the 1/60 path).
- No registers needed beyond memory access, through
  `REX_KERNEL_MEMORY()->virtual_membase()` as `GuestCall` does
  (`enhanced_hooks.cpp:73-77`).
- It returns true only when the cvar is on, `0x83709514 != 0`, and both
  `0x83709510` and `0x83709530` are 0. Before it returns true, it stores 5 to
  `0x835E0088`.
- `0x824178CC` is reached by fall-through from `0x824178C8` and by
  `bge cr6,0x824178cc` at `0x824178A4`. Both paths need the hook.
- Then `build.ps1 -Action Codegen`. The flag itself is never written, so skip
  and stop behave as shipped.

Not recommended as a shortcut: setting `0x83709510` (`-BENCHMARK`) at runtime.
It also forces 1/60, but it is read in eight more places, among them
`0x824177F4`, where the tick calls `0x82608E38(0)` (probably `appRequestExit`, hypothesis) once a
frame limit at `this+0x18` is passed, and it skips the `GetMaxTickRate` sleep.

## 7. Base v5 to TU2 addresses used here

From `python scripts/tu_addrmap.py` (report only), plus
`.scratch/tu2-dlc/issues/01-tu2-addrmap-report.md`:

| Base v5 | TU2 | Method | What |
|---|---|---|---|
| `0x82417890` | `0x82417780` | function | `FEngineLoop::Tick` |
| `0x82417A18` | `0x82417908` | function | `stw` of 5 to the counter |
| `0x82861648` | `0x82862CC0` | function | `USeqAct_Interp::Play` |
| `0x824D6110` | `0x824D5F30` | function | Matinee stop path |
| `0x824D6A38` | `0x824D6858` | function* (code changed) | Matinee stop path |
| `0x82860490` | `0x82861B08` | function | `ResetMovementInitialTransforms` |
| `0x824142D0` | `0x82414180` | function | Init (command line) |
| `0x8286FCF0` | `0x82871328` | function | console commands |
| `0x827AEAE8` | `0x827AF6A0` | function | `-BENCHMARK` flag from object flags (older note) |
| `0x82608FD8` | `0x82608EF8` | function | `appSleep` |
| `0x837094F4` | `0x83709514` | data-ref | half-rate flag |
| `0x837094F0` | `0x83709510` | data-ref | `-BENCHMARK` |
| `0x83709510` | `0x83709530` | data-ref | `-DUMPMOVIE` |
| `0x835E0088` | `0x835E0088` | data-ref | half-rate countdown |
| `0x8370BD20` | `0x8370BD40` | data-ref | `GCurrentTime` |

## 8. Choppy animation in 60 fps entrances (ticket 02)

Date 2026-10-11. With `wwe_60fps_entrances` on, the owner found the wrestler animation in
entrances "a little bit too choppy", with a steady 60 ticks/s at 1/60 s
(`analysis/proto-60fps/ab.log`). Ticket:
`.scratch/60fps-entrances/issues/02-choppy-wrestler-animation.md`.

### 8.1 Result

**Cause (confirmed): the crowd, not the wrestler, animates at 30 Hz.** THQ's crowd
scheduler `0x82B55A38` spreads the crowd over the ticks when the step is under 1/30 s. At
1/60 s it ticks one of two crowd groups per tick, each with twice the step, so every
crowd member gets a new pose on every other tick. At 1/30 s, which is what the original
entrance runs at, it ticks every group on every tick. With 60 fps entrances on, the
camera now moves at 60 Hz while about 160 crowd skeletons still change pose at 30 Hz.

**The entering wrestler is fine.** The wrestler's skeleton (69 bones, plus a 55-bone
attachment) runs its own Tick and `UpdateSkelPose` on every tick, and its pose changes
on every tick (`cccc…`, 60–61 updates per 60–61 ticks; route run `on60g` in §8.4).

**Fix (prototype, built).** The midasm hook `Wwe60fpsCrowd` at `0x82B55A90`
(`wweallstars_manifest.toml:465`, `src/game_tick_log.cpp:572`) sends the scheduler down
its own path for steps of 1/30 s and over. That path is taken only while
`wwe_60fps_entrances` is on, the 1/30 flag `0x83709514` is set, and `-BENCHMARK` and
`-DUMPMOVIE` are clear, i.e. exactly when `Wwe60fpsEntrances` steps a flagged cinematic
at 1/60 s. Every crowd group then ticks on every tick with the real step, as all of
them do in the 30 Hz original. During the prototype a temporary cvar,
`wwe_60fps_crowd`, turned the crowd half off for A/B; it was removed once the owner
accepted the look (2026-10-11). Matches are not affected,
because the flag is clear there, and the crowd keeps the game's 30 Hz round robin in
them.

Measured on the scripted Exhibition route (`tests/scenes/exhibition-1v1.toml`, headless,
60 fps entrances on):

| | Crowd copies per skeleton per second (111 / 56 / 31 bones) | Crowd leader pattern | Work mean / max per tick |
|---|---|---|---|
| Before (`on60g`) | 29.4 / 28.6 / 29.7 of 61 | `.c.c.c.c…` | 1.2–1.4 ms / 1.9 ms |
| With `Wwe60fpsCrowd` (`crowd60`) | 58.7 / 56.3 / 60.4 of 61 | `cccccccc…` | 2.2–2.6 ms / 5.2 ms |

The extra work stays well inside the 16.7 ms budget.

**The owner's run, re-read.** In `analysis/proto-60fps/t02b.log` the two 111-bone
skeletons (`419EDC00`, `48693200`) and the 56-bone one with `.c.c.c…` were crowd group
leaders, not wrestlers. Their updates were complementary (31 + 30 = 61 per second),
which is the scheduler's round robin. The report then listed only the four largest
skeletons, so the 69-bone wrestler was never printed. With 60 fps off, the same
skeletons showed `cccc` at 30 ticks/s, which is the scheduler's 1/30 s path.

### 8.2 The crowd scheduler `0x82B55A38` (confirmed)

`r30` = this, `f31` = dt, `r28` = 0 (`0x82B55A5C`). The dt test:

```
82B55A90  lis r11,-32223 ; lfs f0,30568(r11)   ; [0x82217768] = 0x3D088886 = 0.0333333
82B55A98  fcmpu cr6,f31,f0
82B55A9C  blt cr6,0x82b55b98                   ; dt < 1/30 -> round robin
82B55AA0  lwz r11,116(r30) ; stfs f31,164(r30) ; all groups: +0xA4 = dt
82B55AC8  lwz r8,896(r9) ; ori r7,r8,4096 ; stw r7,896(r9)   ; +0x380 |= 0x1000, list +0x70
          (the same for the lists at +0x7C, +0x88 and +0x94)
```

Ghidra (TU2 project), the round-robin branch:

```c
if (param_1 < (double)DAT_82217768) {                 // dt < 1/30
  *(int *)(param_2 + 0xa0) = *(int *)(param_2 + 0xa0) + 1;
  iVar4 = FUN_82aea348();
  iVar4 = (-(uint)(*(char *)(iVar4 + 0x36) != '\0') & 2) + 2;   // 2 groups (4 if +0x36)
  if (iVar4 <= *(int *)(param_2 + 0xa0)) *(undefined4 *)(param_2 + 0xa0) = 0;
  *(float *)(param_2 + 0xa4) = (float)((double)(longlong)iVar4 * param_1);   // dt x groups
  ... clear 0x1000 on every crowd mesh, then set it on group [+0xA0] only ...
} else {
  *(float *)(param_2 + 0xa4) = (float)param_1;
  ... set 0x1000 on every crowd mesh ...
}
```

The consumer is the component tick loop `0x8280F870`. Its caller is TickActors `0x828102B0`,
called from `0x82811198`, which is called from UWorld::Tick `0x828114C0`.

```
8280FA08  bl 0x8256d378 ; mr r31,r3              ; the actor's cinema object
8280FA14  bl 0x825732d0 ; mr r30,r3              ; the component as a skeletal mesh
8280FA2C  lwz r11,896(r3) ; rlwinm r10,r11,0,18,18 ; beq 0x8280fa68   ; +0x380 & 0x2000: crowd
8280FA3C  rlwinm r11,r11,0,19,19 ; beq 0x8280fb3c  ; & 0x1000 clear -> skip this tick
8280FA58  bl 0x827e5ba8 ; bl 0x82713a68 ; lfs f31,164(r3)   ; dt = scheduler +0xA4
```

On a tick that is not skipped, the crowd uses a shared-pose cache. The pose cache is at
`AnimTree+0xCC`: `+0x128` is a time stamp (double) and `+0x130` the component that last
updated. One component per cache ticks (`ConditionalTick` `0x8276A488`, which calls
`USkeletalMeshComponent::Tick`). The rest copy its LocalAtoms (`0x82772AE0`) and
SpaceBases (`0x82799CB8`) at `0x8280FB00–0x8280FB20`:

```
8280FAAC  lwz r11,296(r31) ; lfd f0,-17088(r26)  ; stamp vs GCurrentTime 0x8370BD40
8280FAC4  fcmpu cr6,f13,f0 ; bge 0x8280fb00       ; already updated this tick -> copy
8280FAD4  bl 0x8276a488                           ; else tick it and stamp it
```

In the pose log this is why most crowd skeletons show `k` (copied) and only one leader
per cache shows `c`.

### 8.3 What was ruled out on the way

- **The SD_Cinema frame rate `0x836FC214`** is only stored (`0x8256D24C`, `0x8256D34C`,
  `0x83418B4C`). A scan of every `lis`+D-form and `addi` base finds no reader. It does
  not drive animation.
- **No animation code reads the 1/30 flag `0x83709514`.** Its readers are the tick
  (`0x824178D0`), the SkipCinema input (`0x824F03E8`) and the console present-interval
  path (`0x82768130`).
- **USkeletalMeshComponent::Tick `0x8279A2D8`.** It is vtable slot `0xE8` of
  `0x82018B58` and `0x820191C0`. It calls TickAnimNodes `0x827947F0` and
  `UpdateSkelPose(dt, TRUE)` `0x82798160` (at `0x8279A410`) on every tick. The only
  gate is the "rendered within 1.0 s" test `0x8279A3C4–0x8279A3F4` (`[0x82001CC8]` =
  1.0). It has no parity or time-step gate.
- **THQ's cinema anim track `0x8256E8B8`** (vtable `0x8205DD04`) takes its position
  continuously from the Matinee time. It has no 30 fps snap.
- **AActor::Tick `0x8280D430`** stores `bTicked` (`+0x74` bit 27 = `GWorld+0x12C & 1`).
  This is UE3's ticked-this-frame toggle, used by the deferred pass `0x8280F378`. It is
  not a 30 Hz gate.
- **A slot `0x140` "skip tick" check `0x8280B4F8`** skips an actor not rendered for 5 s
  (`[0x8206D980]` = 5.0). It does not apply here.

Tools: the first pass used `disasm.py` because the MCP's Ghidra 12.1.2 lacked the
Xenon360 language. The second pass used the TU2 project in Ghidra 12.1.4. The decisive
data came from runs of the scripted Exhibition route with the pose log. Those runs
were headless, and the scripted input (`--wwe_input_script`) stops at the arena
select, before the intro.

### 8.4 The diagnostic: `--wwe_pose_log`

**Removed (2026-10-11).** The diagnostic below lived in `src/game_tick_log.cpp` only
while ticket 02 ran; the line numbers refer to that uncommitted version. It crashed the
owner's run when an entrance ended (`analysis/proto-60fps/t02c.log`: "read of guest
0x2D2D2D2D" on the game thread, right after a `pose 1s:` header): its once-a-second report
read the owner pointer of a skeleton freed during that second. Rebuild it from this
description if it is needed again, and drop freed components first.

`src/game_tick_log.cpp` (cvar `:48`, default false):
- `REX_HOOK_RAW(sub_82798160)` (`:455`), UpdateSkelPose: `RecordPose` (`:261`) reads
  SpaceBases (`+0x2B8`, TArray of 64-byte FMatrix, `:178`) for skeletons of 20–2000
  bones. For each tick it keeps the largest change in bone translation, and it records
  the caller.
- `REX_HOOK_RAW(sub_8279A2D8)` (`:465`) marks Tick, `REX_HOOK_RAW(sub_82799CB8)`
  (`:493`) marks a shared-pose copy (only from `0x8280FB1C`), and the hooks on
  `0x8280F870` / `0x8280F1C8` (`:475`, `:483`) mark which component-tick loop reached
  the owner actor (`comp+0x44`).
- `ReportPoses` (`:300`) prints the following once a second. First a summary line.
  Then one line per class (owner vtable and bone count) with the updates, Ticks and
  copies per skeleton. Then up to six lines for skeletons that run their own Tick,
  which leaves the crowd copies out. Each of those has a pattern with one character per
  tick:

  | Character | Meaning |
  |---|---|
  | `c` | changed |
  | `s` | a small change |
  | `B` | a big change |
  | `=` | same pose |
  | `k` | copied |
  | `t` | Tick without an update |
  | `.` | nothing |

  Each is followed by the owner's vtable and its slots `0x13C` / `0x140`.

Route runs (Exhibition, headless; logs in the session scratchpad, not kept): `on60g`
(60 fps entrances on, before the fix) and `crowd60` (with `Wwe60fpsCrowd`).

## Open questions (only an in-game run answers these)

1. Does the work per tick in the intro and entrances stay under 16.7 ms at
   60 Hz on the owner's PC, and on the Steam Deck / low-end preset? (E1 tick
   log; `work max` matters more than the mean.)
2. Do the entrance music and the Matinee stay in sync over a whole entrance?
   This includes pyro hits on the beat and the titantron/jumbotron cue points.
3. Does anything visibly run at double speed (a frame-counted Kismet delay, a
   per-frame camera lerp, crowd card animation)?
4. Does the 720p arena intro Bink (30 fps) make every other 60 Hz tick
   heavier, so the intro judders even though entrances are fine? If so, should
   the setting cover entrances only? That needs a way to tell the intro
   Matinee apart (E0).
5. Which Matinees carry the `0x40000` bit outside the measured route
   (victory scenes, Path of Champions cutscenes, Create-an-Entrance preview)?
   And does the owner want them all at 60, or entrances only?
6. Does the skip button still skip with the setting on (E2)?
7. Does the helper tick `0x82410988` ever run during an entrance with the
   counter kept at 5? Expected never; one log line in a hook on it confirms.
8. Default on or off, and live (F4 page only) or startup (launcher too)? Owner
   decisions for the spec. wwe-svr2011-pc ships it on by default.
9. Online play: does a frame-rate difference between two peers in an entrance
   matter? Entrances are probably not lock-step over the network, but that is
   not checked (`docs/research/online-mode-feasibility.md`).

## Sources

- TU2 disassembly (`scripts/disasm.py`, `analysis/xex/tu2.pe`):
  - `0x82417780–0x82417DE4` (tick), `0x82862CC0` (Play), `0x824D5F30`,
    `0x824D6858` (stop paths);
  - `0x824F03B0–0x824F0498` (SkipCinema), `0x82768088–0x8276816C`
    (present parameters), `0x82871A20–0x82871A94` (LOCKSTEP/LOCKED/UNLOCKED);
  - `0x824144C0–0x82414590` (command-line switches), `0x82410988–0x82410A6C`,
    `0x8243B738–0x8243B7A4` (helper tick), `0x8256D230`, `0x8256D310`,
    `0x83418B40`, `0x82872700` (SETCINEMAFRAMERATE);
  - constants `0x82002F38`, `0x82001CCC`, `0x8352F758`.
- Ghidra (MCP) decompilation of `0x82417780` and `0x82862CC0` from a raw import
  of `analysis/xex/tu2.pe` (scratch project, base `0x82000000`).
- This repo: `src/game_tick_log.cpp:28-55, 140-150`, `src/d3d_census.cpp:602-604`,
  `src/settings_cvars.cpp:41-44`, `src/settings/registry.h:126-129`,
  `src/settings/settings_page.cpp:125-137`, `src/wweallstars_app.h:107`,
  `src/vibration/enhanced_hooks.cpp:73-77, 114-119`,
  `src/native/native_renderer.cpp:116, 636, 1515-1529`,
  `wweallstars_manifest.toml:197-201`, `scripts/scene_runner.py:56-66`,
  `launcher/src/Launcher.Core/SettingsSchema.cs:73`,
  `launcher/src/Launcher.App/MainWindow.axaml.cs:1324`,
  `launcher/tests/Launcher.Tests/SettingsSchemaTests.cs:8-40`.
- Earlier notes: `docs/research/movement-judder.md` §1–2,
  `docs/research/entrance-video-stutter.md` (last section, tick-log table),
  `.scratch/zero-stutter/issues/15-sixty-hz-cinematics.md` (owner chose
  "faithful" on 2026-10-04; this feature revisits that as a setting),
  `docs/adr/0016-guest-vblank-locked-to-display.md`.
- ReXGlue: `SDK/src/ui/presenter.cpp:94-110, 668-683`,
  `SDK/src/graphics/graphics_system.cpp:41, 402-415`,
  `SDK/src/kernel/xboxkrnl/xboxkrnl_video.cpp:77, 440, 584`,
  `SDK/src/ui/window.cpp:63`.
- wwe-svr2011-pc `b4884ac`:
  https://github.com/KaikoClanworth1/wwe-svr2011-pc/blob/b4884ac95884406b9b51157479de5a228888fc83/port/src/menu_hooks.cpp#L444-L504
- SVR2011-Recomp `6a12d52`: `docs/performance.md:607-612`,
  `analysis/reddit-post-v0.3.1.md:19`.
- Xenia Canary game-patches `106bb0a`:
  https://github.com/xenia-canary/game-patches/blob/106bb0a40be27b5a2444162562975e4885e1ce74/patches/54510866%20-%20WWE%20All%20Stars.patch.toml
