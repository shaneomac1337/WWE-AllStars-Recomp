"""Parse and compare Quazal Net-Z SyncSim replay logs (GAME:\\Station<n>.log).

Usage:
  python scripts/syncsim_log.py dump <Station.log> [--ticks A:B] [--stats]
  python scripts/syncsim_log.py compare <Station1.log> <Station2.log>

Exit codes: 0 in sync (or dump done), 1 checksums differ, 2 no common steps,
3 not a well-formed SyncSimReplayLog.

Format notes and the code addresses behind them: docs/research/syncsim-replay-log.md.
All integers in the file are little-endian.

File = header block + records.
  header: u32 size (0x228), then 0x228 bytes. Name "SyncSimReplayLog" (0x004),
          u32 1 (0x104), "Quazal Net-Z" (0x108), two base u32s (0x208, 0x20C),
          u32 record count (0x210), then SyncSimReplayHeader (0x214):
          m_uiRNGSeed, m_uiDivergingStep, m_uiLastInputStep, m_uiLastStep,
          m_uiLastSimClock, m_uiLastSSF.
  record: u32 12, u32 seq, u32 payload size, u32 type (1 = step), payload.
  step payload: u32 len (= size - 4), u32 step, u32 last input step, u8 count,
          then count x (u32 DOHandle, object datasets).
  dataset: u32 scatter mask, u32 0xDEADBEEF, one value per set mask bit
          (delta: a field absent from the mask keeps its previous value).

Objects (each station records only the objects it owns):
  class 0x14 SimStation: SimStationInfo {StepFrequency u16, MaxRTT u16,
          MinBandwidth u32}, StationChecksum {UserChecksum0..4, SimulationChecksum}
          (6 x u32). The checksums are shared state; StationInfo is per station.
  class 0x19 SyncInputHandler: axes (4 x 1 byte), buttons (1 x u16). Per station.
"""
from __future__ import annotations

import argparse
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

GUARD = 0xDEADBEEF
HEADER_FIELDS = ("rng_seed", "diverging_step", "last_input_step", "last_step",
                 "last_sim_clock_ms", "last_ssf")
CHECKSUM_NAMES = ("UserChecksum0", "UserChecksum1", "UserChecksum2", "UserChecksum3",
                  "UserChecksum4", "SimulationChecksum")
SIM_CHECKSUM = 5  # 0x830DD968 passes index 5 to the live compare 0x830DD830
STATION_INFO_DATASET = 0  # SimStation's first dataset: StepFrequency, MaxRTT, MinBandwidth
STATION_CHECKSUM_DATASET = 1  # SimStation's second dataset

CLASS_SIMSTATION = 0x14
CLASS_INPUT = 0x19
# Per class: list of datasets, each a list of field sizes in mask-bit order.
LAYOUT = {
    CLASS_SIMSTATION: [[2, 2, 4], [4] * 6],
    CLASS_INPUT: [[1, 1, 1, 1], [2]],
}


class FormatError(Exception):
    pass


@dataclass
class ObjectStep:
    handle: int
    datasets: list[tuple[int, list[int]]]  # (mask, values in bit order)

    @property
    def cls(self) -> int:
        return self.handle >> 22


@dataclass
class Step:
    seq: int
    step: int
    last_input_step: int
    objects: list[ObjectStep]
    offset: int


@dataclass
class ReplayLog:
    path: Path
    header: dict
    steps: list[Step] = field(default_factory=list)


def _u32(d: bytes, o: int) -> int:
    if o + 4 > len(d):
        raise FormatError(f"u32 at {o} runs past the end ({len(d)} bytes)")
    return struct.unpack_from("<I", d, o)[0]


def parse(path: Path) -> ReplayLog:
    d = path.read_bytes()
    if len(d) < 0x22C or _u32(d, 0) != 0x228 or d[4:20] != b"SyncSimReplayLog":
        raise FormatError(f"{path}: not a SyncSimReplayLog")
    header = {
        "name": d[4:0x104].split(b"\0")[0].decode(),
        "version": _u32(d, 0x104),
        "product": d[0x108:0x208].split(b"\0")[0].decode(),
        "base_208": _u32(d, 0x208),
        "base_20c": _u32(d, 0x20C),
        "record_count": _u32(d, 0x210),
    }
    for i, name in enumerate(HEADER_FIELDS):
        header[name] = _u32(d, 0x214 + 4 * i)
    log = ReplayLog(path, header)

    o = 0x22C
    while o < len(d):
        if o + 16 > len(d):
            raise FormatError(f"{path}: truncated record header at 0x{o:X}")
        hlen, seq, size, rtype = struct.unpack_from("<4I", d, o)
        if hlen != 12 or rtype != 1:
            raise FormatError(f"{path}: unexpected record header at 0x{o:X}: "
                              f"{hlen} {seq} {size} {rtype}")
        p = d[o + 16:o + 16 + size]
        if len(p) != size:
            raise FormatError(f"{path}: truncated payload at 0x{o:X}")
        log.steps.append(_parse_step(p, seq, o, path))
        o += 16 + size
    if header["record_count"] != len(log.steps):
        raise FormatError(f"{path}: header counts {header['record_count']} records, "
                          f"the file holds {len(log.steps)}")
    return log


def _parse_step(p: bytes, seq: int, offset: int, path: Path) -> Step:
    if len(p) < 13:
        raise FormatError(f"{path}: step payload of {len(p)} bytes at 0x{offset:X}")
    blen, step, last_input, count = struct.unpack_from("<IIIB", p, 0)
    if blen != len(p) - 4:
        raise FormatError(f"{path}: step length mismatch at 0x{offset:X}")
    q = 13
    objs = []
    for _ in range(count):
        handle = _u32(p, q)
        q += 4
        layout = LAYOUT.get(handle >> 22)
        if layout is None:
            raise FormatError(f"{path}: unknown object class 0x{handle >> 22:X} "
                              f"(handle 0x{handle:08X}) at 0x{offset:X}")
        dsets = []
        for sizes in layout:
            mask = _u32(p, q)
            if _u32(p, q + 4) != GUARD:
                raise FormatError(f"{path}: missing 0xDEADBEEF guard at 0x{offset:X}+{q + 4}")
            q += 8
            if mask >> len(sizes):
                raise FormatError(f"{path}: mask 0x{mask:X} has unknown fields at 0x{offset:X}")
            vals = []
            for bit, sz in enumerate(sizes):
                if mask & (1 << bit):
                    if q + sz > len(p):
                        raise FormatError(f"{path}: step at 0x{offset:X} ends inside a field")
                    vals.append(int.from_bytes(p[q:q + sz], "little"))
                    q += sz
            dsets.append((mask, vals))
        objs.append(ObjectStep(handle, dsets))
    if q != len(p):
        raise FormatError(f"{path}: {len(p) - q} unparsed bytes in step at 0x{offset:X}")
    return Step(seq, step, last_input, objs, offset)


def _dataset_series(log: ReplayLog, dataset: int, width: int) -> dict[int, tuple[int | None, ...]]:
    """Step -> full values of one dataset of the station's own SimStation, rebuilt
    from the delta records. None for fields not yet sent."""
    state: list[int | None] = [None] * width
    out = {}
    for s in log.steps:
        sims = [o for o in s.objects if o.cls == CLASS_SIMSTATION]
        if len(sims) != 1:
            raise FormatError(f"{log.path}: step {s.step} has {len(sims)} SimStation objects")
        mask, vals = sims[0].datasets[dataset]
        it = iter(vals)
        for bit in range(width):
            if mask & (1 << bit):
                state[bit] = next(it)
        out[s.step] = tuple(state)
    return out


def checksum_series(log: ReplayLog) -> dict[int, tuple[int | None, ...]]:
    """Step -> full StationChecksum (6 values) of the station's own SimStation."""
    return _dataset_series(log, STATION_CHECKSUM_DATASET, len(CHECKSUM_NAMES))


def _summary(values: list[int]) -> dict | None:
    if not values:
        return None
    return {"min": min(values), "max": max(values), "mean": sum(values) / len(values)}


def stats(log: ReplayLog) -> dict:
    """The match's pacing: StepFrequency and MaxRTT over the steps (min, max, mean, from
    the rebuilt SimStationInfo; steps before a field was first sent are left out), and
    ms per step from the header (m_uiLastSimClock / m_uiLastStep)."""
    info = _dataset_series(log, STATION_INFO_DATASET, 3).values()
    h = log.header
    return {
        "steps": len(log.steps),
        "last_ssf": h["last_ssf"],
        "ms_per_step": h["last_sim_clock_ms"] / h["last_step"] if h["last_step"] else None,
        "step_frequency": _summary([v[0] for v in info if v[0] is not None]),
        "max_rtt": _summary([v[1] for v in info if v[1] is not None]),
    }


def _hex(v: int | None) -> str:
    return "--------" if v is None else f"0x{v:08X}"


def describe(log: ReplayLog) -> str:
    h = log.header
    steps = [s.step for s in log.steps]
    handles = sorted({o.handle for s in log.steps for o in s.objects})
    contiguous = steps == list(range(steps[0], steps[0] + len(steps))) if steps else True
    return (f"{log.path}: {len(log.steps)} steps ({steps[0] if steps else '-'}.."
            f"{steps[-1] if steps else '-'}, contiguous={contiguous}), "
            f"objects {', '.join(f'0x{x:08X}' for x in handles)}; header "
            + ", ".join(f"{k}={h[k]}" for k in ("record_count",) + HEADER_FIELDS))


def compare(a: ReplayLog, b: ReplayLog) -> int:
    print(describe(a))
    print(describe(b))
    seeds = a.header["rng_seed"], b.header["rng_seed"]
    print(f"header rng_seed: {seeds[0]} vs {seeds[1]} -> {'same' if seeds[0] == seeds[1] else 'DIFFERENT'}")
    for log in (a, b):
        if log.header["diverging_step"]:
            print(f"{log.path.name}: header m_uiDivergingStep = "
                  f"{log.header['diverging_step']} (Net-Z saw a divergence while recording)")
    ca, cb = checksum_series(a), checksum_series(b)
    common = sorted(set(ca) & set(cb))
    if not common:
        print("no common steps")
        return 2
    print(f"compared steps: {len(common)} ({common[0]}..{common[-1]}); "
          f"only in {a.path.name}: {len(set(ca) - set(cb))}, only in {b.path.name}: "
          f"{len(set(cb) - set(ca))}")
    first_sim = next((t for t in common if ca[t][SIM_CHECKSUM] != cb[t][SIM_CHECKSUM]), None)
    first_any = next((t for t in common if ca[t] != cb[t]), None)
    if first_any is None:
        print("SimulationChecksum and UserChecksum0-4: in sync on every compared step")
        return 0
    if first_sim is None:
        print("SimulationChecksum: in sync on every compared step")
    else:
        print(f"SimulationChecksum: first differing step {first_sim}: "
              f"{_hex(ca[first_sim][SIM_CHECKSUM])} vs {_hex(cb[first_sim][SIM_CHECKSUM])}")
    t = first_any
    diff = [CHECKSUM_NAMES[i] for i in range(len(CHECKSUM_NAMES)) if ca[t][i] != cb[t][i]]
    print(f"any checksum: first differing step {t} ({', '.join(diff)})")
    n = sum(1 for t in common if ca[t] != cb[t])
    print(f"steps with any difference: {n} of {len(common)}")
    return 1


def print_stats(log: ReplayLog) -> None:
    s = stats(log)
    mps = "-" if s["ms_per_step"] is None else f"{s['ms_per_step']:.2f}"
    print(f"last_ssf {s['last_ssf']}, {mps} ms per step over {s['steps']} steps")
    for name, key, unit in (("StepFrequency", "step_frequency", ""), ("MaxRTT", "max_rtt", " ms")):
        v = s[key]
        print(f"{name} " + ("never sent" if v is None else
                            f"min {v['min']} max {v['max']} mean {v['mean']:.2f}{unit}"))


def dump(log: ReplayLog, lo: int, hi: int) -> None:
    print(describe(log))
    series = checksum_series(log)
    for s in log.steps:
        if not lo <= s.step <= hi:
            continue
        parts = []
        for o in s.objects:
            ds = " ".join(f"m{m:X}[{','.join(f'{v:X}' for v in vals)}]" for m, vals in o.datasets)
            parts.append(f"0x{o.handle:08X}:{ds}")
        cs = " ".join(_hex(v) for v in series[s.step])
        print(f"step {s.step:5d} li {s.last_input_step:5d} | {' | '.join(parts)} || {cs}")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("dump")
    d.add_argument("log", type=Path)
    d.add_argument("--ticks", default="1:20", help="first:last step, inclusive")
    d.add_argument("--stats", action="store_true",
                   help="print the pacing summary (StepFrequency, MaxRTT, ms per step) instead")
    c = sub.add_parser("compare")
    c.add_argument("a", type=Path)
    c.add_argument("b", type=Path)
    args = ap.parse_args(argv)
    try:
        if args.cmd == "dump" and args.stats:
            log = parse(args.log)
            print(describe(log))
            print_stats(log)
            return 0
        if args.cmd == "dump":
            try:
                lo, hi = (int(x) for x in args.ticks.split(":"))
            except ValueError:
                ap.error(f"--ticks wants first:last, got {args.ticks!r}")
            dump(parse(args.log), lo, hi)
            return 0
        return compare(parse(args.a), parse(args.b))
    except FormatError as e:
        print(f"format error: {e}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
