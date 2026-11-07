// Reversals: the reverser's snap
// and the reversed wrestler's jolt (seam 2), and who they are (seam 3).

#include <catch_amalgamated.hpp>

#include <cstdint>
#include <optional>

#include "fake_guest.h"
#include "vibration/force_feedback.h"
#include "vibration/mat_impact.h"
#include "vibration/shapes.h"

using wwe::test::FakeGuest;
using wwe::vibration::Reversed;
using wwe::vibration::ReversalRole;
using wwe::vibration::ReversalShape;
using wwe::vibration::RumblePad;
using wwe::vibration::Shape;

namespace {

constexpr uint32_t kReverser = 0x400;
constexpr uint32_t kOther = 0x600;

uint32_t Cast(uint32_t object) {
  return object == FakeGuest::kPawn || object == kOther ? object : 0;
}

}  // namespace

TEST_CASE("the reverser feels a snap: two right taps with a gap") {
  const Shape shape = ReversalShape(ReversalRole::kReverser);
  REQUIRE(shape.samples.size() == 3);
  const int right[] = {60, 0, 60};
  for (size_t i = 0; i < 3; ++i) {
    INFO("sample " << i);
    CHECK(shape.samples[i].left.amplitude == 0);
    CHECK(shape.samples[i].right.amplitude == right[i]);
    CHECK(shape.samples[i].seconds * 1000 == Catch::Approx(42).margin(0.5));  // 2.5 frames
  }
  CHECK(shape.samples[0].crack);
  CHECK_FALSE(shape.samples[1].crack);
  CHECK(shape.samples[2].crack);
}

TEST_CASE("the reversed wrestler feels a jolt: a left thump that falls away") {
  const Shape shape = ReversalShape(ReversalRole::kReversed);
  REQUIRE(shape.samples.size() == 2);
  CHECK(shape.samples[0].crack);
  CHECK(shape.samples[0].left.amplitude == 60);
  CHECK(shape.samples[0].right.amplitude == 0);
  CHECK(shape.samples[1].left.amplitude == 60);
  CHECK(shape.samples[1].left.function == wwe::vibration::kCosineFall);
  CHECK(shape.samples[1].right.amplitude == 0);
  CHECK(shape.samples[1].seconds * 1000 == Catch::Approx(250).margin(0.5));
}

TEST_CASE("the reversed wrestler is the reverser's target, then its last attacker") {
  FakeGuest guest(1);
  guest.Store32(kReverser + 0x81C, FakeGuest::kPawn);
  guest.Store32(kReverser + 0x95C, kOther);
  REQUIRE(Reversed(guest.base(), kReverser, Cast) == FakeGuest::kPawn);
  CHECK(RumblePad(guest.base(), FakeGuest::kPawn) == 1u);

  FakeGuest untargeted;
  untargeted.Store32(kReverser + 0x95C, kOther);
  untargeted.Store32(kReverser + 0x938, FakeGuest::kPawn);
  CHECK(Reversed(untargeted.base(), kReverser, Cast) == kOther);
}

TEST_CASE("a target that is no pawn falls back to the last attacker") {
  FakeGuest guest;
  guest.Store32(kReverser + 0x81C, 0x700);  // not a pawn
  guest.Store32(kReverser + 0x95C, kOther);
  CHECK(Reversed(guest.base(), kReverser, Cast) == kOther);
}

TEST_CASE("no pawn, or the reverser itself, is never the reversed wrestler") {
  FakeGuest weapon;
  weapon.Store32(kReverser + 0x81C, 0x700);  // not a pawn
  CHECK_FALSE(Reversed(weapon.base(), kReverser, Cast));

  FakeGuest self;
  self.Store32(FakeGuest::kPawn + 0x81C, FakeGuest::kPawn);
  CHECK_FALSE(Reversed(self.base(), FakeGuest::kPawn, Cast));
}

TEST_CASE("the AI's wrestler gets no reversal rumble") {
  FakeGuest guest;
  guest.PossessByAi();
  guest.Store32(kReverser + 0x81C, FakeGuest::kPawn);
  REQUIRE(Reversed(guest.base(), kReverser, Cast));
  CHECK_FALSE(RumblePad(guest.base(), FakeGuest::kPawn));
}
