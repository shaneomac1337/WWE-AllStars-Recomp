// Finisher wind-up: its shape
// (seam 2) and the one-per-second debounce per wrestler.

#include <catch_amalgamated.hpp>

#include <chrono>

#include "vibration/shapes.h"
#include "vibration/wind_up.h"

using namespace std::chrono_literals;
using wwe::vibration::Shape;
using wwe::vibration::WindUpDebounce;
using wwe::vibration::WindUpShape;

TEST_CASE("a wind-up rises on the left motor only to half speed, then lets go") {
  const Shape shape = WindUpShape();
  REQUIRE(shape.samples.size() == 2);
  const auto& rise = shape.samples[0];
  CHECK(rise.left.amplitude == 50);
  CHECK(rise.left.function == wwe::vibration::kLinearIncreasing);
  CHECK(rise.right.amplitude == 0);
  CHECK(rise.seconds * 1000 == Catch::Approx(750).margin(0.5));
  const auto& release = shape.samples[1];
  CHECK(release.left.amplitude == 50);
  CHECK(release.left.function == wwe::vibration::kLinearDecreasing);
  CHECK(release.right.amplitude == 0);
  CHECK(release.seconds * 1000 == Catch::Approx(100).margin(0.5));
  CHECK(shape.Peak().left < 100);
}

TEST_CASE("a wrestler gets one wind-up per second however many states it enters") {
  WindUpDebounce debounce;
  const auto t0 = WindUpDebounce::Clock::time_point{} + 10s;

  CHECK(debounce.Take(0x500, t0));
  CHECK_FALSE(debounce.Take(0x500, t0 + 400ms));
  CHECK_FALSE(debounce.Take(0x500, t0 + 999ms));
  CHECK(debounce.Take(0x500, t0 + 1s));
}

TEST_CASE("the debounce is per wrestler") {
  WindUpDebounce debounce;
  const auto t0 = WindUpDebounce::Clock::time_point{} + 10s;

  CHECK(debounce.Take(0x500, t0));
  CHECK(debounce.Take(0x600, t0 + 100ms));
}
