// The online step rate (.scratch/online-latency/ ticket 03; evidence in
// docs/research/online-latency.md §2.3 and the manifest entries WweStepRateSample
// and WweStepRatePublish). Two thin hooks inside SyncSim's adaptive-rate
// calculation (TU2 0x830D8FA0) replace its truncating sample and publish with the
// rules in step_rate.h, so two 60 Hz ticks per step publish 30, not 29. SyncSim
// runs only while a Net-Z session is active, so offline play never reaches them.
// Always on: both peers must run the same rule, and the server only matches
// identical builds.

#include "generated/default/wweallstars_pch.h"

#include <bit>
#include <cstdint>

#include <rex/runtime.h>

#include "guest_memory.h"
#include "online/step_rate.h"

namespace {

using wwe::guest::LoadU32;

// SyncSim fields: the step count (1 at init 0x830D81C4, +1 per step at 0x830DA038-44
// in the update 0x830D9F00), and those
// 0x830D8FA0 reads: the cap (lwz 160), the whole ms since the last
// step (lwz 164) and the smoothed mean (stfs 0(r11), r11 = r31 + 188).
constexpr uint32_t kSimStep = 0x78;
constexpr uint32_t kSimRateCap = 0xA0;
constexpr uint32_t kSimElapsedMs = 0xA4;
constexpr uint32_t kSimRateMean = 0xBC;

}  // namespace

// Before 0x830D9020: r10 holds the game's sample min(cap, 1000 / elapsed). The
// first kStartSteps sample the cap (ticket 06).
void WweStepRateSample(PPCRegister& r10, PPCRegister& r31) {
  const uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  r10.u64 = wwe::step_rate::SessionSampleRate(LoadU32(base, r31.u32 + kSimStep),
                                              LoadU32(base, r31.u32 + kSimElapsedMs),
                                              LoadU32(base, r31.u32 + kSimRateCap));
}

// Before 0x830D9084: r30 holds the truncated mean the game is about to publish.
void WweStepRatePublish(PPCRegister& r30, PPCRegister& r31) {
  const uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  const float mean = std::bit_cast<float>(LoadU32(base, r31.u32 + kSimRateMean));
  r30.u64 = wwe::step_rate::PublishRate(mean, LoadU32(base, r31.u32 + kSimRateCap));
}
