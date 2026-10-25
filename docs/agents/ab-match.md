# A/B performance between builds

`pwsh scripts/ab-match.ps1 -Builds old=<exe>,new=<exe>` plays the same CPU 1v1 on each build,
three rounds alternating, and prints the `scripts/compare_builds.py` table: game work per tick,
present interval stdev, and 5 s windows with a frame over 25 ms. One game window at a time,
parked off-screen; about 6 minutes per run. Ask the owner before a run: it is a long scripted
session on their PC and GPU.

A difference counts only when it exceeds the spread between rounds of the same build.

## Recording a result

A measurement note (ticket, research doc) names the command line and the flags, not only the
table: display lock on or off, vsync, window size, settings changed from the defaults. Ticket
05 of `high-framerate` kept only its numbers, so a later review could not tell which present
path it measured.

## Older releases

Release zips are in `out/release/`; extract one to a scratch folder and pass
`<folder>/game/wweallstars.exe`. Every build gets the dev cache (`cache/vulkan`: shader pack,
pipeline store), as the dev build does.

- **Renderer fallback.** `session.ps1 start` waits for the native renderer's start line. On
  `running on Emulated` it closes the game and throws with the log path, so a fallback never
  becomes a black window or a measurement of the wrong renderer. On 2026-10-24 the 1.1.0
  release fell back with "could not create Vulkan resources" when started this way. The
  cause is still unknown. Read the log before retrying.
- **Script flags.** Flags a build does not define are not passed: `ab-match.ps1` passes only
  `--present_pacing_stats` and `--wwe_tick_log`, which every release since 1.0.0 has.
  `--wwe_input_script_p2` (scripted P2 pad) exists from 1.2.0 on, so idle-P2 routes do not run
  on 1.1.0 and earlier; the CPU 1v1 does.
- **Defaults that moved.** Before 1.6.0 the render size was `wwe_native_supersample=true`
  (2x2 of 720p); from 1.6.0 it is `wwe_native_resolution_scale=2`. Both are 2560x1440.
  `wwe_120fps_matches` and `wwe_skip_startup_logos` default to off;
  `wwe_short_loading_screens` defaults to on from 1.7.0 (off before).
- **Frame-anchored routes** (`at <frame>` lines, `scripts/input/menus-frames.txt`) assume the
  full logos and loading screens: keep Skip startup logos off, and on builds from 1.6.1 on
  pass `--wwe_short_loading_screens=false` (1.7.0 turns it on by default).
