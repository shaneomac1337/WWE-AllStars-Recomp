import unittest

import numpy as np

import tempfile
from pathlib import Path

from PIL import Image

from scripts.scene_runner import (DEFAULTS, ENHANCEMENTS, RUN_FLAGS, SHEET_CELL, SceneError,
                                  contact_sheet, envelope, judge_capture, load_scene,
                                  native_pipeline_flags, renderer_failures,
                                  renderer_flags)

GOOD = """
name = "boot-main-menu"
from = "boot"
input = '''
at 2900
press START 6f
'''
[[capture]]
frame = 2700
[[capture]]
frame = 3200
"""


def absdiff(ref, test):
    """A stand-in for FLIP: per-pixel mean absolute difference, 0..1."""
    return np.abs(ref - test).mean(axis=2)


class RendererFlagTests(unittest.TestCase):
    def test_each_run_names_its_renderer_so_the_settings_default_never_decides(self):
        # Native is the default (ADR-0020): an Emulated oracle run must ask for Emulated.
        self.assertEqual(renderer_flags("native"), ["--wwe_native_renderer=true"])
        self.assertEqual(renderer_flags("emulated"),
                         ["--wwe_native_renderer=false", "--readback_resolve=full"])


class LoadSceneTests(unittest.TestCase):
    def test_a_valid_scene_gets_the_default_checks(self):
        scene = load_scene(GOOD)
        self.assertEqual(scene.name, "boot-main-menu")
        self.assertEqual(scene.frames, [2700, 3200])
        self.assertIn("press START 6f", scene.input)
        self.assertEqual(scene.check, DEFAULTS)

    def test_check_overrides_one_value_and_keeps_the_rest(self):
        scene = load_scene(GOOD + "\n[check]\ntile_hot_max = 80\n")
        self.assertEqual(scene.check["tile_hot_max"], 80)
        self.assertEqual(scene.check["classic_runs"], DEFAULTS["classic_runs"])

    def test_bad_scenes_say_what_is_wrong(self):
        cases = {
            'from = "boot"\ninput = ""\n[[capture]]\nframe = 1\n': "name",
            'name = "x"\nfrom = "title"\ninput = ""\n[[capture]]\nframe = 1\n': "from",
            'name = "x"\nfrom = "boot"\ninput = ""\n': "capture",
            'name = "x"\nfrom = "boot"\ninput = ""\n[[capture]]\nframe = -3\n': "frame",
            GOOD + "\n[check]\nmean_flip_max = 0.1\n": "mean_flip_max",
            "name = ": "TOML",
        }
        for text, needle in cases.items():
            with self.subTest(needle=needle):
                with self.assertRaises(SceneError) as caught:
                    load_scene(text)
                self.assertIn(needle, str(caught.exception))


class EnvelopeTests(unittest.TestCase):
    def test_a_pixel_matching_any_emulated_run_is_not_hot(self):
        native = np.zeros((4, 4, 3), np.float32)
        emulated_a = native.copy()
        emulated_a[0, 0] = 1.0  # run A is mid-fade at this pixel
        emulated_b = native.copy()
        env = envelope(native, [emulated_a, emulated_b], absdiff)
        self.assertEqual(env[0, 0], 0.0)

    def test_a_pixel_wrong_against_every_run_stays_hot(self):
        native = np.zeros((4, 4, 3), np.float32)
        native[1, 2] = 1.0
        emulated = np.zeros((4, 4, 3), np.float32)
        env = envelope(native, [emulated, emulated.copy()], absdiff)
        self.assertEqual(env[1, 2], 1.0)


class TileRuleTests(unittest.TestCase):
    def env_with_hot(self, pixels, shape=(64, 64)):
        env = np.zeros(shape, np.float32)
        for y, x in pixels:
            env[y, x] = 0.9
        return env

    def test_more_than_k_hot_pixels_in_one_tile_fails(self):
        hot = [(y, x) for y in range(8) for x in range(7)][:51]
        verdict = judge_capture(self.env_with_hot(hot), DEFAULTS)
        self.assertFalse(verdict["passed"])
        self.assertEqual(verdict["worst_tile_hot"], 51)
        self.assertEqual(verdict["failing_tiles"], [[0, 0]])

    def test_exactly_k_hot_pixels_passes(self):
        hot = [(y, x) for y in range(8) for x in range(7)][:50]
        self.assertTrue(judge_capture(self.env_with_hot(hot), DEFAULTS)["passed"])

    def test_a_hot_edge_spread_over_tiles_passes(self):
        # A one-pixel line across the whole image: 32 hot pixels per tile.
        verdict = judge_capture(self.env_with_hot([(10, x) for x in range(64)]), DEFAULTS)
        self.assertTrue(verdict["passed"])
        self.assertEqual(verdict["worst_tile_hot"], 32)

    def test_pixels_at_the_threshold_are_not_hot(self):
        env = np.full((32, 32), DEFAULTS["hot_flip"], np.float32)
        self.assertEqual(judge_capture(env, DEFAULTS)["worst_tile_hot"], 0)

    def test_mean_is_reported_but_never_decides(self):
        env = np.full((32, 32), 0.4, np.float32)  # high mean, nothing above 0.5
        verdict = judge_capture(env, DEFAULTS)
        self.assertTrue(verdict["passed"])
        self.assertAlmostEqual(verdict["mean_flip"], 0.4, places=5)

    def test_partial_edge_tiles_count(self):
        hot = [(y, x) for y in range(64, 70) for x in range(64, 73)]  # 54 px in a 6-row tile
        verdict = judge_capture(self.env_with_hot(hot, shape=(70, 73)), DEFAULTS)
        self.assertFalse(verdict["passed"])
        self.assertEqual(verdict["failing_tiles"], [[2, 2]])


def report(**overrides):
    clean = {"native_requested": True, "native_active": True, "fallback": "none", "frames": 4301,
             "debug_cvars": [], "draw_skips": {}}
    return {**clean, **overrides}


def skip(total=3, frame=120, key="00000000deadbeef"):
    return {"total": total, "first_frame": frame, "first_key": key}


class RendererRulesTests(unittest.TestCase):
    def test_a_clean_native_run_passes(self):
        self.assertEqual(renderer_failures(report(), run=1), [])

    def test_any_draw_skip_fails_and_names_reason_frame_and_key(self):
        failures = renderer_failures(report(draw_skips={"no_texture": skip(2, 150, "1f260000")}),
                                     run=1)
        self.assertEqual(len(failures), 1)
        self.assertIn("no_texture", failures[0])
        self.assertIn("150", failures[0])
        self.assertIn("1f260000", failures[0])

    def test_pipeline_pending_is_allowed_on_the_first_run_only(self):
        pending = report(draw_skips={"pipeline_pending": skip()})
        self.assertEqual(renderer_failures(pending, run=1), [])
        self.assertIn("pipeline_pending", renderer_failures(pending, run=2)[0])

    def test_a_renderer_fallback_fails(self):
        failures = renderer_failures(report(native_active=False, fallback="stale_shader_pack"),
                                     run=1)
        self.assertTrue(any("stale_shader_pack" in f for f in failures))

    def test_a_gpu_failure_fails_with_its_vulkan_result(self):
        failures = renderer_failures(report(gpu_failure="VK_ERROR_DEVICE_LOST"), run=1)
        self.assertEqual(failures, ["gpu failure: VK_ERROR_DEVICE_LOST"])

    def test_native_not_active_fails(self):
        self.assertTrue(renderer_failures(report(native_active=False), run=1))

    def test_a_debug_cvar_fails(self):
        failures = renderer_failures(report(debug_cvars=["wwe_native_debug_no_blend"]), run=1)
        self.assertIn("wwe_native_debug_no_blend", failures[0])

    def test_an_enhancement_on_fails_with_its_name(self):
        failures = renderer_failures(
            report(enhancements=[{"name": "wwe_native_supersample", "on": True}]), run=1)
        self.assertEqual(len(failures), 1)
        self.assertIn("wwe_native_supersample", failures[0])

    def test_enhancements_listed_off_pass(self):
        rep = report(enhancements=[{"name": "wwe_native_supersample", "on": False}])
        self.assertEqual(renderer_failures(rep, run=1), [])

    def test_the_runner_turns_every_enhancement_off(self):
        self.assertIn("wwe_native_supersample", ENHANCEMENTS)
        self.assertIn("wwe_native_cas", ENHANCEMENTS)
        self.assertEqual(ENHANCEMENTS["wwe_vibration_mode"], "1")  # Original
        self.assertEqual(ENHANCEMENTS["wwe_native_color_mode"], "0")  # Original colour
        for name, off in ENHANCEMENTS.items():
            self.assertIn(f"--{name}={off}", RUN_FLAGS)

    def test_scene_runs_force_full_vibration_strength(self):
        # Pro vibration 02: a player's lower strength never reaches a scene run.
        self.assertIn("--wwe_vibration_strength=100", RUN_FLAGS)

    def test_scene_runs_keep_the_unsynced_present_mode(self):
        # Scenes keep the pacing their oracles were captured with (smooth-presentation 09).
        self.assertIn("--present_vsync=false", RUN_FLAGS)
        # playable-build 42: frames match the 1280x720 oracles whatever the copied settings say.
        self.assertIn("--wwe_native_resolution_scale=1", RUN_FLAGS)
        self.assertIn("--vblank_follows_display=false", RUN_FLAGS)

    def test_native_runs_share_one_pipeline_store_through_the_setting(self):
        # zero-stutter 02: each run has its own cache_root, so the store the
        # second run starts from is named by a setting, not the working directory.
        out = Path("D:/scene-out")
        self.assertEqual(native_pipeline_flags(out),
                         [f"--wwe_native_pipeline_dir={(out / 'native-pipelines').as_posix()}"])

    def test_a_missing_report_fails(self):
        self.assertIn("no session report", renderer_failures(None, run=1)[0])


class ContactSheetTests(unittest.TestCase):
    def test_one_row_per_capture_with_emulated_native_and_heat_and_a_verdict_band(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp)
            for name, colour in (("c.png", (0, 0, 255)), ("n.png", (0, 255, 0)),
                                 ("heat.png", (255, 0, 0))):
                Image.new("RGB", (1280, 720), colour).save(out / name)
            capture = lambda frame, passed: {
                "frame": frame, "passed": passed,
                "images": {"classic-1": str(out / "c.png"), "native-1": str(out / "n.png")},
                "native": {"native-1": {"passed": passed, "worst_tile_hot": 0 if passed else 99,
                                        "heatmap": "heat.png"}}}
            result = {"scene": "s", "passed": False,
                      "captures": [capture(2700, True), capture(3150, False)]}
            sheet = Image.open(contact_sheet(result, out))
            w, h = SHEET_CELL
            self.assertEqual(sheet.size[0], 3 * w)
            self.assertGreaterEqual(sheet.size[1], 2 * h)
            row = lambda i: sheet.size[1] * (2 * i + 1) // 4
            self.assertEqual(sheet.getpixel((w // 2, row(0))), (0, 0, 255))      # Emulated
            self.assertEqual(sheet.getpixel((w + w // 2, row(0))), (0, 255, 0))  # Native
            self.assertEqual(sheet.getpixel((2 * w + w // 2, row(0))), (255, 0, 0))  # heat
            band_pass = sheet.getpixel((3 * w - 3, sheet.size[1] // 2 - 3))
            band_fail = sheet.getpixel((3 * w - 3, sheet.size[1] - 3))
            self.assertNotEqual(band_pass, band_fail)


if __name__ == "__main__":
    unittest.main()
