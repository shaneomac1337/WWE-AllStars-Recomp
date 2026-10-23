# GitHub issues: status and what to fix next

Date 2026-10-23. Question: what is the real status of every GitHub issue on
`shaneomac1337/WWE-AllStars-Recomp`, and what should be fixed next? Research
only: nothing was posted, labelled, closed or edited on GitHub, and no code
changed. GitHub was read through `gh` (authenticated as the owner). The crash
report and log attached to #3 were downloaded to the session scratchpad, not
the repo.

**[V]** marks a fact checked against a primary source (issue URL, commit,
repo file:line, or the #3 attachments). **[I]** marks an inference.

## Answer

- **6 issues: 2 closed, 4 open.** No pull requests. Releases: 1.0.0 to 1.4.0;
  1.4.0 (2026-10-17) is latest with 931 downloads of the zip. [V]
- **#1 and #2 are fixed and confirmed by a reporter**, and the owner closed
  both today (2026-10-23 17:40-17:41 UTC). The owner notes are right for both. [V]
- **#3 (Iris Xe) is still broken, and the 1.3.0 lead is ruled out.** The
  reporter ran 1.4.0 with `--vulkan_force_legacy_device` as asked: pipeline
  libraries were off and Native still lost the device on the first frame with
  real draws. They also say Emulated is "unplayable", so this player has no way
  to play. Their reply (2026-10-19) has **no owner answer yet**. [V]
- **#1's reporter says Native doesn't work on their UHD 620 either** (Emulated
  does). The owner closed #1 and asked for a new issue. So two Intel iGPU
  players fail on Native. [V]
- **#5 (unlock) and #6 (keyboard) are new and unanswered.** #6 is cheap: the
  SDK's keyboard-as-controller driver is already in the build behind
  `mnk_mode` (off), and nothing in the launcher or README mentions it. [V]
- **#4 (Android)** has an unanswered follow-up question. [V]
- **The online-latency work (ecdb737) is on `main` but in no release.** No
  issue asks for it directly. #2 only mentioned latency outside Europe, which
  is a different cause: the server's location. [V]

**Ranked next:** (1) #3/Intel Native device loss, starting with things we can do
ourselves; (2) a keyboard option for #6; (3) answer #5 and test the in-game
cheat code; (4) ship 1.5.0 with online-latency plus (2); (5) leave #4 parked.
Replies are pending on #3, #5, #6 and #4 (§3).

## 1. Sources

| Source | Read | Notes |
|---|---|---|
| `gh issue list --state all --limit 200` | 2026-10-23 | #1-#6 |
| `gh issue view <n> --comments`, `gh api .../issues/<n>/comments`, `.../events` | 2026-10-23 | comment ids and times below |
| `gh pr list --state all` | 2026-10-23 | none |
| `gh release list`, `gh release view v1.4.0` | 2026-10-23 | 1.0.0 to 1.4.0 |
| #3 attachments: `runtime - Copy.log` (1.1.0) and `crash-20261020-011907.zip` (1.4.0) | 2026-10-23 | scratchpad only |
| `git log`, `.scratch/gpu-robustness/`, `.scratch/unlock-everything/`, `.scratch/online-latency/`, `docs/research/`, SDK at `.tools/rexglue-source` (f5337cd) | 2026-10-23 | |

## 2. Per issue

### #1 Crashes when I select the iso: closed, fixed-and-confirmed

<https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1>, sxdot,
opened 2026-10-11.

**Report.** Version 1.0, "Intel UHD 620 w/ latest driver", Windows 11,
Native, "Launcher only": "Launcher crashes when I pick the USA/Europe iso",
"There is no log made" (issue body). On 1.1: "Still crashes when selecting the
ISO or Title Update file. The entire app just becomes unresponsive"
([c6113493972](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6113493972)),
"Even just changing the game data path freezes it"
([c6113513576](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6113513576)). [V]

**What was done.** File-picker logging and "reduce effects" in 4c4efa6
(gpu-robustness 05), released in 1.3.0. The owner's reply took back the shader
guess and asked for `launcher.previous.log` and a `--reduce-effects` run
([c6231063131](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6231063131),
2026-10-17; recorded in 1900c54). [V]

**Outcome.** "I tried the latest and it works now. Though the native renderer
doesn't work, the emulated one does"
([c6238282459](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6238282459),
2026-10-17 18:06 UTC, five hours after 1.4.0 was published). The owner closed
it, asking for a new issue with logs for Native
([c6371033633](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/1#issuecomment-6371033633),
closed 2026-10-23 17:41). [V]

**Open ends.**
- We don't know what fixed the freeze. The reporter didn't say whether they
  used reduce effects, and no log was attached. Unless the freeze comes back,
  there is nothing to act on. [I]
- The Native failure on UHD 620 is not reported in detail. It is probably the
  same family as #3 (Intel Windows driver, Native only), but there is no log to
  show it. [I] It is ranked together with #3 below.

### #2 Online Multiplayer doesn't join or connect games: closed, fixed-and-confirmed

<https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/2>,
kamikaze-black, opened 2026-10-14.

**Report.** 1.1.0, AMD, Windows 11, Native, Online: "I cannot see lobbies my
friends made nor any new lobbies. The random players that are active on the
lobby are all 1v1 on smackdown and unable to join. Ranked server is completely
empty" (issue body). BitWLegacy: "same thing is happening to my friends and
me" ([c6162039911](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/2#issuecomment-6162039911)). [V]

**What was done.** aeae3e7 "Online: abandoned sessions expire (issue #2)" (the
server side, live for 1.1.0 too) and 87d6373 "Online: a copied install gets
its own XUID (issue #2)", both 2026-10-16 and shipped in 1.2.0. The owner
explained both, said Ranked doesn't work, and named the Czech server's latency
outside Europe
([c6220504957](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/2#issuecomment-6220504957)). [V]

**Outcome.** BitWLegacy: "It works now for me, thank you!"
([c6258011386](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/2#issuecomment-6258011386),
2026-10-19). The original reporter never replied. The owner closed it on
2026-10-23 17:40 with no closing comment (issue events). [V]

**Open ends, not bugs in this issue.**
- Ranked Match is still unsupported (owner, same comment). [V]
- Latency outside Europe depends on where the server is.
  `docs/research/free-server-hosting.md` (untracked, 2026-10-16) looks at moving it. [V]
- The online-latency work (30 Hz SyncSim step rate, network-thread waits,
  full rate from the start) is merged to `main` at ecdb737. All six tickets
  are resolved (`.scratch/online-latency/issues/01`-`06`, `**Status:** resolved`).
  It is not in any release. It smooths a regular hitch, which neither #2
  reporter mentioned. [V]
- The two-network match check is still `ready-for-human`
  (`.scratch/online-play/issues/19-two-network-match.md`), and one-sided
  reachability is `needs-info` (`.../20-one-sided-reachability.md`). [V]

### #3 Crash immediately after starting the game: open, needs-info (lead ruled out)

<https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3>,
ma8005370-cmd, opened 2026-10-14.

**Report.** 1.1.0, "Intel Iris xe latest driver", Windows 11, Native, "When
starting the game", a screenshot and `runtime - Copy.log` (issue body). [V]

**The 1.1.0 log** (attachment): Iris Xe, vendor 0x8086 device 0x9A49, driver
0x195BB4, listed twice as a physical device; "pipeline libraries on". There is
one frame with just a clear (`frame 0: drawn=0 ... clears=1`). Then two vertex
declarations and a depth target are set up, and right after the next presents:
`native renderer: vkQueueSubmit failed` (log lines 154, 171-178). [V]

**What was done.** 1.3.0 turns this into a clean GPU failure with a crash
report, and the launcher offers SWITCH TO EMULATED (gpu-robustness 01-03:
74137a8, 70cc35c; `launcher/src/Launcher.Core/GameExitReport.cs:83-86`;
`src/native/gpu_failure.h:14,40-43`). The owner's reply asked for the crash
zip and a run with `--vulkan_force_legacy_device`, suspecting graphics pipeline
libraries (GPL)
([c6231063313](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3#issuecomment-6231063313),
2026-10-17; `.scratch/gpu-robustness/issues/11-final-release.md:69-73`). [V]

**Reporter's answer, 2026-10-19:** "i've tried updating and doing the command
prompt but it still crashes, playing on emulated is unplayable", with the
game's GPU-failure dialog and `crash-20261020-011907.zip`
([c6273035627](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/3#issuecomment-6273035627)).
**No owner reply since** (4 days). [V]

**What the crash report shows** (zip contents, scratchpad):
- `manifest.txt`: `version: 1.4.0`, `commit: a1e6eef`, `renderer: native`,
  `gpu: Intel(R) Iris(R) Xe Graphics`, `driver: 0x00195BB4`,
  `reason: gpu failure VK_ERROR_DEVICE_LOST (vkWaitForFences)`. The install
  folder is still named `...-1.2.0-f4ac605-...`: they unpacked 1.4.0 over it. [V]
- `runtime.log`: driverInfo 101.7092. "Vulkan optional device features (forced
  off by vulkan_force_legacy_device)": graphicsPipelineLibrary,
  extendedDynamicState, presentWait/presentWait2 all off. "pipeline libraries
  off ...; full pipelines". 165 pipelines prebuilt, 0 failed (lines 106-112,
  138, 168). [V]
- The failure is in the same place as on 1.1.0: frame 0 is a lone clear (line
  151), the two declarations and the depth target follow (lines 170-172), one
  present (line 173), then `gpu failure VK_ERROR_DEVICE_LOST (vkWaitForFences)`
  (line 174). The wait is the per-slot fence at the start of the next frame
  (`src/native/native_renderer.cpp:871-873`). So the GPU faulted while
  executing the **first frame with real draws**. [V]
- `wweallstars.settings.toml`: supersample off, scale 1, CAS 0, anisotropy 2
  (the Low preset). [V]

**What this tells us.**
- **GPL is not the cause**: the device is lost with GPL and extended dynamic
  state forced off. The idea "turn GPL off on Intel automatically" (ticket 11)
  would not help. [V]
- Pipeline compilation is not the cause either: everything prebuilt with 0
  failures, and the render thread compiled 0 pipelines (line 176). The fault
  is at GPU execution time: a shader, a descriptor or an address the Intel
  driver can't handle. [I]
- Four shaders were missing from the pack before the failure, and live top-ups
  convert them (lines 156-159, 165-166). The same hashes (`D4CDC65DBE42D862`,
  `E9807558581FBC4D`) show up in the owner's AMD logs
  (`analysis/high-framerate/owner-*.log`), and AMD runs fine. They are normal
  for this boot path and not proven to matter. [V for the presence, I for the
  relevance]
- Native uses descriptor indexing with update-after-bind and partially bound
  bindings (`src/native/vk_context.cpp:56-80`) and buffer device addresses
  (`vk_context.cpp:141-143`). These are the usual suspects for
  driver-specific GPU faults. The Xenia-derived Emulated path doesn't use them
  the same way. Unverified. [I]
- Emulated is too slow on this machine, so for Iris Xe users Native is the only
  playable path. [V for the quote, I for the conclusion]

**Plausible next steps, cheapest first.**
1. **Run the Vulkan validation layer on our own AMD GPU.** No Khronos
   validation layer is installed here (`docs/research/tooling-recommendations.md:44`,
   row 5, "1 h"; `C:\VulkanSDK` is absent). Run the boot-to-first-match path
   with `--vulkan_validation_enabled` and look for errors around frame 1:
   descriptor misuse, out-of-range BDA, wrong image layouts, sync hazards.
   AMD tolerates undefined behaviour that Intel faults on, so this is the only
   step we can do ourselves, and it needs no Intel hardware. It is not GPU
   tuning (memory `feedback-dont-touch-gpu.md`). [I]
2. **Find the faulting draw from one reporter run.** Add a debug switch that
   submits and waits after every draw (or every N draws) in the first frames,
   and logs the last draw that completed: its pixel and vertex shader hashes and
   its pass. One run on the reporter's machine names the culprit. The switch
   belongs next to `wwe_native_debug_fail_submit`
   (`src/native/native_renderer.cpp:96`) and the submit at `:2757-2760`. About
   half a day. [I]
3. **Meanwhile, ask the reporter for one cheap test with existing switches.**
   For example `--wwe_native_debug_skip_ps=<hash>`
   (`src/native/native_renderer.cpp:102`, checked at `:2038-2039`) for the
   top-up pixel shader `E9807558581FBC4D`, and `--wwe_native_debug_null_slots`
   (`:91`) to bind default textures. Each run removes one suspect. How a
   UINT64 cvar parses hex was not checked; check it before posting. [I]
4. A borrowed Intel laptop (Iris Xe or UHD 620) would close the loop. Mesa on
   Linux would not reproduce a Windows-driver fault. [I]

**Step 1 done, 2026-10-23 (validation on our AMD card).** The layer is already
installed by MSYS2 (`C:\msys64\mingw64\bin\VkLayer_khronos_validation.dll`,
1.4.350); its registry entry points to a folder that doesn't exist, so set
`VK_LAYER_PATH=C:\msys64\mingw64\bin` and pass `--vulkan_validation_enabled=true`.
Two boots to the splash (`analysis/session-val`, `analysis/session-val2`,
the second with `--wwe_native_trace_frame=20`) gave only two errors: [V]
- `VUID-vkCmdBindVertexBuffers2-pStrides-06209`, from the first draws on.
  The splash quads are `DrawPrimitiveUP` draws (`draw vertices ... prim 13 count 4
  stride 16`) with declaration 40052100 `0.0@0+0:2C23A5 5.0@0+8:2C23A5
  10.0@0+16:1A23A6`: float2 position, float2 UV, **float4 colour at +16**, so
  location 8 reaches byte 32 but the stride is 16. This is the same declaration
  the reporter's failing frame creates (their `runtime.log`, the line after
  `planned pipelines prebuilt`). `UploadVertices` copies only `count * stride`
  bytes (`src/native/native_renderer.cpp:2172-2180`), so the last vertex's colour
  is read 16 bytes past the uploaded data. On the dynamic-stride path this
  breaks a Vulkan rule. On the reporter's legacy path (static strides) an
  attribute past the stride is allowed, and `robustBufferAccess` is on in both
  logs, so this **is a real bug but not proven to be the Iris Xe fault**. [V for
  the error and the declaration, I for the link to #3]
- `UNASSIGNED-Threading-MultipleThreads-Write` on `vkAcquireNextImageKHR`, once
  per boot, in the SDK presenter
  (`.tools/rexglue-source/src/ui/vulkan/vulkan_presenter.cpp:1663`). Two threads
  touch the swapchain at once at startup. Lower priority. [V]

**Fix (uncommitted, 2026-10-23).** The game's stride is the real one: the same
vertex shader `F8EEF827EF514AF3` draws with both declaration 40052100 (stride
16, float4 colour at +16) and 40058630 (stride 20, D3DCOLOR at +16), and
`DrawPrimitiveUP` passes 16 itself. So the colour overlaps the next vertex, and
Xenos fetches those bytes. `OnDrawVertices` now widens the host stride to the
widest attribute extent and repacks each vertex from its guest position
(`guest::HostVertexStride`, `GuestVertexBytes`, `RepackVertices` in
`src/native/guest_decode.h`; tests in `tests/native/test_guest_decode.cpp`).
After the fix the validation boot shows no stride error through frame 300
(1,970 draws, no draw skips); only the presenter threading error is left. Sync
validation was asked for three ways (`VK_VALIDATION_VALIDATE_SYNC`,
`VK_LAYER_VALIDATE_SYNC`, `vk_layer_settings.txt`) and reported nothing, but
the layer never confirmed it was on, so **sync is unchecked**. Every validation
boot left a `wweallstars.exe` stuck while exiting (not killable, holding the exe
and `rexruntimerd.dll`); a reboot clears them. [V]

The earlier text below predates the fix. No other validation errors through about 10 s of boot. Next: find what stride
the vertex shader's own vfetch uses for that colour (the ground truth on
Xenos), make the upload cover the largest attribute extent, rerun validation,
then step 2. Sync validation (`VK_VALIDATION_VALIDATE_SYNC=1`) was not run. [I]

**Effort.** Step 1: about 1 h of setup plus one run. Step 2: half a day plus a
reporter round trip. The fix itself is unknown until the culprit is named. [I]

### #4 Android Port: open, understood (deferred)

<https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/4>,
JonathanMM077, opened 2026-10-17. "Any chance we can get an android port? Saw
the SVR 2011 was able to and think would be great for AYN users" (body). Owner:
"quite difficult because I have no android phone at home"
([c6236947234](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/4#issuecomment-6236947234)).
Reporter: "Is there a way you can do it without an android? Like maybe
emulating android on pc?"
([c6303966930](https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/4#issuecomment-6303966930),
2026-10-20), **unanswered**. [V]

**Repo status.** Out of scope by ADR-0006 ("Windows x64 ... Linux and Steam
Deck ... No macOS"; Android not listed) and by the gpu-robustness spec's Out of
Scope. SVR2011's Android build is a native arm64 build with Turnip through
adrenotools (`docs/research/svr2011-pc-port.md:73,358`). Android was the most
requested feature on Reddit, from 9 people (`docs/research/community-feedback.md:55,126-130`). [V]

**Assessment.** An x86 Android emulator on a PC doesn't test what matters:
arm64 code generation and Adreno/Turnip GPU drivers on devices like the AYN
Odin. So the answer to the follow-up is "not usefully". Effort is large: a new
platform layer, packaging and input. SVR2011's port is prior art the owner may
reuse (memory `reference-svr2011-pc.md`). [I]

### #5 character unlock command does not work: open, needs-info

<https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/5>,
gozaoo-user, opened 2026-10-19, labelled enhancement. The whole body: "It just
won't work, no matter what." No version, no input device, no log, no comments. [V]

**Which "command" it means is unclear.** There are two candidates:
- **The launcher option "Unlock everything"** (GAMEPLAY tab, startup setting,
  off by default; `src/settings/registry.h:142-146`; hooks in
  `src/unlock_everything.cpp:101-115`). The owner played it through and it
  worked ("seems it works good",
  `.scratch/unlock-everything/issues/02-owner-playtest.md`). It needs a
  restart after it is changed (`startup=true`). [V]
- **The in-game CHEATS code `UnlockAll`** (Extras > CHEATS,
  L,Y,D,L,Y,X,L,X,Y,D,R,X,L,U,X,R). Our research found the code path intact in
  TU2 (`docs/research/unlock-everything.md:87-90,137-160`), but nobody has
  played it through on this build. One cheat site says the code "is disabled in
  newer game updates" (same file, :90). It needs D-pad and face-button input.
  Keyboard input is off by default (#6), so a keyboard-only player can't enter
  it at all. [V for the facts, I for the link to #6]

**Missing:** version, which of the two was tried, controller or keyboard, and a
session log (`logs\runtime.log`). **Our own check:** the owner enters
`UnlockAll` once on a test profile. The research warns that the real cheat
raises the Fantasy Warfare counter in the save (`unlock-everything.md:478-480`),
so back up the save first. Effort: a 5-minute check plus a reply. [I]

### #6 Keyboard support: open, understood (cheap)

<https://github.com/shaneomac1337/WWE-AllStars-Recomp/issues/6>, wiktorkoc,
opened 2026-10-23, labelled enhancement: "Bro plz add a full support for
keyboard because now it isn't work,hope can also change all buttons for that to
set it up however anyone like." No comments. [V]

**Repo status.**
- The SDK builds a keyboard/mouse-to-controller driver into every input system
  (`.tools/rexglue-source/src/input/input_system.cpp:506-510`). It is enabled
  only by the cvar `mnk_mode`, default false
  (`.tools/rexglue-source/src/input/mnk/mnk_input_driver.cpp:26,199-201`). The
  default bindings are cvars, `keybind_a` ... `keybind_start`
  (`mnk_input_driver.cpp:33-58`): for example Space = A, WASD = left stick,
  Shift+arrows = D-pad, Return = Start. [V]
- The project uses it only for development (`docs/boot-investigation.md:51`;
  `src/script_input.cpp:22-24`, `wwe_keyboard_player2`). `mnk_mode` is not in
  the settings registry, the launcher or the README (grep, no hits). [V]
- ROADMAP's v1.0 definition lists "keyboard and mouse" input
  (`docs/ROADMAP.md:25`). [V]
- The game reads `userdata\wweallstars.settings.toml` through
  `rex::cvar::LoadConfig` (`src/settings/settings_page.cpp:340`). The launcher
  keeps lines it doesn't know (`launcher/src/Launcher.Core/SettingsFile.cs:9`).
  `IsEnabled()` reads the cvar on every poll. So `mnk_mode = true` in that file,
  or `game\wweallstars.exe --mnk_mode=true`, should turn keyboard play on
  today. Untested. [V for the code, I that it works end to end]

**Plausible fix.**
1. A "Keyboard as controller" row on the CONTROLS tab: an entry in
   `src/settings/registry.h`, plus the schema tests
   (`launcher/tests/Launcher.Tests/SettingsSchemaTests.cs`,
   `tests/core/test_settings_registry.cpp`). Add the default key map to the
   README. About half a day. SVR2011 shipped the same thing (`6f64494`, per
   `docs/research/svr2011-pc-port.md:317`). [I]
2. Rebinding ("change all buttons") means a key-binding page writing the
   `keybind_*` cvars. Several days. A controller/keyboard mapping request also
   appeared on Reddit (pfhkob3, `community-feedback.md:58`). [I]

## 3. Replies the owner should post

Nothing was posted from here. Suggested points:

- **#3 (priority, reporter waiting since 2026-10-19).** Thank them. Say the
  report ruled out the GPU feature we suspected (it still fails with it off),
  and that the fault is on the first frame that draws. Then either ask for the
  existing-switch tests (§2 #3, step 3), or say a diagnostic build is coming
  that names the failing draw (step 2). Acknowledge that Emulated is too slow on
  Iris Xe.
- **#5.** Ask: version, launcher option or in-game cheat code, controller or
  keyboard, and `logs\runtime.log`. Point to Settings > GAMEPLAY > Unlock
  everything, and say the game must be restarted after it is turned on.
- **#6.** Say keyboard play exists but is hidden: start with
  `game\wweallstars.exe --mnk_mode=true` (once checked), give the default keys,
  and say a launcher option and later rebinding are planned.
- **#4.** Answer the follow-up: an emulator on a PC doesn't test the real
  arm64 and Adreno driver path, so it isn't a substitute. The request stays
  open.
- **Reddit (pfhkob3).** The 1.3.0 reply drafted in
  `.scratch/gpu-robustness/issues/11-final-release.md:61-74` is marked "still
  unposted". Reddit was not checked today, so this is unverified. [V for the
  ticket note]

## 4. Classification

| # | Title | State | Class |
|---|---|---|---|
| 1 | Crashes when I select the iso | closed 2026-10-23 | fixed-and-confirmed (launcher freeze); Native on UHD 620 still failing, unreported in detail |
| 2 | Online Multiplayer doesn't join or connect games | closed 2026-10-23 | fixed-and-confirmed (1 of 2 reporters) |
| 3 | Crash immediately after starting the game | open | open-needs-info: GPL lead ruled out, cause unknown, reply pending |
| 4 | Android Port | open | open-understood, deferred (ADR-0006) |
| 5 | character unlock command does not work | open | open-needs-info |
| 6 | Keyboard support | open | open-understood, cheap first step |
| — | online-latency (no issue) | main ecdb737 | fixed-in-main-unreleased |

## 5. What to fix next, ranked

1. **Intel Native device loss (#3, plus #1's Native note).** It has the most
   impact: two reporters, and on Iris Xe neither renderer is usable, so they
   can't play at all. Intel laptops are also the low-end audience the Low
   preset is for (`src/settings/gpu_suggestion.h:37`, per
   `next-steps-project-review.md`). Start with the validation-layer run on our
   AMD (about 1 h, reproducible here). Then add the per-draw fault-finder switch
   (about half a day) and send it to the #3 reporter. Risk: the fix may need a
   renderer change we can't test without Intel hardware.
2. **Keyboard option (#6).** Small effort, broad benefit, no RE needed: the
   driver is already in the build. Verify `--mnk_mode=true` first, then add the
   CONTROLS row and README keys. Rebinding comes later.
3. **#5 unlock.** Play the `UnlockAll` cheat code once on a backed-up profile
   (5 minutes) and reply asking for details. If the cheat code fails on TU2,
   that is a real bug. If it works, the issue is probably input (#6) or the
   restart rule.
4. **Release 1.5.0** with the online-latency work (ecdb737, owner-checked in
   tickets 05 and 06) and item 2. The fixes are already done; releasing them
   costs only the release work.
5. **#4 Android.** Leave it parked. It is large, can't be tested here, and is
   out of the platform scope. Reconsider only if the owner gets a device or
   reuses SVR2011's Android layer.
