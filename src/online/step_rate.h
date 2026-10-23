// The online step rate (.scratch/online-latency/ ticket 03; evidence in
// docs/research/online-latency.md §2.3 and the manifest entries WweStepRateSample
// and WweStepRatePublish). Pure decisions, tested in tests/core/test_step_rate.cpp,
// used by src/online/step_rate_hooks.cpp.
//
// SyncSim's adaptive rate (TU2 0x830D8FA0) samples min(cap, 1000 / elapsed) from the
// whole milliseconds since the last step, smooths the samples (running mean, then
// weight 0.1) and publishes the truncated mean. Both the sample and the publish
// truncate, so it settles at 29 (34 ms) where two 60 Hz ticks (33.3 ms) allow 30.

#pragma once

#include <cstdint>

namespace wwe::step_rate {

// The rate one interval allows. elapsed_ms is the difference of two whole-ms clock
// readings, so the true interval is longer than elapsed_ms - 1: the sample is the
// highest rate that interval is consistent with. 33 and 34 ms give 30, 66 and 67 ms
// give 15; a real stall (50 ms) still lowers it (20). 0 (no interval) gives the cap,
// as the game does.
inline uint32_t SampleRate(uint32_t elapsed_ms, uint32_t cap) {
  if (elapsed_ms == 0) return cap;
  const uint32_t interval = elapsed_ms > 1 ? elapsed_ms - 1 : 1;
  const uint32_t rate = 1000 / interval;
  return rate < cap ? rate : cap;
}

// The steps at the start of a match whose intervals are not a rate (ticket 06). In
// ol06diag SyncSim steps twice at bring-up, waits out the match intro (9115 ms
// measured for step 3), then steps 4..30 come three ticks (50 ms) apart until the
// fight, whatever rate is published (held 0 ms: each update found its deadline
// passed). The game's mean starts at 0 with weight 0.1 / sum until the sum reaches 1
// (constructor 0x830D8A18), so the 0 for step 3 weighs 1/4 and the 20s hold it at 20:
// the fight then starts holding for 50 ms deadlines and climbs to 30 in about a
// second, the stutter after the bell. Steps up to this count sample the cap, a margin
// of three over the 30 measured; the count is the same on every peer.
inline constexpr uint32_t kStartSteps = 90;

// SampleRate for the calculation after step `step` (sim+0x78).
inline uint32_t SessionSampleRate(uint32_t step, uint32_t elapsed_ms, uint32_t cap) {
  return step <= kStartSteps ? cap : SampleRate(elapsed_ms, cap);
}

// What to publish for the smoothed mean of the samples: the nearest whole rate,
// within 1 (the game divides 1000 by it) and the cap. The game truncates instead;
// after any stall its single-precision mean of 30s recovers only to 29.999985, which
// truncates to 29 for good.
inline uint32_t PublishRate(float mean, uint32_t cap) {
  if (!(mean >= 1.5f)) return 1;
  if (mean >= float(cap)) return cap;
  return uint32_t(mean + 0.5f);
}

}  // namespace wwe::step_rate
