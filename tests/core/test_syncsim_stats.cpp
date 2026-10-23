// The online stats line (online-latency ticket 01): SyncSim update results in,
// one-second totals out.

#include <catch_amalgamated.hpp>

#include <optional>

#include "online/syncsim_stats.h"

using wwe::syncsim_stats::Accumulator;
using wwe::syncsim_stats::Second;
using wwe::syncsim_stats::kHeld;
using wwe::syncsim_stats::kStalled;
using wwe::syncsim_stats::kStepped;

TEST_CASE("a second of clean steps reports the steps and nothing held or stalled", "[syncsim]") {
  Accumulator stats;
  std::optional<Second> second;
  // 30 steps, 33 ms apart, from t = 0 to t = 990 ms: the window is still open.
  for (int i = 0; i < 30; ++i) {
    CHECK_FALSE(stats.Add(kStepped, i * 33000ull).has_value());
  }
  // The call at 1000 ms closes the second and counts in it.
  second = stats.Add(kStepped, 1000000);
  REQUIRE(second.has_value());
  CHECK(second->steps == 31);
  CHECK(second->held_us == 0);
  CHECK(second->stalled_us == 0);
  CHECK(second->longest_stall_us == 0);
}

TEST_CASE("time after a held or stalled update counts until the next update", "[syncsim]") {
  Accumulator stats;
  // A step at 0, then the update holds for the deadline twice (0.4 ms + 0.3 ms)
  // before the step at 34 ms: the 33.3 ms after the step is game time, not held.
  stats.Add(kStepped, 0);
  stats.Add(kHeld, 33300);
  stats.Add(kHeld, 33700);
  stats.Add(kStepped, 34000);
  // A stall for remote input from 100 ms: 5 ms, 7 ms, then stepped at 112 ms.
  stats.Add(kStalled, 100000);
  stats.Add(kStalled, 105000);
  stats.Add(kStepped, 112000);
  const auto second = stats.Add(kStepped, 1000000);
  REQUIRE(second.has_value());
  CHECK(second->steps == 4);
  CHECK(second->held_us == 700);
  CHECK(second->stalled_us == 12000);
  CHECK(second->longest_stall_us == 12000);
}

TEST_CASE("each second starts empty, and a stall across the boundary is split", "[syncsim]") {
  Accumulator stats;
  stats.Add(kStepped, 0);
  stats.Add(kHeld, 500000);
  // A stall from 990 ms to 1,030 ms: the update at 1,000 ms closes the first second
  // with the 10 ms stalled so far, the rest lands in the next one.
  stats.Add(kStalled, 990000);
  const auto first = stats.Add(kStalled, 1000000);
  REQUIRE(first.has_value());
  CHECK(first->steps == 1);
  CHECK(first->held_us == 490000);
  CHECK(first->stalled_us == 10000);
  CHECK(first->longest_stall_us == 10000);  // still stalled: the longest so far

  stats.Add(kStepped, 1030000);  // the stall ends after 40 ms
  CHECK_FALSE(stats.Add(kStepped, 1999000).has_value());
  const auto second = stats.Add(kStepped, 2000000);
  REQUIRE(second.has_value());
  CHECK(second->steps == 3);
  CHECK(second->held_us == 0);
  CHECK(second->stalled_us == 30000);
  CHECK(second->longest_stall_us == 40000);
}

TEST_CASE("a gap with no updates, as between matches, opens a fresh second", "[syncsim]") {
  Accumulator stats;
  stats.Add(kStepped, 0);
  // The next match starts a minute later: the gap belongs to no second.
  const auto gap = stats.Add(kStepped, 60000000);
  CHECK_FALSE(gap.has_value());
  CHECK_FALSE(stats.Add(kStepped, 60500000).has_value());
  const auto second = stats.Add(kStepped, 61000000);
  REQUIRE(second.has_value());
  CHECK(second->steps == 3);
}

TEST_CASE("the input lead is the latency in whole step periods, at least one", "[syncsim]") {
  using wwe::syncsim_stats::InputLead;
  // 29 steps/s: a 34 ms period (1000 / 29 truncated, as the game divides).
  CHECK(InputLead(30, 29) == 1);  // the latency floor
  CHECK(InputLead(34, 29) == 1);
  CHECK(InputLead(35, 29) == 2);
  CHECK(InputLead(68, 29) == 2);
  CHECK(InputLead(69, 29) == 3);
  // The starting latency, 90 ms, at 30 steps/s (33 ms).
  CHECK(InputLead(90, 30) == 3);
  CHECK(InputLead(0, 30) == 1);
  // Before SyncSim has a rate the lead is unknown.
  CHECK(InputLead(90, 0) == 0);
}
