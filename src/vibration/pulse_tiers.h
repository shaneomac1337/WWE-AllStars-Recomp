// Pulse tiers (CONTEXT.md): the tier of hit pulse each combat type gives in
// Enhanced. Each tier's waveform is its
// hit shape (shapes.h).

#pragma once

#include <cstdint>
#include <optional>
#include <string_view>

namespace wwe::vibration {

// kCritical is the critical-health heartbeat, not a combat type.
enum class Tier { kLight, kStrong, kGrab, kWeapon, kBigMoment, kCritical };

inline std::string_view ToString(Tier tier) {
  constexpr std::string_view kNames[] = {"light",  "strong",     "grab",
                                         "weapon", "big_moment", "critical"};
  return kNames[static_cast<int>(tier)];
}

// SD_AnimState.ECombatType (values in docs/research/controller-vibration.md).
// 0 (NONE: damage over time, timed damage) and unknown values give no pulse.
inline std::optional<Tier> TierForCombatType(uint8_t combat_type) {
  switch (combat_type) {
    case 1:  // STRIKE
    case 7:  // RUNNING_STRIKE
      return Tier::kLight;
    case 2:   // STRONG_STRIKE
    case 4:   // AERIAL
    case 9:   // RUNNING_STRONG_STRIKE
    case 16:  // CHARGED_STRIKE
      return Tier::kStrong;
    case 5:   // FINISHER
    case 15:  // EXTREME_SIGNATURE
      return Tier::kBigMoment;
    case 3:   // GRAB
    case 6:   // STRONG_GRAB
    case 8:   // RUNNING_GRAB
    case 10:  // RUNNING_STRONG_GRAB
    case 17:  // CHARGED_GRAB
      return Tier::kGrab;
    case 11:  // WEAPON_STRONG
    case 12:  // WEAPON_WIDE
    case 13:  // WEAPON_WIDE_STRONG
    case 14:  // WEAPON
    case 18:  // WEAPON_BURST
      return Tier::kWeapon;
    default:
      return std::nullopt;
  }
}

}  // namespace wwe::vibration
