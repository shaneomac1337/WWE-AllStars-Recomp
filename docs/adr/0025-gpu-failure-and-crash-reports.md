# ADR-0025: A GPU failure ends the session; crash reports stay on the player's PC

Status: accepted (2026-10-16, the owner's decision for 1.3.0).

## Context

Both open crash reports after 1.2.0 come from Intel integrated GPUs. In issue #3 (Iris Xe), Native
logs `vkQueueSubmit failed` on the first frame and the game then exits as a crash, so the launcher
treats it as a crash and never offers Emulated (`launcher/src/Launcher.Core/GameExitReport.cs`).
Neither issue arrived with enough to diagnose it: #1 wrote no log, and #3 has a log but no dump
(`docs/research/next-steps-project-review.md`, `docs/research/community-feedback.md`). The owner
has no Intel hardware, so causes have to be found from players' reports.

## Decision

- A **GPU failure** (CONTEXT.md) ends the session: the game logs the reason and exits on purpose.
  It does not recreate the Vulkan device or hand over to Emulated mid-session. The launcher offers
  Emulated for the next start. Native stays the default on every GPU, including untested ones.
- A **crash report** is one zip made on the player's PC when the game crashes, the game ends a session
  with a GPU failure, or the launcher crashes. It holds the logs, the settings, the GPU and driver,
  and a small dump (thread stacks, modules, the memory they point at). It holds no guest memory,
  so no game data. Nothing is uploaded: the player attaches it to their issue.

## Considered options

- Recovering the device mid-session, or switching to Emulated in-process: much work for rare
  failures, and match state may not survive.
- Picking Emulated by default on Intel integrated GPUs: hides which GPUs actually fail.
- An upload button to the owner's server: needs an endpoint and a privacy promise, for a handful
  of reports a month.
- Full dumps: hundreds of MB, and they contain game data that players must not post publicly.

## Consequences

- A crash inside recompiled guest code shows host frames only; if those prove too thin, the
  report can add guest context as text later without changing this decision.
- A launcher that freezes (issue #1) cannot report itself; that case relies on its log.
