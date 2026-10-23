// The online step rate (.scratch/online-latency/ ticket 03): what SyncSim's
// adaptive-rate calculation publishes as the local StepFrequency.

#include <catch_amalgamated.hpp>

#include <cstdint>
#include <vector>

#include "online/step_rate.h"

using wwe::step_rate::SampleRate;

TEST_CASE("steps two 60 Hz ticks apart sample the full rate of 30", "[online][rate]") {
  // 33.3 ms measured in whole milliseconds is 33 or 34.
  CHECK(SampleRate(33, 30) == 30);
  CHECK(SampleRate(34, 30) == 30);
}

TEST_CASE("the 4-player variant samples its full rate of 15", "[online][rate]") {
  // Four ticks per step: 66.7 ms measures 66 or 67.
  CHECK(SampleRate(66, 15) == 15);
  CHECK(SampleRate(67, 15) == 15);
}

TEST_CASE("a real stall still lowers the sampled rate", "[online][rate]") {
  CHECK(SampleRate(51, 30) == 20);
  CHECK(SampleRate(101, 30) == 10);
  CHECK(SampleRate(201, 15) == 5);
  CHECK(SampleRate(5000, 30) == 0);
}

TEST_CASE("no interval yet samples the cap, as the game does", "[online][rate]") {
  CHECK(SampleRate(0, 30) == 30);
  CHECK(SampleRate(1, 30) == 30);
}

using wwe::step_rate::PublishRate;

TEST_CASE("the published rate climbs back to the cap after a stall", "[online][rate]") {
  // The game's mean after one 20 Hz sample in a run of 30s settles at 29.999985f
  // in single precision, which truncation would publish as 29 for good.
  CHECK(PublishRate(29.999985f, 30) == 30);
  CHECK(PublishRate(30.0f, 30) == 30);
  CHECK(PublishRate(14.99999f, 15) == 15);
}

TEST_CASE("the published rate rounds to nearest and stays within 1 and the cap", "[online][rate]") {
  CHECK(PublishRate(29.4f, 30) == 29);
  CHECK(PublishRate(20.6f, 30) == 21);
  CHECK(PublishRate(31.0f, 30) == 30);
  CHECK(PublishRate(0.2f, 30) == 1);
  CHECK(PublishRate(-3.0f, 30) == 1);
}

namespace {

// The game's smoothing at 0x830D9020-0x830D906C, in single precision: the weight
// sum (+0xC4) grows by 0.1 (+0xC0) per sample; while it is below 1 the weight is
// 0.1 / sum (a running mean), then 0.1. The mean (+0xBC) starts at 0.
struct GameMean {
  float mean = 0.0f;
  float weight_sum = 0.0f;
  float Add(uint32_t sample) {
    float weight = 0.1f;
    weight_sum += weight;
    if (weight_sum < 1.0f) weight = weight / weight_sum;
    const float product = float(sample) * weight;
    mean = float(double(1.0f - weight) * double(mean) + double(product));
    return mean;
  }
};

}  // namespace

TEST_CASE("mixed 33 and 34 ms intervals never publish below 30", "[online][rate]") {
  GameMean game;
  for (int i = 0; i < 2000; ++i) {
    const uint32_t elapsed = (i % 3 == 0 || i % 7 == 0) ? 34 : 33;
    REQUIRE(PublishRate(game.Add(SampleRate(elapsed, 30)), 30) == 30);
  }
}

TEST_CASE("mixed 66 and 67 ms intervals never publish below 15", "[online][rate]") {
  GameMean game;
  for (int i = 0; i < 2000; ++i) {
    const uint32_t elapsed = (i % 2 == 0) ? 67 : 66;
    REQUIRE(PublishRate(game.Add(SampleRate(elapsed, 15)), 15) == 15);
  }
}

TEST_CASE("a stall lowers the published rate, which then returns to 30", "[online][rate]") {
  GameMean game;
  for (int i = 0; i < 100; ++i) game.Add(SampleRate(33, 30));
  CHECK(PublishRate(game.Add(SampleRate(301, 30)), 30) == 27);  // 0.9 x 30 + 0.1 x 3
  uint32_t published = 0;
  for (int i = 0; i < 1000; ++i) published = PublishRate(game.Add(SampleRate(34, 30)), 30);
  CHECK(published == 30);
}

using wwe::step_rate::SessionSampleRate;

namespace {

// The whole ms SyncSim measured for steps 1..40 of a match (ol06diag, copy B): two
// steps at bring-up, the match intro (9115 ms), steps 4..30 three ticks apart before
// the fight, then the fight (the update comes one tick after each step).
std::vector<uint32_t> MatchStart() {
  std::vector<uint32_t> elapsed = {33, 1, 9115};
  for (uint32_t step = 4; step <= 30; ++step) elapsed.push_back(step % 3 == 0 ? 49 : 50);
  for (uint32_t step = 31; step <= 40; ++step) elapsed.push_back(19);
  return elapsed;
}

}  // namespace

TEST_CASE("the game's rule drops a match start to 20", "[online][rate]") {
  GameMean game;
  uint32_t published = 0;
  for (uint32_t step = 1; step <= 30; ++step) {
    published = PublishRate(game.Add(SampleRate(MatchStart()[step - 1], 30)), 30);
  }
  CHECK(published == 20);
}

TEST_CASE("a match starts and stays at the full rate", "[online][rate]") {
  GameMean game;
  const auto elapsed = MatchStart();
  for (uint32_t step = 1; step <= elapsed.size(); ++step) {
    REQUIRE(PublishRate(game.Add(SessionSampleRate(step, elapsed[step - 1], 30)), 30) == 30);
  }
}

TEST_CASE("the start of a 4-player match samples its cap of 15", "[online][rate]") {
  CHECK(SessionSampleRate(3, 9115, 15) == 15);
  CHECK(SessionSampleRate(20, 50, 15) == 15);
}

TEST_CASE("after the start a real stall still lowers the sampled rate", "[online][rate]") {
  CHECK(SessionSampleRate(1000, 51, 30) == 20);
  CHECK(SessionSampleRate(1000, 301, 30) == 3);
  CHECK(SessionSampleRate(1000, 34, 30) == 30);
}
