# GitHub issues: triage and what to fix first

Date 2026-10-25. Question: what is the status of every GitHub issue on
`shaneomac1337/WWE-AllStars-Recomp`, open and closed, and what should be fixed
first? Research only: nothing was posted, labelled, closed or edited on GitHub,
and no code changed. GitHub was read through `gh` (`gh issue list --state all`,
`gh issue view <n> --comments`, `gh api .../issues/<n>/comments` and `/events`
for comment ids and times). The log attached to #9 was downloaded to the
session scratchpad, not the repo. This note follows
`github-issues-status-2026-10-23.md`; #1 to #6 are re-checked here, #7 to #10
are new.

**[V]** marks a fact checked against a primary source (issue URL, comment,
commit, repo file:line, or the #9 attachment). **[I]** marks an inference.

Releases (`gh release list`): 1.4.0 2026-10-17 (988 zip downloads), 1.5.0
2026-10-23 21:13 UTC (`eec7b22`, 92), 1.6.0 2026-10-24 10:52 UTC (`17c83e4`,
181). 1.6.1 (`c8b0116`) is pushed and zipped, with no GitHub release yet. [V]

## Answer

- **10 issues: 3 closed, 7 open.** Two new bug reports since 2026-10-23 (#8
  keyboard, #9 music) and two new requests (#7, closed; #10 Big Picture). [V]
- **#1 and #2 stay fixed and confirmed.** No activity after the owner closed
  them on 2026-10-23. [V]
- **#3 (Iris Xe, Native) is fixed but unconfirmed.** 1.5.0 shipped the vertex
  repack (`af87d35`) and the owner asked for a retest on 2026-10-23 21:14 UTC.
  The reporter hasn't answered. [V]
- **#6 and #8 are the same request from the same reporter** (wiktorkoc): the
  keyboard doesn't work. That is by design today: the SDK's keyboard driver is
  in the build but off (`mnk_mode` false), and nothing exposes it. It is the
  cheapest real fix on the list. [V]
- **#9 (music missing in some 4-way matches) is new and under-specified.** The
  attached log shows no music error, and its install folder says 1.5.0 while
  the issue says 1.6.0. [V]
- **#10 is mostly answered by the README.** `game\wweallstars.exe` starts
  without the launcher, and both sticks open the in-game settings. Controller
  navigation of the launcher is out of scope by the launcher spec. [V]
- **Replies are pending on #4, #6/#8, #9 and #10.** #3 and #5 wait on their
  reporters. [V]

**Fix first:** (1) a keyboard option for #6/#8; (2) Intel Native device loss
(#3): nudge for a retest on 1.6.1, and build the per-draw fault finder in the
meantime; (3) #9 music: ask for details, then try to reproduce in 4-way
Elimination; (4) reply to #10 and close it or turn it into a launcher ticket;
(5) #5: test the in-game cheat code once, then close it as needs-info if the
reporter stays silent; (6) leave #4 parked.

## 1. Every issue

Last activity is the issue's `updatedAt`. "Setup" is what the reporter gave.

| # | Title | State | Last activity | Setup | Status | Related ticket or commit |
|---|---|---|---|---|---|---|
| [1](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1) | Crashes when I select the iso | closed | 2026-10-23 | Intel UHD 620, latest driver, Win 11, Native, launcher 1.0/1.1 | **fixed and confirmed** (launcher freeze). Native on UHD 620 still fails, no log | `4c4efa6` (gpu-robustness 05, 1.3.0); `.scratch/gpu-robustness/issues/05-file-picker-logging-reduce-effects.md` |
| [2](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/2) | Online Multiplayer doesn't join or connect games | closed | 2026-10-23 | AMD, Win 11, Native, 1.1.0 | **fixed and confirmed** (by BitWLegacy, not the opener) | `aeae3e7`, `87d6373` (1.2.0) |
| [3](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3) | Crash immediately after starting the game | open | 2026-10-23 | Intel Iris Xe (0x9A49), driver 101.7092, Win 11, Native; last tested 1.4.0 | **fixed but unconfirmed** (a possible fix; cause unproven) | `af87d35` (1.5.0); gpu-robustness 01-03 (`74137a8`, `70cc35c`); `.scratch/gpu-robustness/issues/11-final-release.md` (ready-for-human) |
| [4](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/4) | Android Port | open | 2026-10-20 | n/a (AYN handhelds) | **feature request**, parked (ADR-0006) | `docs/research/android-port.md` (untracked); no ticket |
| [5](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/5) | character unlock command does not work | open | 2026-10-23 | none given | **support question**, needs-info | `.scratch/unlock-everything/` 01-03, all resolved |
| [6](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/6) | Keyboard support | open | 2026-10-24 | none in the body; see #8 | **feature request** (labelled enhancement) | none |
| [7](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/7) | please Update your svr 2011 recomp. | closed | 2026-10-24 | "potato" PC, no detail | **feature request**, out of scope (other project) | none |
| [8](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/8) | Keyboard problem | open | 2026-10-24 | NVIDIA RTX 5070, latest driver, Win 11, Native, 1.4.0-1.6.0 | **duplicate** of #6 | none |
| [9](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/9) | Music does not play during matches sometimes | open | 2026-10-24 | NVIDIA RTX 5080 Laptop GPU, driver 617.42, Win 11, Native, "1.6.0" (log: 1.5.0 folder) | **open bug**, needs-info | none; related: `.scratch/faster-starts/issues/03-shorter-loading-screens.md` |
| [10](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/10) | Supress launcher when launching game or allow controller to navigate the menu | open | 2026-10-24 | Windows, R5 5600X, RX 6750 XT, Steam Big Picture | **support question** (direct start exists) plus a **feature request** (launcher pad navigation) | `.scratch/launcher/spec.md:399` (out of scope) |

## 2. Per issue

### #1 and #2: closed, nothing new

No comments after the owner closed both on 2026-10-23
([#1 c6371033633](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6371033633),
#2 closed 17:40 UTC with no comment, issue events). The confirmations predate
the closures:
[#1 c6238282459](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6238282459)
("I tried the latest and it works now", 2026-10-17, after 1.3.0) and
[#2 c6258011386](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/2#issuecomment-6258011386)
("It works now for me", 2026-10-19, after 1.2.0). No regression has been
reported. [V]

The only open end is #1's last sentence: "the native renderer doesn't work, the
emulated one does" on UHD 620. The owner asked for a new issue with logs; none
was opened. It belongs with #3 (§4). [V]

### #3 Crash immediately after starting the game: fixed but unconfirmed

Timeline [V]:
- 2026-10-14: opened on 1.1.0 with `runtime - Copy.log` (`vkQueueSubmit failed`
  on the first frame that draws).
- 2026-10-17: owner asks for a crash zip and a `--vulkan_force_legacy_device`
  run ([c6231063313](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3#issuecomment-6231063313)).
- 2026-10-19: reporter on 1.4.0: "still crashes, playing on emulated is
  unplayable", with `crash-20261020-011907.zip`
  ([c6273035627](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3#issuecomment-6273035627)).
  That zip showed `VK_ERROR_DEVICE_LOST (vkWaitForFences)` with graphics
  pipeline libraries forced off, so GPL is ruled out
  (`github-issues-status-2026-10-23.md` §2 #3).
- 2026-10-23 21:14 UTC: owner says 1.5.0 has a possible fix and asks for a
  retest on Native
  ([c6374868833](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3#issuecomment-6374868833)).
  1.5.0 had been published one minute earlier (21:13 UTC).
- Nothing since. The latest comment came after the fix, but it is the owner's,
  so the reporter has neither confirmed nor reported a regression.

**What shipped.** `af87d35` makes `OnDrawVertices` widen the host vertex
stride and repack `DrawPrimitiveUP` vertices whose colour element reads past
the guest stride (`src/native/native_renderer.cpp:2044,2091,2289`;
`src/native/guest_decode.h:189,200`). It was the only Vulkan validation error
in the frame where the Iris Xe device is lost. Its own commit message says the
link to #3 is unproven. The 1.5.0 notes ask Intel users to report on #3
(`docs/releases/1.5.0.md:26-32`). [V]

**What does not exist yet.** The per-draw fault finder proposed on 2026-10-23
(submit and wait after each draw in the first frames, log the last one that
finished) is not in the code. The only Native debug switches are
`wwe_native_debug_target`, `_no_blend`, `_fail_submit` and `_skip_ps`
(`src/native/native_renderer.cpp:69,94,96,102`). The device-loss exit is
`EndSessionWithGpuFailure` (`:1650`), reached from the slot-fence wait
(`:913-915`) and the submit (`:2901-2904`). There is no Intel-specific code
path in `src/` or the launcher (grep `0x8086|intel`, no hits). [V]

**Likely cause, if the repack wasn't it.** A GPU-side fault on the first real
draws, from something the AMD driver tolerates: descriptor indexing with
update-after-bind and partially bound bindings, or buffer device addresses
(`src/native/vk_context.cpp:153-155` requires them). Sync validation never
confirmed it ran (`github-issues-status-2026-10-23.md` §2 #3). [I]

### #4 Android Port: feature request, parked

The reporter's follow-up "maybe emulating android on pc?"
([c6303966930](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/4#issuecomment-6303966930),
2026-10-20) is still unanswered (comments API: two comments, the owner's first).
Since then `docs/research/android-port.md` (untracked, 2026-10-23) has a
fuller answer: the x86_64 Android emulator tests the platform layer,
lifecycle, storage and 16 KB pages, but not arm64 code, phone GPU drivers or
performance (`android-port.md:36-45,348-349`). The prior art is the SVR2011
PC port's APK, reusable with credit (`android-port.md:19-23`). ADR-0006 lists
no Android target (`docs/adr/0006-platforms.md:7`). [V]

### #5 character unlock command does not work: support question, needs-info

Body: "It just won't work, no matter what." The owner replied 2026-10-23
21:33 UTC pointing to the launcher's Unlock everything and asking for details
([c6375174240](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/5#issuecomment-6375174240)).
No answer since. [V]

The launcher option is registered at `src/settings/registry.h:138` (GAMEPLAY);
all three unlock-everything tickets are resolved
(`.scratch/unlock-everything/issues/01-03`, `Status: resolved`). The in-game
CHEATS code `UnlockAll` was never played through on this build; the spec
forbids using it in tests because it writes the save
(`.scratch/unlock-everything/spec.md:52`). The word "command" fits a cheat code
better than a launcher toggle. A keyboard-only player can't enter a cheat code
at all (see #6), which may be the real cause. [V for the code and tickets, I
for the link to #6]

### #6 Keyboard support and #8 Keyboard problem: one request, duplicate

- #6 (2026-10-23, labelled enhancement): "full support for keyboard because
  now it isn't work ... svr 07,08,09,10,11 have keyboard full support ... hope
  can also change all buttons". `updatedAt` is 2026-10-24 13:19 with no comment
  or event then, so the body was edited (it now names the SVR games). [V for
  the text, I for the edit]
- #8 (2026-10-24, same author wiktorkoc): RTX 5070, Win 11, Native, versions
  "1.6.0,1.5.0,1.4.0 etc": "buttons in port don't react, can't do anything".
  Opened as a bug; the owner relabelled it enhancement 2026-10-24 18:52 UTC
  (events). No comments on either. [V]

**Code.** The keyboard works only when a cvar turns it on:
- The SDK adds its keyboard/mouse-to-controller driver to every input system
  (`.tools/rexglue-source/src/input/input_system.cpp:506-510`), but
  `IsEnabled()` returns `mnk_mode`, default `false`
  (`.tools/rexglue-source/src/input/mnk/mnk_input_driver.cpp:26,199-201`).
  Bindings are the `keybind_*` cvars (`:33-59`): WASD left stick, Space or
  `;` A, Backspace or `'` B, L X, P Y, Q/E triggers, 1/3 shoulders, arrows
  right stick, Shift+arrows D-pad, Return Start, Tab Back. Input needs window
  focus (`:484,500`). [V]
- `mnk_mode` is not in the settings registry (no `mnk_mode` hit in `src/` or
  `launcher/src/`), and the launcher spec left it out of v1
  (`.scratch/launcher/spec.md:270,402`). The README only says "A controller is
  recommended" (`README.md:210`). [V]
- It works in practice: owner sessions used `--mnk_mode=true
  --wwe_keyboard_player2=true` (`.scratch/high-framerate/issues/07-climb-sink.md:57-59`;
  `src/script_input.cpp:22-23`). An earlier attempt to drive it with
  synthetic `keybd_event` presses failed
  (`.scratch/playable-build/issues/01-classic-exhibition-match.md:74`), which
  is about the injection, not the driver. [V for the notes, I for the reading]
- The settings file is flat `key = value` lines
  (`analysis/session-prematch-all/userdata/wweallstars.settings.toml`), the
  game loads it with `rex::cvar::LoadConfig` (`src/settings/settings_page.cpp:350`),
  and the launcher keeps lines it doesn't show
  (`launcher/src/Launcher.Core/SettingsFile.cs:6-10`). So `mnk_mode = true` in
  `userdata\wweallstars.settings.toml` should enable the keyboard today, from
  the launcher too. Not tested end to end. [V for the code, I that it works]

No `.scratch` ticket covers it. ROADMAP's v1.0 list names keyboard and mouse
(`docs/ROADMAP.md:25`), and a Reddit commenter asked for keyboard mapping
(`community-feedback.md:58`). [V]

### #7 please Update your svr 2011 recomp: closed, out of scope

A request to keep working on the owner's SVR2011 recomp; it also says "Native
renderer saved us potato users". The owner pointed to KaikoClanworth1's port
and closed it the same day
([c6380474378](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/7#issuecomment-6380474378)).
Nothing to do here. [V]

### #9 Music does not play during matches sometimes: open bug, needs-info

**Report** (2026-10-24 18:19 UTC, Quantum1997): "1.6.0", RTX 5080, Win 11,
Native, Exhibition, Elimination Fatal 4 Way. "Sometimes the music won't start
when a match is started. So far it only happened during elimination fatal 4
way matches, (I think it also might've happened on a tag team ...). So far, it
has not happened during a 1v1". No crash. Attached `WWE.All.Stars.log.zip`
(`mods.log`, `runtime.log`). No comments. [V]

**What the log shows** (`runtime.log`, 1,245 lines, 20:58:54 to 21:13:35 local) [V]:
- Install folder `C:\...\WWEAllStarsRecomp-1.5.0-eec7b22-win-x64` (line 1).
  But the settings are the 1.6.0 Ultra preset: render size 3840x2160 on a
  2560x1600 window (lines 150, 174) and CAS 0.25 (line 2), which is Ultra's
  `{"3", "0.25", "5"}` (`src/settings/registry.h:25`). So 1.6.0 was probably
  unpacked over a 1.5.0 folder, as the README's update step says
  (`README.md:231-233`). The runtime log has no version line, so this can't be
  proven: only the crash manifest records it (`src/crash_report.cpp:206`). [I]
- Mods: 0 (`mods.log`). DLC0-2 installed (lines 272-274). Signed in online,
  but the match was offline. Xbox One pad over SDL (line 215).
- One arena load, `Arena_001` at 21:00:32 (line 392); match play runs to about
  21:13 (vibration events). The log doesn't say which match type this was or
  whether its music played.
- 51 `XmaContext N: There are no bits to copy!` errors, in groups of three
  contexts (9-11, then 12-14), starting 8 s after the arena load (line 435).
  This is normal for this build: every exhibition logs 33-42 of them
  (`docs/research/stuck-audio-loop.md:25`;
  `.scratch/playable-build/issues/15-stale-xma-read-counter.md:62`), and the
  owner's matches have music. So they are not evidence for #9. [V for the
  counts, I for the conclusion]
- No audio error, no device change, no stall. `xma-stats` shows the stale-ring
  path in use (`not_valid_full_ring` rising, e.g. line 809), as in every run
  since playable-build 15-17. [V]

**Likely causes, unranked** [I]:
1. A load race. The match's music is a separate load: `SeqAct_SyncGamePlayAsync`
   has a second input `Finished_Audio_Music_Load`, fired by
   `AUDIO_Music_003.xxx` after `Load_Music` (`docs/research/loading-times.md:89-92`).
   A 4-way match loads four wrestlers, so its music load finishes later. If
   the music cue fires before the load completes, it plays nothing.
2. An XMA/FMOD voice-allocation edge: a 4-way match has more simultaneous
   voices, and the emulated XMA decoder reads stale rings when a context runs
   dry (`stuck-audio-loop.md:18-21`).
3. An original-game bug. Not checked against Xenia or an Xbox 360.

**Relation to 1.6.1.** 1.6.1's opt-in "Shorter loading screens" cuts the 5.0 s
delay right before `SeqAct_SyncGamePlayAsync`, the one the faster-starts spec
called "likeliest to hide a race with music loading"
(`.scratch/faster-starts/spec.md:135-138`). Ticket 03 ran 4-way matches with
it and heard the music cue, so it found no race
(`.scratch/faster-starts/issues/03-shorter-loading-screens.md:35-37`). #9 was
reported before 1.6.1 with that option absent, so it is not the cause. But
if cause 1 is real, the option could make it more frequent. [V for the
tickets, I for the risk]

**Needs RE?** Finding which event starts match music and whether it can fire
before the load finishes needs Ghidra on the Kismet/FMOD path. Not done here.

### #10 Suppress launcher or allow controller navigation: support question plus request

Body (2026-10-24 23:23 UTC, thomasman52): Windows, R5 5600X, RX 6750 XT, Steam
Big Picture; "a way to launch the game directly or a way for the controller to
navigate the launcher menu". No comments. [V]

**Already possible:** after first setup, `game\wweallstars.exe` starts without
the launcher, "or add it to Steam (Game Mode on a Steam Deck)"
(`README.md:235-242`; the exe resolves its folders itself,
`src/wweallstars_app.h:67-80`; documented since 1.3.0,
`docs/releases/1.3.0.md:29-31`). Settings are reachable in game: "Press F4, or
click both sticks, to open the PC SETTINGS page" (`README.md:113`; the combo
check is `src/settings/settings_page.cpp:305-318`). [V]

**Not possible:** pad navigation of the launcher. The launcher spec lists
"Controller navigation and a Big Picture mode in the launcher" as out of scope
(`.scratch/launcher/spec.md:399`). DLC, mods and repair still need the
launcher (`README.md:241-242`). [V]

## 3. Fix first, ranked

### 1. Keyboard as a controller (#6, #8)

- **Why first.** A keyboard-only player can't play at all (#8: "can't do
  anything"). Two issues, plus a Reddit request. The driver already ships, so
  it is the most tractable fix here, and the info is complete. [V]
- **Cause.** `mnk_mode` defaults to false and nothing exposes it
  (`mnk_input_driver.cpp:26,199-201`; no registry entry). [V]
- **Next step.**
  1. Check `mnk_mode = true` in `userdata\wweallstars.settings.toml` on a
     release build, from the launcher, through a menu and a match. 10 minutes.
     If it works, reply with it today (§5).
  2. Add a "Keyboard as controller" row to CONTROLS next to the vibration rows
     (`src/settings/registry.h:131-137`), with the schema tests
     (`launcher/tests/Launcher.Tests/SettingsSchemaTests.cs`,
     `tests/core/test_settings_registry.cpp`), and list the default keys in the
     README. About half a day. Consider turning it on by default: the driver
     only acts on key presses with the window focused (`:484,500`). Check
     first that F4, F12 and Alt+Enter don't collide with any `keybind_*`. [I]
  3. Rebinding ("change all buttons") is a key-binding page writing the
     `keybind_*` cvars, several days. Ticket it separately. [I]
- **Tracker.** No ticket yet; this needs one (a small feature, `ready-for-agent`).

### 2. Intel Native device loss (#3, #1's Native note)

- **Why second.** Most severe (Iris Xe players can't play: Native loses the
  device and Emulated is "unplayable",
  [c6273035627](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3#issuecomment-6273035627)),
  and Intel laptops are the low-end audience. But two reporters only, a fix
  already shipped and untested, and no Intel hardware here. [V for the quote,
  I for the ranking]
- **Cause.** Unknown. The vertex-stride bug was fixed in `af87d35`; if 1.5.0+
  still crashes, suspect descriptor indexing or buffer device addresses
  (`src/native/vk_context.cpp:153-155`). [I]
- **Next step.**
  1. When 1.6.1 is published, ask the #3 reporter to retest on it (one
     comment, §5). Leave #3 open until they answer.
  2. Meanwhile build the per-draw fault finder: a cvar next to
     `wwe_native_debug_fail_submit` (`src/native/native_renderer.cpp:96`) that
     submits and waits after each draw for the first N frames and logs the last
     draw that finished (shader hashes, pass), using the existing wait path
     (`:2607-2609`). About half a day. It turns the next crash report into a
     named culprit. [I]
  3. Run sync validation properly once (`reference-vulkan-validation` memory;
     the 2026-10-23 run never confirmed sync was on). [I]
- **Tracker.** `.scratch/gpu-robustness/issues/11-final-release.md` is
  `ready-for-human` but is about the Reddit reply, not this. The fault finder
  needs its own ticket.

### 3. Music missing in 4-way matches (#9)

- **Why third.** Players hit it in multi-man matches, which are common, but it
  is intermittent, not a crash, and there is one report with no failing
  session identified. Not tractable until it reproduces. [I]
- **Cause.** Unknown; a music-load race is the first suspect
  (`loading-times.md:89-92`). [I]
- **Next step.**
  1. Ask the reporter (§5): the exact version, whether the attached session
     had a match without music and which one, the settings (60 fps entrances,
     120 fps matches), and a log from a session where it happens, with
     `--log_file_opens=Music --wwe_faster_starts_trace=true` (both cvars ship:
     `.tools/rexglue-source/src/kernel/xboxkrnl/xboxkrnl_io.cpp:33`;
     `src/faster_starts.cpp:30`). They can be set as `REX_<CVAR>` environment
     variables when starting through the launcher.
  2. Try to reproduce: five offline Elimination Fatal 4-Way matches on the
     owner's build with the same two flags, with Shorter loading screens off
     and then on. Compare when the music package opens with when the
     `Game_Settings` delays fire. If it reproduces, the Kismet/FMOD path needs
     Ghidra RE.
  3. Before publishing 1.6.1, nothing is needed: the option is off by default,
     and ticket 03 heard music in 4-way runs.
  4. Small, separate: log the version and commit at startup, as the crash
     manifest does (`src/crash_report.cpp:206`), so a log shows its build. #9
     shows the cost: its folder and settings disagree.
- **Tracker.** No ticket. Open one in a new `.scratch/` feature when it
  reproduces.

### 4. Big Picture start (#10)

- **Why fourth.** Already possible (README), so a reply solves it; the
  launcher pad navigation part is a nice-to-have. [V]
- **Next step.** Reply (§5). Leave it open as an enhancement only if the
  owner wants launcher pad navigation; otherwise close after the reply. A
  cheap middle ground: a launcher "Add to Steam" button that points at
  `game\wweallstars.exe` (also out of scope in `.scratch/launcher/spec.md:400`). [I]

### 5. Unlock (#5)

- **Next step.** Wait for the reporter. If there is no reply in a week, close
  as needs-info. The owner can play `UnlockAll` once on a backed-up profile
  (the cheat writes the save, `.scratch/unlock-everything/spec.md:52`) to know
  whether the cheat works on TU2. If keyboard is the cause, ranked item 1 fixes
  it. [I]

### 6. Android (#4)

- **Next step.** Answer the follow-up (§5) and keep it open as the request it
  is. No work until the owner decides on a phone or a remote device
  (`android-port.md:36-45`). [I]

## 4. Clusters

- **Keyboard input off by default: #6, #8, possibly #5.** One root cause:
  `mnk_mode` false and unexposed. #8 is a duplicate of #6 (same author, same
  request). #5 may be a keyboard player who can't enter the cheat code;
  unconfirmed. [V for #6/#8, I for #5]
- **Intel iGPU on Native: #3, #1 (closing comment).** Same family: Intel
  Windows driver, Native only, Emulated works or is too slow. Only #3 has logs
  ([#1 c6238282459](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6238282459)). [I]
- **Launcher friction for pad-only players: #10, plus the Reddit request
  "Game exe without having to use the launcher"** (`community-feedback.md:58`).
  Answered by direct start; the docs could make it more visible. [V]
- **Requests about other projects: #7.** Single issue, closed.

## 5. Replies the owner should post

Nothing was posted from here. Suggested text; adjust the tone as you like.

**#6 (and close #8 as its duplicate).** Check step 1 of §3 item 1 first.

> Thanks for the report. Keyboard play is in the game but switched off for now. To turn it on, open `userdata\wweallstars.settings.toml` in your game folder with Notepad, add a line `mnk_mode = true`, save, and start the game. Default keys: WASD move, Space = A, Backspace = B, L = X, P = Y, Q/E = triggers, 1/3 = bumpers, arrow keys = right stick, Shift+arrows = D-pad, Enter = Start, Tab = Back. A launcher option for it is coming in the next release, and changing the keys is on the list after that. I'm closing #8 as a duplicate of this one.

**#9.**

> Thanks for the report and the log. The log doesn't show an audio error, so I need to know which match it belongs to. Could you tell me: the exact version (the folder in your log is named 1.5.0, but you mention 1.6.0), whether one of the matches in that session had no music, and which settings you use under GAMEPLAY (60 fps entrances, 120 fps matches)? If it happens again, please attach the log from that session right away (`logs\runtime.log`, or the launcher's "open log" button) and say which match had no music. I'll try to reproduce it in Elimination Fatal 4-Way matches here.

**#10.**

> You can skip the launcher already: after the first setup, add `game\wweallstars.exe` from your game folder to Steam as a non-Steam game and launch it from Big Picture. It uses the settings you chose in the launcher. In game, click both sticks (or press F4) to open the PC SETTINGS page with your controller. You still need the launcher to add DLC or mods, or to repair an install. Controller navigation in the launcher itself isn't planned yet; I'll keep this open as a request.

**#4.**

> An Android emulator on a PC would help with part of it (the app shell, storage, startup), but not with what decides whether it runs well: the ARM build and phone GPU drivers. Those need real devices, so I can't promise it yet. I'm keeping this open as a request.

**#3 (when 1.6.1 is published, only if the reporter is still silent).**

> 1.6.1 is out and includes the 1.5.0 change for Intel GPUs. If you get a chance, please try it on Native and tell me whether it starts. If it still crashes, the new crash report will help a lot.

**#5.** No reply needed now; the owner asked on 2026-10-23. If still silent by
about 2026-11-01, close as needs-info with a one-line note that it can be
reopened with the version, the launcher option or cheat code used, and the log.

**#1, #2, #7, #8 (after closing as duplicate).** None.

## 6. Sources

| Source | Read | Notes |
|---|---|---|
| `gh issue list --state all --limit 200` | 2026-10-25 | #1-#10 |
| `gh issue view <n> --comments`, `--json body`, `gh api .../issues/<n>/comments`, `/events`, `/timeline` | 2026-10-25 | comment ids and times above |
| `gh release list`, `gh release view v1.4.0/v1.5.0/v1.6.0 --json assets` | 2026-10-25 | dates, download counts |
| #9 attachment `WWE.All.Stars.log.zip` (`runtime.log`, `mods.log`) | 2026-10-25 | scratchpad only |
| `git log` (`af87d35`, `eec7b22`, `17c83e4`, `c8b0116`), `docs/releases/1.5.0.md`, `1.6.0.md`, `1.6.1.md` | 2026-10-25 | |
| `.scratch/` (gpu-robustness, unlock-everything, launcher, faster-starts, high-framerate, playable-build) | 2026-10-25 | ticket statuses |
| `src/`, `launcher/src/`, `.tools/rexglue-source/src/input/` | 2026-10-25 | file:line above |
| `docs/research/github-issues-status-2026-10-23.md`, `loading-times.md`, `stuck-audio-loop.md`, `android-port.md`, `community-feedback.md` | 2026-10-25 | |
