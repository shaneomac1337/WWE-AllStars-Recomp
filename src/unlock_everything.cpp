// Unlock everything (wwe_unlock_everything, .scratch/unlock-everything/, evidence in
// docs/research/unlock-everything.md §2, §4.1 and §4.3). All of it does nothing while the
// setting is off:
//
// - The unlockables refresh 0x82BE7560 rebuilds each item's unlocked bit from the save,
//   or sets every bit when bit 0x80000000 of [manager+0x34] is set (what the CHEATS code
//   UnlockAll sets). A midasm hook sets that bit before the five list refreshers run.
// - The Fantasy Warfare menu reads its progress through 0x82BDAD28; a midasm hook raises
//   the result to the match count, so every match opens. The saved counter stays as it is.
// - Real unlocks are still earned (ticket 03). The two code paths that fill the pending
//   list by testing item bits, the post-match award pass 0x82BE7A00 and the attire cheat
//   0x82C1AD88, run with the bits rebuilt from the save alone (the earned view), so they
//   find what the player really earned, add it to the pending list, and the game shows
//   it and commits it to the save (0x82BE72E0) as usual. Afterwards every bit is set again.
//
// Nothing here writes the save: only the pending commit 0x82BE72E0 writes unlock bytes,
// and the pending list only ever holds what the game itself awarded.

#include "generated/default/wweallstars_pch.h"

#include <algorithm>
#include <cstdint>

#include <rex/cvar.h>
#include <rex/hook.h>

#include "guest_memory.h"

REXCVAR_DECLARE(bool, wwe_unlock_everything);  // settings_cvars.cpp

DECLARE_REX_FUNC(sub_82BE6328);  // reset: clears every item bit but 6 default moves
DECLARE_REX_FUNC(sub_82BE7560);  // refresh: sets the bits from the save (or all, flag)
DECLARE_REX_FUNC(sub_82BE7A00);  // post-match award pass
DECLARE_REX_FUNC(sub_82C1AD88);  // attire cheat code

namespace {

using wwe::guest::LoadU32;
using wwe::guest::StoreU32;

constexpr uint32_t kManagerPointer = 0x8374C164;  // [ ] = unlockables manager
constexpr uint32_t kFlagsOffset = 0x34;            // manager: bit 31 = everything unlocked
constexpr uint32_t kDataOffset = 0x38;             // manager: unlockables data object
constexpr uint32_t kEverythingUnlocked = 0x80000000;
// The reset 0x82BE6328 also zeroes two words of the data object (stw r10,52(r11) /
// stw r10,56(r8) at 0x82BE6334/0x82BE633C) that the commit 0x82BE72E0 adds up
// (+0x34 += +0x38, capped at +0x3C). The earned view keeps them.
constexpr uint32_t kDataCounterA = 0x34;
constexpr uint32_t kDataCounterB = 0x38;

// Set while the earned view is rebuilt, so the refresh hook leaves the flag clear.
thread_local bool g_earned_view = false;

// Runs `original` with the item bits rebuilt from the save alone, then sets every bit
// again. The guest argument registers are kept for the original call and its result
// register r3 for the caller.
template <typename Original>
void WithEarnedView(PPCContext& ctx, uint8_t* base, Original original) {
  const uint32_t manager = LoadU32(base, kManagerPointer);
  const uint32_t data = manager ? LoadU32(base, manager + kDataOffset) : 0;
  if (!REXCVAR_GET(wwe_unlock_everything) || data == 0) {
    original(ctx, base);
    return;
  }

  const PPCRegister args[] = {ctx.r3, ctx.r4, ctx.r5, ctx.r6, ctx.r7, ctx.r8, ctx.r9, ctx.r10};
  const uint32_t counter_a = LoadU32(base, data + kDataCounterA);
  const uint32_t counter_b = LoadU32(base, data + kDataCounterB);

  StoreU32(base, manager + kFlagsOffset,
        LoadU32(base, manager + kFlagsOffset) & ~kEverythingUnlocked);
  g_earned_view = true;
  ctx.r3.u64 = manager;
  sub_82BE6328(ctx, base);
  ctx.r3.u64 = manager;
  sub_82BE7560(ctx, base);
  g_earned_view = false;
  StoreU32(base, data + kDataCounterA, counter_a);
  StoreU32(base, data + kDataCounterB, counter_b);

  ctx.r3 = args[0];
  ctx.r4 = args[1];
  ctx.r5 = args[2];
  ctx.r6 = args[3];
  ctx.r7 = args[4];
  ctx.r8 = args[5];
  ctx.r9 = args[6];
  ctx.r10 = args[7];
  original(ctx, base);
  const PPCRegister result = ctx.r3;

  // The refresh only sets bits and never touches the pending list (data +0x40..+0x48),
  // so what the original added stays for the popup and the commit.
  ctx.r3.u64 = manager;
  sub_82BE7560(ctx, base);  // the refresh hook sets the flag again
  ctx.r3 = result;
}

}  // namespace

void WweUnlockEverything(PPCRegister& r3) {
  if (!REXCVAR_GET(wwe_unlock_everything) || g_earned_view) {
    return;
  }
  uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  const uint32_t address = r3.u32 + kFlagsOffset;
  StoreU32(base, address, LoadU32(base, address) | kEverythingUnlocked);
}

void WweUnlockFantasyWarfare(PPCRegister& r3, PPCRegister& r23) {
  if (!REXCVAR_GET(wwe_unlock_everything)) {
    return;
  }
  r3.u64 = std::max(r3.s32, r23.s32);
}

// Post-match award pass, called once from the results screen 0x82AFD6B0 (bl at
// 0x82AFDC38, r3 = manager from 0x82BE6288 at 0x82AFDC30). It tests each item's
// conditions only while its bit is clear (lwz +0x18 / rlwinm bit 31 per list), adds the
// met ones to the pending list (0x82AFC278) and returns pending count != 0. The caller
// then copies the pending list for the popup (0x82AFD5C0), commits it (0x82BE72E0 at
// 0x82AFDD60) and clears it (0x82BE78E8); none of these read the item bits.
REX_HOOK_RAW(sub_82BE7A00) {
  WithEarnedView(ctx, base, __imp__sub_82BE7A00);
}

// Attire cheat code (0x82C1AFB0 calls it for each of the three attire codes). It acts
// only if one of the two attires is still locked (0x82BE6798, the item bit), then adds
// them to the pending list, shows the popup 0x82C1A9E0 and commits (0x82BE72E0).
REX_HOOK_RAW(sub_82C1AD88) {
  WithEarnedView(ctx, base, __imp__sub_82C1AD88);
}
