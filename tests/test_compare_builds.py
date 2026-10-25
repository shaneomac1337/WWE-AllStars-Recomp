import unittest

from scripts.compare_builds import compare, format_table


def tick_log(seconds):
    """A runtime log with one --wwe_tick_log summary line per (ticks, at 1/30 s, work mean)."""
    return [f"[2026-10-04 18:00:{i:02d}.000] [info] [core] [t1] game tick 1s: {ticks} ticks "
            f"({half} at 1/30 s, 0 other steps), work mean {work:.2f} ms max 9.0 ms, "
            f"tick max 17.0 ms, dilation 1.000..1.000"
            for i, (ticks, half, work) in enumerate(seconds)]


def pacing_log(windows, minute="01"):
    """--present_pacing_stats 5 s lines, one per (frames, stdev ms, max ms), 5 s apart."""
    return [f"[2026-10-04 18:{minute}:{5 * i:02d}.000] [info] [core] [t2] Paced presentation 5s: "
            f"{frames} frames, interval 16.667 ms mean, {sd:.3f} ms stdev, {mx:.3f} ms max; "
            f"0 vblanks without a new frame (0 presented the last frame again), 0 frames "
            f"replaced before display, {frames} host presents; window paints: 0 forced, 0 UI, "
            f"0 requested, 0 outdated"
            for i, (frames, sd, mx) in enumerate(windows)]


class CompareTests(unittest.TestCase):
    def test_mean_work_per_tick_and_change_against_the_first_build(self):
        rows = compare({"v2o2": tick_log([(60, 0, 10.0)] * 5),
                        "v3o2": tick_log([(60, 0, 8.0)] * 5)}, skip_s=0, span_s=60)
        self.assertEqual([r.name for r in rows], ["v2o2", "v3o2"])
        self.assertEqual(rows[0].ticks, 300)
        self.assertAlmostEqual(rows[1].work_ms, 8.0)
        self.assertIsNone(rows[0].change_pct)
        self.assertAlmostEqual(rows[1].change_pct, -20.0)

    def test_only_the_span_after_the_skipped_seconds_counts(self):
        # Two loading seconds, three menu seconds, then a slow second past the span.
        log = tick_log([(20, 0, 40.0)] * 2 + [(60, 0, 10.0), (60, 0, 12.0), (60, 0, 11.0), (60, 0, 50.0)])
        [row] = compare({"v2o2": log}, skip_s=2, span_s=3)
        self.assertEqual(row.ticks, 180)
        self.assertAlmostEqual(row.work_ms, 11.0)

    def test_ticks_are_weighted_when_seconds_differ(self):
        [row] = compare({"v2o2": tick_log([(60, 0, 10.0), (30, 30, 16.0)])}, skip_s=0, span_s=60)
        self.assertAlmostEqual(row.work_ms, 12.0)

    def test_table_names_the_baseline_and_signs_the_change(self):
        rows = compare({"v2o2": tick_log([(60, 0, 10.0)]), "v3o2": tick_log([(60, 0, 9.5)]),
                        "v2o3": tick_log([(60, 0, 10.25)])}, skip_s=0, span_s=60)
        self.assertEqual(format_table(rows).splitlines()[2:], [
            "| v2o2 | 60 | 10.00 ms | baseline | - | - |",
            "| v3o2 | 60 | 9.50 ms | -5.0 % | - | - |",
            "| v2o3 | 60 | 10.25 ms | +2.5 % | - | - |"])

    def test_pacing_stdev_is_frame_weighted_and_hitch_windows_counted_in_the_span(self):
        # Ticks from 18:00:00 for 120 s; pacing windows at 18:01:00, :05, :10, then one at 18:03
        # past the span.
        log = (tick_log([(60, 0, 10.0)] * 60) + pacing_log([(300, 0.010, 17.0), (100, 0.050, 40.0),
                                                            (300, 0.010, 17.1)])
               + pacing_log([(300, 9.0, 99.0)], minute="03"))
        [row] = compare({"v2o2": log}, skip_s=0, span_s=120)
        self.assertAlmostEqual(row.pacing_sd_ms, (300 * 0.010 + 100 * 0.050 + 300 * 0.010) / 700)
        self.assertEqual((row.hitch_windows, row.pacing_windows), (1, 3))
        self.assertTrue(format_table([row]).splitlines()[2].endswith("| 0.016 ms | 1/3 |"))


if __name__ == "__main__":
    unittest.main()
