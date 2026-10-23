// The online stats line (.scratch/online-latency/ ticket 01; evidence in
// docs/research/online-latency.md §2.2 and the manifest entry WweSyncSimStats).
// A hook after SyncSim's update call (TU2 0x82A621B4) feeds each return
// code and the host time into the accumulator in syncsim_stats.h, and once a
// second logs steps, time held for the step deadline, time stalled for remote
// input, the longest stall, the input lead and the latency. It reads guest
// memory only. The stats are off unless --wwe_net_trace.

#include "generated/default/wweallstars_pch.h"

#include <chrono>
#include <cstdint>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/runtime.h>

#include "guest_memory.h"
#include "online/syncsim_stats.h"

namespace {

using wwe::guest::LoadU32;

// SyncSim fields the lead calculation 0x830D7880 reads.
constexpr uint32_t kSimStepFrequency = 0x98;
constexpr uint32_t kSimLatencyMs = 0xB0;

bool TraceOn() {
  static const bool on = rex::cvar::Query<bool>("wwe_net_trace");
  return on;
}

uint64_t NowUs() {
  using namespace std::chrono;
  return uint64_t(duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count());
}

// Only the game thread runs SyncSim's step.
wwe::syncsim_stats::Accumulator g_stats;

}  // namespace

void WweSyncSimStats(PPCRegister& r3, PPCRegister& r31) {
  if (!TraceOn()) {
    return;
  }
  const auto second = g_stats.Add(r3.u32, NowUs());
  if (!second) {
    return;
  }
  const uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  const uint32_t frequency = LoadU32(base, r31.u32 + kSimStepFrequency);
  const uint32_t latency_ms = LoadU32(base, r31.u32 + kSimLatencyMs);
  REXLOG_INFO(
      "[net] syncsim 1s: {} steps, held {:.1f} ms, stalled {:.1f} ms, longest stall {:.1f} ms, "
      "lead {}, latency {} ms, rate {}",
      second->steps, second->held_us / 1000.0, second->stalled_us / 1000.0,
      second->longest_stall_us / 1000.0, wwe::syncsim_stats::InputLead(latency_ms, frequency),
      latency_ms, frequency);
}
