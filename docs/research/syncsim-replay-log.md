# SyncSim replay log (`Station<n>.log`)

What the Net-Z SyncSim `-NETZRECORD` file holds, how the game detects a
desync, and what `-NETZPLAYBACK` does. [V] = read from the code or checked on
the recordings; [I] = inferred. Tool: `scripts/syncsim_log.py`. Addresses are
TU2 (ADR-0022; mapped from base v5 with `scripts/tu_addrmap.py` and checked with
`scripts/disasm.py`, online-latency ticket 01).

## Opening the log

- `0x82A61420` (record) and `0x82A61638` (playback) format the name
  `L"Station%x.log"` (`0x821AFB38`) with the station number `[0x83740588]`
  (hex), then call `0x830D8480` (record, r6=1) or `0x830D8488` (playback, r6=0).
  Both go to `0x830D7E68`. [V]
- `0x830D7E68` refuses when a log already exists (`sim+0xA98`) or when the sim
  state `sim+0x84` isn't 0. It builds the 0x254-byte `SyncSimReplayLog`
  (constructor `0x830DC768`) and sets `sim+0xA9C` (0 = record, 1 = playback).
  Record opens the file for writing (`0x830E0808`). Playback opens it for
  reading (`0x830E0890`) and reads the header (`0x830E02F0`). [V]

## File layout (all little-endian) [V]

The parser reads every byte of all eight logs with nothing left over.

| Offset | Content |
|---|---|
| 0x000 | u32 header size `0x228` |
| 0x004 | name `"SyncSimReplayLog"`, 0x100 bytes |
| 0x104 | u32 1 |
| 0x108 | `"Quazal Net-Z"`, 0x100 bytes |
| 0x208 | u32 `0x00120001`, u32 `0x075D00DA` (base fields, not decoded) |
| 0x210 | u32 record count (base `+0x224`, incremented in `0x830DFF50`) |
| 0x214 | `SyncSimReplayHeader`, six u32 written by `0x830DF958` from `log+0x23C..0x250`: `m_uiRNGSeed`, `m_uiDivergingStep`, `m_uiLastInputStep`, `m_uiLastStep`, `m_uiLastSimClock` (ms), `m_uiLastSSF`. The DDL names are at `0x835C6127..0x835C62B8`. |
| 0x22C | records |

`0x830E0400` rewrites the header at file offset 0 after every record
(`0x830D9B84`) and when the log closes (`0x830D7960`). The header is always
current.

**Record** (`0x830DFF50`): u32 12, u32 seq, u32 payload size, u32 type
(always 1), then the payload.

**Step payload** (`0x830D9950`): u32 length, u32 step (`sim+0x78`), u32 last
input step (`sim+0x7C`), u8 object count. Each object follows as a u32
DOHandle plus its data, written through the object's vtable `+0x48`.

**Object data:** one block per dataset: u32 scatter mask, u32 `0xDEADBEEF`,
then one value for each set bit. These are deltas: a field missing from the
mask keeps its last value. Each station records only its own objects (two of
them):

| Class (handle>>22) | Handles | Datasets |
|---|---|---|
| 0x14 SimStation [I] | `0x05000801` (st 1), `0x05000802` (st 2) | SimStationInfo {StepFrequency u16, MaxRTT u16, MinBandwidth u32}, then StationChecksum {UserChecksum0..4, SimulationChecksum} (6 x u32) |
| 0x19 SyncInputHandler [I] | `0x06400803`, `0x06400804` | 4 axes (1 byte each), then buttons (u16) |

The class names come from matching the field count and sizes against the
DDL (`0x835C5703..0x835C5CD7`). [I]

## Shared vs station-specific

- **Shared** (equal when in sync): `m_uiRNGSeed`, and the StationChecksum
  values for each step. [V] They match on every step of match1 and match2.
- **Station-specific:** DOHandles, the input object (different on 2351 of
  match1's 7133 steps), StationInfo, and the file's end (the last step,
  `m_uiLastSimClock`, the record count). The dry runs stop one step apart.

## Live desync detection

- `0x830D9F00` (SyncSim update, called at `0x82A621B4` with r5=1) calls
  `0x830DD968`, which is `0x830DD830(localStation, 5)`. That function walks
  every SimStation and compares field 5 (SimulationChecksum, read with
  `0x830DE8C8`) against the local value. On a mismatch it stores the step in
  `m_uiDivergingStep` (`0x830D9FEC`), sets state 6, closes the log
  (`0x830D7918`, which rewrites the header) and returns 3. [V]
- The game's switch at `0x82A621B8` maps return value 3 to `0x82A60F78`. That
  function (TU2) calls `0x830D85D8(sim, 0)` when the sim (`this+0x54`) exists, then
  `0x830B8880(sim, 0x8000, L"")`, which is a bare `blr` (an empty function,
  trace compiled out). It then calls `0x82B6EEE8(6)`, the online-error setter.
  [V]
- `0x82B6EEE8` builds the name `L"SyncSim Diverge"` (case 6 of the switch at
  `0x82B6EB60`, string `0x8221CDE0`), frees it at once (`0x824595E0`) and
  posts the error code (`0x82B6EDC8`, `0x824F0C68`). [V] The game's error UI
  is the only place it shows. [I]
- **Not in our logs:** no import is called on this path, so
  `OutputDebugString` logging in the SDK (`xam_debug.cpp`) never fires. No
  trace hook covers `0x82A60F78` or `0x82B6EEE8`. A log-only midasm hook on
  either would make a desync visible.
- **Offline check:** a desync while recording leaves a non-zero
  `m_uiDivergingStep` in the header. All recorded logs have 0.

## `-NETZPLAYBACK` [V unless marked]

- It reads `GAME:\Station<n>.log`, where `<n>` is this copy's station number
  `[0x83740588]`. It goes through `0x82A617D8`, so it needs a live hosted
  Net-Z session, just like recording.
- On sim start (`0x830D9D5C`) it takes the RNG seed from the header. When
  recording, the seed is written there instead (`0x830D9D94`).
- Each step, `0x830D8490` reads one record and applies each recorded object
  through its vtable `+0x4C`. Only this station's own objects are in the file;
  the other station's inputs must come from the session. [I]
- That function makes no direct comparison against the recorded checksums. A
  mismatch can only show up through the same live check, which returns 3 and
  raises error 6 with no log line. [I]
- In playback, `0x830DA0FC` returns 4 when `m_uiDivergingStep == sim+0x7C`. In
  other words, playback stops at a recorded divergence. With
  `m_uiDivergingStep` = 0 that never happens. How a clean playback ends was not
  traced.
