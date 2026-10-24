// Faster starts (.scratch/faster-starts/spec.md): Shorter loading screens (ticket 03) cuts
// three fixed Kismet waits of the pre-match load to one tick: the 2.0 s output-link delay
// before the intro movie (Streaming_Assets), and the 5.0 s and 3.0 s SeqAct_Delays of
// Spwn_Sys' Game_Settings (loading-times.md §1). Each is matched by the object it belongs to
// (faster_starts.h); every other wait keeps its value. A SeqAct_Delay starts in its Activated
// (0x827A73B0): Duration (+0xF4) is copied to RemainingTime (+0xFC), which UpdateOp counts
// down. A delayed output link is queued by USequence::ExecuteActiveOps (0x827B8AA0) in the
// sequence's DelayedActivatedOps (+0xF8 data, +0xFC count; 0x10 bytes each: activator op,
// target op, input index, RemainingDelay +0xC) and counted down at the start of its next call.
//
// Nothing is cut while a Net-Z session is up (0x83740590, the same test as 120 fps matches):
// peers would leave the waits on different ticks.

#include "generated/default/wweallstars_pch.h"

#include <algorithm>
#include <bit>
#include <cstdint>
#include <string>
#include <vector>

#include <rex/cvar.h>
#include <rex/hook.h>
#include <rex/logging.h>

#include "faster_starts.h"
#include "guest_memory.h"

REXCVAR_DECLARE(bool, wwe_short_loading_screens);  // settings_cvars.cpp
REXCVAR_DEFINE_BOOL(wwe_faster_starts_trace, false, "WWEAllStars",
                    "Developer: log every Kismet delay and delayed output link the game starts, "
                    "with its object path and length (faster-starts ticket 03)");
REXCVAR_DEFINE_BOOL(wwe_faster_starts_assume_online, false, "WWEAllStars",
                    "Developer: Shorter loading screens acts as if a Net-Z session were up, "
                    "so an offline run shows what it does online (nothing)");

namespace {

using wwe::guest::LoadU32;
using wwe::guest::StoreU32;

// GNames (the FName constructor 0x827522A0): a TArray of entry pointers at 0x83734554,
// its count after it; an entry's UTF-16 text starts at +0x10.
constexpr uint32_t kNamesData = 0x83734554;
constexpr uint32_t kNamesCount = 0x83734558;
// UObject: Outer at +0x20, Name (an FName's index, its number after it) at +0x24, Class at
// +0x2C. Read from live Matinees: SeqAct_Cinema_0 < Main_Sequence < PersistentLevel <
// TheWorld < Intro_Standard.
constexpr uint32_t kOuterOffset = 0x20;
constexpr uint32_t kNameOffset = 0x24;
constexpr uint32_t kClassOffset = 0x2C;
// A Net-Z session runs (game_tick_log.cpp, At120Allowed).
constexpr uint32_t kNetZSessionActive = 0x83740590;
// Heap objects live at 0x40000000 and up.
constexpr uint32_t kHeapStart = 0x40000000;

std::string NameText(const uint8_t* base, uint32_t index) {
  if (index >= LoadU32(base, kNamesCount)) return {};
  const uint32_t entry = LoadU32(base, LoadU32(base, kNamesData) + 4 * index);
  if (entry < kHeapStart) return {};
  std::string text;
  for (uint32_t i = 0; i < 64; ++i) {
    const uint32_t at = entry + 0x10 + 2 * i;
    const uint16_t c = uint16_t(base[at] << 8 | base[at + 1]);
    if (c == 0) break;
    text += c < 0x80 ? char(c) : '?';
  }
  return text;
}

// The name of the package `object` was loaded from: its outermost Outer.
std::string PackageOf(const uint8_t* base, uint32_t object) {
  for (int depth = 0; depth < 16; ++depth) {
    const uint32_t outer = LoadU32(base, object + kOuterOffset);
    if (outer < kHeapStart) return NameText(base, LoadU32(base, object + kNameOffset));
    object = outer;
  }
  return {};
}

// A Net-Z session is up, or a developer run says to act as if one were.
bool Online(const uint8_t* base) {
  return LoadU32(base, kNetZSessionActive) != 0 || REXCVAR_GET(wwe_faster_starts_assume_online);
}

// The UObject's name, as UE3 prints it (Name_N for a numbered FName).
std::string ObjectName(const uint8_t* base, uint32_t object) {
  const uint32_t number = LoadU32(base, object + kNameOffset + 4);
  return NameText(base, LoadU32(base, object + kNameOffset)) +
         (number ? "_" + std::to_string(number - 1) : "");
}

std::string ClassName(const uint8_t* base, uint32_t object) {
  const uint32_t cls = LoadU32(base, object + kClassOffset);
  return cls >= kHeapStart ? ObjectName(base, cls) : std::string();
}

// Where a Kismet op sits: its package, the sequence that holds it, its own name and class.
wwe::faster_starts::KismetOp OpOf(const uint8_t* base, uint32_t op) {
  const uint32_t sequence = LoadU32(base, op + kOuterOffset);
  return {PackageOf(base, op), sequence >= kHeapStart ? ObjectName(base, sequence) : std::string(),
          ObjectName(base, op), ClassName(base, op)};
}

std::string Path(const wwe::faster_starts::KismetOp& op) {
  return op.package + ":" + op.sequence + "." + op.name + " [" + op.cls + "]";
}

}  // namespace

// USeqAct_GameMatchStart::Activated (vtable 0x821E2B18 +0x114): the bell, where Game_Settings
// turns the controllers on. Logged as the end mark of the pre-match load
// (scripts/prematch_timer.py).
DECLARE_REX_FUNC(sub_82BBDFF8);
REX_HOOK_RAW(sub_82BBDFF8) {
  REXLOG_INFO("faster starts: match start (GameMatchStart)");
  __imp__sub_82BBDFF8(ctx, base);
}

namespace wwe::faster_starts {

bool IsCutDelay(const KismetOp& op, float duration) {
  // Game_Settings (Spwn_Sys, shared by every arena): SeqAct_Delay_0, 5.0 s before
  // SyncGamePlayAsync (E81), and SeqAct_Delay_8, 3.0 s after ForceGarbageCollection (E85)
  // (loading-times.md §1; names from a wwe_faster_starts_trace run). Its other delays (2.0,
  // 1.0, 0.5, 0.25 s around the taunts and the bell) stay.
  if (op.package != "Spwn_Sys" || op.sequence != "Game_Settings" || op.cls != "SeqAct_Delay") return false;
  return (op.name == "SeqAct_Delay_0" && duration == 5.0f) ||
         (op.name == "SeqAct_Delay_8" && duration == 3.0f);
}

bool IsCutLink(const KismetOp& activator, const KismetOp& target, float delay) {
  // Streaming_Assets' entry (Arena_00N E1362 -> E1245): the sequence's activation event links
  // to the intro movie with ActivateDelay 2.0. All six arenas have it.
  return activator.package.starts_with("Arena_") && activator.sequence == "Streaming_Assets" &&
         activator.cls == "SeqEvent_SequenceActivated" && target.sequence == "Streaming_Assets" &&
         target.cls == "SeqAct_PlayMovie" && delay > 1.9f && delay <= 2.0f;
}

}  // namespace wwe::faster_starts

namespace {

constexpr uint32_t kDelayDuration = 0xF4;       // SeqAct_Delay.Duration
constexpr uint32_t kDelayRemaining = 0xFC;      // SeqAct_Delay.RemainingTime
constexpr uint32_t kDelayedOpsData = 0xF8;      // USequence.DelayedActivatedOps
constexpr uint32_t kDelayedOpsCount = 0xFC;
constexpr uint32_t kDelayedOpSize = 0x10;       // FActivateOp
constexpr uint32_t kDelayedOpRemaining = 0xC;   // FActivateOp.RemainingDelay

float LoadF32(const uint8_t* base, uint32_t address) { return std::bit_cast<float>(LoadU32(base, address)); }

// Shorter loading screens is on and no Net-Z session is up.
bool CutWaits(const uint8_t* base) {
  return REXCVAR_GET(wwe_short_loading_screens) && !Online(base);
}

// The delayed links logged or cut while they are queued, so each queued activation is handled
// once: the entry's activator and target, with the sequence that holds it.
struct LinkKey {
  uint32_t sequence, activator, target;
  bool operator==(const LinkKey&) const = default;
};
std::vector<LinkKey> g_links_seen;

}  // namespace

// SeqAct_Delay::Activated: Duration -> RemainingTime. A cut delay starts with nothing left,
// so its next UpdateOp fires the output: one tick instead of its length.
DECLARE_REX_FUNC(sub_827A73B0);
REX_HOOK_RAW(sub_827A73B0) {
  const uint32_t op = ctx.r3.u32;
  __imp__sub_827A73B0(ctx, base);
  const bool trace = REXCVAR_GET(wwe_faster_starts_trace);
  const bool cut_on = CutWaits(base);
  if (!trace && !cut_on) return;
  const float duration = LoadF32(base, op + kDelayDuration);
  const wwe::faster_starts::KismetOp where = OpOf(base, op);
  const bool cut = cut_on && wwe::faster_starts::IsCutDelay(where, duration);
  if (cut) StoreU32(base, op + kDelayRemaining, 0);
  if (cut || trace) {
    REXLOG_INFO("faster starts: delay {} {:.2f} s{}", Path(where), duration, cut ? ", cut" : "");
  }
}

// USequence::ExecuteActiveOps (r3 = the sequence): a delayed output link it queued this call
// is cut to zero, so the next call fires it.
DECLARE_REX_FUNC(sub_827B8AA0);
REX_HOOK_RAW(sub_827B8AA0) {
  const uint32_t sequence = ctx.r3.u32;
  __imp__sub_827B8AA0(ctx, base);
  const uint32_t count = LoadU32(base, sequence + kDelayedOpsCount);
  const uint32_t data = LoadU32(base, sequence + kDelayedOpsData);
  auto queued = [&](const LinkKey& key) {
    for (uint32_t i = 0; i < count; ++i) {
      const uint32_t entry = data + i * kDelayedOpSize;
      if (LoadU32(base, entry) == key.activator && LoadU32(base, entry + 4) == key.target) return true;
    }
    return false;
  };
  // A link that fired (or was dropped) leaves the list: forget it, so the same link queued
  // again later, in the next match, is handled again.
  std::erase_if(g_links_seen, [&](const LinkKey& key) { return key.sequence == sequence && !queued(key); });
  if (count == 0 || count > 256) return;
  const bool trace = REXCVAR_GET(wwe_faster_starts_trace);
  const bool cut_on = CutWaits(base);
  if (!trace && !cut_on) return;
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t entry = data + i * kDelayedOpSize;
    const LinkKey key{sequence, LoadU32(base, entry), LoadU32(base, entry + 4)};
    if (std::ranges::any_of(g_links_seen, [&](const LinkKey& seen) { return seen == key; })) continue;
    g_links_seen.push_back(key);
    const float remaining = LoadF32(base, entry + kDelayedOpRemaining);
    const wwe::faster_starts::KismetOp activator = OpOf(base, key.activator);
    const wwe::faster_starts::KismetOp target = OpOf(base, key.target);
    const bool cut = cut_on && wwe::faster_starts::IsCutLink(activator, target, remaining);
    if (cut) StoreU32(base, entry + kDelayedOpRemaining, 0);
    if (cut || trace) {
      REXLOG_INFO("faster starts: delayed link {} -> {} {:.2f} s{}", Path(activator), Path(target),
                  remaining, cut ? ", cut" : "");
    }
  }
}
