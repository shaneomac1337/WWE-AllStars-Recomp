// Finisher wind-up: a finisher
// or extreme signature may enter several attack states, so each wrestler gets
// at most one wind-up per second.

#pragma once

#include <chrono>
#include <cstdint>
#include <unordered_map>

namespace wwe::vibration {

class WindUpDebounce {
 public:
  using Clock = std::chrono::steady_clock;
  static constexpr Clock::duration kPeriod = std::chrono::seconds(1);

  // True when `pawn` gets a wind-up at `now`, which then starts its second.
  bool Take(uint32_t pawn, Clock::time_point now) {
    // Pawns of past matches are forgotten once their second is over.
    std::erase_if(last_, [&](const auto& entry) { return now - entry.second >= kPeriod; });
    const auto [it, first] = last_.try_emplace(pawn, now);
    if (!first) {
      if (now - it->second < kPeriod) {
        return false;
      }
      it->second = now;
    }
    return true;
  }

 private:
  std::unordered_map<uint32_t, Clock::time_point> last_;
};

}  // namespace wwe::vibration
