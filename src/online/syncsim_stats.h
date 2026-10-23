// The online stats line (.scratch/online-latency/ ticket 01; evidence in
// docs/research/online-latency.md §2.2): SyncSim update results in, one-second
// totals out. Pure, tested in tests/core/test_syncsim_stats.cpp, used by the
// log-only hook in src/online/syncsim_stats_hook.cpp.
//
// SyncSim's update (TU2 0x830D9F00, called at 0x82A621B4) returns 0 when it
// stepped, 1 when the inputs are ready but the step deadline isn't, and 2 when a
// station's input is missing. On 1 and 2 the game thread yields and calls it
// again at once, so the time from a 1 or 2 to the next call is time the frame
// was held for the deadline or stalled for remote input.

#pragma once

#include <cstdint>
#include <optional>

namespace wwe::syncsim_stats {

inline constexpr uint32_t kStepped = 0;
inline constexpr uint32_t kHeld = 1;
inline constexpr uint32_t kStalled = 2;

struct Second {
  uint32_t steps = 0;
  int64_t held_us = 0;
  int64_t stalled_us = 0;
  int64_t longest_stall_us = 0;  // one run of code 2s, also one still running
};

inline constexpr uint64_t kWindowUs = 1000000;
// Longer than any wait inside the update loop (its stall sleeps are 1 s and 2.4 s):
// a gap past it means no match ran in between, so a fresh second starts.
inline constexpr uint64_t kGapUs = 10000000;

class Accumulator {
 public:
  // One update result at host time now_us. Returns the totals when this call
  // closes a second; the call counts in the second it closes.
  std::optional<Second> Add(uint32_t code, uint64_t now_us) {
    if (!started_ || now_us - previous_us_ > kGapUs) {
      started_ = true;
      window_start_us_ = now_us;
      current_ = {};
      stall_us_ = 0;
    } else {
      const int64_t since_us = int64_t(now_us - previous_us_);
      if (previous_code_ == kHeld) current_.held_us += since_us;
      if (previous_code_ == kStalled) {
        current_.stalled_us += since_us;
        stall_us_ += since_us;
      }
    }
    if (code != kStalled && stall_us_ > 0) {
      if (stall_us_ > current_.longest_stall_us) current_.longest_stall_us = stall_us_;
      stall_us_ = 0;
    }
    previous_code_ = code;
    previous_us_ = now_us;
    if (code == kStepped) ++current_.steps;
    if (now_us - window_start_us_ < kWindowUs) return std::nullopt;
    Second done = current_;
    if (stall_us_ > done.longest_stall_us) done.longest_stall_us = stall_us_;
    current_ = {};
    window_start_us_ = now_us;
    return done;
  }

 private:
  uint32_t previous_code_ = kStepped;
  uint64_t previous_us_ = 0;
  int64_t stall_us_ = 0;  // the stall in progress
  bool started_ = false;
  uint64_t window_start_us_ = 0;
  Second current_;
};

// The input lead L in steps, as 0x830D7880 computes it from the sim's latency
// (sim+0xB0, ms) and StepFrequency (sim+0x98): ceil(latency / (1000 / freq)) with
// integer division, at least 1. 0 while the rate is unknown.
inline uint32_t InputLead(uint32_t latency_ms, uint32_t step_frequency) {
  if (step_frequency == 0) return 0;
  const uint32_t period_ms = 1000 / step_frequency;
  if (period_ms == 0) return 0;
  const uint32_t lead = (latency_ms + period_ms - 1) / period_ms;
  return lead == 0 ? 1 : lead;
}

}  // namespace wwe::syncsim_stats
