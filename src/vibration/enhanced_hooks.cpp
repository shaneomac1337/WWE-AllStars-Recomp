// Enhanced vibration's guest hooks (evidence in
// docs/research/controller-vibration.md and docs/research/impact-events.md).
// Each adds effects to the rumble mixer (mode.h) for the pad of the wrestler
// it concerns; none writes a guest waveform (docs/adr/0017).
// - 0x82AE63D4, the aerial check in the pawn damage function 0x82AE58C0,
//   which only hits that passed the game's own possession check (vtable+0x2D0)
//   reach: the hit's pulse for the victim. In Enhanced it skips the game's
//   aerial call (jump to 0x82AE6400), so an aerial hit is not doubled.
// - 0x82AE63B8, the same hit just before that possession check, which a hit
//   on the AI's wrestler never passes: the attacker confirm.
// - 0x82AE649C, where the game has found the victim's health crossing below
//   its critical threshold and is about to play SFX_UI_HUD_CRITICAL_01: the
//   critical-health heartbeat.
// - 0x82B36054, the start of PlayGroundEvent 0x82B36048: mat impacts for the
//   wrestler hitting the mat and its partner.
// - 0x8259A9DC, SETIASC's category-6 (reversal) branch: a snap for the
//   reverser and a jolt for the wrestler reversed.
// - 0x824E33E8 in the attack-state enter 0x824E3318, reached only for a
//   finisher or extreme signature: a rising wind-up for both wrestlers.
// - 0x82AEAE28, AttemptMatchEnd's success path: every pad is cleared, then
//   the loser feels a big moment and the winner a warm double pulse.
// - 0x825A5020, SETSCSK's execute: an impact sized by the camera shake for
//   the script's wrestler and an echo for its partner (rope bounces, aerial
//   landings and other impacts with no slam notify).
// In Original and Off the game's code runs as shipped.

#include "generated/default/wweallstars_pch.h"

#include <bit>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>

#include <fmt/format.h>

#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>

#include "vibration/force_feedback.h"
#include "vibration/mat_impact.h"
#include "vibration/mode.h"
#include "vibration/effect_log.h"
#include "vibration/pulse_tiers.h"
#include "vibration/shapes.h"
#include "vibration/wind_up.h"

// Cast to the game's pawn class: (object) -> the pawn, or null.
DECLARE_REX_FUNC(sub_82435EC0);

namespace {

constexpr uint32_t kDamageCombatType = 0x18;

std::mutex g_log_mutex;
wwe::vibration::EffectLog g_log;

void Log(std::string line) {
  std::optional<std::string> taken;
  {
    std::lock_guard lock(g_log_mutex);
    taken = g_log.Take(std::move(line));
  }
  if (taken) {
    REXLOG_INFO("{}", *taken);
  }
}

// Calls into the guest on a side context over the hooked function's stack
// (the SDK's rex::CallFrame pattern; a midasm hook gets registers, not ctx).
struct GuestCall {
  PPCRegister& r1;
  PPCRegister& r13;
  PPCFPSCRRegister& fpscr;
  uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();

  // `object` as a game pawn, or 0 when it is none (a weapon, a cinema actor),
  // the way the game's own PFGEFMOD execute 0x82598E44 casts before it.
  uint32_t CastToPawn(uint32_t object) const {
    PPCContext call{};
    call.r1 = r1;
    call.r13 = r13;
    call.fpscr = fpscr;
    call.r3.u64 = object;
    sub_82435EC0(call, base);
    fpscr = call.fpscr;
    return call.r3.u32;
  }
};

// Adds `shape` for the pad of the wrestler `pawn`, if a local player controls
// it (RumbleTarget's AI gate and manager check), and logs it.
void Rumble(const uint8_t* base, uint32_t pawn, std::string_view event, std::string_view role,
            const wwe::vibration::Shape& shape, std::string_view detail = {}) {
  using namespace wwe::vibration;
  const std::optional<uint32_t> pad = RumblePad(base, pawn);
  if (!pad) {
    return;
  }
  AddEffect(*pad, shape);
  Log(EffectLine(event, role, *pad, shape, detail));
}

std::string HitDetail(wwe::vibration::Tier tier, uint8_t combat_type) {
  return fmt::format("tier={} combat_type={}", ToString(tier), combat_type);
}

}  // namespace

// r28 is the victim pawn, r29 the damage record. True skips the game's aerial
// call.
bool WweHitPulse(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r28,
                 PPCRegister& r29) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return false;
  }
  GuestCall guest{r1, r13, fpscr};
  const uint8_t combat_type = guest.base[r29.u32 + kDamageCombatType];
  const std::optional<Tier> tier = TierForCombatType(combat_type);
  if (!tier) {
    return true;
  }
  Rumble(guest.base, r28.u32, "hit", "victim", ShapeFor(*tier), HitDetail(*tier, combat_type));
  return true;
}

// The same hit, before the game's possession gate on the victim: r28 is the
// victim pawn, r29 the damage record. The hit's attacker feels it land,
// lighter than the victim does. Void: the gate runs as shipped.
void WweAttackerConfirm(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr,
                        PPCRegister& r28, PPCRegister& r29) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  const uint8_t combat_type = guest.base[r29.u32 + kDamageCombatType];
  const std::optional<Tier> tier = TierForCombatType(combat_type);
  if (!tier) {
    return;
  }
  const std::optional<Shape> confirm = AttackerShapeFor(*tier);
  const std::optional<uint32_t> attacker =
      HitAttacker(guest.base, r28.u32, [&](uint32_t object) { return guest.CastToPawn(object); });
  if (confirm && attacker) {
    Rumble(guest.base, *attacker, "hit", "attacker", *confirm, HitDetail(*tier, combat_type));
  }
}

// r28 is the victim pawn, whose health just dropped below the critical
// threshold. The game's crossing check fires once per crossing.
void WweCriticalPulse(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr,
                      PPCRegister& r28) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  // Not behind the game's possession gate here, so RumbleTarget's own check
  // keeps an AI wrestler's controller out.
  Rumble(guest.base, r28.u32, "critical", "victim", ShapeFor(Tier::kCritical));
}

namespace {

// An impact for `pawn` (0 when the cast found none) and a lighter echo for
// its partner in the exchange.
void ImpactAndEcho(GuestCall& guest, uint32_t pawn, wwe::vibration::ImpactSize size,
                   std::string_view event, const std::string& detail) {
  using namespace wwe::vibration;
  if (!pawn) {
    return;
  }
  Rumble(guest.base, pawn, event, ToString(ImpactRole::kImpact),
         ImpactShape(size, ImpactRole::kImpact), detail);
  if (const std::optional<uint32_t> partner = Partner(guest.base, pawn)) {
    const uint32_t other = guest.CastToPawn(*partner);
    if (other && other != pawn) {
      Rumble(guest.base, other, event, ToString(ImpactRole::kEcho),
             ImpactShape(size, ImpactRole::kEcho), detail);
    }
  }
}

}  // namespace

// r3 is the actor whose animation or script fired the ground event (any
// skeletal mesh owner on the notify path), r4 the event type. Void: the
// ground sound still plays.
void WweMatImpact(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r3,
                  PPCRegister& r4) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  // Footsteps fire several times a second: filter before any guest call.
  const uint8_t type = uint8_t(r4.u32);
  const std::optional<ImpactSize> size = ImpactSizeForGroundEvent(type);
  if (!size) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  ImpactAndEcho(guest, guest.CastToPawn(r3.u32), *size, "impact", fmt::format("type={}", type));
}

// r3 is the pawn that just landed a reversal (SETIASC category 6, already
// cast). Void: the reversal counter still counts.
void WweReversal(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r3) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  const uint32_t reverser = r3.u32;
  Rumble(guest.base, reverser, "reversal", ToString(ReversalRole::kReverser),
         ReversalShape(ReversalRole::kReverser));
  const std::optional<uint32_t> reversed =
      Reversed(guest.base, reverser, [&](uint32_t object) { return guest.CastToPawn(object); });
  if (reversed) {
    Rumble(guest.base, *reversed, "reversal", ToString(ReversalRole::kReversed),
           ReversalShape(ReversalRole::kReversed));
  }
}

namespace {

constexpr uint32_t kStateCombatType = 0x43;

std::mutex g_wind_up_mutex;
wwe::vibration::WindUpDebounce g_wind_up;

// One wind-up per wrestler per second (a finisher may enter several states).
void WindUp(const uint8_t* base, uint32_t pawn, std::string_view role, std::string_view detail) {
  using namespace wwe::vibration;
  {
    std::lock_guard lock(g_wind_up_mutex);
    if (!g_wind_up.Take(pawn, WindUpDebounce::Clock::now())) {
      return;
    }
  }
  Rumble(base, pawn, "wind_up", role, WindUpShape(), detail);
}

}  // namespace

// r23 is the attack state being entered, r29 the attacker (already cast).
// Only combat types 5 (finisher) and 15 (extreme signature) reach here. Void.
void WweWindUp(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r23,
               PPCRegister& r29) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  const uint8_t combat_type = guest.base[r23.u32 + kStateCombatType];
  if (TierForCombatType(combat_type) != Tier::kBigMoment) {
    return;
  }
  const std::string detail = fmt::format("combat_type={}", combat_type);
  const uint32_t attacker = r29.u32;
  WindUp(guest.base, attacker, "attacker", detail);
  if (const uint32_t target = guest::Load32(guest.base, attacker + guest::kPawnCurrentTarget)) {
    const uint32_t victim = guest.CastToPawn(target);
    if (victim && victim != attacker) {
      WindUp(guest.base, victim, "victim", detail);
    }
  }
}

// r23 is the match's winner, r28 its loser (AttemptMatchEnd succeeded). Void.
void WweMatchEnd(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r23,
                 PPCRegister& r28) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  // Nothing from the match plays on into its end: only the end effects do,
  // and once they finish the pads are quiet.
  ClearEffects();
  Log("vibration effects cleared: reason=match_end");
  const uint32_t loser = r28.u32 ? guest.CastToPawn(r28.u32) : 0;
  const uint32_t winner = r23.u32 ? guest.CastToPawn(r23.u32) : 0;
  if (loser) {
    Rumble(guest.base, loser, "match_end", ToString(MatchEndRole::kLoser),
           MatchEndShape(MatchEndRole::kLoser));
  }
  if (winner && winner != loser) {
    Rumble(guest.base, winner, "match_end", ToString(MatchEndRole::kWinner),
           MatchEndShape(MatchEndRole::kWinner));
  }
}

namespace {

constexpr uint32_t kShakeSize = 0x10;   // SETSCSK size, a float
constexpr uint32_t kNodeScript = 0x34;  // the script node's owner, then +0x44 its actor
constexpr uint32_t kScriptActor = 0x44;

}  // namespace

// r3 is the SETSCSK event (+0x10 size), r4 the script node whose actor runs
// it. Void: the camera still shakes.
void WweCameraShake(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r3,
                    PPCRegister& r4) {
  using namespace wwe::vibration;
  if (CurrentMode() != Mode::kEnhanced) {
    return;
  }
  if (!r4.u32) {
    return;
  }
  GuestCall guest{r1, r13, fpscr};
  const float shake = std::bit_cast<float>(guest::Load32(guest.base, r3.u32 + kShakeSize));
  const std::optional<ImpactSize> size = ImpactSizeForShake(shake);
  const uint32_t script = guest::Load32(guest.base, r4.u32 + kNodeScript);
  if (!size || !script) {
    return;
  }
  const uint32_t actor = guest::Load32(guest.base, script + kScriptActor);
  if (!actor) {
    return;
  }
  ImpactAndEcho(guest, guest.CastToPawn(actor), *size, "shake", fmt::format("size={:.2f}", shake));
}
