// Match music after Restart Match (GitHub #9; match_music.h has the rule, the ticket the
// evidence). The music sequence's stop chain ends in SeqAct_FmodEventDynamicLoading_0's Unload
// input, which runs after the restarted match has loaded its new track into the same
// variable; this keeps that track loaded. The chain is AUDIO_Music_*'s own Kismet, so the
// hooks look only at objects from those packages.
//
// SeqAct_FmodEventDynamicLoading::Activated 0x8289F098 (vtable 0x8212DE10 +0x114): input 0
// (Load, impulse at InputLinks +0x90 -> +0x0C) loads the event at +0xEC and returns; input 1
// (Unload, impulse at +0x30) clears bit 31 of +0xF0, calls the event's UnloadSoundData
// (vtable +0xD0) and clears its impulse. Nothing else, no output links.
// UFmodEvent (vtable 0x820DD890): UnloadSoundData 0x8289F6B8 (+0xD0), also called by the
// match teardown. SeqAct_FmodPlayEvent::Activated 0x8289E4E0 (vtable 0x8212E6D0 +0x114):
// input 0 (Play, impulse at +0x0C) plays the event at +0xEC.

#include "generated/default/wweallstars_pch.h"

#include <cstdint>
#include <mutex>

#include <rex/hook.h>
#include <rex/logging.h>

#include "guest_memory.h"
#include "guest_names.h"
#include "match_music.h"

namespace {

using wwe::guest::LoadU32;
using wwe::guest::ObjectName;
using wwe::guest::PackageOf;
using wwe::guest::StoreU32;

constexpr uint32_t kInputLinks = 0x90;
constexpr uint32_t kFirstInputImpulse = 0x0C;
constexpr uint32_t kSecondInputImpulse = 0x30;
constexpr uint32_t kOpEvent = 0xEC;
constexpr uint32_t kDynamicLoadingLoaded = 0xF0;
constexpr uint32_t kImpulse = 0x80000000;

std::mutex g_mutex;
wwe::match_music::TrackLoads g_loads;

bool FromMusicPackage(const uint8_t* base, uint32_t object) {
  return object && PackageOf(base, object).starts_with("AUDIO_Music_");
}

bool Impulse(const uint8_t* base, uint32_t op, uint32_t input) {
  return (LoadU32(base, LoadU32(base, op + kInputLinks) + input) & kImpulse) != 0;
}

void ClearBit31(uint8_t* base, uint32_t address) {
  StoreU32(base, address, LoadU32(base, address) & ~kImpulse);
}

}  // namespace

// UFmodEvent::UnloadSoundData
DECLARE_REX_FUNC(sub_8289F6B8);
REX_HOOK_RAW(sub_8289F6B8) {
  const uint32_t event = ctx.r3.u32;
  __imp__sub_8289F6B8(ctx, base);
  std::lock_guard lock(g_mutex);
  g_loads.Unloaded(event);
}

// SeqAct_FmodPlayEvent::Activated
DECLARE_REX_FUNC(sub_8289E4E0);
REX_HOOK_RAW(sub_8289E4E0) {
  const uint32_t op = ctx.r3.u32;
  const uint32_t event = LoadU32(base, op + kOpEvent);
  if (event && Impulse(base, op, kFirstInputImpulse)) {
    std::lock_guard lock(g_mutex);
    g_loads.Played(event);
  }
  __imp__sub_8289E4E0(ctx, base);
}

// SeqAct_FmodEventDynamicLoading::Activated
DECLARE_REX_FUNC(sub_8289F098);
REX_HOOK_RAW(sub_8289F098) {
  const uint32_t op = ctx.r3.u32;
  const uint32_t event = LoadU32(base, op + kOpEvent);
  if (!event || !FromMusicPackage(base, op)) {
    __imp__sub_8289F098(ctx, base);
    return;
  }
  // A Load in the same activation runs first and returns.
  if (Impulse(base, op, kFirstInputImpulse)) {
    __imp__sub_8289F098(ctx, base);
    std::lock_guard lock(g_mutex);
    g_loads.Loaded(event);
    return;
  }
  if (Impulse(base, op, kSecondInputImpulse)) {
    bool stale;
    {
      std::lock_guard lock(g_mutex);
      stale = g_loads.IsStaleUnload(event);
    }
    if (stale) {
      // The original's Unload without the UnloadSoundData call.
      ClearBit31(base, op + kDynamicLoadingLoaded);
      ClearBit31(base, LoadU32(base, op + kInputLinks) + kSecondInputImpulse);
      REXLOG_INFO("match music: kept {} loaded (Unload from the previous track's stop chain)",
                  ObjectName(base, event));
      return;
    }
  }
  __imp__sub_8289F098(ctx, base);
}
