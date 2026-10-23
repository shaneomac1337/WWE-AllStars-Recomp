# ADR-0027: Online pacing fixes are always on and match on both peers

Status: accepted (2026-10-23, `.scratch/online-latency/`, tickets 03, 04 and 06).

## Context

An online match is SyncSim lockstep: every peer steps the same simulation from
the same inputs, and the step rate each peer publishes is part of that
simulation. The shipped game settles at 29 Hz where two 60 Hz ticks allow 30
(both its sample and its publish truncate), starts every match at 20 Hz and
climbs to 30 over about 3 s after the bell, and Net-Z's Transport Thread
sends queued input only once per 10 ms pass. Together they gave the 29 Hz
hitch, the stutter after the bell, and stalls for remote input
(`docs/research/online-latency.md`).

## Decision

The online pacing fixes are always on while a Net-Z session runs, with no cvar
and no launcher setting, and offline play never reaches them:

- **30 Hz rule** (`src/online/step_rate.h`, hooks in
  `src/online/step_rate_hooks.cpp`): the rate calculation `0x830D8FA0` samples
  the highest rate an interval of whole milliseconds is consistent with and
  publishes the rounded mean, so two ticks per step publish 30 (15 for four
  players) and never stick at 29.
- **Start-at-cap window** (ticket 06): the first 90 steps (`kStartSteps`)
  sample the cap, so the match intro's long gap does not drag the mean down
  and the fight starts at 30.
- **Network-thread wake** (ticket 04): the SDK's `WSAWaitForMultipleEvents`
  waits for any event with a real timeout, a watcher thread completes pending
  overlapped receives as datagrams land, and every blocking wait-any also waits
  on one shared host event, the wake slot
  (`patches/rexglue-net-waits.patch`, `EndBlockingEventWait` in
  `rex/kernel/xam/net_wake.h`). The hooks in `src/online/net_wake_hooks.cpp`
  signal it right after SyncSim queues or re-sends input (`0x830DBEC0`,
  `0x830DC0E8`), so the input goes out now instead of up to 10 ms later.

## Considered options

- **An Enhancement toggle, like 60 fps entrances.** Rejected: the published
  rate and the start window feed the lockstep simulation, so two peers with
  different settings would pace differently and could desync. The project's
  online server only matches identical builds, so "always on" is what keeps
  every peer on the same rule.
- **Keep the adaptive rate off (`sim+0xA9E`).** Rejected: a real stall must
  still lower the rate; the rule only removes the truncation trap.
- **Seed the game's mean (or weight sum) at the cap instead of a step
  window.** Rejected (ticket 06): the pre-fight steps 50 ms apart still sample
  20 and drag the mean down after any seed. A window counted in steps is the
  same on every peer and needs no guest-state write.

## Consequences

- A build with these fixes must not play against one without them; the
  server's build match enforces this.
- Changing `kStartSteps` or the rate rule is a protocol change between peers,
  not a local tweak.
- The wake slot is shared by every title thread's blocking wait-any; a wake
  ends one wait, which the title reads as `WSA_WAIT_TIMEOUT`. In this title
  Net-Z's Transport Thread is the only such waiter.
- Online play goes beyond console behaviour (the 360 has the same 29 Hz trap),
  in line with the project's Enhancement stance, but without the opt-in.
