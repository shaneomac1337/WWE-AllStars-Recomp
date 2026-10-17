# What to build next: a review of the project's own state

Date 2026-10-16, the day 1.2.0 shipped. Question: going by the project's
own state, what should be built next, and why? Research only: no code was
changed, nothing was committed and the game was not run. Sources: the repo at
`f4ac605`, the local tracker in `.scratch/`, the owner's memory notes
(`~/.claude/projects/D--Projects-WWE-ALLSTARS-RECOMP/memory/`), the public repo
`shaneomac1337/WWE-AllStars-Recomp` through `gh` (read 2026-10-16), and the
sister project `D:\Projects\SVR2011-RECOMP`.

**[V]** marks a fact checked against a primary source (file:line, ticket,
issue or API answer). **[I]** marks an inference.

## Answer

Ranked, highest value first:

| # | Candidate | Value | Effort | Risk |
|---|---|---|---|---|
| 1 | **Intel and other untested GPUs: survive and explain a GPU failure, then fix issues #1 and #3** | High: 2 of the 3 open public issues, both unanswered or stalled | Medium (fallback, crash report); unknown (root causes) | Medium: no Intel hardware here, so a tester is needed |
| 2 | **Crash reports players can attach** (minidump + symbolized stack, one "report a bug" zip) | High as an enabler: every remote bug so far arrived with no usable trace or none at all | Small (about 1 day) | Low |
| 3 | **Online: close the loop on issue #2** (the two-network check never run, one-sided reachability, the server's location) | Medium-high: real friend groups are trying it now | Small code (ticket 20), HITL (19), ops (hosting) | Medium: needs a second network or the reporters |
| 4 | **Ready-for-agent backlog and tracker hygiene** (zero-stutter 16, 19, 20, 21; 34 `ready-for-human` tickets, many for shipped work) | Medium: removes stale noise; 16 is an owner decision left undone since 2026-10-04 | Small | Low (16 needs a speed check) |
| 5 | **120 fps: the second running throw** (high-framerate 08) and the next 60-vs-120 sweep | Low-medium: experimental, off by default, needs a 120 Hz display | Medium (RE in progress) | Low |
| 6 | **Post-v1 roadmap item 3: mod and asset hooks**, with SVR2011-pc's content features as the model | Medium long-term, no demand on record yet | Large | Medium |

**Top pick: #1, done in the order "#2's crash report first, then the GPU-failure
fallback, then the Intel root causes".** The crash report is the fastest way
to make #1 tractable without Intel hardware, and the fallback turns an
immediate crash into a playable Emulated session plus a clear message.

## 1. Where the project stands

- **Releases.** 1.0.0 (2026-10-10), 1.1.0 (2026-10-11), 1.2.0 (2026-10-16).
  Zip downloads: 259, 616 and 29 (1.2.0 on its first day). 22 stars, 1 fork,
  Discussions off. [V] (`gh api repos/.../releases`, `gh api repos/...`,
  2026-10-16)
- **Momentum.** The last 60 commits are, in order: launch (v1.0.0, README,
  Reddit kit, `6ed938d`, `7857e3b`), 60 fps entrances and Unlock everything
  (1.1.0, `f530266`), then 120 fps matches (`d89412e`..`4aff63c`) and two
  online fixes for issue #2 (`aeae3e7`, `87d6373`, 1.2.0 `f4ac605`). Every
  feature since launch has been a player-facing enhancement done in the
  owner's "rapid" mode (memory `project-60fps-entrances.md`,
  `project-unlock-everything.md`). [V]
- **The roadmap.** Post-v1 order: online play, 60 fps, then "Mod and asset
  hooks" (`docs/ROADMAP.md:54-62`). Online shipped as experimental in 1.0.0
  (`docs/releases/1.0.0.md:23-24`) and frame rate work in 1.1.0/1.2.0, so item
  3 is the only roadmap item not started. The roadmap's own Status section
  stops at 2026-10-02 (`docs/ROADMAP.md:38-40`). [V]
- **What is untested.** "NVIDIA and Intel GPUs, Windows 10, desktop Linux"
  (`README.md:36`; `docs/releases/1.2.0.md:34-36`). Tested only on the
  owner's RX 7900 XT and a Steam Deck through Proton (`README.md:35`). [V]

### 1.1 Public issues (read 2026-10-16) [V]

| # | Title | Reporter's GPU | State |
|---|---|---|---|
| 1 | Crashes when I select the iso | Intel UHD 620, Win 11, 1.0 then 1.1 | Open. Launcher freezes on picking the ISO or TU, and on changing the game data path; "There is no log made". Owner guessed shader prebuild on Intel; reporter answered it freezes "as soon as you select a file" and asked for a contact channel. No reply since. |
| 2 | Online Multiplayer doesn't join or connect games | AMD, 1.1.0 | Open. Owner diagnosed ghost lobbies and shared XUIDs, fixed both (server + 1.2.0), asked for logs if it persists, and mentioned the CZ server's latency outside Europe. |
| 3 | Crash immediately after starting the game | Intel Iris Xe, 1.1.0 | Open, **no owner reply**. Log attached. |

Issue #3's log (downloaded to the session scratchpad, not the repo) shows the
GPU detected as Intel Iris Xe (vendor 0x8086, device 0x9A49) and listed
twice, preset suggested "low", 7,862 shader libraries and 125 pipelines
prebuilt with 0 failures, then `native renderer: vkQueueSubmit failed`
about 1 s after boot, on the first presents (log lines 9-11, 134, 147-148,
178). [V]

### 1.2 Open tickets in `.scratch/` [V]

Count of `Status:` lines across `.scratch/*/issues/*.md`: 161 resolved, 10
wontfix, **34 ready-for-human, 6 ready-for-agent, 5 needs-triage, 1
needs-info** (launcher-online uses `**Status:**` and adds one more
ready-for-human, 06). The ones that matter here:

| Ticket | Status | What it is / blocks |
|---|---|---|
| high-framerate/08 running throw | ready-for-agent (blocked by 07, resolved) | Overhead throw fixed; the second running throw still launches ~30 units low at 120 (`08-running-throw.md:70-89`). Shipped as a known issue (`1.2.0.md:11-12`). |
| zero-stutter/16 strict FP | ready-for-agent | `-ffp-model=strict -fno-strict-aliasing` never reached the generated code; owner chose on 2026-10-04 to add both and measure (`16-strict-fp-on-recompiled-code.md:29-35,52`). Not done. |
| zero-stutter/19 launcher asks the game for its device | ready-for-agent | First session after a driver update runs before the pipeline rebuild (`19-...md:10-20`). |
| zero-stutter/20 smoke.ps1 close, windowed scenes | ready-for-agent | Dev tooling; owner rule "never kill the game" (`20-...md:10-14`). |
| zero-stutter/21 AMD driver cache split by device profile | ready-for-agent | Device loss when one exe path changes device profile (`21-...md:10-25`). The trigger it names, updating from a pre-GPL 0.1.0 build, predates every public release [I]. |
| zero-stutter/18 guest read of 0x1C at boot | needs-triage, parked | 2 of ~13 starts on 2026-10-04 (`18-...md:15-30,43`). |
| online-play/19 match across two home connections; launcher-online/06 PC + Deck ship check | ready-for-human | Never run: "no remote tester for now" (`online-play/issues/19-two-network-match.md:10-12`; `launcher-online/issues/06-ship-check-pc-and-steam-deck.md:3-12`). |
| online-play/20 one-sided reachability | needs-triage | When only one side is reachable, the two sides settle on different routes and "the game sees two addresses for one peer" (`20-one-sided-reachability.md:10-16`). |
| online-play/13 4-player and CAS sharing | needs-triage | Spec milestone 5, after v1.0 (`.scratch/online-play/spec.md:72-73`). |
| low-end-preset/14 sign-off | ready-for-agent (owner) | Prepared 2026-10-05 on a staged release (`14-owner-sign-off.md:23-39`). |
| in-game-settings/09 borderless | needs-triage | May close as wontfix (`09-borderless-display-mode.md:14-20`). |
| playable-build/37 Emulated dark | needs-info | Reopened 2026-10-04, not reproduced (`37-...md:54-60`). |

Most of the 34 ready-for-human tickets are owner sign-offs and captures for
features that have since shipped in 1.0.0-1.2.0, for example zero-stutter
01-14 (prebuilt pipelines ship in 1.0.0, `1.0.0.md:19`), tu2-dlc 05-07
(release 1.0.0 is out), smooth-presentation 12-13. [V for the shipping, I
that the sign-offs are de facto done]

### 1.3 Owner constraints that shape the ranking [V]

- The owner plays by hand; no long scripted sessions (memory
  `feedback-owner-drives-game.md:8-12`). Agent GPU starts are approved one at
  a time, windowed (`progress-log.md:73`).
- Never tune the owner's GPU, not even as a low-end stand-in; low-end
  approximations come "from in-game means ... or real low-end
  hardware/testers" (`feedback-dont-touch-gpu.md:11-15`).
- Players never open firewalls; unreachable players use the relay
  (`feedback-no-firewall-changes.md:11-14`). Online is for friends; pick what
  works, licence aside (`feedback-online-license.md:11-15`).
- The owner tests on one AMD RX 7900 XT (`user-profile.md:11`).

## 2. Candidates

### 2.1 Intel and other untested GPUs (top pick)

**What.** Three parts, in this order:

1. **Native survives a GPU failure.** Today a failed `vkQueueSubmit` logs one
   line and returns (`src/native/native_renderer.cpp:2709-2711`); nothing
   switches renderer or tells the player. The fallback reasons the launcher
   understands are all start-up ones: shader pack problems and
   `vulkan_setup` (`src/native/renderer_fallback.h:24-31`). A crash exit gets
   `OfferEmulated: false` (`launcher/src/Launcher.Core/GameExitReport.cs:46-49`),
   so a player who crashes like #3 is never offered Emulated. Add a
   `device_lost`/`submit_failed` reason that ends the session cleanly with the
   fallback line the launcher already parses
   (`GameExitReport.cs:25-31`), so the launcher offers "Switch the renderer
   to Emulated". This is the SVR2011-pc idea of "stuck-GPU ... fallbacks"
   (`docs/research/svr2011-pc-port.md:420`, `:485`) applied to the one case
   this project still lacks. [V for the gap, I for the design]
2. **Issue #3, Iris Xe.** Find why the first submits fail. Inputs: the
   attached log (§1.1); the open question "Which ... Intel iGPU drivers expose
   every feature Native requires (`vk_context.cpp:153-160`)? Should those GPUs
   be in scope at all?" (`docs/research/performance-and-low-end-hardware.md:535-536`);
   Intel Arc lacks present_id2/wait2 (same file `:307`); the Vulkan validation
   layer is not installed on the dev machine
   (`docs/research/tooling-recommendations.md` table row 5). The driver
   listed the GPU twice in #3's log, worth checking against device selection.
   [V for the inputs, I for the lead]
3. **Issue #1, launcher freeze on UHD 620.** The launcher has no Vulkan, and
   the freeze comes before setup or any shader step, so the owner's shader
   guess (issue #1 comment) does not fit the report [I]. What does run when a
   file dialog closes: the window's `Activated` handler restarts the backdrop
   animations (`launcher/src/Launcher.App/MainWindow.axaml.cs:145`,
   `:361-368`), and picking a folder rebuilds the screen synchronously on the
   UI thread (`:428-434`). The ISO check itself runs off the UI thread
   (`:754-765`). A first lead is Avalonia's compositor on that Intel driver
   with the animated, Mica window (`:374-380`); this is unverified. The
   reporter offered to help debug.

**Evidence of value.** Two of three open issues are Intel crashes, and #3 has
no reply (§1.1). Laptops with Intel iGPUs are a common player machine, and
the README lists Intel as untested (`README.md:36`). The game already picks
the Low preset for an integrated GPU (`src/settings/gpu_suggestion.h:37`), so
the project intends to run there. [V]

**Effort and risk.** Part 1: about a day, testable at the exit-report seam
with xUnit and a fake log, the way the existing fallbacks are [I]. Parts 2-3:
unknown; the owner has no Intel GPU and must not use his own as a stand-in
(§1.3), so the loop runs through the reporters' logs or a borrowed laptop.
Candidate 2 shortens that loop.

**Dependencies.** Candidate 2 (crash reports) first. A decision the
performance note left open: are Intel iGPUs in scope, and if a driver lacks a
required feature, is Emulated the answer (`performance-and-low-end-hardware.md:535-536`)?

### 2.2 Crash reports players can attach

**What.** On a crash, write a minidump and a symbolized text stack next to
the session log, and give the launcher one action that zips the newest
session (log, report, settings, `vulkaninfo`-style device lines) for an issue.
SVR2011-pc does the first half: "minidump plus a symbolized text report in
`UserData\crashes`" with PDBs from `-gline-tables-only`
(`docs/research/svr2011-pc-port.md:360-362`; adoption row 9 at `:492`). This
project already builds with `-gline-tables-only`
(`.scratch/zero-stutter/issues/16-strict-fp-on-recompiled-code.md:18`) but has
no minidump or crash-report code: a search of `src/`, `launcher/src/` and
`patches/` for `MiniDumpWriteDump`/`minidump`/`crash_report` finds nothing.
[V]

**Evidence of value.** Issue #1: "There is no log made". Issue #3: a log that
stops at the submit failure with no stack. Issue #2: the owner had to ask for
"the time of the attempt (with your time zone)" and a zipped log folder
(issue #2, owner's 2026-10-16 comment). The launcher's crash message names
the exception code only (`GameExitReport.cs:33-43`). [V]

**Effort and risk.** About 1 day (SVR2011-pc's estimate, `svr2011-pc-port.md:492`).
Low risk: it runs only after a crash. A launcher-side log of its own would
also cover #1's "no log" case [I].

**Dependencies.** None. It is the cheapest step that helps candidates 1 and 3.

### 2.3 Online: close the loop on issue #2

**What.**

- **Hear back from #2.** The fixes are in (`aeae3e7` server, `87d6373` client,
  `docs/releases/1.2.0.md:14-21`); the owner asked for logs if it still fails.
  The memory note says "next: watch #2 for replies after 1.2.0"
  (`project-issue2-online.md:18`). [V]
- **Run the two-network match** that has never been played
  (online-play/19, launcher-online/06, §1.2). The #2 reporters are real
  two-network players; a session with one of them could stand in for the
  owner's Deck-on-hotspot plan (`19-two-network-match.md:23-59`). [I]
- **Ticket 20, one-sided reachability.** Each side probes on its own, so a
  pair where only one side is reachable ends on two routes
  (`20-one-sided-reachability.md:10-16`). That is the expected case for
  friends behind different home routers with UPnP off by default, and a
  plausible remaining cause of "doesn't join or connect" [I]. The ticket
  lists three options and an acceptance test with a one-direction drop switch
  (`:21-34`). Designs must stay relay-first, no firewall steps (§1.3).
- **Server location.** The owner told #2 the Czech VPS is slow outside
  Europe. `docs/research/free-server-hosting.md:15-31` finds Oracle Cloud
  Always Free the only $0 host that fits, with idle reclamation as its risk,
  and notes a relay between two US players would still sit in the EU unless a
  second region is added (`:94-98`). Moving the record needs no client
  release (`:29-31`). [V]

**Effort and risk.** Ticket 20: small to medium code in the SDK online patch
plus server, testable with the switch it specifies. 19/06: one HITL session.
Hosting: an ops task outside the VPS scope the agent may touch alone (memory
`feedback-vps-scope.md:8-11`).

**Dependencies.** 20 has none (`20-...md:5`). 19 needs a second network.
Online 120 fps is off by design, so no interaction with candidate 5
(`docs/releases/1.2.0.md:10-11`).

### 2.4 Ready-for-agent backlog and tracker hygiene

**What.**

- **zero-stutter/16:** add `-ffp-model=strict -fno-strict-aliasing` to the
  generated code and the game, measure with `scripts/compare_builds.py`, keep
  them unless they cost speed or fail a scene. The owner decided this on
  2026-10-04 (`16-...md:52`). It is a correctness guard: the generated code
  type-puns guest memory through unions under strict aliasing today, with no
  miscompile known (`:29-32`). Ship it in a release on its own, since online
  only pairs the same build anyway (`1.2.0.md:30-31`). [V]
- **zero-stutter/19** (device check before Play), **20** (scripts close, not
  kill), **21** (driver-cache split; lower priority now, see §1.2).
- **Close or re-label stale tickets.** 34 ready-for-human, most for work
  that shipped (§1.2). A pass with the owner turns them into resolved or
  wontfix so the frontier shows real work. [I]
- **Update `docs/ROADMAP.md`'s Status**, which stops at M2 on 2026-10-02
  (`docs/ROADMAP.md:38-40`).

**Effort and risk.** Small each. 16 needs one measured build and a scene
check, which the owner drives (§1.3).

### 2.5 120 fps: the second running throw

**What.** Finish high-framerate/08: the second running throw's anchor fires
on 197.5 but the pawn first moves on 198.0, losing ~30 units; next probe is
`pawn+0x1C1C..0x1C24` on 197.5 (`.scratch/high-framerate/issues/08-running-throw.md:70-89`).
Then continue the owner's "keep fixing bug groups with a 60-vs-120 sweep"
(memory `project-120fps-matches.md:26`).

**Evidence of value.** It is the only named known issue in 1.2.0
(`1.2.0.md:11-12`) and the feature with the most recent momentum (§1). But it
is experimental and off by default (`1.2.0.md:9`), and it needs a 120 Hz or
VRR display and a strong GPU (`1.2.0.md:10-13`), so few players see it [I].

**Effort and risk.** Medium: RE is in progress with a working probe (git
stash "climb probe [DEBUG-climb]", `project-120fps-matches.md:28`). Each step
needs the owner at the controls. Low risk: fixes sit behind bits of
`wwe_120fps_fixes` and do nothing at 60 (`08-running-throw.md:40-44`).

### 2.6 Mod and asset hooks (roadmap item 3)

**What.** The last post-v1 roadmap item (`docs/ROADMAP.md:61`), "designed in
but ship after v1.0" (`docs/adr/0001-enhanced-port-scope.md:11`). SVR2011-pc
shows the shape: user music through XMP, custom titantron movies from a host
device, save backup/import in the launcher
(`docs/research/svr2011-pc-port.md:308-314`, `:323-328`, `:363-365`). The
other gaps against it are self-update with its own unzip (`:351-352`;
this launcher only checks for releases, `1.0.0.md:31-32`) and
Optimus/PowerXpress exports (`:355`), which this project also lacks (same
search as §2.2). [V]

**Evidence of value.** No public issue asks for mods, and the Reddit
comments are not tracked in the repo (`.scratch/public-launch/launch-kit.md:186-216`).
The roadmap and ADR still commit to it. [V]

**Effort and risk.** Large; needs a spec and research first (asset formats
are UE3 packages, `tooling-recommendations.md` "One correction"). Medium
risk: new hooks into guest file I/O.

**Not ranked:** Ranked Match is out of scope in the online spec
(`.scratch/online-play/spec.md:151`); 4-player online (online-play/13) waits
until two-player online is proven across networks (candidate 3); a native
Linux build is not needed while the Deck plays through Proton
(`README.md:220-223`), though ADR-0006 still plans it.

## 3. Sister project comparison

`D:\Projects\SVR2011-RECOMP` (the owner's own) is behind this project: its
recent commits are native-renderer bring-up and a Classic/Native launcher
choice (`git log` there: `6a12d52`, `e8d6cb2`), and its README lists Road to
WrestleMania, Universe and online as untested (README line 17). It has no
feature this project lacks. [V]

The more useful comparison is KaikoClanworth1's `wwe-svr2011-pc`, already
studied in `docs/research/svr2011-pc-port.md`. Of its features, this project
has adopted the scripted input, the fallbacks at start-up, the pipeline
prebuild, the GPU-wait yield, SSAA, the in-game settings and DLC. Not
adopted: the crash reporter (§2.2), the stuck-GPU fallback (§2.1), self-update,
save backup, Optimus/PowerXpress exports, user music and movies (§2.6). [V for
the list in that note, I for "adopted", from the 1.0.0 notes and `src/`]

## 4. Top recommendation

Build **candidate 1, starting with candidate 2's crash report**:

1. Crash report and a bug-report zip (about 1 day, no hardware needed).
2. A `device_lost`/`submit_failed` fallback so Native failures end in an
   Emulated offer instead of a dead window (about 1 day, xUnit at
   `GameExitReport`).
3. Reply on #3 and #1 with a build that has both, ask the reporters to run it,
   and diagnose Iris Xe and the UHD 620 launcher freeze from what comes back.

Why this over 120 fps or mods: it is the only work aimed at the bugs real
players have filed (§1.1), it needs no Intel hardware to start, it fits the
owner's rules (no GPU tuning, no long scripted runs), and steps 1-2 help every
future report, online ones included. Candidate 3's ticket 20 is the natural
next item once #2's reporters answer.
