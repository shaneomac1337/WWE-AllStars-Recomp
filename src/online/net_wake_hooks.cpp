// Wakes Net-Z's Transport Thread when SyncSim queues input for its peers
// (.scratch/online-latency/ ticket 04; docs/research/online-latency.md §2.2).
//
// The Transport Thread sends what the game queued once per pass, and a pass starts
// when a receive event fires or its 10 ms WSAWaitForMultipleEvents timeout runs out.
// SyncSim's input check 0x830DC390 (called from the update's 0x830D9E30) queues this
// station's newest input with 0x830DBEC0 and re-sends unacked inputs to a waiting peer
// with 0x830DC0E8. Both run on the game thread, about 0.5 ms into the tick after the
// step, not at the step itself (sync test nw04d: input queued, then sent 0.1 ms later
// by the spinning thread of the shipped SDK). Without a wake the input waited up to
// 10 ms for the next pass, and whenever the two copies' step phases left less slack
// than that, the host stalled for it (ticket 04 comments). Waking the thread right
// after the queue sends it at once, as the spinning thread did.
// Always on: it only runs while SyncSim runs.

#include "generated/default/wweallstars_pch.h"

#include <rex/kernel/xam/net_wake.h>

// Sends this station's newest input to its peers.
DECLARE_REX_FUNC(sub_830DBEC0);
REX_HOOK_RAW(sub_830DBEC0) {
  __imp__sub_830DBEC0(ctx, base);
  rex::kernel::xam::EndBlockingEventWait();
}

// Re-sends the inputs a waiting peer has not acknowledged.
DECLARE_REX_FUNC(sub_830DC0E8);
REX_HOOK_RAW(sub_830DC0E8) {
  __imp__sub_830DC0E8(ctx, base);
  rex::kernel::xam::EndBlockingEventWait();
}
