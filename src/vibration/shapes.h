// Hit shapes (CONTEXT.md): the two-motor waveform each pulse tier plays in
// Enhanced (research table in
// docs/research/rumble-best-practices.md 3.1). One table; the owner playtests
// and retunes it here.

#pragma once

#include <algorithm>
#include <cstdint>
#include <optional>
#include <string_view>
#include <vector>

#include "vibration/pulse_tiers.h"

namespace wwe::vibration {

// UE3 EWaveformFunction, as the force-feedback sampler 0x829ED788 reads it
// (t is the time into the sample, d its length).
enum WaveformFunction : uint8_t {
  kConstant = 0,
  kLinearIncreasing = 1,  // t/d
  kLinearDecreasing = 2,  // 1 - t/d
  kSineRise = 3,          // sin(t/d * pi/2)
  kCosineFall = 4,        // sin((t/d + 1) * pi/2)
  kSineBump = 5,          // sin(t/d * pi)
  kNoise = 6,             // random 0..amplitude every frame
};

// One motor of a waveform sample: amplitude in percent of full motor speed
// (the sampler 0x829ED788 multiplies it by 655, so 100 is the most) and the
// UE3 EWaveformFunction it is shaped by.
struct Motor {
  uint8_t amplitude;
  uint8_t function;
};

// One waveform sample. The native stores it as {left amplitude, right
// amplitude, left function, right function, duration}: the motors share a
// length. The rumble mixer outputs a crack on at least two requests, however
// long the frames.
struct Sample {
  Motor left;
  Motor right;
  float seconds;
  bool crack = false;
};

// The strongest amplitude per motor.
struct Motors {
  uint8_t left;
  uint8_t right;
};

struct Shape {
  std::vector<Sample> samples;

  Motors Peak() const {
    Motors peak{0, 0};
    for (const Sample& s : samples) {
      peak.left = std::max(peak.left, s.left.amplitude);
      peak.right = std::max(peak.right, s.right.amplitude);
    }
    return peak;
  }

  float Seconds() const {
    float total = 0;
    for (const Sample& s : samples) total += s.seconds;
    return total;
  }
};

namespace detail {

// 0x8292B4B8 moves to the next sample once the elapsed time reaches the
// sample's length; 2.5 frames keeps two frames on a crack under frame-time
// jitter.
constexpr float kCrackSeconds = 2.5f / 60;
// A big moment's impact: three frames, held 3.5.
constexpr float kImpactSeconds = 3.5f / 60;

// A shape's opening crack: constant on both motors.
constexpr Sample Crack(uint8_t left, uint8_t right, float seconds = kCrackSeconds) {
  return {{left, kConstant}, {right, kConstant}, seconds, true};
}

// The critical heartbeat (Microsoft's GamepadVibration sample uses the same
// shape): two beats on the left motor with a gap.
constexpr Sample kBeat{{50, kConstant}, {0, kConstant}, 0.070f};
constexpr Sample kBeatGap{{0, kConstant}, {0, kConstant}, 0.120f};

}  // namespace detail

inline Shape ShapeFor(Tier tier) {
  using namespace detail;
  switch (tier) {
    case Tier::kLight:
      return {{Crack(10, 55)}};
    case Tier::kStrong:
      // The body is 40, not pro vibration's 45: a strike is a frequent
      // effect and stays under the hierarchy line (full-coverage spec).
      return {{Crack(35, 85),
               {{40, kLinearDecreasing}, {0, kConstant}, 0.100f}}};
    case Tier::kGrab:
      return {{Crack(60, 60),
               {{70, kCosineFall}, {0, kConstant}, 0.220f}}};
    case Tier::kWeapon:
      return {{Crack(30, 100),
               {{30, kLinearDecreasing}, {35, kNoise}, 0.100f}}};
    case Tier::kBigMoment:
      return {{Crack(100, 100, kImpactSeconds),
               {{100, kCosineFall}, {30, kLinearDecreasing}, 0.550f}}};
    case Tier::kCritical:
      return {{kBeat, kBeatGap, kBeat}};
  }
  return {{}};
}

// Attacker confirm (CONTEXT.md): what your pad gets when your hit lands. A
// right-motor tap, with a short left fall for a grab and a stronger one for a
// finisher, always lighter on both motors than the same hit taken. Critical
// health is the victim's alone.
inline std::optional<Shape> AttackerShapeFor(Tier tier) {
  using namespace detail;
  switch (tier) {
    case Tier::kLight:
      return Shape{{Crack(0, 25)}};
    case Tier::kStrong:
    case Tier::kWeapon:
      return Shape{{Crack(0, 35)}};
    case Tier::kGrab:
      return Shape{{Crack(0, 35),
                    {{25, kLinearDecreasing}, {0, kConstant}, 0.100f}}};
    case Tier::kBigMoment:
      return Shape{{Crack(0, 60),
                    {{50, kCosineFall}, {0, kConstant}, 0.300f}}};
    case Tier::kCritical:
      break;
  }
  return std::nullopt;
}

// A reversal: the wrestler who caught the move and the one whose move it was.
enum class ReversalRole { kReverser, kReversed };

inline std::string_view ToString(ReversalRole role) {
  return role == ReversalRole::kReverser ? "reverser" : "reversed";
}

// The reverser feels a snap, two right-motor taps with a two-frame gap; the
// reversed wrestler a jolt, a left thump that falls away (momentum lost).
inline Shape ReversalShape(ReversalRole role) {
  using namespace detail;
  if (role == ReversalRole::kReverser) {
    return {{Crack(0, 60), {{0, kConstant}, {0, kConstant}, kCrackSeconds}, Crack(0, 60)}};
  }
  return {{Crack(60, 0), {{60, kCosineFall}, {0, kConstant}, 0.250f}}};
}

// A finisher or extreme signature winding up, for both wrestlers: the left
// motor rises to half speed over 750 ms, then lets go over 100 ms unless the
// big moment lands on top of it.
inline Shape WindUpShape() {
  return {{{{50, kLinearIncreasing}, {0, kConstant}, 0.750f},
           {{50, kLinearDecreasing}, {0, kConstant}, 0.100f}}};
}

// The end of a match, for the wrestler who lost and the one who won.
enum class MatchEndRole { kWinner, kLoser };

inline std::string_view ToString(MatchEndRole role) {
  return role == MatchEndRole::kWinner ? "winner" : "loser";
}

// The loser feels a big moment; the winner a warm double pulse on the left
// (sine bumps, no crack), deliberately not a "hurt" pattern.
inline Shape MatchEndShape(MatchEndRole role) {
  if (role == MatchEndRole::kLoser) {
    return ShapeFor(Tier::kBigMoment);
  }
  constexpr Sample kPulse{{40, kSineBump}, {0, kConstant}, 0.150f};
  return {{kPulse, {{0, kConstant}, {0, kConstant}, 0.100f}, kPulse}};
}

}  // namespace wwe::vibration
