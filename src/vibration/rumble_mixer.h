// The rumble mixer (CONTEXT.md): Enhanced's effects per pad, layered by
// per-motor maximum and played on real time
//.

#pragma once

#include <array>
#include <chrono>
#include <cstdint>
#include <optional>
#include <random>
#include <vector>

#include "vibration/shapes.h"

namespace wwe::vibration {

// Motor speeds as a pad request carries them (0-65535).
struct Speeds {
  uint16_t left;
  uint16_t right;
};

class RumbleMixer {
 public:
  using Clock = std::chrono::steady_clock;
  static constexpr uint32_t kPads = 4;
  // The most one request advances the effects, so a hitch or a stall between
  // requests stretches them rather than jumping ahead.
  static constexpr Clock::duration kMaxStep = std::chrono::milliseconds(50);

  // `pad` outside 0-3 is ignored. The effect starts at the pad's next Sample.
  void Add(uint32_t pad, const Shape& shape);

  // The pad's request at `now`: its effects advance by the real time since its
  // last request (at most kMaxStep) and are layered by per-motor maximum.
  Speeds Sample(uint32_t pad, Clock::time_point now);

  void Clear();

 private:
  struct Effect {
    Shape shape;
    bool started = false;
    size_t sample = 0;
    double elapsed = 0;     // seconds into the current sample
    uint32_t requests = 0;  // requests the current sample was output on
  };

  struct Pad {
    std::vector<Effect> effects;
    std::optional<Clock::time_point> last_request;
  };

  // Moves `effect` on by `seconds`; false once it has ended.
  static bool Advance(Effect& effect, double seconds);
  uint16_t Speed(const Motor& motor, double t);

  std::array<Pad, kPads> pads_;
  std::minstd_rand noise_;
};

}  // namespace wwe::vibration
