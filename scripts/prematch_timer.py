"""Times the pre-match load (faster-starts spec, "Testing Decisions", seam 1).

Drives a scripted route (scripts/idle-route.ps1: -Kind 1v1 is Hogan vs Cena, 4way a Fatal 4
Way; every pad idle) from boot with a throwaway profile, on the first arena or one further
right (--arena-steps), and reports the time from the arena confirm (the route's
`frame 7000: press A`) to the match start the game logs
(`faster starts: match start`, SeqAct_GameMatchStart's Activated, the bell). It also lists the
waits Shorter loading screens cut, and saves a screenshot a few seconds after the
match starts. The game is stopped, and checked gone, before the result is printed.

    python scripts/prematch_timer.py --name base
    python scripts/prematch_timer.py --name short --set wwe_short_loading_screens=true
    python scripts/prematch_timer.py --name arena3 --arena-steps 2 --set wwe_short_loading_screens=true
    python scripts/prematch_timer.py --name four --kind 4way

Short runs only (about 2-3 minutes each); one game at a time.
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONFIRM = "frame 7000: press A"
STARTED = "faster starts: match start (GameMatchStart)"
STAMP = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\]")


def pwsh(*args):
    return subprocess.run(["pwsh", "-NoProfile", "-Command", *args], cwd=ROOT, check=True,
                          capture_output=True, text=True).stdout


def stamp(line):
    match = STAMP.match(line)
    return datetime.strptime(match.group(1), "%Y-%m-%d %H:%M:%S.%f") if match else None


def game_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq wweallstars.exe"], capture_output=True,
                         text=True).stdout
    return "wweallstars.exe" in out


def result(log_text):
    """The run's numbers from its runtime log: confirm-to-start seconds (None if either mark
    is missing) and the faster-starts lines."""
    confirm = started = None
    notes = []
    for line in log_text.splitlines():
        if confirm is None and CONFIRM in line:
            confirm = stamp(line)
        elif confirm is not None and started is None and STARTED in line:
            started = stamp(line)
        if "faster starts:" in line and STARTED not in line:
            notes.append(line[line.index("faster starts:"):])
    seconds = (started - confirm).total_seconds() if confirm and started else None
    return seconds, notes


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--name", required=True, help="session name: analysis/session-prematch-<name>/")
    parser.add_argument("--set", action="append", default=[], metavar="CVAR=VALUE",
                        help="a game setting for this run (repeat for several)")
    parser.add_argument("--kind", choices=["1v1", "4way"], default="1v1", help="the idle-route.ps1 route")
    parser.add_argument("--arena-steps", type=int, default=0,
                        help="RIGHT presses on arena select before the confirm (0: the first arena)")
    parser.add_argument("--timeout", type=int, default=300, help="seconds to wait for the match start")
    parser.add_argument("--settle", type=float, default=3.0, help="seconds after the match start for the screenshot")
    args = parser.parse_args(argv)

    if game_running():
        sys.exit("A wweallstars.exe is already running; close it first (one game at a time).")
    session = f"prematch-{args.name}"
    work = ROOT / "analysis" / f"session-{session}"
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    route, user = work / "route", work / "userdata"
    shutil.copytree(ROOT / "userdata" / "vulkan", user)
    pwsh(f"& ./scripts/idle-route.ps1 -Dir '{route}' -Kind {args.kind}")
    if args.arena_steps:
        # Arena select is up from about frame 6700; the confirm stays at 7000.
        p1 = (route / "p1.txt").read_text(encoding="utf-8").splitlines()
        at = p1.index("at 7000")
        steps = []
        for i in range(args.arena_steps):
            steps += [f"at {6800 + 20 * i}", "press RIGHT 6f"]
        (route / "p1.txt").write_text("\n".join(p1[:at] + steps + p1[at:]) + "\n", encoding="utf-8")
    pads = sorted(route.glob("p[2-4].txt"))
    extra = ["--headless=true", "--fullscreen=false", "--wwe_tick_log=true", "--log_file_opens=Arena_"] + \
        [f"--wwe_input_script_{pad.stem}={pad}" for pad in pads] + [f"--{setting}" for setting in args.set]
    quoted = ",".join("'" + e.replace("'", "''") + "'" for e in extra)
    pwsh(f"& ./scripts/session.ps1 start -Name {session} -UserData '{user}' -ExtraArguments @({quoted})")
    log = work / "runtime.log"
    try:
        pwsh(f"& ./scripts/session.ps1 input -Name {session} (Get-Content '{route / 'p1.txt'}')")
        deadline = time.monotonic() + args.timeout
        def started():
            return log.exists() and result(log.read_text(encoding="utf-8", errors="replace"))[0] is not None

        while time.monotonic() < deadline and not started() and game_running():
            time.sleep(1)
        if started():
            time.sleep(args.settle)
            pwsh(f"& ./scripts/session.ps1 shot -Name {session} started")
    finally:
        pwsh(f"& ./scripts/session.ps1 stop -Name {session}")
        for _ in range(20):
            if not game_running():
                break
            time.sleep(1)
    left = game_running()
    seconds, notes = result(log.read_text(encoding="utf-8", errors="replace") if log.exists() else "")
    log_text = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
    arenas = sorted(set(re.findall(r"\b(Arena_\d{3})", log_text)))
    report = {"name": args.name, "kind": args.kind, "arena_steps": args.arena_steps, "arenas_seen": arenas,
              "settings": args.set, "confirm_to_match_start_s": seconds,
              "faster_starts": notes, "game_left_running": left}
    (work / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    if left:
        sys.exit("wweallstars.exe did not exit: stop here and tell the owner.")
    return 0 if seconds is not None else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
