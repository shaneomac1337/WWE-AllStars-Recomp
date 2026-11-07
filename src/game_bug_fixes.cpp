// Bugs in the original game's code. Evidence for each one is in wweallstars_manifest.toml.

#include "generated/default/wweallstars_pch.h"

#include <cstdint>
#include <string_view>

#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>

#include "guest_memory.h"

DECLARE_REX_FUNC(sub_82740618);  // flush async loading (inferred from its callers)
DECLARE_REX_FUNC(sub_82628880);  // load a package
DECLARE_REX_FUNC(sub_825C5260);  // open a movie

namespace {

constexpr std::u16string_view kCapSaveName = u"%s%.8X.SAV";

uint32_t AllocateCapSaveName() {
  const uint32_t address =
      REX_KERNEL_MEMORY()->SystemHeapAlloc(uint32_t(kCapSaveName.size() + 1) * 2);
  if (!address) {
    REXLOG_WARN("Create A Superstar: no guest memory for the save name format");
    return 0;
  }
  wwe::guest::StoreU16String(REX_KERNEL_MEMORY()->virtual_membase(), address, kCapSaveName);
  return address;
}

}  // namespace

void WweCapSaveMountName(PPCRegister& r4) {
  static const uint32_t format = AllocateCapSaveName();
  if (format) {
    r4.u64 = format;
  }
}

// GitHub #16: see wweallstars_manifest.toml. r3 is the movie the open returned.
void WweLoaderMovieRetry(PPCRegister& r1, PPCRegister& r13, PPCFPSCRRegister& fpscr, PPCRegister& r3,
                         PPCRegister& r24, PPCRegister& r30, PPCRegister& r31) {
  if (r3.u32) {
    return;
  }
  uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  const auto name = [&](uint32_t offset) {
    const uint32_t chars = wwe::guest::LoadU32(base, r31.u32 + offset);
    return wwe::guest::LoadU32(base, r31.u32 + offset + 4) ? chars : r24.u32;
  };
  PPCContext call{};
  call.r1 = r1;
  call.r13 = r13;
  call.fpscr = fpscr;
  sub_82740618(call, base);
  call.r3.u64 = 0;
  call.r4.u64 = name(0x58);
  call.r5.u64 = 0;
  sub_82628880(call, base);
  call.r3.u64 = r30.u32;
  call.r4.u64 = name(0x78);
  call.r5.u64 = 1;
  call.r6.u64 = 1;
  sub_825C5260(call, base);
  fpscr = call.fpscr;
  r3.u64 = call.r3.u32;
  REXLOG_WARN("Create A Superstar: the loader movie did not open; after a flush and a reload it {}",
              r3.u32 ? "did" : "still did not");
}
