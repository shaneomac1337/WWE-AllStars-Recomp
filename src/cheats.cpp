// Cheats (GitHub #15; evidence in docs/research/cheats.md). Each hook reads its row on every call,
// so a row applies at once, also during a match. Every hook does nothing in an online session:
// SyncSim checksums wrestler state, so a cheat on one side would desync the match.

#include "generated/default/wweallstars_pch.h"

#include <bit>
#include <cstdint>

#include <rex/cvar.h>
#include <rex/hook.h>
#include <rex/system/kernel_state.h>

#include "guest_memory.h"

REXCVAR_DECLARE(bool, wwe_cheat_infinite_finishers);
REXCVAR_DECLARE(bool, wwe_cheat_infinite_signatures);
REXCVAR_DECLARE(bool, wwe_cheat_max_health);
REXCVAR_DECLARE(bool, wwe_cheat_weak_enemy);
REXCVAR_DECLARE(bool, wwe_cheat_no_reversal);

DECLARE_REX_FUNC(sub_82B0C070);  // add to the finisher meter, raises the HUD's "ready" state

namespace {

using wwe::guest::LoadU32;
using wwe::guest::StoreU32;

constexpr uint32_t kNetZSessionActive = 0x83740590;
// The game's pawn table: count at +4, pawn i at +0x0C + 4i, at most 4 (0x82B02C78).
constexpr uint32_t kPawnTable = 0x837450D0;
constexpr uint32_t kPawnTableCount = 0x04;
constexpr uint32_t kPawnTableFirst = 0x0C;
constexpr int32_t kPawnTableMax = 4;
constexpr uint32_t kPawnTeam = 0x5A4;
constexpr uint32_t kPawnFlags = 0x6FC;
constexpr uint32_t kPawnSpawnedAsCpu = 0x80;
constexpr uint32_t kPawnStats = 0x19B0;
constexpr uint32_t kStatsEnergy = 0x130;
constexpr uint32_t kStatsEnergyMax = 0x138;
constexpr uint32_t kStatsFinisher = 0x13C;
constexpr uint32_t kStatsFinisherMax = 0x144;
constexpr int64_t kWeakEnemyDamage = 3;

uint8_t* Base() { return REX_KERNEL_MEMORY()->virtual_membase(); }

bool Online(const uint8_t* base) { return LoadU32(base, kNetZSessionActive) != 0; }

float LoadF32(const uint8_t* base, uint32_t address) {
  return std::bit_cast<float>(LoadU32(base, address));
}

// A team is a player team if any of its pawns did not spawn as a CPU wrestler. The pawn's own
// flag is not enough: a tag swap can leave a human driving a pawn that spawned as a CPU.
bool OnPlayerSide(const uint8_t* base, uint32_t pawn) {
  const uint32_t team = LoadU32(base, pawn + kPawnTeam);
  const int32_t count = int32_t(LoadU32(base, kPawnTable + kPawnTableCount));
  for (int32_t i = 0; i < count && i < kPawnTableMax; ++i) {
    const uint32_t other = LoadU32(base, kPawnTable + kPawnTableFirst + uint32_t(i) * 4);
    if (other && LoadU32(base, other + kPawnTeam) == team &&
        !(LoadU32(base, other + kPawnFlags) & kPawnSpawnedAsCpu)) {
      return true;
    }
  }
  return false;
}

}  // namespace

// The pawn tick, r31 the pawn. Runs just before a bl that takes r3, which this leaves alone.
void WweCheatTick(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r31) {
  const bool finishers = REXCVAR_GET(wwe_cheat_infinite_finishers);
  const bool signatures = REXCVAR_GET(wwe_cheat_infinite_signatures);
  if (!finishers && !signatures) {
    return;
  }
  uint8_t* base = Base();
  const uint32_t stats = LoadU32(base, r31.u32 + kPawnStats);
  if (!stats || Online(base) || !OnPlayerSide(base, r31.u32)) {
    return;
  }
  if (signatures) {
    StoreU32(base, stats + kStatsEnergy, LoadU32(base, stats + kStatsEnergyMax));
  }
  if (finishers) {
    const float missing =
        LoadF32(base, stats + kStatsFinisherMax) - LoadF32(base, stats + kStatsFinisher);
    if (missing > 0.0f) {
      PPCContext call{};
      call.r1 = r1;
      call.r13 = r13;
      call.fpscr = fpscr;
      call.r3.u64 = stats;
      call.f1.f64 = missing;
      sub_82B0C070(call, base);
      fpscr = call.fpscr;
    }
  }
}

// All health damage: r3 the pawn, r5 the amount.
void WweCheatDamage(PPCRegister& r3, PPCRegister& r5) {
  const bool max_health = REXCVAR_GET(wwe_cheat_max_health);
  const bool weak_enemy = REXCVAR_GET(wwe_cheat_weak_enemy);
  if ((!max_health && !weak_enemy) || r5.s64 <= 0) {
    return;
  }
  const uint8_t* base = Base();
  if (Online(base)) {
    return;
  }
  if (OnPlayerSide(base, r3.u32)) {
    if (max_health) {
      r5.u64 = 0;
    }
  } else if (weak_enemy) {
    r5.s64 *= kWeakEnemyDamage;
  }
}

// The reversal trigger's evaluate, r30 the pawn trying to reverse. True takes the game's own
// "no" exit for a press that is locked out.
bool WweCheatNoReversal(PPCRegister& r30) {
  if (!REXCVAR_GET(wwe_cheat_no_reversal)) {
    return false;
  }
  const uint8_t* base = Base();
  return !Online(base) && !OnPlayerSide(base, r30.u32);
}
