# Loading times: the pre-match load

Date 2026-10-24. Question from the owner: can the recomp's loading times be cut
significantly? Scope, set by the owner during the work: the **pre-match load**
(confirming wrestlers and arena → the match starting, `CONTEXT.md`, Loading).
The bar is **at least half** of 1.6.0's pre-match load on the same PC. Defaults may
change speed only. **Skippables** (logos, movies, loading-screen minimum times) go
behind an opt-in launcher toggle and are never the default. A lever ships on by
default only if it cannot desync online peers.

Every guest address is a **TU2** address (ADR-0022). ReXGlue paths (`SDK/`) are
relative to `.tools/rexglue-source` (`f5337cdc` plus `patches/`). Kismet facts come
from the extracted disc (`assets/Game/CookedXenon`) and TU2's `updatedata/`, read by a
throwaway prototype (LZO1X decoder, UE3 package table and tagged-property reader)
that stayed in the session scratchpad, as in `shader-containers-from-disc.md`.
Export numbers below (`E1180`) are 1-based export-table indices in the named package.

Labels: **measured** (a log or sampler line), **read** (data or code read directly),
**inferred** (follows from the measured and read facts but was not observed directly).

## Short answer

**No, not with speed-only defaults. Yes, by well over half, but only through opt-in
skippables.** Confidence: high for the breakdown, medium for the size of the gain
from skippables, because no build has tried them yet.

- The pre-match load on the owner's PC is **about 75 s with intro and entrances
  watched** and **about 32 s when the player skips them** (two measured runs, below).
- **Loading work is small: about 2.7 s of game-thread work and about 105 MB of reads**
  across the whole pre-match load (measured). The host renderer, shaders, pipelines and
  texture uploads add almost nothing (measured: 0 pipelines compiled, renderer time
  ≤ 4.6 ms in every load-window hitch).
- **The rest is the game's own presentation and fixed waits:** the arena intro (~12 s)
  and two entrances (~40 s), which the game already lets the player skip; **13.6 s of
  fixed Kismet delays** on the critical path (read), **8 s of them behind a "LOADING"
  coaching-tips screen while nothing loads** (measured: no reads, game thread idle);
  and ~3 s of in-ring taunts before the bell.
- Speed-only levers can save **about 0.3–1.5 s** (inferred). That is ≤ 2 % of the
  watched path and ≤ 5 % of the skipped path. It cannot reach the 50 % bar.
- An opt-in "fast pre-match" (trim the Kismet delays, auto-skip the intro and
  entrances) would take the skipped path from ~32 s to **~11–19 s** and the watched path
  from ~75 s to **~11–13 s** (inferred). It is not online-safe. Turn it off while a
  Net-Z session runs, as 120 fps does.

## 1. How the game loads a match (read)

- **Packages.** 5,414 `.xxx` UE3 packages (file version 398, licensee 37), 2.26 GB.
  Most large ones are chunk-compressed with flag `2` and 128 KB blocks. A block of
  `Arena_001.xxx` (offset 249, 31,572 → 131,072 bytes) decodes exactly with LZO1X, so
  the codec is LZO, not LZX or zlib. Some packages (`UI_LOADER`, `loadCoachingTips`,
  `Level_Intros_Main`) are uncompressed, and 31 (`CombinedStartup_*`, TU2's `Engine.xxx`)
  are whole-file LZO wrappers with a little-endian header.
- **Config.** TU2's `Coalesced.ini` (`updatedata/`), `[Core.System]`:
  `AsyncIOBandwidthLimit=0`, so the async I/O system is not throttled. `[Engine.Engine]`
  `UseStreaming=False`. `[Engine.StartupPackages] bPreloadPackagesFromMemory=True`.
  Nothing there sets a loading-screen minimum time.
- **The flow is Kismet in the arena's persistent map.** Each `Arena_00N.xxx` holds a
  `Main_Sequence` whose sub-sequences stream the level in groups
  (`SeqAct_MultiSD_StreamingGroup`, 32 in `Arena_001`). They pass control on through
  "Finished Loading" outputs, `SeqAct_GarbageCollectAndWait` (5) and `SeqAct_Delay` (8).
  All six arenas have the same delays (read: `Arena_001` to `Arena_006`). The match
  start is in the shared `Spwn_Sys.xxx`, sequence `Game_Settings`.
- **Who reads.** 206 of 208 logged package opens come from one guest thread (`t8492`),
  not the game thread (`t29100`) (`analysis/session-loadtime1/runtime.log`,
  `--log_file_opens=.xxx`). The SDK serves every `NtReadFile` synchronously on the
  calling thread (`SDK/src/kernel/xboxkrnl/xboxkrnl_io.cpp:222`,
  `if (true || file->is_synchronous())`). The read is a host `ReadFile` with an offset
  (`SDK/src/core/filesystem_win.cpp:129`, via `host_path_file.cpp:33`).

### The critical path, with its fixed delays (read)

`SeqAct_Delay`'s class default is `Duration=1.0` (`updatedata/Engine.xxx`, export
12577 `Default__SeqAct_Delay`). A delay without its own `Duration` therefore waits 1 s.
Delays count game time: the engine steps a fixed 1/60 s per tick and never uses
measured time (`60fps-entrances.md`, Answer 2).

| Phase | Kismet (package, export) | Fixed wait |
|---|---|---|
| Before the intro | `FrontEnd_Variables` delay (`Arena_001` E1176) | 0.1 s |
| | `Streaming_Assets` entry: output link `ActivateDelay=2.0` → `SeqAct_PlayMovie` (E1362 → E1245) | **2.0 s** |
| | `Match_Rules_Cin` delay (E1181), then `Level_&_Character_Intros` delay after `SD_LoadAsset` (E1179) | 0.5 + 0.5 s |
| After the entrances | `Unload_Cinema_Assets` delay (E1183), `Trigs_Vols_Splines` delay (E1178), `Match_Rules` delay (E1177) | 0.25 + 0.5 + 0.5 s |
| Start_Game → bell | `Game_Settings`: delay 5.0 (`Spwn_Sys` E81) → `SeqAct_SyncGamePlayAsync` (E133) → `ForceGarbageCollection` (E90) → delay **3.0** (E85) → `Pre_Match_Trigger_Logic` (taunts) → delay 1.0 (E86, default) → bell, delay 0.25 (E83) → controller on, `GameMatchStart` (E94) | **5.0 + 3.0** + 1.0 + 0.25 s |
| Total on the path | | **≈ 13.6 s** |

Off the 1v1 path: `Level_Assets` has a 1.0 s delay commented **"1 sec delay to fix disk
only bug"** (E1180). It runs on the "In Game" branch of a `SeqCond_CompareBool` (E1317),
not the cinema branch this route took. `StoryMode_Headers` has another 1.0 s (E1182).
`SeqAct_SyncGamePlayAsync` has a second input from the music load
(`Finished_Audio_Music_Load`, E159, fired by `AUDIO_Music_003.xxx` E39 after
`Load_Music`). In both runs it still finished 5.0 s after `Start_Game` (§2), so in
practice the 5.0 s delay sets the time.

## 2. Where the time goes (measured)

Two short scripted runs of the 1.6.0-era build (`out/build/.../wweallstars.exe`, HEAD
`bc586f2`, Native, 2560x1440). Route: `scripts/idle-route.ps1 -Kind 1v1` (Hogan vs Cena,
`Arena_001`, Standard). Flags: `--wwe_tick_log=true --log_file_opens=.xxx`, a 250 ms
process I/O and CPU sampler, and in run 2 frame dumps every 60 frames. Both processes were
closed and confirmed gone. Logs: `analysis/session-loadtime1/`,
`analysis/session-loadtime2/` (gitignored).

### Run 1: intro and entrances watched (`session-loadtime1`)

| Time | Event (log line) |
|---|---|
| 15:52:07.137 | arena confirmed (`frame 7000: press A`, line 704) |
| 07.56–07.96 | `loadCoachingTips.xxx`, `Arena_001.xxx` opened (line 709); 9.6 MB read; one 117 ms tick |
| **07.96–09.86** | **no reads, game thread in the limiter 978 ms of 1 s** (`game tick 1s … limiter 978 ms`) = the 0.1 + 2.0 s Kismet wait |
| 09.86–11.63 | 26 packages opened (arena cinema assets, intro), ~31 MB; ticks of 22–43 ms; two 0.5 s Kismet gaps |
| 11.78 | intro starts (`1/30 flag 1`, 116.8 ms tick): **4.65 s after confirm** |
| 11.78–23.86 | arena intro (12.1 s) |
| 23.86–46.5, 47.06–04.13 | entrances, Hogan (~22.6 s), Cena (~17 s); each loads its packages at its start |
| 15:53:04.79–08.56 | 50 packages, ~65 MB; **840.0 ms tick** (work 437.1, render thread 387.7; line 952) and **296.3 ms tick** (line 998). These two are the only blocking ticks |
| ~08.47 | `Start_Game` (inferred: `Default_Weapons` at 08.35 is `Match_Rules`' last group) |
| **13.478** | 28.7 ms tick = `ForceGarbageCollection` (line 1015), **5.0 s after `Start_Game`** |
| ~16.5 → ~22 | loading screen off, taunts, bell (inferred from run 2's offsets) |

The draw count turns static (two idle pads in a live match) between frames 11400
(15:53:21.3) and 11700 (15:53:26.3). **Total ≈ 75 s** (inferred, ±3 s).

### Run 2: every cinematic skipped with A (`session-loadtime2`)

- Confirm 16:01:29.716 → intro 34.257: **4.54 s** (same shape as run 1).
- A at frame 7300 (0.7 s into the intro) did not skip. A at 7500 did, and one press each
  skipped the two entrances. Cinematics took 10.9 s in all. That number reflects the
  press schedule, not a floor.
- Entrance end (~45.2) → last package (48.64): 805.7 ms and 271.3 ms blocking ticks again.
- **Frames 8140–8620 (16:01:49.5–57.5) show the coaching-tips "LOADING" screen** (frame
  dumps). The sampler shows **no reads** after 48.9, and the game thread is idle
  (`work mean 1.2–1.6 ms`). This is the 5.0 s + 3.0 s Kismet wait.
- 8680 (58.5): wrestlers taunting in the ring. **HUD first in frame 8920 (16:02:02.5)**;
  frame 8860 has none.
- **Total ≈ 32.5 s.** About 13.6 s of it is fixed delay.

### Totals across both runs

| | Run 1 (watched) | Run 2 (skipped) |
|---|---|---|
| Pre-match load | ≈ 75 s | ≈ 32.5 s |
| Game-thread work in the load windows (Σ ticks × work mean from the per-second lines) | 0.88 + 1.87 s | 0.83 + 1.80 s |
| Of which the two blocking ticks | 840 + 296 ms | 806 + 271 ms |
| Package reads | ~41 MB before the intro + ~65 MB after (sampler) | same files |
| Renderer in load hitches | ≤ 4.6 ms per frame, 0 pipelines compiled, ≤ 0.79 MB textures per frame | same |

The two blocking ticks repeat in the owner's own session of 2026-10-15
(`analysis/high-framerate/owner-run-1930.log` lines 810 and 825: 851.8 ms, 292.4 ms).

**Where the wait goes in the skipped path (inferred from the above):**
13.6 s Kismet delays, ~4 s taunts and bell, ~11 s cinematics before the skip took
effect, ~3 s loading work, ~1 s menu and frame-paced streaming.

## 3. What is not a lever (measured or read)

- **I/O.** About 105 MB per pre-match load, read in bursts of under 1 s from the OS file
  cache. No throttle (`AsyncIOBandwidthLimit=0`). Synchronous emulated reads are not a
  bottleneck at this volume. A cold cache (first match after a reboot, HDD) was not
  measured (open question 4).
- **Shaders and pipelines.** All 135–138 pipelines are prebuilt before play (ADR-0019,
  log `prebuilding 135 optimized pipelines`). Every load hitch reports
  `pipelines compiled on this thread 0`.
- **Host renderer and texture upload.** ≤ 4.6 ms renderer time and ≤ 0.79 MB uploaded per
  hitch frame (`native_renderer.cpp:2523` hitch line). The 388 ms render-thread wait in the
  big tick is guest code: the hitch lines at frames 10490 and 10503 (211 and 208 ms) show
  0.4–0.6 ms of renderer time.
- **Decompression.** LZO1X is among the fastest decoders. At ~105 MB compressed per load,
  even a slow recompiled decoder costs well under a second, and it runs off the game
  thread (inferred; the guest decoder was not located, open question 3).
- **Graphics settings.** The runs used the session's default settings. Whatever they
  enable showed no pipeline or texture work in the load windows beyond the counts above.
  Remaster on and off were not compared.
- **Spin-waits.** The limiter and render-thread waits already sleep (`spin-loops.md`
  §5–6). The idle seconds are game-time waits, not host spinning.

## 4. Levers, ranked

"Hook" means a game-code hook in `src/` (function or midasm). A function that codegen
has not generated becomes a manifest seed with instruction-level evidence, never a stub
(strict mode). "Online-safe" asks whether the lever can change anything a peer sees:
tick count, game time, RNG, or SyncSim timing.

| # | Lever | Expected gain (pre-match) | Default or opt-in | Risk | Effort | Kind | Online-safe? |
|---|---|---|---|---|---|---|---|
| 1 | **Trim the fixed Kismet delays** (scale `SeqAct_Delay` durations and link `ActivateDelay`s during the pre-match load; at least the 2.0 s link and the 5.0 s / 3.0 s pair) | **8–13.6 s** (≈ 25–42 % of the skipped path) | **Opt-in skippable**: these are loading-screen minimum times | Medium. "1 sec delay to fix disk only bug" shows that at least one delay hides a race. The 5.0 s may hide the music-load or `SyncGamePlayAsync` race. Test all match types and 6 arenas | M | Hook on the `SeqAct_Delay` activation (and the sequence's delayed-link path) in the guest Kismet VM. Address not located yet, so a seed may be needed | **No.** It changes game ticks before the bell, and SyncSim already runs then (its step 3 spans the match intro, manifest comment at the `WweStepRateSample` hook). Off while `[0x83740590] != 0` (Net-Z session), as `At120Allowed()` does (`src/game_tick_log.cpp:327-334`) |
| 2 | **Auto-skip intro and entrances** using the game's own `SkipCinema` (the named event queued through `0x824DB1E0` while `0x83709514` is set, `0x824F03E8`; corrected in faster-starts ticket 02; `60fps-entrances.md` "the cinema-skip input") | ~1–3 s per cinematic over a player who skips by hand; **~50 s** over one who watches | **Opt-in skippable** | Low. It is the game's own skip path, and 60 fps entrances already reads the same flag | S | Hook in the existing tick hook (`FEngineLoop::Tick` `0x82417780`, `src/game_tick_log.cpp`); no seed | **No** (unverified). Peers would skip at different ticks. Off in a Net-Z session, or both peers must match (`online-latency.md` lever 6) |
| 3 | **Faster guest code in the blocking ticks** (codegen `-O3`, `x86-64-v3`, ThinLTO, PGO; `zero-stutter.md` §6) | 0.2–0.6 s of ~2.7 s work (inferred) | Default | Low. It is a build change, measured as a whole | M | Build/runtime | **Yes.** It changes host wall time per tick only. Tick count, game time and state are identical |
| 4 | **Frame-paced streaming.** Packages open 33–50 ms apart (2–3 ticks each, e.g. 16:01:32.436 → .503 → .553 …) while the game thread idles in the limiter (757 ms of 1 s at 15:52:10.56). Driving the async loader more than once per tick, or blocking-flushing it, would compress this | ≤ 1–1.5 s (inferred, unmeasured) | Default only if online-safe | Medium. The per-tick loader driver was not found. Loads would end in fewer ticks | M–L | Hook; probably a seed | **Probably not.** It changes how many ticks a load takes, so Kismet's "Finished Loading" fires on a different tick. Safe only if every affected step comes before SyncSim starts (open question 2) |
| 5 | **Native LZO1X** (replace the guest decoder with a host one) | ≤ 0.3 s (inferred) | Default | Low | S–M | Hook on the guest decoder. Not located (open question 3); it may need a seed | **Yes.** Same bytes out, off the game thread |
| 6 | Vendor startup movies (`[VendorStartup]` in TU2's `Coalesced.ini`: disclaimer 5.0 s unskippable, THQ 9.5 s, WWE 2.5 s) | 0 for pre-match; up to 17 s at boot | Opt-in skippable | Low | S | Data: the launcher's Coalesced merge (`launcher/src/Launcher.Core/CoalescedBundle.cs`, ADR-0026) | Yes (boot only) |

**Against the 50 % bar** (inferred):
- Levers 3–5 together, the speed-only set: ~0.5–2 s off ~32.5 s or ~75 s, which is
  **2–6 %**.
- Lever 1 alone: skipped path ~32.5 → ~19 s (**−42 %**).
- Levers 1 and 2: **~11–13 s** on either path, which is **−60 to −85 %**.

Lever 6 covers boot only, so it is not part of the pre-match result. It could join the
same toggle at no extra cost.

## 5. Open questions and the measurement for each

1. **What `SeqAct_SyncGamePlayAsync` waits for.** It finished exactly 5.0 s after
   `Start_Game` in run 1 (08.47 → 13.478), although the music-load input exists. Is it a
   local async-load barrier or a peer sync? *Measure:* a log-only hook on the op's activation
   and completion (find its native through the `USeqAct_SyncGamePlayAsync` class at string
   `0x821EBAA0`), plus the time `Finished_Audio_Music_Load` fires. Run offline and in
   `sync-test.ps1`.
2. **When SyncSim starts stepping relative to the pre-match load.** This decides whether
   levers 1, 2 and 4 could ever be online-safe. *Measure:* `--wwe_net_trace`-style
   log of the first SyncSim step (sim start `0x830D9528`, manifest comment) next to
   `--log_file_opens=.xxx` and the tick log in a two-copy `sync-test.ps1` session.
3. **Where the guest LZO decoder is and what it costs.** The LZO1X constants
   (`addi …,-0x801`, `-0x4000`) did not lead to it in TU2 (`0x828E41D8` matched and is
   unrelated). *Measure:* a Tracy or WPR capture of the 15:53:04–09 window. Attribute host
   samples to `sub_*` functions and confirm which thread runs them.
4. **Cold-cache I/O.** Both runs read from a warm file cache. *Measure:* the same route
   right after a reboot (or after `RAMMap` "Empty Standby List"), with the I/O sampler. Compare
   the burst lengths at 07.6–08.0 and 05.0–08.8.
5. **What fills the 840 ms tick.** It holds 437 ms of game-thread work and 388 ms of
   guest render-thread wait, right after `Template_Complete` → `GarbageCollectAndWait`
   (`Arena_001` E1345 → E1204). Is it the GC, or the render-thread flush of the
   unloaded cinema assets? *Measure:* a Tracy zone on the GC entry and the render-thread
   flush, same route.
6. **Is the "disk only bug" delay still needed on PC**, and are the 0.5 s waits ordering
   guards? *Measure:* lever 1 as a cvar, A/B over the 6 arenas and every match type,
   watching for missing ring, crowd or audio and the existing `stuck-audio-loop` symptoms.
7. **How fast a human can skip.** The A at 0.7 s into the intro did nothing; the next at
   4.0 s skipped. *Measure:* presses every 10 frames from intro start, to find the
   earliest frame the skip input is honoured.
8. **Does the game have an "entrances off" option?** If it does, `Level_Assets` would
   take the "In Game" branch with the 1 s "disk only bug" delay. *Measure:* walk the
   options menu once and repeat run 1 with the option set.
