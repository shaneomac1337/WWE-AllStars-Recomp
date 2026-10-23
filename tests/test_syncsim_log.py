import contextlib
import io
import struct
import tempfile
import unittest
from pathlib import Path

from scripts.syncsim_log import FormatError, compare, main, parse, stats

SIMSTATION = 0x05000801
INPUT = 0x06400803


def dataset(sizes, values):
    """A dataset block: scatter mask, guard, then the values of the set bits."""
    mask, body = 0, b""
    for bit, (size, value) in enumerate(zip(sizes, values)):
        if value is not None:
            mask |= 1 << bit
            body += value.to_bytes(size, "little")
    return struct.pack("<II", mask, 0xDEADBEEF) + body


def step_record(seq, step, checksums, info=(None, None, None)):
    """One step: the SimStation with the given (sparse) StationInfo {StepFrequency, MaxRTT,
    MinBandwidth} and StationChecksum, plus an input object."""
    objects = (struct.pack("<I", SIMSTATION) + dataset([2, 2, 4], list(info))
               + dataset([4] * 6, checksums)
               + struct.pack("<I", INPUT) + dataset([1] * 4, [None] * 4) + dataset([2], [None]))
    body = struct.pack("<II", step, step + 1) + bytes([2]) + objects
    payload = struct.pack("<I", len(body)) + body
    return struct.pack("<4I", 12, seq, len(payload), 1) + payload


def replay_log(steps, record_count=None, seed=1234, clock_ms=1000, ssf=29):
    """A Station<n>.log with one record per (step, checksums[, info])."""
    header = bytearray(0x22C)
    struct.pack_into("<I", header, 0, 0x228)
    header[4:20] = b"SyncSimReplayLog"
    struct.pack_into("<I", header, 0x104, 1)
    header[0x108:0x114] = b"Quazal Net-Z"
    struct.pack_into("<I", header, 0x210, len(steps) if record_count is None else record_count)
    last = steps[-1][0] if steps else 0
    struct.pack_into("<6I", header, 0x214, seed, 0, last + 1, last, clock_ms, ssf)
    return bytes(header) + b"".join(step_record(i, *s) for i, s in enumerate(steps))


def full(sim):
    return [1, 2, 3, 4, 5, sim]


class SyncSimLogTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def write(self, name, data):
        path = Path(self.dir.name) / name
        path.write_bytes(data)
        return path

    def run_compare(self, a, b):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = compare(parse(self.write("Station1.log", a)), parse(self.write("Station2.log", b)))
        return code, out.getvalue()

    def test_stations_with_equal_checksums_are_in_sync(self):
        steps = [(1, full(10)), (2, [None] * 5 + [11]), (3, [None] * 5 + [12])]
        code, out = self.run_compare(replay_log(steps), replay_log(steps))
        self.assertEqual(code, 0)
        self.assertIn("in sync on every compared step", out)

    def test_the_first_differing_step_is_reported_after_rebuilding_deltas(self):
        a = replay_log([(1, full(10)), (2, [None] * 5 + [11]), (3, [None] * 5 + [12])])
        # Station 2 diverges at step 2 and stays different.
        b = replay_log([(1, full(10)), (2, [None] * 5 + [99]), (3, [None] * 5 + [12])])
        code, out = self.run_compare(a, b)
        self.assertEqual(code, 1)
        self.assertIn("SimulationChecksum: first differing step 2: 0x0000000B vs 0x00000063", out)
        self.assertIn("steps with any difference: 1 of 3", out)

    def test_a_checksum_one_station_has_not_sent_yet_counts_as_a_difference(self):
        a = replay_log([(1, full(10))])
        b = replay_log([(1, [1, 2, 3, 4, 5, None])])
        code, out = self.run_compare(a, b)
        self.assertEqual(code, 1)
        self.assertIn("first differing step 1: 0x0000000A vs --------", out)

    def test_a_truncated_step_is_a_format_error(self):
        data = replay_log([(1, full(10))])
        # Cut the step payload short but keep the record and length fields consistent.
        cut = 6
        record = bytearray(data[0x22C:-cut])
        struct.pack_into("<I", record, 8, struct.unpack_from("<I", record, 8)[0] - cut)
        struct.pack_into("<I", record, 16, struct.unpack_from("<I", record, 16)[0] - cut)
        with self.assertRaises(FormatError):
            parse(self.write("Station1.log", data[:0x22C] + bytes(record)))

    def test_a_record_count_that_disagrees_with_the_records_is_a_format_error(self):
        with self.assertRaises(FormatError):
            parse(self.write("Station1.log", replay_log([(1, full(10)), (2, full(11))], record_count=3)))

    def test_stats_summarise_the_rebuilt_station_info_and_ms_per_step(self):
        # Delta-encoded: step 2 sends only MaxRTT, so its StepFrequency stays 30; step 4
        # sends nothing, so it repeats step 3's values.
        steps = [(1, full(10), (30, 40, 1000)), (2, [None] * 6, (None, 60, None)),
                 (3, [None] * 6, (29, 50, None)), (4, [None] * 6)]
        log = parse(self.write("Station1.log", replay_log(steps, clock_ms=136, ssf=29)))
        s = stats(log)
        self.assertEqual(s["steps"], 4)
        self.assertEqual(s["last_ssf"], 29)
        self.assertAlmostEqual(s["ms_per_step"], 34.0)
        self.assertEqual((s["step_frequency"]["min"], s["step_frequency"]["max"]), (29, 30))
        self.assertAlmostEqual(s["step_frequency"]["mean"], (30 + 30 + 29 + 29) / 4)
        self.assertEqual((s["max_rtt"]["min"], s["max_rtt"]["max"]), (40, 60))
        self.assertAlmostEqual(s["max_rtt"]["mean"], (40 + 60 + 50 + 50) / 4)

    def test_dump_stats_prints_the_summary(self):
        steps = [(1, full(10), (30, 40, 1000)), (2, [None] * 6, (29, 60, None))]
        path = self.write("Station1.log", replay_log(steps, clock_ms=68, ssf=29))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(["dump", str(path), "--stats"])
        self.assertEqual(code, 0)
        self.assertIn("last_ssf 29, 34.00 ms per step over 2 steps", out.getvalue())
        self.assertIn("StepFrequency min 29 max 30 mean 29.50", out.getvalue())
        self.assertIn("MaxRTT min 40 max 60 mean 50.00 ms", out.getvalue())


if __name__ == "__main__":
    unittest.main()
