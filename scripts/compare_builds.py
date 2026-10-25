"""Compare build-flag variants by the game's own work per engine tick (ticket 11).

Each variant is a runtime log of a start with --wwe_tick_log=true. The game's work per tick
(the tick minus its frame limiter's wait, from the once-a-second "game tick 1s" lines) is
averaged over the same span of each run, and every build is compared with the first one.
With --present_pacing_stats=true as well, the "Paced presentation 5s" lines in that span add
the present interval's stdev (frame-weighted) and the 5 s windows holding a frame over 25 ms.

    python scripts/compare_builds.py v2o2=analysis/runtime-a/runtime.log v3o2=... [--skip 20] [--span 40]
"""
import argparse
import datetime
import re
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_hitches import LOG_TIME, load_tick_seconds, parse_time  # noqa: E402

PACED = re.compile(r"Paced presentation 5s: (\d+) frames, interval [\d.]+ ms mean, ([\d.]+) ms stdev, "
                   r"([\d.]+) ms max")
HITCH_MS = 25.0


@dataclass
class Row:
    name: str
    ticks: int
    work_ms: float
    change_pct: float | None
    pacing_sd_ms: float | None = None
    hitch_windows: int = 0
    pacing_windows: int = 0


def load_pacing_windows(lines):
    """--present_pacing_stats 5 s lines: (time, frames, interval stdev ms, interval max ms)."""
    windows = []
    for line in lines:
        match, paced = LOG_TIME.match(line), PACED.search(line)
        if match and paced:
            windows.append((parse_time(match.group(1)), int(paced.group(1)), float(paced.group(2)),
                            float(paced.group(3))))
    return windows


def compare(logs, skip_s, span_s):
    """logs: {name: runtime log lines}, baseline first. Only the summary seconds from skip_s
    to skip_s + span_s after each run's first one count."""
    rows = []
    for name, lines in logs.items():
        samples, pacing = load_tick_seconds(lines), load_pacing_windows(lines)
        if samples:
            start = samples[0][0] + datetime.timedelta(seconds=skip_s)
            end = start + datetime.timedelta(seconds=span_s)
            samples = [s for s in samples if start <= s[0] < end]
            pacing = [w for w in pacing if start <= w[0] < end]
        ticks = sum(s[1] for s in samples)
        work = sum(s[1] * s[3] for s in samples) / ticks if ticks else float("nan")
        change = None
        if rows:
            change = 100 * (work - rows[0].work_ms) / rows[0].work_ms
        frames = sum(w[1] for w in pacing)
        pacing_sd = sum(w[1] * w[2] for w in pacing) / frames if frames else None
        hitches = sum(w[3] > HITCH_MS for w in pacing)
        rows.append(Row(name, ticks, work, change, pacing_sd, hitches, len(pacing)))
    return rows


def format_table(rows):
    lines = ["| Build | Ticks | Game work per tick | Change | Present interval sd | Windows with a hitch |",
             "|---|---|---|---|---|---|"]
    for r in rows:
        change = "baseline" if r.change_pct is None else f"{r.change_pct:+.1f} %"
        sd = "-" if r.pacing_sd_ms is None else f"{r.pacing_sd_ms:.3f} ms"
        hitches = f"{r.hitch_windows}/{r.pacing_windows}" if r.pacing_windows else "-"
        lines.append(f"| {r.name} | {r.ticks} | {r.work_ms:.2f} ms | {change} | {sd} | {hitches} |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("runs", nargs="+", help="name=path/to/runtime.log, the baseline first")
    parser.add_argument("--skip", type=float, default=0, help="seconds left out after the first summary line")
    parser.add_argument("--span", type=float, default=1e9, help="seconds compared after --skip")
    args = parser.parse_args()
    logs = {}
    for run in args.runs:
        name, _, path = run.partition("=")
        logs[name] = Path(path).read_text(encoding="utf-8", errors="replace").splitlines()
    print(format_table(compare(logs, args.skip, args.span)))


if __name__ == "__main__":
    main()
