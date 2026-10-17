"""Run a scene and judge Native against the Emulated envelope (ticket 22).

A scene (tests/scenes/*.toml) is scripted input from boot plus the guest frame
numbers to capture. The runner plays it headless on Emulated several times and
twice on Native, dumping every capture frame in each run
(--wwe_native_dump_frames) and exiting after the last one
(--wwe_exit_after_frame). Each Native capture is judged against the Emulated
envelope: per pixel, the smallest NVIDIA FLIP error against any Emulated run, so
run-to-run animation phase is not an artifact. A capture fails when a
tile x tile block has more than tile_hot_max pixels with envelope FLIP above
hot_flip. Mean FLIP is reported, never used to decide (ticket 03).

Each Native run's session report (--wwe_native_report) is held to ticket 10's
renderer rules (renderer_failures): any draw skip, a renderer fallback, Native
not active or a wwe_native_debug_* cvar fails the scene. The second Native run
starts with the pipeline cache the first saved; pipeline_pending is allowed on
the first run only.

Both dumps are display-referred sRGB (Emulated's front buffer, Native's
presented image), which is what FLIP's LDR mode expects, so no gamma step is
needed between them.

    python scripts/scene_runner.py tests/scenes/boot-main-menu.toml [--out DIR] [--fresh-oracle]

Speed: the Emulated captures (the oracle) are cached under analysis/oracle/ by
scene input, frames and run count, so changing Native costs only the Native
runs; --fresh-oracle rebuilds them (do so after changing Emulated, the SDK or
the game route). --quick makes one Native run instead of two, for iterating;
it cannot check the hitch-free rule. Runs never overlap: the game animates by
elapsed time, so a run slowed below 60 fps by another one reaches a different
state at the same frame number (ticket 23).

Results, captures, heatmaps and the sign-off contact sheet (sheet.png) go to
analysis/scenes/<name>/ (not committed).
Exit code: 0 pass, 1 fail, 2 the scene could not run.
"""
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / "out/build/win-amd64-relwithdebinfo-vulkan/wweallstars.exe"

# Enhancements (CONTEXT.md): options beyond the oracle, by cvar with the value
# that turns each off (vibration and colour: Original, as the Xbox 360). Every scene run
# turns them off; a new enhancement is added here and to the native session report.
ENHANCEMENTS = {"wwe_native_supersample": "false", "wwe_native_cas": "0",
                "wwe_vibration_mode": "1", "wwe_native_color_mode": "0",
                "wwe_60fps_entrances": "false", "wwe_120fps_matches": "false",
                "wwe_unlock_everything": "false"}

# Runtime flags every run gets: every enhancement off, full vibration strength (a player setting,
# not an enhancement), presents without vsync and the guest vblank on the host clock, as when the
# oracles were captured (ADR-0016: scene runs keep the fixed 60.000 Hz pacing).
# (--wwe_lock_guest_time is not passed: ticket 30 showed it does not make runs reproducible.)
RUN_FLAGS = [f"--{name}={off}" for name, off in ENHANCEMENTS.items()] + [
    "--wwe_vibration_strength=100", "--present_vsync=false", "--vblank_follows_display=false",
    # The oracles are 1280x720; a player's Render resolution would make Native's frames larger.
    "--wwe_native_resolution_scale=1"]

# The judgement every scene uses unless its [check] overrides a value.
DEFAULTS = {
    # The scene key, the classic-N run labels (oracle paths) and result.json's
    # classic_noise_worst_tile_hot keep the renderer's former name (ADR-0020).
    "classic_runs": 3,   # Emulated runs forming the envelope
    "hot_flip": 0.5,     # a pixel is hot above this envelope FLIP
    "tile": 32,          # tile edge in pixels
    "tile_hot_max": 50,  # K: hot pixels a tile may hold
}


class SceneError(Exception):
    pass


@dataclass
class Scene:
    name: str
    input: str
    frames: list
    check: dict = field(default_factory=lambda: dict(DEFAULTS))


def load_scene(text):
    try:
        data = tomllib.loads(text)
    except tomllib.TOMLDecodeError as error:
        raise SceneError(f"bad TOML: {error}") from error
    name = data.get("name")
    if not isinstance(name, str) or not name:
        raise SceneError("scene needs a non-empty 'name'")
    if data.get("from") != "boot":
        raise SceneError(f"scene '{name}': 'from' must be \"boot\" (the only start point so far)")
    script = data.get("input", "")
    if not isinstance(script, str):
        raise SceneError(f"scene '{name}': 'input' must be a string of scripted-pad lines")
    captures = data.get("capture")
    if not isinstance(captures, list) or not captures:
        raise SceneError(f"scene '{name}': needs at least one [[capture]] with a frame")
    frames = []
    for capture in captures:
        frame = capture.get("frame") if isinstance(capture, dict) else None
        if not isinstance(frame, int) or isinstance(frame, bool) or frame < 0:
            raise SceneError(f"scene '{name}': every [[capture]] needs frame = <guest frame >= 0>")
        frames.append(frame)
    check = dict(DEFAULTS)
    for key, value in data.get("check", {}).items():
        if key not in DEFAULTS:
            raise SceneError(f"scene '{name}': unknown [check] key '{key}' "
                             f"(known: {', '.join(DEFAULTS)})")
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            raise SceneError(f"scene '{name}': [check] {key} must be a number")
        check[key] = value
    return Scene(name, script, sorted(set(frames)), check)


def renderer_failures(report, run):
    """Ticket 10's renderer rules over a Native run's session report: any draw
    skip, a GPU failure, a renderer fallback, Native not active, a debug cvar or an
    enhancement fails the scene. pipeline_pending is allowed on the first Native run (run 1) only."""
    if report is None:
        return ["no session report (the run did not exit cleanly)"]
    failures = []
    if report.get("gpu_failure"):
        failures.append(f"gpu failure: {report['gpu_failure']}")
    if report.get("fallback", "none") != "none":
        failures.append(f"renderer fallback: {report['fallback']}")
    if not report.get("native_active"):
        failures.append("Native was not active")
    for cvar in report.get("debug_cvars", []):
        failures.append(f"debug cvar set: {cvar}")
    for enhancement in report.get("enhancements", []):
        if enhancement.get("on"):
            failures.append(f"enhancement on: {enhancement['name']}")
    for reason, record in report.get("draw_skips", {}).items():
        if reason == "pipeline_pending" and run == 1:
            continue
        failures.append(f"draw skip {reason}: {record['total']}, first at frame "
                        f"{record['first_frame']}, key {record['first_key']}")
    return failures


def flip_map(reference, test):
    """Per-pixel FLIP error (0..1) of test against reference, both HxWx3 sRGB in 0..1."""
    import flip_evaluator as flip
    error, _, _ = flip.evaluate(reference, test, "LDR", applyMagma=False)
    return np.asarray(error, np.float32).squeeze()


def envelope(native, oracle_runs, error_map=flip_map):
    """Per pixel, the smallest error of the Native image against any Emulated run."""
    return np.minimum.reduce([error_map(oracle_run, native) for oracle_run in oracle_runs])


def judge_capture(env, check):
    tile = int(check["tile"])
    hot = env > check["hot_flip"]
    h, w = hot.shape
    rows, cols = -(-h // tile), -(-w // tile)
    padded = np.zeros((rows * tile, cols * tile), bool)
    padded[:h, :w] = hot
    counts = padded.reshape(rows, tile, cols, tile).sum(axis=(1, 3))
    failing = np.argwhere(counts > check["tile_hot_max"])
    return {
        "passed": not len(failing),
        "worst_tile_hot": int(counts.max()),
        "failing_tiles": [[int(y), int(x)] for y, x in failing],
        "mean_flip": float(env.mean()),
        "hot_fraction": float(hot.mean()),
    }


def heatmap(env, verdict, tile, path):
    """Envelope FLIP as black -> red -> yellow -> white, failing tiles outlined in cyan."""
    v = np.clip(env, 0, 1)
    rgb = np.stack([np.clip(v * 3, 0, 1), np.clip(v * 3 - 1, 0, 1), np.clip(v * 3 - 2, 0, 1)], -1)
    image = Image.fromarray((rgb * 255).astype(np.uint8))
    draw = ImageDraw.Draw(image)
    for y, x in verdict["failing_tiles"]:
        draw.rectangle([x * tile, y * tile, x * tile + tile - 1, y * tile + tile - 1],
                       outline=(0, 255, 255))
    image.save(path)


SHEET_CELL = (480, 270)  # one image on the contact sheet
SHEET_BAND = 24          # verdict line under each row


def contact_sheet(result, out):
    """The owner's sign-off sheet: per capture, Emulated | Native | heatmap of the
    first Native run, then a green or red band with the frame and verdict.
    Edge and filtering differences pass the metric; this is where they are judged."""
    w, h = SHEET_CELL
    captures = [c for c in result["captures"] if "native" in c]
    sheet = Image.new("RGB", (3 * w, len(captures) * (h + SHEET_BAND)), (0, 0, 0))
    draw = ImageDraw.Draw(sheet)
    for row, capture in enumerate(captures):
        native_label, verdict = next(iter(capture["native"].items()))
        cells = [capture["images"]["classic-1"], capture["images"][native_label],
                 out / verdict["heatmap"]]
        top = row * (h + SHEET_BAND)
        for column, path in enumerate(cells):
            sheet.paste(Image.open(path).convert("RGB").resize((w, h)), (column * w, top))
        band = (20, 110, 40) if capture["passed"] else (150, 25, 25)
        draw.rectangle([0, top + h, 3 * w - 1, top + h + SHEET_BAND - 1], fill=band)
        runs = "  ".join(f"{label}: {'pass' if v['passed'] else 'FAIL'} "
                         f"(worst tile {v['worst_tile_hot']})"
                         for label, v in capture["native"].items())
        draw.text((6, top + h + 5), f"frame {capture['frame']}   Emulated | Native | heat   {runs}",
                  fill=(255, 255, 255))
    path = out / "sheet.png"
    sheet.save(path)
    return path


def load_image(path):
    return np.asarray(Image.open(path).convert("RGB"), np.float32) / 255


def native_pipeline_flags(out):
    """Every Native run of a scene uses one pipeline store under out, named by
    --wwe_native_pipeline_dir: each run has its own cache_root, and run 2 must
    start from the store run 1 saved."""
    return [f"--wwe_native_pipeline_dir={(out / 'native-pipelines').as_posix()}"]


def renderer_flags(renderer):
    """The flags that pick a run's renderer ("native" or "emulated"). Each run names it, so
    the settings default (Native, ADR-0020) never decides; Emulated reads its front
    buffer back for the dump."""
    if renderer == "native":
        return ["--wwe_native_renderer=true"]
    return ["--wwe_native_renderer=false", "--readback_resolve=full"]


def run_game(label, renderer, scene, out, exe, extra=()):
    """One headless run that dumps every capture frame and exits after the last
    one; returns ({frame: png path}, session report or None)."""
    run_dir = out / label
    run_dir.mkdir(parents=True, exist_ok=True)
    script = run_dir / "input.txt"
    script.write_text(scene.input.strip() + "\n")
    dumps = run_dir / "dumps"
    report_path = run_dir / "report.json"
    report_path.unlink(missing_ok=True)
    # Each run starts from a copy of the save data and SDK cache, so no run
    # sees another's autosave.
    for name, source in (("userdata", ROOT / "userdata/vulkan"), ("cache", ROOT / "cache/vulkan")):
        shutil.rmtree(run_dir / name, ignore_errors=True)
        if source.exists():
            shutil.copytree(source, run_dir / name)
    flags = ["--gpu_plugin=xenos", "--vulkan_device=-1", "--render_target_path_vulkan=fsi",
             f"--game_data_root={ROOT / 'assets'}", f"--update_data_root={ROOT / 'updatedata'}", f"--user_data_root={run_dir / 'userdata'}",
             f"--cache_root={run_dir / 'cache'}", f"--log_file={run_dir / 'runtime.log'}",
             "--log_level=info", "--headless", "--audio_mute=true",
             f"--wwe_input_script={script}",
             f"--wwe_native_dump_frames={','.join(map(str, scene.frames))}",
             f"--wwe_native_dump_dir={dumps.as_posix()}",
             f"--wwe_native_report={report_path.as_posix()}",
             f"--wwe_exit_after_frame={max(scene.frames)}", *RUN_FLAGS]
    flags += renderer_flags(renderer)
    flags += list(extra)
    proc = subprocess.Popen([str(exe), *flags], cwd=exe.parent,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # Boot is about 20 s; the game presents 60 frames per second when it keeps up.
    try:
        proc.wait(timeout=max(scene.frames) / 30 + 90)
    except subprocess.TimeoutExpired:
        print(f"  {label}: still running past the deadline, killed", flush=True)
        proc.kill()
        proc.wait()
    shots = {}
    for frame in scene.frames:
        ppm = dumps / f"frame-{frame:06}.ppm"
        if ppm.exists():
            png = run_dir / f"frame-{frame:06}.png"
            Image.open(ppm).convert("RGB").save(png)
            ppm.unlink()
            shots[frame] = png
    report = json.loads(report_path.read_text()) if report_path.exists() else None
    print(f"  {label}: {len(shots)}/{len(scene.frames)} captures", flush=True)
    return shots, report


def emulated_inputs():
    """What decides Emulated's output besides the scene: the SDK patches and the
    codegen manifest. A change to either gives every scene a new oracle."""
    digest = hashlib.sha256()
    for path in [*sorted((ROOT / "patches").glob("rexglue-*.patch")),
                 ROOT / "wweallstars_manifest.toml"]:
        if path.exists():
            digest.update(path.name.encode() + path.read_bytes())
    return digest.hexdigest()


def oracle_dir(scene):
    """Where a scene's Emulated captures are kept; any change to what Emulated
    plays or captures gives a new directory. Other Emulated changes (a new
    XEX, a local SDK edit) need --fresh-oracle."""
    key = json.dumps([scene.input, scene.frames, int(scene.check["classic_runs"]),
                      emulated_inputs(), RUN_FLAGS])
    return ROOT / "analysis/oracle" / f"{scene.name}-{hashlib.sha256(key.encode()).hexdigest()[:12]}"


def cached_oracle(directory, labels, frames):
    shots = {label: {f: directory / label / f"frame-{f:06}.png" for f in frames} for label in labels}
    complete = all(p.exists() for runs in shots.values() for p in runs.values())
    return shots if complete else None


def run_scene(scene, out, exe, native_extra=(), fresh_oracle=False, quick=False):
    emulated_labels = [f"classic-{i + 1}" for i in range(int(scene.check["classic_runs"]))]
    # The second Native run starts with the pipeline cache the first one saved:
    # the hitch-free run, where pipeline_pending must be zero.
    native_labels = ["native-1"] if quick else ["native-1", "native-2"]
    oracle = oracle_dir(scene)
    shots, reports = {}, {}
    cached = None if fresh_oracle else cached_oracle(oracle, emulated_labels, scene.frames)
    if cached:
        print(f"  Emulated oracle reused from {oracle}", flush=True)
        shots.update(cached)
    else:
        shutil.rmtree(oracle, ignore_errors=True)
        for label in emulated_labels:
            shots[label], _ = run_game(label, "emulated", scene, oracle, exe)
    # Native run 1 starts from an empty pipeline cache and saves it; run 2
    # starts from that, so pipeline_pending must be zero there.
    shutil.rmtree(out / "native-pipelines", ignore_errors=True)
    for label in native_labels:
        shots[label], reports[label] = run_game(label, "native", scene, out, exe,
                                                [*native_pipeline_flags(out), *native_extra])
    renderer = {label: {"report": reports[label],
                        "failures": renderer_failures(reports[label], run=i + 1)}
                for i, label in enumerate(native_labels)}
    captures = []
    for frame in scene.frames:
        missing = [label for label in shots if frame not in shots[label]]
        if missing:
            captures.append({"frame": frame, "passed": False, "error": f"no dump from {missing}"})
            continue
        oracle_runs = [load_image(shots[label][frame]) for label in emulated_labels]
        # Emulated against its own other runs: the noise the envelope has to absorb.
        noise = max(judge_capture(envelope(oracle_runs[i], oracle_runs[:i] + oracle_runs[i + 1:]),
                                  scene.check)["worst_tile_hot"]
                    for i in range(len(oracle_runs))) if len(oracle_runs) > 1 else None
        verdicts = {}
        for label in native_labels:
            env = envelope(load_image(shots[label][frame]), oracle_runs)
            verdict = judge_capture(env, scene.check)
            heat = out / f"heat-{label}-{frame:06}.png"
            heatmap(env, verdict, int(scene.check["tile"]), heat)
            verdicts[label] = {**verdict, "heatmap": heat.name}
        captures.append({"frame": frame, "passed": all(v["passed"] for v in verdicts.values()),
                         "classic_noise_worst_tile_hot": noise, "native": verdicts,
                         "images": {label: shots[label][frame].as_posix() for label in shots}})
    passed = (all(c["passed"] for c in captures) and
              not any(r["failures"] for r in renderer.values()))
    result = {"scene": scene.name, "passed": passed, "check": scene.check,
              "oracle": oracle.as_posix(),
              "renderer": renderer, "captures": captures}
    (out / "result.json").write_text(json.dumps(result, indent=1))
    contact_sheet(result, out)
    return result


def main(argv):
    parser = argparse.ArgumentParser(description=(__doc__ or "").split("\n")[0])
    parser.add_argument("scene", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--exe", type=Path, default=EXE)
    parser.add_argument("--native-arg", action="append", default=[],
                        help="extra runtime flag for the Native runs, e.g. a debug cvar")
    parser.add_argument("--fresh-oracle", action="store_true",
                        help="rerun Emulated instead of reusing the cached oracle captures")
    parser.add_argument("--quick", action="store_true",
                        help="one Native run instead of two (skips the hitch-free check)")
    args = parser.parse_args(argv)
    try:
        scene = load_scene(args.scene.read_text(encoding="utf-8"))
    except (OSError, SceneError) as error:
        print(f"scene error: {error}", file=sys.stderr)
        return 2
    if not args.exe.exists():
        print(f"no game build at {args.exe}", file=sys.stderr)
        return 2
    out = (args.out or ROOT / "analysis/scenes" / scene.name).resolve()
    out.mkdir(parents=True, exist_ok=True)
    print(f"scene {scene.name}: frames {scene.frames}, out {out}", flush=True)
    result = run_scene(scene, out, args.exe.resolve(), args.native_arg, args.fresh_oracle,
                        args.quick)
    for label, run in result["renderer"].items():
        for failure in run["failures"]:
            print(f"  {label}: FAIL  {failure}")
    for c in result["captures"]:
        if "error" in c:
            print(f"  frame {c['frame']}: FAIL  {c['error']}")
            continue
        for label, v in c["native"].items():
            print(f"  frame {c['frame']} {label}: {'PASS' if v['passed'] else 'FAIL'}  worst tile "
                  f"{v['worst_tile_hot']} hot px (K={scene.check['tile_hot_max']}), mean FLIP "
                  f"{v['mean_flip']:.4f}, Emulated noise {c['classic_noise_worst_tile_hot']}")
    print(f"scene {scene.name}: {'PASS' if result['passed'] else 'FAIL'}  ({out / 'result.json'})")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
