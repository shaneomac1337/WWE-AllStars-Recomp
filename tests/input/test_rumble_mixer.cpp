// The rumble mixer at the pad seam: effects added for a pad, observed at fake pads with an injected
// clock.

#include <catch_amalgamated.hpp>

#include <chrono>

#include <rex/cvar.h>

#include "fake_pads.h"
#include "vibration/mat_impact.h"
#include "vibration/mode.h"
#include "vibration/shapes.h"

using namespace rex::input;
using namespace wwe::test;
using namespace std::chrono_literals;
using wwe::vibration::Shape;
using wwe::vibration::Tier;

namespace {

// A clock the test moves by hand, Configure on the rig, and a clean mixer and
// default settings when the test ends.
struct MixerRig : Rig {
  wwe::vibration::Clock::time_point now{};
  wwe::vibration::GameState game{};

  explicit MixerRig(std::vector<std::vector<FakeDriver::Device>> drivers = {
                        {{kPadA, false, X_ERROR_SUCCESS}, {kPadB, false, X_ERROR_SUCCESS}}})
      : Rig(std::move(drivers)) {
    wwe::vibration::Configure(&input, [this] { return now; }, [this] { return game; });
  }

  ~MixerRig() {
    wwe::vibration::ClearEffects();
    rex::cvar::ResetToDefault("wwe_vibration_mode");
    rex::cvar::ResetToDefault("wwe_vibration_strength");
  }

  // One game request with both motors at rest, `step` after the last one; the
  // motors pad A received.
  Received Frame(uint32_t user = 0, std::chrono::microseconds step = 16667us) {
    now += step;
    received.clear();
    Rumble(user, 0, 0);
    REQUIRE(!received.empty());
    return received.front();
  }
};

// One constant sample per motor, in percent of full speed.
Shape Constant(uint8_t left, uint8_t right, float seconds) {
  return {{{{left, wwe::vibration::kConstant}, {right, wwe::vibration::kConstant}, seconds}}};
}

}  // namespace

TEST_CASE("an effect added for a pad plays at that pad's next request") {
  MixerRig rig;
  wwe::vibration::AddEffect(0, Constant(40, 100, 0.100f));

  const Received frame = rig.Frame();

  CHECK(frame.left == 26200);  // 40 x 655, as the game's sampler scales
  CHECK(frame.right == 65500);
}

TEST_CASE("a 550 ms body ends after 550 ms of requests, at any frame rate") {
  // Real time: the game's slow motion stretched this to 2.75 s on its own
  // waveform (the owner's match log); the mixer counts host time only.
  const float rate = GENERATE(60.0f, 30.0f, 144.0f);
  const auto step = std::chrono::microseconds(std::lround(1e6 / rate));
  MixerRig rig;
  wwe::vibration::AddEffect(0, Constant(70, 0, 0.550f));

  int nonzero = 0;
  for (int i = 0; i < int(rate * 2); ++i) {
    if (rig.Frame(0, step).left) ++nonzero;
  }

  // The first request plays it at 0 ms and every later one 1/rate further in:
  // 33 requests at 60 Hz (0 to 533 ms), 17 at 30 Hz, 80 at 144 Hz.
  const int expected = int(std::ceil(0.550 * rate));
  CHECK(nonzero == expected);
}

TEST_CASE("a crack is felt on two requests even through a long frame") {
  // The light hit shape is one 2.5-frame crack; at 10 fps a request lands
  // only every 100 ms.
  MixerRig rig;
  wwe::vibration::AddEffect(0, wwe::vibration::ShapeFor(Tier::kLight));

  const Received first = rig.Frame(0, 100ms);
  const Received second = rig.Frame(0, 100ms);
  const Received third = rig.Frame(0, 100ms);

  CHECK(first.right == 36025);  // 55 x 655
  CHECK(second.right == 36025);
  CHECK(third.right == 0);
}

TEST_CASE("a light crack plays over a heavy body without cancelling it") {
  // A big moment's body (left 100 cosine fall over 550 ms) is still playing
  // when a light hit lands: right takes the crack, left keeps the body.
  MixerRig rig;
  wwe::vibration::AddEffect(0, wwe::vibration::ShapeFor(Tier::kBigMoment));
  for (int i = 0; i < 10; ++i) rig.Frame();  // 150 ms: into the body

  wwe::vibration::AddEffect(0, wwe::vibration::ShapeFor(Tier::kLight));
  const Received frame = rig.Frame();

  CHECK(frame.right == 36025);  // the crack, 55 x 655
  CHECK(frame.left > 26200);    // the body, still above 40 %
  CHECK(frame.left < 65500);
}

TEST_CASE("a finisher's slam is felt over its big moment") {
  // The owner's match: every mat impact during a big moment was skipped. Now a
  // strong impact (grab shape, crack 60/60) on top of the body gives the body's
  // left and the crack's right.
  MixerRig rig;
  wwe::vibration::AddEffect(0, wwe::vibration::ShapeFor(Tier::kBigMoment));
  for (int i = 0; i < 20; ++i) rig.Frame();  // 333 ms in: right body ~12 %

  wwe::vibration::AddEffect(0, wwe::vibration::ShapeFor(Tier::kGrab));
  const Received frame = rig.Frame();

  CHECK(frame.right == 39300);  // 60 x 655
}

TEST_CASE("an effect rumbles only its own pad") {
  MixerRig rig;
  wwe::vibration::AddEffect(1, Constant(50, 50, 0.100f));

  const Received pad0 = rig.Frame(0);
  rig.now -= 16667us;  // the same frame for user 1
  const Received pad1 = rig.Frame(1);

  CHECK(pad0.id == kPadA);
  CHECK(pad0.left == 0);
  CHECK(pad0.right == 0);
  CHECK(pad1.id == kPadB);
  CHECK(pad1.left == 32750);
  CHECK(pad1.right == 32750);
}

TEST_CASE("each motor gets the stronger of the game's request and the mixer") {
  MixerRig rig;
  wwe::vibration::AddEffect(0, Constant(50, 10, 0.100f));

  rig.Rumble(0, 20000, 40000);

  REQUIRE(rig.received.size() == 1);
  CHECK(rig.received[0].left == 32750);   // the mixer's 50 %
  CHECK(rig.received[0].right == 40000);  // the game's own
}

TEST_CASE("strength scales the mix and the ledger keeps only the game's request") {
  MixerRig rig;
  REQUIRE(rex::cvar::SetFlagByName("wwe_vibration_strength", "50"));
  wwe::vibration::AddEffect(0, Constant(100, 0, 0.100f));

  rig.Rumble(0, 0, 20000);

  REQUIRE(rig.received.size() == 1);
  CHECK(rig.received[0].left == 16375);  // 65500 x 0.25, the strength-squared curve
  CHECK(rig.received[0].right == 5000);
  const VibrationLedger ledger = rig.input.vibration_ledger();
  CHECK(ledger.peak_left == 0);
  CHECK(ledger.peak_right == 20000);
}

TEST_CASE("Off silences the mixer's effects") {
  MixerRig rig;
  REQUIRE(rex::cvar::SetFlagByName("wwe_vibration_mode", "0"));
  wwe::vibration::AddEffect(0, Constant(100, 100, 0.100f));

  const Received frame = rig.Frame();

  CHECK(frame.left == 0);
  CHECK(frame.right == 0);
}

TEST_CASE("pausing stops the effects and none resumes after unpausing") {
  MixerRig rig;
  wwe::vibration::AddEffect(0, Constant(70, 70, 0.500f));
  REQUIRE(rig.Frame().left == 45850);

  rig.game.paused = true;
  const Received paused = rig.Frame();
  rig.game.paused = false;
  const Received resumed = rig.Frame();

  CHECK(paused.left == 0);
  CHECK(paused.right == 0);
  CHECK(resumed.left == 0);
  CHECK(resumed.right == 0);
}

TEST_CASE("while paused Enhanced sends zero, even for the game's own request") {
  // The game's waveform keeps advancing through a pause (ticket 01).
  MixerRig rig;
  rig.game.paused = true;

  rig.Rumble(0, 1000, 2000);

  REQUIRE(rig.received.size() == 1);
  CHECK(rig.received[0].left == 0);
  CHECK(rig.received[0].right == 0);
}

TEST_CASE("in Original a pause leaves the game's request alone") {
  MixerRig rig;
  REQUIRE(rex::cvar::SetFlagByName("wwe_vibration_mode", "1"));
  rig.game.paused = true;

  rig.Rumble(0, 1000, 2000);

  REQUIRE(rig.received.size() == 1);
  CHECK(rig.received[0].left == 1000);
  CHECK(rig.received[0].right == 2000);
}

TEST_CASE("leaving Enhanced drops the effects still playing") {
  MixerRig rig;
  wwe::vibration::AddEffect(0, Constant(70, 70, 0.500f));
  REQUIRE(rig.Frame().left == 45850);

  REQUIRE(rex::cvar::SetFlagByName("wwe_vibration_mode", "1"));
  const Received original = rig.Frame();
  REQUIRE(rex::cvar::SetFlagByName("wwe_vibration_mode", "2"));
  const Received enhanced = rig.Frame();

  CHECK(original.left == 0);
  CHECK(enhanced.left == 0);
}

TEST_CASE("a reverser's snap keeps its gap through long frames") {
  // Tap, 2.5-frame gap, tap: at 20 fps the gap is shorter than a frame but
  // still gets a request of its own.
  MixerRig rig;
  wwe::vibration::AddEffect(
      0, wwe::vibration::ReversalShape(wwe::vibration::ReversalRole::kReverser));

  std::vector<uint16_t> right;
  for (int i = 0; i < 8; ++i) right.push_back(rig.Frame(0, 50ms).right);

  const std::vector<uint16_t> expected = {39300, 39300, 0, 39300, 39300, 0, 0, 0};
  CHECK(right == expected);
}

TEST_CASE("a new world (leaving the match) drops every effect") {
  MixerRig rig;
  rig.game.world = 0x90000000;
  rig.Frame(0);  // the match's world is loaded
  wwe::vibration::AddEffect(0, Constant(70, 70, 0.500f));
  wwe::vibration::AddEffect(1, Constant(70, 70, 0.500f));
  REQUIRE(rig.Frame(0).left == 45850);

  rig.game.world = 0x90100000;
  const Received pad0 = rig.Frame(0);
  const Received pad1 = rig.Frame(1);

  CHECK(pad0.left == 0);
  CHECK(pad1.left == 0);
}

TEST_CASE("after a match end the pads go quiet once its effects finish") {
  // A long effect still playing when the match ends is dropped; only the
  // match-end effect plays, then nothing.
  MixerRig rig;
  wwe::vibration::AddEffect(0, Constant(30, 30, 5.0f));
  rig.Frame();

  wwe::vibration::ClearEffects();
  wwe::vibration::AddEffect(
      0, wwe::vibration::MatchEndShape(wwe::vibration::MatchEndRole::kWinner));  // 400 ms
  int nonzero_after_400ms = 0;
  for (int i = 0; i < 60; ++i) {
    const Received frame = rig.Frame();
    if (i * 16.667 >= 400 && (frame.left || frame.right)) ++nonzero_after_400ms;
  }

  CHECK(nonzero_after_400ms == 0);
}

TEST_CASE("a shake and a slam in the same frame feel like the stronger one") {
  // A strong slam notify (crack 60/60) and a medium camera shake (45/45) for
  // the same landing: the maximum, not the sum.
  using wwe::vibration::ImpactRole;
  using wwe::vibration::ImpactShape;
  using wwe::vibration::ImpactSize;
  MixerRig rig;
  wwe::vibration::AddEffect(0, ImpactShape(ImpactSize::kStrong, ImpactRole::kImpact));
  wwe::vibration::AddEffect(0, ImpactShape(ImpactSize::kMedium, ImpactRole::kImpact));

  const Received frame = rig.Frame();

  CHECK(frame.left == 39300);  // 60 x 655
  CHECK(frame.right == 39300);
}
