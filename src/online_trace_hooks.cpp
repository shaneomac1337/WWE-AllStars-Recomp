// Online trace hooks (from the online spike). Each
// hook sits just after the `mflr r12` that opens a Quazal login, Net-Z or XSession
// function, logs the first argument registers and r12 (the caller's return
// address), and changes nothing. They log only with --wwe_net_trace (the SDK cvar
// from patches/rexglue-online.patch); off, they return at once.
// Addresses and what they are: docs/research/online-mode-feasibility.md §1.4,
// §1.5 and §5 step 2.

#include "generated/default/wweallstars_pch.h"

#include <cstdint>

#include <rex/cvar.h>
#include <rex/logging.h>

namespace {

bool TraceOn() {
  static const bool on = rex::cvar::Query<bool>("wwe_net_trace");
  return on;
}

void Trace(const char* what, uint32_t address, const PPCRegister& r3, const PPCRegister& r4,
           const PPCRegister& r5, const PPCRegister& r6, const PPCRegister& r12) {
  if (!TraceOn()) {
    return;
  }
  REXLOG_INFO("[net] guest {:08X} {}(r3={:08X} r4={:08X} r5={:08X} r6={:08X}) from {:08X}",
              address, what, r3.u32, r4.u32, r5.u32, r6.u32, r12.u32);
}

}  // namespace

#define WWE_ONLINE_TRACE(address, what)                                                     \
  void WweOnlineTrace_##address(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5,         \
                                PPCRegister& r6, PPCRegister& r12) {                       \
    Trace(what, 0x##address, r3, r4, r5, r6, r12);                                         \
  }

// Quazal login (JobLSPLogin and its caller); the steps run in the order listed.
WWE_ONLINE_TRACE(82C27BD0, "QuazalConfigAndLogin")
WWE_ONLINE_TRACE(830EBEB8, "LoginStart")
WWE_ONLINE_TRACE(830EBC88, "LoginJobCreate")
WWE_ONLINE_TRACE(830EC9F8, "JobLSPLoginCtor")
WWE_ONLINE_TRACE(830EC910, "LoginEnumerateLsp")
WWE_ONLINE_TRACE(830EC760, "LoginWaitForLsp")
WWE_ONLINE_TRACE(830EC638, "LoginXNetConnect")
WWE_ONLINE_TRACE(830EC4B8, "LoginConnectStatus")
WWE_ONLINE_TRACE(830EC1E8, "LoginRendezVous")
// Net-Z XNet startup and its release.
WWE_ONLINE_TRACE(83079248, "NetZXNetInit")
WWE_ONLINE_TRACE(830791E0, "NetZXNetRelease")
WWE_ONLINE_TRACE(830C4CB0, "NetZXNetReleaseCaller")
// SyncSim bring-up and the UE3 XSession paths.
WWE_ONLINE_TRACE(82A617D8, "SyncSimBringUp")
WWE_ONLINE_TRACE(829DEC40, "XSessionSearchPath")
WWE_ONLINE_TRACE(829DF460, "XSessionCreatePath")
WWE_ONLINE_TRACE(829E1020, "HostKeyCreate")
