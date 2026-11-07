// Mat impacts (CONTEXT.md): a rumble when a wrestler's body hits the mat,
// sized by the game's own ground-event type, for that wrestler and a lighter
// echo for the other wrestler in the exchange (evidence
// in docs/research/impact-events.md, section A).

#pragma once

#include <cmath>
#include <cstdint>
#include <optional>
#include <string_view>

#include "vibration/force_feedback.h"
#include "vibration/shapes.h"

namespace wwe::vibration {

enum class ImpactSize { kStrong, kMedium, kLight };

// The wrestler hitting the mat gets the impact; its partner the echo.
enum class ImpactRole { kImpact, kEcho };

inline std::string_view ToString(ImpactRole role) {
  return role == ImpactRole::kImpact ? "impact" : "echo";
}

// AnimNotify_FmodGroundEvent.EAnimNotifyEventType, the r4 of PlayGroundEvent
// 0x82B36048. Walking and footsteps fire several times a second, so anything
// but a slam or heavy stomp gives nothing.
inline std::optional<ImpactSize> ImpactSizeForGroundEvent(uint8_t type) {
  switch (type) {
    case 9:   // Slam_Heavy
    case 13:  // HalfSlam_Mat
      return ImpactSize::kStrong;
    case 8:  // Slam_Med
      return ImpactSize::kMedium;
    case 6:   // Stomp_Heavy
    case 7:   // Slam_Reg
    case 12:  // Barrier_Impact
      return ImpactSize::kLight;
    default:  // 0-5 walk to Stomp_Med, 10 Rattle, 11 Barrier
      return std::nullopt;
  }
}

// A camera shake's size (SETSCSK +0x10, docs/research/impact-events.md
// section B): up to 1 and below 2 light, 2 to 2.5 and below 3 medium, 3 or
// more strong. No size, no rumble.
inline std::optional<ImpactSize> ImpactSizeForShake(float size) {
  if (!(size > 0)) {
    return std::nullopt;
  }
  if (size < 2) {
    return ImpactSize::kLight;
  }
  return size < 3 ? ImpactSize::kMedium : ImpactSize::kStrong;
}

namespace detail {

inline Shape Scaled(Shape shape, double factor) {
  for (Sample& s : shape.samples) {
    s.left.amplitude = uint8_t(std::lround(s.left.amplitude * factor));
    s.right.amplitude = uint8_t(std::lround(s.right.amplitude * factor));
  }
  return shape;
}

}  // namespace detail

// A strong impact is the grab shape (the weight of hitting the mat); medium
// and light are scaled down from it, and the echo is half the impact.
inline Shape ImpactShape(ImpactSize size, ImpactRole role) {
  constexpr double kSize[] = {1.0, 0.75, 0.5};
  const double echo = role == ImpactRole::kEcho ? 0.5 : 1.0;
  return detail::Scaled(ShapeFor(Tier::kGrab), kSize[static_cast<int>(size)] * echo);
}

namespace guest {

// GetTargetPawn 0x82AE0600's kinds (SD_AnimStateConcretePuppetMaster.ETargetTypes).
constexpr uint32_t kPawnCurrentTarget = 0x81C;  // kind 1
constexpr uint32_t kPawnLastHitVictim = 0x938;  // kind 0
constexpr uint32_t kPawnLastAttacker = 0x95C;   // kind 5

}  // namespace guest

// The other wrestler in `pawn`'s exchange: its current target, else the last
// wrestler it hit, else the last one who hit it.
inline std::optional<uint32_t> Partner(const uint8_t* base, uint32_t pawn) {
  using namespace guest;
  for (uint32_t field : {kPawnCurrentTarget, kPawnLastHitVictim, kPawnLastAttacker}) {
    if (const uint32_t other = Load32(base, pawn + field)) {
      return other;
    }
  }
  return std::nullopt;
}

// The attacker of the hit on `victim` at the hit hook 0x82AE63D4: its last
// attacker (TakeHit 0x82AE4DD0 writes it at 0x82AE5200, before the damage
// call), through the game's pawn cast `cast` before any read of it. Nothing
// when it is no pawn or the victim itself.
template <typename Cast>
std::optional<uint32_t> HitAttacker(const uint8_t* base, uint32_t victim, Cast cast) {
  const uint32_t last = guest::Load32(base, victim + guest::kPawnLastAttacker);
  const uint32_t attacker = last ? cast(last) : 0;
  if (!attacker || attacker == victim) {
    return std::nullopt;
  }
  return attacker;
}

// The wrestler whose move `reverser` reversed at SETIASC category 6, which
// records no partner (docs/research/controller-vibration.md, ticket 01
// section 4, inferred): its current target, else its last attacker, else its
// last hit victim: the first that the game's pawn cast `cast` (before any
// read of it) finds to be another pawn.
template <typename Cast>
std::optional<uint32_t> Reversed(const uint8_t* base, uint32_t reverser, Cast cast) {
  using namespace guest;
  for (uint32_t field : {kPawnCurrentTarget, kPawnLastAttacker, kPawnLastHitVictim}) {
    if (const uint32_t other = Load32(base, reverser + field)) {
      if (const uint32_t pawn = cast(other); pawn && pawn != reverser) {
        return pawn;
      }
    }
  }
  return std::nullopt;
}

}  // namespace wwe::vibration
