# Online matches: latency and stutter

Date 2026-10-21. Question: online Player Matches work but feel "a little bit
stuttery". Is that the game's own netcode, or can we cut latency and stutter?
Research only: no code, SDK, server or VPS was changed. Game addresses are
**TU2** (ADR-0022), read in Ghidra (`ghidra/WWEAllStarsTU2`, `default.xexp`,
entry `0x8241A7E0` checked). SDK paths are relative to `.tools/rexglue-source`
(`f5337cdc` plus `patches/`, the online code from `patches/rexglue-online.patch`).

**[V]** marks a fact checked against code, a log or a primary source. **[I]**
marks an inference. Note: `syncsim-replay-log.md` and §1.5 of
`online-mode-feasibility.md` give **base v5** addresses for SyncSim. The TU2 code
sits +0x40C0 higher (update `0x830D5E40` → `0x830D9F00`, bring-up `0x82A5F000` →
`0x82A617D8`).

## Answer

- **Most of the stutter is the game's own netcode, and two parts of it can be
  fixed on our side.** Net-Z SyncSim is delay-based input lockstep. The game
  thread blocks inside the frame until the next step is due and every remote
  input has arrived. There is no prediction, rollback or catch-up (§2). [V]
- **Stutter source 1, periodic, every match, every connection: the 29 Hz step
  trap.** SyncSim measures its own step interval in whole milliseconds,
  truncates the rate, and settles at 29 steps/s (34 ms) against two 60 Hz ticks
  (33.3 ms). So each step makes the game thread wait about 0.7 ms more. The
  game runs at about 58.8 ticks/s, and a 60 Hz display shows one repeated frame
  about every 0.8 s (§2.3). The recordings agree: `last_ssf = 29`, 34.07 ms per
  step (`high-framerate.md` §5). [V] for the mechanism and the rate, [I] for
  the repeated frame. The console would behave the same way, but **this is a
  small hook we can fix**, and it is the best candidate for what the owner sees.
- **Stutter source 2, depends on the connection: stalls on late input.** The
  input lead is `ceil(0.625 × maxRTT / 34 ms)` steps, at least 1. That leaves
  only about 0.125 × RTT of slack for jitter. A packet later than that freezes
  the game thread until it arrives (§2.2). [V] for the formula, [I] for how
  often it fires. Lower RTT or jitter, or a slightly larger lead, reduces it.
- **Our host layer adds almost no latency, but it burns a core.** The game's
  "Transport Thread" waits on socket events with a 10 ms timeout. Our
  `WSAWaitForMultipleEvents` turns that into a zero wait and always reports
  "event 0 signalled", so the thread polls every socket in a hot loop (§1.3).
  Receives are picked up within microseconds, but one host core spins for the
  whole session. That could cost frames on 4-core machines such as the Steam
  Deck. [V] for the code path, [I] for the cost.
- **The relay adds a hop through the Czech VPS.** Every player who can't be
  reached from outside (no public IP, no UPnP) goes through it, because there
  is no NAT hole punching (§1.1). Inside Europe the relay's round trip is about
  15 ms (owner's PC, tickets 16/17). For two players far from Prague it adds
  their round trip to the VPS. [V] for the routing, [I] for the cost abroad.
- **120 fps matches are already off online** (`src/game_tick_log.cpp:320-334`).
  A 120 Hz peer would diverge at once, so the guard is correct. **60 fps
  entrances have no online guard** (§3). [V]
- **Rollback is not realistic.** It needs a full, deterministic save and load
  of the whole game state every frame, and it would replace SyncSim with our own
  protocol (§4). [V] for GGPO's requirements, [I] for the cost.

## 1. How online play works today (host side)

### 1.1 Transport and routing [V]

- The game's peer sockets are real host UDP sockets. VDP (protocol `0xFE`,
  port 1000) is plain UDP, and so is UDP 9100 (`src/system/xsocket.cpp:50-53`,
  `online-mode-feasibility.md` §6.6). There is no TCP on the match path, so
  Nagle doesn't apply. No socket option is set beyond what the game asks for:
  no `SO_RCVBUF`/`SO_SNDBUF` and no DSCP (`xsocket.cpp:72-91`).
- **Route choice:** `XNetXnAddrToInAddr` gives the game a peer's local address
  (same public IP) or its public one (`src/kernel/xam/xam_net.cpp:732-757`).
  With the relay configured, `relay::AddressFor` first sends a direct probe:
  6 tries, 100 ms apart, to UDP 9101, from an ephemeral socket
  (`src/kernel/xam/live/relay.cpp:65-67, 333-365`). Answered → direct. Not
  answered → a stand-in loopback address `127.200.0.x` (`relay.cpp:415-452`).
  The route stays fixed for the session.
- **No hole punching.** The probe comes from a fresh ephemeral port, not from
  the game's ports, and nothing opens a NAT mapping toward the peer. "Direct"
  therefore means the peer has a public IP, UPnP (`--wwe_online_upnp`, off by
  default; ticket 18), or shares the LAN. Most home players end up relayed. [I]
- **Relayed path:** game socket → stand-in pump socket on loopback → relay
  thread (`select`, 50 ms timeout, wakes on data; `relay.cpp:262-307`) →
  `ASR1 send` to the VPS (`46.36.38.4:32482`, `server/deploy/compose.yaml:21`) →
  one Go goroutine reads and writes per packet (`server/internal/relay/relay.go:204-220`)
  → the peer's relay thread → `sendto` from the stand-in to the game socket
  (`relay.cpp:212-234`). That is two extra loopback hops and one wakeup of a
  host thread per side, all event-driven, plus a per-player token bucket of
  400 packets/s with a burst of 800 (`relay.go:56-57, 139-149`). [V]
- **Measurements we have:** the relay round trip, owner's PC → VPS → PC, is
  about 15 ms (max 20–25 ms) (`.scratch/online-play/issues/16-relay-forced.md`,
  `17-relay-fallback.md`). Every same-PC sync test was in sync and "feels the
  same as direct". No two-network match has been measured yet (ticket 19 is
  open). [V]

### 1.2 QoS is faked [V]

`XNetQosLookup` reports every target at 10 ms RTT and 5 Mbit/s
(`xam_net.cpp:1345-1390`, RTT at 1380-1381). SyncSim does **not** use it for its
lead; it uses Net-Z's own endpoint RTT (§2.2). The fake only affects the lobby's
"Ping Time".

### 1.3 Receive path: overlapped I/O and the event wait [V]

What the game does (Ghidra, TU2):
- Net-Z runs a dedicated **"Transport Thread"** (string `0x82302518`, entry
  `0x830BAC08`, created in `0x830BAFC8` because the static byte `[0x8352E0B8]`
  is 1).
- Each pass calls `0x830C49D8`, which is
  `WSAWaitForMultipleEvents(8, events, fWaitAll=FALSE, 10 ms, FALSE)` at
  `0x830C4AA8`. The timeout is `[0x835C11A8]` = 10. The game treats `0xC0`,
  `0x102` and `-1` as "no event".
- On a hit it checks each event with a zero timeout (`0x830C4B04`) and resets it
  (`0x830C4B3C`). It then reads the receive with
  `WSAGetOverlappedResult(fWait=FALSE)` (`0x830B96D0`) and re-posts the
  overlapped `WSARecvFrom` (`0x830B9644`).
- Sends go through one `WSASendTo` call site (`0x830B94D4`). No separate flush
  timer was found.

What our SDK does with that:
- `NetDll_WSAWaitForMultipleEvents` passes the millisecond timeout as a
  *positive* NT tick count (`xam_net.cpp:530-535`). `XObject::TimeoutTicksToMs`
  reads any positive value as an absolute time and returns 0
  (`src/system/xobject.cpp:189-201`). **The 10 ms wait becomes a zero wait.**
- It passes `fWaitAll` as the NT wait type. In the NT enum 0 means WaitAll, and
  `XObject::WaitMultiple` treats non-zero as WaitAny (`xobject.cpp:276`). So
  `fWaitAll=FALSE` waits for *all* events (KWAIT_TYPE:
  <https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-kewaitformultipleobjects>).
- On a timeout, `WaitMultiple` returns `X_STATUS_TIMEOUT` (`0x102`, a success
  code; `xobject.cpp:311-313`). `XFAILED` is false, so the export **returns 0**,
  which the game reads as "event 0 signalled" (`xam_net.cpp:538-543`).
- An arriving datagram never sets the event. A pending receive is only tried
  when the guest calls `WSAGetOverlappedResult` (`xam_net.cpp:405-474`;
  `include/rex/system/overlapped_receive.h:46-90`).

Net effect:
- Every wait returns at once with "signalled", so the Transport Thread polls
  `WSAGetOverlappedResult` → `select(0)` → `recvfrom` on every slot, in a loop
  that only `SwitchToThread`s (`xobject.cpp:311-313`,
  `src/core/threading_win.cpp:87-90`).
- **Receive latency: microseconds. CPU: one host core spinning.**
- This works only because the two bugs cancel out. Fixing the timeout alone
  would make the thread really sleep 10 ms. With no event set on arrival, it
  would then never see a signal, and receives would stop. The timeout fix and
  arrival signalling must ship together. [I]

### 1.4 Timers [V]

- Guest `Sleep(n ms)` → `KeDelayExecutionThread` → `::Sleep(n)`
  (`src/system/xthread.cpp:1000-1040`, `threading_win.cpp:96-102`). Nothing calls
  `timeBeginPeriod` (`movement-judder.md` §2).
- Since Windows 10 2004, a process that doesn't call it "does not get a higher
  resolution than the default", and Windows 11 drops the guarantee for
  minimized or occluded windows
  (<https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod>).
- **Online, the waits that matter don't depend on it.**
  - SyncSim's waits are `appSleep(0)` yields (§2.2).
  - The relay thread is event-driven.
  - The frame limiter uses `PreciseSleep` (`src/game_tick_log.cpp:231-265`).
  - Only the 50 ms no-socket sleep and the >5 s stall sleeps are millisecond
    sleeps. Low priority.
- `SIO_UDP_CONNRESET` is turned off on the relay's own sockets
  (`relay.cpp:88-95`), but not on the game's sockets. An ICMP port-unreachable
  (a peer gone, a closed stand-in) can then fail the next receive with
  `WSAECONNRESET` (<https://learn.microsoft.com/en-us/windows/win32/winsock/winsock-ioctls>,
  `SIO_UDP_CONNRESET`). It doesn't affect latency; it's a robustness note.

## 2. How the game's netcode works (Net-Z SyncSim, TU2)

### 2.1 Steps and ticks [V]

- The engine tick calls the Net-Z session tick `0x82A62908` (from
  `0x82417D04`/`0x82417D1C`). In match state 3 it runs a step every N engine
  ticks:
  - N = 3 until step 30, then 2.
  - N = 4 when `0x82B70C08` is true (more than 2 players with a per-slot flag;
    the exact meaning is open).
- The step is `0x82A61D80`, whose update loop calls `0x830D9F00` at
  `0x82A621B4`.
- A step also has a wall-clock deadline: the next one is due at
  `now + 1000/freq` ms (`0x830D9F00`). The clock is a local Quazal millisecond
  clock (`0x83074B78` → `0x82421AD0`). There is no shared clock and no catch-up.
- Game settings (`0x82A5FB98`, `0x82A60330`, `0x82A617D8`):
  - latency 90 ms at the start, clamp 30–300 ms
  - StepFrequency and its cap: 30 (15 when `0x82B70C08` is true)
  - redundancy window 1
  - adaptive rate on (`sim+0xA9E = 1`)
- None of these come from an ini. All are immediates in code.

### 2.2 Input delay and the stall [V]

- **Lead:** `0x830D9750` sets `latency = clamp(0.625 × max over stations of
  MaxRTT, 30, 300)` ms every step. The lead is
  `L = max(1, ceil(latency / (1000/freq)))` steps (`0x830D7880`,
  `0x830D95E0-0x830D962C`).
  - MaxRTT is Net-Z's own endpoint RTT (`0x830DD128` → `0x830773A8`).
  - At 34 ms per step: L = 1 below about 54 ms RTT, 2 up to about 109 ms, 3 at
    150 ms.
- **Send:** one new input per step into a 75-entry ring. Each send carries the
  newest and the previous input. While a peer is waiting, unacked inputs are
  re-sent once per step period (`0x830DBEC0`, `0x830DC390` → `0x830DC0E8`).
- **Update return codes** (`0x830D9F00`), handled by the loop at
  `0x82A62140-0x82A622C4`, which **keeps the game thread inside the frame**:

| Code | Meaning | What the game thread does |
|---|---|---|
| 0 | stepped | continue the frame |
| 1 | inputs ready, deadline not reached | `appSleep(0)` and retry (`0x82A621E4`) |
| 2 | a station's input missing (`0x830D9E30` → `0x830DC390`) | `appSleep(0)` spin up to 5 s, then 1 s / 2.4 s sleeps (table `0x821AF4C8`); "waiting" UI after 3 s (`0x82B74278`, [I]) |
| 3 | checksum diverged | `Sleep(3000)`, error `0x82A60F78` |
| 4/5/6 | replay end / end requested / stopped | — |

- So a late input freezes the picture for exactly as long as it is late, and
  the time is never caught up. The engine limiter doesn't compensate either
  (`movement-judder.md` §1). Since the lead budget is about 0.625 × RTT against
  a one-way trip of about 0.5 × RTT, the slack is about 0.125 × RTT: 5–7 ms at
  40–55 ms RTT. [I]

### 2.3 Why the rate is stuck at 29 Hz [V for code and logs, I for the visible effect]

- After each step, `sim+0xA4` holds the whole milliseconds since the previous
  step.
- `0x830D8FA0` publishes `min(cap, 1000/elapsed)`, smoothed and **truncated**,
  as the local StepFrequency (`0x830D8F30`). Peers then use the minimum over
  all stations.
- Two 60 Hz ticks measure 33 or 34 ms, which gives 30 or 29, and the
  truncated average is 29. At 29 the period is `1000/29 = 34` ms, so every
  interval measures at least 34 ms and the rate never climbs back.
- The recordings show exactly this: `last_ssf = 29`, 243004 ms / 7133 steps =
  34.07 ms (`high-framerate.md` §5).
- Effect:
  - Every step, return 1 holds the game thread about 0.7 ms past its two-tick
    budget, and the limiter doesn't catch up. The game makes about 58.8
    ticks/s.
  - The guest vblank is locked to the display at 60 Hz (ADR-0016), and the
    presenter shows at most one new guest frame per guest vblank. So about
    1.2 frames per second are shown twice: **a held frame about every 0.8 s,
    on every connection.** [I]
- The 4-player variant runs 4 ticks (66.7 ms) per step with a cap of 15. The
  same truncation would settle it at 14 (71 ms), about 56 ticks/s with a held
  frame about every 0.25 s. [I, unmeasured]

## 3. Interaction with the frame-rate features [V]

- **120 fps matches:** `At120Allowed` refuses while `[0x83740590]` (Net-Z
  session active) is set (`src/game_tick_log.cpp:320-334`). An online match
  ticks 1/60, and the guest vblank follows that rate (ADR-0024). That's
  correct: a step counts engine ticks, so 1/120 halves the game time per step
  and diverges on the first checksum.
- **60 fps entrances:** `InSixtyFpsCinematic` has no Net-Z check
  (`game_tick_log.cpp:272-281`). ADR-0024 notes 1/30 s cinematics inside a match.
  If they run while SyncSim steps, two peers with different
  `wwe_60fps_entrances` settings would compute different game time per step and
  diverge. The server matches builds, not settings. Both sync tests ran the
  default (on). Open: `60fps-entrances.md` question 9. [I]

## 4. Rollback is not a realistic option

- GGPO-style rollback needs `save_game_state`, `load_game_state` and
  `advance_frame` callbacks. It replays several frames inside one displayed
  frame, and the engine must be "fully deterministic"
  (<https://github.com/pond3r/ggpo/blob/master/doc/DeveloperGuide.md>,
  <https://github.com/pond3r/ggpo/blob/master/doc/README.md>). [V]
- Here the state is the whole guest: a 0x18C0000-byte image plus heaps, 27
  guest threads, audio and GPU state. SyncSim is also built around one step per
  two real ticks, with blocking waits. Rollback would mean snapshotting guest
  memory every frame, running ticks several times faster than real time, and
  replacing Net-Z's protocol (`online-mode-feasibility.md` §4(c)). [I]
- What *can* change cheaply is SyncSim's own knobs: rate, lead and clamp
  (§2.1). They are immediates or a few instructions, reachable with log-free
  midasm hooks, as the frame-rate features already do.

## 5. Options, ranked by payoff against effort and risk

| # | Option | Expected effect | Effort | Risk |
|---|---|---|---|---|
| 1 | **Unstick the step rate: 30 Hz.** Make `0x830D8FA0` round instead of truncate, or clear the adaptive flag `sim+0xA9E` so the rate stays 30 (period `1000/30 = 33` ms, below two ticks) | No return-1 hold. 60.0 ticks/s online. Removes the held frame every ~0.8 s (and every ~0.25 s in 4-player, if §2.3 holds). Online speed matches offline | 1–2 days: one midasm or function hook, logs, a two-copy sync test | Low. The rate only paces steps; the sim result depends on per-step inputs. Both peers must run it, and the server already only matches identical builds. A wrongly placed hook could cause more stalls, which the logs would show |
| 2 | **Measure first** (§6): count update return codes 1/2 and time per stall, and dump MaxRTT/StepFrequency from the recordings | Tells periodic hold from late-input stall, per connection | ~1 day: a log-only midasm hook after `0x82A621B4` behind `--wwe_net_trace`, plus a `syncsim_log.py` column | None |
| 3 | **Fix the Transport Thread wait together with arrival signalling.** Convert the timeout to relative NT ticks, map `fWaitAll` correctly, return the signalled index or `WSA_WAIT_TIMEOUT`, and have a host watcher (`select` or `WSAEventSelect` on the native sockets with pending receives) set the OVERLAPPED's event when a datagram lands | Frees the spinning core, with the same microsecond receive latency. Most useful on the Deck and 4-core PCs | 2–4 days with Catch2 tests (`overlapped_receive.h` is already unit-tested logic) | Medium: the current behaviour works by accident, and half a fix stops receives (§1.3). The two-copy sync test and an owner match are needed |
| 4 | **More jitter slack: a larger lead.** Raise the 0.625 factor (for example to 0.75) or add one step, through a hook in `0x830D9750` | Fewer late-input stalls on jittery or Wi-Fi links. Costs up to +34 ms input delay at some RTTs | 1 day, plus the same sync test | Low for determinism (input steps are explicit) [I]. It's a feel trade-off: offer it as a setting only if #2 shows stalls |
| 5 | **Direct paths for players behind NAT: relay-assisted UDP hole punching** from the game's own ports. The server tells both sides the other's NAT mapping of the game port, and both send at once (RFC 5128 §3.3, <https://www.rfc-editor.org/rfc/rfc5128#section-3.3>). The relay stays the fallback | Removes the VPS hop for most cone NATs, so lower RTT and jitter, so a smaller lead and fewer stalls for players far from Prague. Needs no firewall change (the owner's rule) | 1–2 weeks: SDK plus server. It also has to settle ticket 20 (one-sided routes) | Medium: symmetric NATs and CGNAT still need the relay, and route agreement is subtle |
| 6 | **Guard 60 fps entrances online** like 120 fps (`[0x83740590]`), or force the setting equal on both peers | Prevents a possible desync or stall in in-match cinematics with mixed settings | Hours | Low. It's 30 fps cinematics online only, or a check first that no SyncSim step runs during them |
| 7 | Relay polish: game-socket `SIO_UDP_CONNRESET` off, and a direct-route RTT log line like the relay's | Robustness and diagnosis, not latency | Hours | None |
| 8 | Relay placement: a second relay or server region | Only helps when both far-away players are relayed. #5 helps more | Ops work (see `free-server-hosting.md`) | Low |

Not recommended:
- `timeBeginPeriod`: the online waits don't depend on it (§1.4).
- Socket buffer or DSCP tuning: datagrams are tiny, about 29 per second per
  peer, and home routers ignore DSCP. [I]
- The VRR route around the 29 Hz cadence: the guest vblank is locked to the
  display (ADR-0016), and #1 fixes the cause instead.
- Rollback (§4).

How to verify each option (owner-driven, short; no long scripted runs):
- #1: two-copy `scripts/sync-test.ps1` match.
  - `syncsim_log.py compare` must stay in sync.
  - The header must show `last_ssf = 30`, and `last_sim_clock / last_step` must
    be about 33.3 ms.
  - `--wwe_tick_log` "game tick 1s" lines must show 60 ticks/s.
  - A PresentMon capture of a match must show no repeated `MsBetweenDisplayChange`
    every ~0.8 s.
- #3: host CPU of the Transport Thread (Process Explorer, thread view) before
  and after, then the same sync test.
- #4 and #5: the stall counters from #2 on a two-network match (ticket 19).

## 6. Measurement plan

What exists:
- **Recordings** (`--cl=-NETZRECORD`, `Station<n>.log`): per-step SimStationInfo
  {StepFrequency, MaxRTT, MinBandwidth}, and in the header `last_ssf` and
  `last_sim_clock_ms` (`scripts/syncsim_log.py`, `syncsim-replay-log.md`). The
  old `analysis/sync-test-*` folders are gone, so nothing is on disk today.
- **`--wwe_tick_log`:** slow ticks (> 20 ms) with the time in "other"
  `appSleep`, which is where SyncSim's wait loop lands (`game_tick_log.cpp:160-165,
  231-265`), plus a per-second tick count.
- **`--wwe_net_trace`:** rate-limited call log, with no timing
  (`include/rex/kernel/xam/net_trace.h`).
- **Relay RTT** every 10 s, relayed peers only (`relay.cpp:173-192`).

Missing, and cheap to add (ticket material):
1. A log-only hook after the update call (`0x82A621B4`). It counts return
   codes 0/1/2 and the wall time spent in 1 and 2 per step, then prints a
   once-a-second line: steps, held-for-deadline ms, stalled-for-input ms,
   longest stall, current L and latency (`sim+0xB0`).
2. `syncsim_log.py dump --stats`: StepFrequency and MaxRTT over the match, and
   ms per step.
3. A direct-route RTT line, for example from the probe socket on the probe
   port, every 10 s.

Sessions (each one short, owner plays by hand):
- A. Two copies on one PC, defaults: proves the 29 Hz hold exists with zero
  network.
- B. Same with option #1: proves it's gone.
- C. One two-network match (ticket 19, Steam Deck on a phone hotspot, relayed):
  stall counters with real jitter.

## Open questions for the owner

1. Does the stutter look like a regular little hitch about once a second
   (§2.3), or irregular freezes (§2.2)? Was it 1v1 or 4-player? Was the
   opponent on Wi-Fi or far from Prague?
2. Option #1 changes the game's own pacing online, and online gets 2 % faster,
   which matches offline. Is that acceptable as an Enhancement that is always
   on online, or should it be a setting? It must be equal on both peers.
3. Is a slightly larger input delay (option #4) acceptable in exchange for
   fewer freezes, and should it be a launcher setting?
4. Is relay-assisted hole punching (#5) worth 1–2 weeks now, or only after the
   measurements show relayed players stall more?
5. 60 fps entrances online: guard it (cinematics at 30 fps online) or force it
   equal on both peers?

## Owner decisions (2026-10-21)

- Symptom: a small regular hitch, which matches the 29 Hz truncation (§2.3).
- Scope for the spec: #1 (30 Hz step rate, always on in online matches, no
  setting), #2 (measurement hook and stats) and #3 (fix the network thread's
  event wait and signal it on receive, as one change).
- Out of scope for now: #4 (larger input delay) and #5 (hole punching).
- 60 fps entrances: no guard needed, entrances are not played in online matches.
