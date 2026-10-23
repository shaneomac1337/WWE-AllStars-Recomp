// Times the game's own engine tick, so a hitch can be told apart from Native's work
// (--wwe_tick_log). FEngineLoop::Tick at 0x82417780 runs one fixed step per frame and
// waits twice, both by spinning on appSleep(0) (0x82608EF8): on the render thread (the
// game-thread fence, 0x8271CB70, which keeps the render thread at most a frame behind)
// and in a 60 Hz frame limiter at its end. The log splits the time in appSleep into the
// two waits; the rest is the game working or waiting on the GPU.
// See docs/research/movement-judder.md §1 for the tick's layout.
//
// The same hooks stop both waits spinning a host core (low-end-preset ticket 10,
// docs/research/spin-loops.md §1 and §5): the limiter sleeps until shortly before the
// deadline the guest loop tests, and the render-thread wait backs off like D3D's GPU wait
// (gpu_wait.cpp); the guest's own tests still decide when each wait ends.
//
// Two midasm hooks make 60 fps entrances (wwe_60fps_entrances): the tick's step choice
// steps flagged cinematics at 1/60 s instead of 1/30 s, and the crowd scheduler then
// ticks the whole crowd every tick.
//
// 120 fps matches (wwe_120fps_matches): a midasm hook at the step join halves the step in a
// match, the guest vblank follows the tick rate (60 or 120 Hz) so each present is a new
// frame, and hooks in the camera, the movement tick and the anim player keep what the game
// counts per tick at its 60 fps timing (docs/research/high-framerate.md §12).

#include "generated/default/wweallstars_pch.h"

#include <algorithm>
#include <bit>
#include <chrono>
#include <cstdint>
#include <cstring>

#include <rex/cvar.h>
#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/runtime.h>
#include <rex/thread.h>

#include "game_tick_log.h"
#include "guest_memory.h"
#include "spin_waits.h"

REXCVAR_DEFINE_BOOL(wwe_tick_log, false, "WWEAllStars",
                    "Log the game's engine tick: a line per slow tick and a summary each second");
REXCVAR_DEFINE_BOOL(wwe_limiter_sleep, true, "WWEAllStars",
                    "Let the game thread's frame waits (the 60 Hz limiter and the wait on the "
                    "render thread) sleep instead of spinning a host core (false: the game's "
                    "own appSleep(0) yield loops, for comparisons)");
REXCVAR_DEFINE_INT32(wwe_120fps_fixes, 111, "WWEAllStars",
                     "120 fps matches: which per-tick fixes run while the step is 1/120 s (a "
                     "bitmask, for comparisons): 1 match camera averaging, 2 jump ease-in ramp, "
                     "4 jump ramp gravity term, 8 animation frame counter, 32 motion locator window "
                     "(ticket 07), 64 bone "
                     "anchor events (ticket 08)");
REXCVAR_DECLARE(bool, wwe_60fps_entrances);  // settings_cvars.cpp
REXCVAR_DECLARE(bool, wwe_120fps_matches);   // settings_cvars.cpp

namespace {

using wwe::guest::LoadU32;
using wwe::guest::StoreU32;

using Clock = std::chrono::steady_clock;

// FEngineLoop fields written by the tick (this = r3): the step and the time dilation.
constexpr uint32_t kDeltaSecondsOffset = 0x28;
constexpr uint32_t kDilationOffset = 0x2C;
// The limiter's clock (spin-loops.md §1): the frame start, a double at this+0xC
// (stored at 0x82417948/5C), and GCurrentTime, which the loop stores just before each
// appSleep call (0x82417D48 and 0x82417D8C).
constexpr uint32_t kFrameStartOffset = 0xC;
constexpr uint32_t kCurrentTime = 0x8370BD40;
// The limiter's call is bl 0x82608ef8 at 0x82417D70; the game calls appSleep(0.0) from
// other places inside the tick too (its own yields), where these values mean nothing.
constexpr uint32_t kLimiterReturn = 0x82417D74;
// The render-thread wait's call is bl 0x82608ef8 at 0x8271CC04, in its loop while the fence
// counter is above the target (0x8271CBC4-0x8271CC10, spin-loops.md §5).
constexpr uint32_t kRenderWaitReturn = 0x8271CC08;
// Set together with two clear flags, the tick steps 1/30 s instead of 1/60 s.
constexpr uint32_t kHalfRateFlag = 0x83709514;
// The command-line switches that force 1/60 s whatever the flag (-BENCHMARK, -DUMPMOVIE).
constexpr uint32_t kBenchmarkFlag = 0x83709510;
constexpr uint32_t kDumpMovieFlag = 0x83709530;
// The countdown the 1/30 path holds at 5; at 0 the helper tick 0x82410988 runs.
constexpr uint32_t kHalfRateCountdown = 0x835E0088;
constexpr double kSlowTickMs = 20.0;

thread_local bool t_in_tick = false;
thread_local uint32_t t_self = 0;  // the FEngineLoop whose tick runs
// The game thread's current tick steps 1/120 s (Wwe120Tick halved it), and how many ticks
// have run: the per-tick fixes below read both from inside GEngine->Tick.
thread_local bool t_step_halved = false;
thread_local uint64_t t_tick_count = 0;
// How often each per-tick fix changed a value this second (wwe_tick_log), by bit.
thread_local uint32_t t_fix_counts[7] = {};

rex::Runtime* g_runtime = nullptr;
// The guest vblank rate last asked for: 120 while ticks run at 120/s, else 0 (the video
// mode's), so presents follow new frames and an FPS counter reads the game's real rate.
double g_vblank_rate = 0.0;

void FollowTickRate(bool ticks_120) {
  const double want = ticks_120 ? 120.0 : 0.0;
  if (want == g_vblank_rate) {
    return;
  }
  auto* graphics = g_runtime ? g_runtime->graphics_system() : nullptr;
  if (!graphics) {
    return;
  }
  g_vblank_rate = want;
  graphics->SetGuestVblankRate(want);
}
// Time in appSleep during the tick: the limiter's, the render-thread wait's, any other call's.
struct Waits {
  uint64_t limiter_ns = 0, render_ns = 0, other_ns = 0;
  uint64_t Total() const { return limiter_ns + render_ns + other_ns; }
  Waits& operator+=(const Waits& other) {
    limiter_ns += other.limiter_ns;
    render_ns += other.render_ns;
    other_ns += other.other_ns;
    return *this;
  }
};
thread_local Waits t_waits;
// When the current render-thread wait on this thread began.
thread_local Clock::time_point t_render_wait_start;

struct Second {
  Clock::time_point start{};
  uint32_t ticks = 0, half_rate_ticks = 0, other_step_ticks = 0, at_60_ticks = 0;
  double work_sum_ms = 0, work_max_ms = 0, total_max_ms = 0;
  // Time in each kind of appSleep wait this second (an online match's SyncSim hold is "other").
  Waits waits;
  float dilation_min = 1e9f, dilation_max = -1e9f;
} g_second;

float LoadF32(const uint8_t* base, uint32_t address) {
  return std::bit_cast<float>(LoadU32(base, address));
}

double LoadF64(const uint8_t* base, uint32_t address) {
  return std::bit_cast<double>(uint64_t(LoadU32(base, address)) << 32 | LoadU32(base, address + 4));
}

// The limiter's time left before its deadline, in guest seconds, from the values the
// loop has just computed.
double LimiterRemaining(const uint8_t* base, uint32_t self) {
  const double dt = double(LoadF32(base, self + kDeltaSecondsOffset)) *
                    double(LoadF32(base, self + kDilationOffset));
  return LoadF64(base, self + kFrameStartOffset) + dt - LoadF64(base, kCurrentTime);
}

double Ms(Clock::duration d) { return std::chrono::duration<double, std::milli>(d).count(); }

void Record(const uint8_t* base, uint32_t self, Clock::duration total, const Waits& waits,
            bool at_60) {
  const double total_ms = Ms(total);
  const double work_ms = std::max(0.0, total_ms - waits.Total() / 1e6);
  const float step = LoadF32(base, self + kDeltaSecondsOffset);
  const float dilation = LoadF32(base, self + kDilationOffset);
  const bool half_rate = LoadU32(base, kHalfRateFlag) != 0;
  if (total_ms > kSlowTickMs) {
    // scripts/analyze_hitches.py reads this line (GAME_TICK).
    REXLOG_INFO("game tick: {:.1f} ms, work {:.1f} ms, limiter {:.1f} ms, render thread "
                "{:.1f} ms, other waits {:.1f} ms; step {:.5f} s, dilation {:.3f}, 1/30 flag {}",
                total_ms, work_ms, waits.limiter_ns / 1e6, waits.render_ns / 1e6,
                waits.other_ns / 1e6, step, dilation, int(half_rate));
  }
  Second& s = g_second;
  const auto now = Clock::now();
  if (s.start == Clock::time_point{}) {
    s.start = now;
  }
  ++s.ticks;
  s.at_60_ticks += at_60;
  s.half_rate_ticks += std::abs(step - 1.0f / 30) < 1e-4f;
  s.other_step_ticks += std::abs(step - 1.0f / 60) >= 1e-4f && std::abs(step - 1.0f / 30) >= 1e-4f;
  s.work_sum_ms += work_ms;
  s.waits += waits;
  s.work_max_ms = std::max(s.work_max_ms, work_ms);
  s.total_max_ms = std::max(s.total_max_ms, total_ms);
  s.dilation_min = std::min(s.dilation_min, dilation);
  s.dilation_max = std::max(s.dilation_max, dilation);
  if (now - s.start >= std::chrono::seconds(1)) {
    if (t_fix_counts[0] | t_fix_counts[1] | t_fix_counts[2] | t_fix_counts[3] |
        t_fix_counts[5] | t_fix_counts[6]) {
      REXLOG_INFO(
          "120 fps fixes 1s: camera {}, ramp {}, ramp gravity {}, anim frames {}, locator {}, "
          "anchors {}",
          t_fix_counts[0], t_fix_counts[1], t_fix_counts[2], t_fix_counts[3], t_fix_counts[5],
          t_fix_counts[6]);
      std::memset(t_fix_counts, 0, sizeof(t_fix_counts));
    }
    REXLOG_INFO("game tick 1s: {} ticks ({} at 1/30 s, {} other steps, {} cinematic at "
                "1/60 s), work mean {:.2f} ms max {:.1f} ms, tick max {:.1f} ms, dilation "
                "{:.3f}..{:.3f}, waits limiter {:.0f} ms render {:.0f} ms other {:.0f} ms",
                s.ticks, s.half_rate_ticks, s.other_step_ticks, s.at_60_ticks,
                s.work_sum_ms / s.ticks, s.work_max_ms, s.total_max_ms, s.dilation_min,
                s.dilation_max, s.waits.limiter_ns / 1e6, s.waits.render_ns / 1e6,
                s.waits.other_ns / 1e6);
    s = Second{};
    s.start = now;
  }
}

}  // namespace

// FEngineLoop::Tick.
DECLARE_REX_FUNC(sub_82417780);
REX_HOOK_RAW(sub_82417780) {
  const uint32_t self = ctx.r3.u32;
  const auto start = Clock::now();
  t_in_tick = true;
  t_self = self;
  t_step_halved = false;
  ++t_tick_count;
  t_waits = {};
  const bool at_60 = REXCVAR_GET(wwe_60fps_entrances) && LoadU32(base, kHalfRateFlag) != 0;
  __imp__sub_82417780(ctx, base);
  t_in_tick = false;
  FollowTickRate(t_step_halved);
  if (REXCVAR_GET(wwe_tick_log)) {
    Record(base, self, Clock::now() - start, t_waits, at_60);
  }
}

// The game-thread fence wait (r3 = the fence counter, r4 = how many frames the render
// thread may lag): runs once per wait, so it only marks when the wait began.
DECLARE_REX_FUNC(sub_8271CB70);
REX_HOOK_RAW(sub_8271CB70) {
  t_render_wait_start = Clock::now();
  __imp__sub_8271CB70(ctx, base);
}

// appSleep(seconds). Only the limiter's and the render-thread wait's own appSleep(0.0) wait on
// the host (spin-loops.md §1, §5); every other call runs as shipped.
DECLARE_REX_FUNC(sub_82608EF8);
REX_HOOK_RAW(sub_82608EF8) {
  const bool zero = ctx.f1.f64 == 0.0;
  const bool limiter = t_in_tick && zero && ctx.lr == kLimiterReturn;
  const bool render_wait = zero && ctx.lr == kRenderWaitReturn;
  if (!t_in_tick && !render_wait) {
    __imp__sub_82608EF8(ctx, base);
    return;
  }
  const auto start = Clock::now();
  const bool sleep_on = REXCVAR_GET(wwe_limiter_sleep);
  int64_t sleep_us = 0;
  if (sleep_on && limiter) {
    sleep_us = wwe::spin_waits::LimiterSleepUs(LimiterRemaining(base, t_self));
  } else if (sleep_on && render_wait) {
    const int64_t waited_us =
        std::chrono::duration_cast<std::chrono::microseconds>(start - t_render_wait_start).count();
    if (wwe::spin_waits::WaitBackoff(waited_us) == wwe::spin_waits::Backoff::kSleep) {
      sleep_us = wwe::spin_waits::kSleepUs;
    }
    // kNone and kYield: the game's own appSleep(0) is already a yield.
  }
  if (sleep_us > 0) {
    rex::thread::PreciseSleep(std::chrono::microseconds(sleep_us));
  } else {
    __imp__sub_82608EF8(ctx, base);
  }
  if (!t_in_tick) {
    return;
  }
  const auto took = uint64_t(
      std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - start).count());
  (limiter ? t_waits.limiter_ns : render_wait ? t_waits.render_ns : t_waits.other_ns) += took;
}

namespace {

// The setting is on and a flagged cinematic plays, with neither switch that already
// forces 1/60 s.
bool InSixtyFpsCinematic() {
  if (!REXCVAR_GET(wwe_60fps_entrances)) {
    return false;
  }
  const uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  return LoadU32(base, kHalfRateFlag) != 0 && LoadU32(base, kBenchmarkFlag) == 0 &&
         LoadU32(base, kDumpMovieFlag) == 0;
}

}  // namespace

// 60 fps entrances (.scratch/60fps-entrances/spec.md; docs/research/60fps-entrances.md
// §1.1, §6 E3): the tick's step choice at 0x824178CC loads the 1/30 flag. True jumps to
// the 1/60 path at 0x82417910 for a flagged cinematic (the arena intro, entrances), as
// the game's own -BENCHMARK/-DUMPMOVIE checks do; the flag itself is left set, so the
// skip input (0x824F03E8) and the Matinee's Play/Stop see it. The 1/30 path stores 5 to
// the countdown; the 1/60 path decrements it (0x82417918-0x8241792C), so 6 leaves the
// same 5 and the helper tick 0x82410988 stays off as on the console.
bool Wwe60fpsEntrances() {
  if (!InSixtyFpsCinematic()) {
    return false;
  }
  StoreU32(REX_KERNEL_MEMORY()->virtual_membase(), kHalfRateCountdown, 6);
  return true;
}

// 60 fps entrances, crowd (ticket 02, docs/research/60fps-entrances.md §8): at a 1/60 s
// step the crowd scheduler 0x82B55A38 ticks one of its two crowd groups per tick, so the
// crowd animates at 30 Hz (as in matches). In a flagged cinematic stepped at 1/60 s, true
// takes the scheduler's own path for steps of 1/30 s and over (0x82B55AA0): every crowd
// group ticks every tick with the real step, as all of them do in the 30 Hz original.
bool Wwe60fpsCrowd() { return InSixtyFpsCinematic(); }

namespace {

// A match is being played: the match's pawn manager (0x837450D0, set up once bit 0 of
// [0x8374523C] is set; docs/research/high-framerate.md §11) has entries. They are ring objects,
// not the wrestlers (ticket 07), but the list is empty in the menus (whose 3D arena backdrop
// runs the crowd too) and through the entrances, and fills about 3 s after them (ticket 03
// runs: 0 in menus, 4 in a 1v1).
constexpr uint32_t kPawnManagerReady = 0x8374523C;
constexpr uint32_t kPawnManagerCount = 0x837450D0 + 0x40;
bool InMatch() {
  const uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  const uint32_t count = LoadU32(base, kPawnManagerCount);
  return (LoadU32(base, kPawnManagerReady) & 1) != 0 && count > 0 && count <= 16;
}

// A Net-Z session runs: the tick's own test before the session tick (lwz r11,0x590 of
// 0x83740000 at 0x82417CD4; 0x82410498 only returns the session singleton). SyncSim counts
// engine ticks (research §5), so an online match keeps 1/60. Being signed in (wwe_online)
// is not enough reason: offline matches then still run at 120.
constexpr uint32_t kNetZSessionActive = 0x83740590;

bool At120Allowed() {
  if (!InMatch()) {
    return false;
  }
  const uint8_t* base = REX_KERNEL_MEMORY()->virtual_membase();
  return LoadU32(base, kNetZSessionActive) == 0 && LoadU32(base, kHalfRateFlag) == 0 &&
         LoadU32(base, kBenchmarkFlag) == 0 && LoadU32(base, kDumpMovieFlag) == 0;
}

}  // namespace

// 120 fps matches (.scratch/high-framerate/spec.md; docs/research/high-framerate.md §1.1,
// §6 a): both step paths join at 0x82417930 with the step in f31, before the tick stores it
// (+0x28), passes dilation * step to GEngine->Tick and waits that long in its limiter. Halving
// f31 there gives 1/120 s ticks and leaves the game's own time dilation (+0x2C) alone: the game
// writes it itself (1.0 at match start, 0.4 in slow motion; ticket 02). Only in a match: 1/30 s
// cinematics (entrances, replays) keep their step, and online play keeps 1/60 (SyncSim counts
// ticks, §5).
void Wwe120Tick(PPCRegister& f31) {
  if (REXCVAR_GET(wwe_120fps_matches) && At120Allowed()) {
    f31.f64 *= 0.5;
    t_step_halved = true;
  }
}

// Per-tick fixes for 1/120 s steps (ticket 04; docs/research/high-framerate.md §11.3, §12).
// Each one restores what a 1/60 s tick does, and only runs while this tick's step was
// halved, so 60 fps play is untouched. wwe_120fps_fixes switches them one by one for
// comparisons. The game counts these in ticks, not in game time: at 1/60 s each tick adds a
// whole frame even in slow motion (dt carries the dilation), so a halved tick adds half.
namespace {

bool Fix120On(int32_t bit) {
  return t_step_halved && (REXCVAR_GET(wwe_120fps_fixes) & bit) != 0;
}

void CountFix120(int32_t bit) { ++t_fix_counts[std::countr_zero(uint32_t(bit))]; }

bool Fix120(int32_t bit) {
  if (!Fix120On(bit)) {
    return false;
  }
  CountFix120(bit);
  return true;
}

constexpr double kHalfFrame = 0.5;

}  // namespace

// Jump ease-in ramp, previous step (0x82AE2138; bit 2): the ramp adds sin(n·a) − sin((n−1)·a)
// to the launch velocity each tick, with n the frames left (+0x1CEC) and 1.0 in f21
// (fsubs f9,f10,f21 at 0x82AE228C). A halved tick moves n by half a frame, so the previous
// point is n − 0.5 and the increments still add up to the whole velocity.
void Wwe120RampPrevious(PPCRegister& f9, PPCRegister& f10) {
  if (Fix120(2)) {
    f9.f64 = double(float(f10.f64 - kHalfFrame));
  }
}

// Jump ease-in ramp, countdown (bit 2): +0x1CEC −= 1.0 per tick (fsubs f11,f13,f21 at
// 0x82AE233C); a halved tick counts down half a frame, so the ramp lasts as long as at 60.
void Wwe120RampCountdown(PPCRegister& f11, PPCRegister& f13) {
  if (Fix120(2)) {
    f11.f64 = double(float(f13.f64 - kHalfFrame));
  }
}

// Jump ramp gravity term (bit 4): vel.z += g − g·dt·60 (fnmsubs f10,f0,f23,f31 at 0x82AE2344,
// f31 = g) adds g·(1 − d) per 1/60 s tick at dilation d (0 at full speed). A halved tick
// computes g − g·d/2; it should add half the 60 fps amount, g·(1 − d)/2, which is f10 − g/2.
void Wwe120RampGravity(PPCRegister& f10, PPCRegister& f31) {
  if (Fix120(4)) {
    f10.f64 = double(float(f10.f64 - f31.f64 * kHalfFrame));
  }
}

// Animation frame counter (bit 8): pawn +0x1D00 adds 1.0 per tick for the current animation
// (fadds f13,f0,f26 at 0x82541068), and the anim FSM compares it with authored 60 Hz frame
// numbers; a halved tick adds half a frame, so frame windows keep their length.
void Wwe120AnimFrames(PPCRegister& f0, PPCRegister& f13) {
  if (Fix120(8)) {
    f13.f64 = double(float(f0.f64 + kHalfFrame));
  }
}

// Motion locator window (bit 32, ticket 07): the anim node tick moves the pawn by the
// "position_locator" bone's track sampled over [t, t + step] (0x825416C8, 0x825416EC; t already
// advanced). The clips are keyed for a lead of one 60 Hz frame, which is one step at 1/60 s. At
// 1/120 s one step is half that, so a climb's lift landed a tick after the pose that compensates
// for it and the body sank into the turnbuckle for two ticks. A halved tick samples
// [t + step, t + 2·step] instead: the same one-frame lead; at 1/60 s nothing changes.
namespace {
void ShiftLocatorTime(PPCRegister& f1, PPCRegister& r28) {
  if (Fix120(32)) {
    const float step = LoadF32(REX_KERNEL_MEMORY()->virtual_membase(), r28.u32);
    f1.f64 = double(float(f1.f64 + step));
  }
}

}  // namespace

void Wwe120LocatorFrom(PPCRegister& f1, PPCRegister& r28) { ShiftLocatorTime(f1, r28); }

void Wwe120LocatorTo(PPCRegister& f1, PPCRegister& r28) { ShiftLocatorTime(f1, r28); }

// Bone anchor events (bit 64, ticket 08): the notify walker fires a statement keyed to frame F
// once time·60 + rate·0.5 >= F. A statement keyed to F covers the 60 Hz frame (F − 1, F]; at
// 1/60 s it fires on the tick that plays that frame, but at 1/120 s on the second of its two
// ticks. A bone anchor (pawn +0x230/+0x24C) holds a bone still while the pose changes, so one
// set a tick late missed half of that change: a running overhead throw launched the victim ~59
// units low. A halved tick lets only the anchor statements look ahead rate·1.0, so they fire
// on the frame's first tick; every other event keeps its timing (firing them all early changes
// which grabs and throws happen). The walker is in order, so an anchor never overtakes an
// earlier event.
constexpr uint32_t kBoneAnchorVtable = 0x82080388;   // Execute 0x82597240
constexpr uint32_t kBoneAnchorVtable2 = 0x820801D0;  // Execute 0x82597168, same fields
void Wwe120AnchorLookAhead(PPCRegister& f0, PPCRegister& f27, PPCRegister& f31,
                           PPCRegister& r30) {
  if (!Fix120On(64)) {
    return;
  }
  const uint32_t vtable = LoadU32(REX_KERNEL_MEMORY()->virtual_membase(), r30.u32);
  if (vtable != kBoneAnchorVtable && vtable != kBoneAnchorVtable2) {
    return;
  }
  CountFix120(64);
  f0.f64 = double(float(f31.f64 + float(f27.f64 * 2.0)));
}

// Match camera averaging (bit 1): 0x82B60A18(dt, ...) pushes one sample of the wrestlers'
// centre per call into a 30-entry ring and returns the mean, 0.5 s of play at 1/60 s but
// 0.25 s at 1/120 s, so the camera follows twice as tightly. The sample is the centre of the
// bounding box of the points at [cam+0x3D4] (count +0x3D8, 12 bytes each). Skipping the push
// on every other tick (dt == 0.0, its mean-only branch) kept the 0.5 s span but froze the
// focus on that tick: a 60 Hz judder at 120 fps (ticket 06). At 1/120 s the output is instead
// the mean of the last 60 samples, 0.5 s, updated every tick. A call with r6 set refills the
// game's ring from one point (a camera cut); that refills this ring too.
namespace {
constexpr int kFocusSamples = 60;
struct FocusRing {
  uint32_t camera = 0;
  uint64_t last_tick = 0;
  int next = 0;
  float samples[kFocusSamples][3] = {};
};
// Only touched while Fix120(1) holds, which reads the game thread's thread_local tick state, so
// only the game thread uses it.
FocusRing g_focus;

void StoreF32(uint8_t* base, uint32_t address, float value) {
  StoreU32(base, address, std::bit_cast<uint32_t>(value));
}

bool FocusSample(const uint8_t* base, uint32_t camera, float sample[3]) {
  const uint32_t points = LoadU32(base, camera + 0x3D4);
  const int32_t count = int32_t(LoadU32(base, camera + 0x3D8));
  if (count <= 0 || points < 0x40000000) {  // heap objects live at 0x40000000 and up
    return false;
  }
  float lo[3], hi[3];
  for (int32_t i = 0; i < count; ++i) {
    for (int k = 0; k < 3; ++k) {
      const float v = LoadF32(base, points + 12 * i + 4 * k);
      lo[k] = i == 0 ? v : std::min(lo[k], v);
      hi[k] = i == 0 ? v : std::max(hi[k], v);
    }
  }
  for (int k = 0; k < 3; ++k) {
    sample[k] = (lo[k] + hi[k]) * 0.5f;
  }
  return true;
}
}  // namespace

DECLARE_REX_FUNC(sub_82B60A18);
REX_HOOK_RAW(sub_82B60A18) {
  const uint32_t camera = ctx.r3.u32, out = ctx.r4.u32;
  const bool refill = (ctx.r6.u32 & 0xFF) != 0;
  float sample[3];
  const bool own = out != 0 && Fix120(1) && FocusSample(base, camera, sample);
  // The game's early outs leave out untouched; a NaN marker tells them apart.
  const uint32_t kMarker = 0x7FC0DEAD;  // a NaN: never a focus coordinate the game writes
  if (own) {
    StoreU32(base, out, kMarker);
  }
  __imp__sub_82B60A18(ctx, base);
  if (own && LoadU32(base, out) != kMarker) {
    FocusRing& ring = g_focus;
    const bool same_tick = ring.camera == camera && ring.last_tick == t_tick_count;
    if (refill || ring.camera != camera || ring.last_tick + 1 < t_tick_count) {
      float fill[3];
      for (int k = 0; k < 3; ++k) {
        fill[k] = refill ? LoadF32(base, out + 4 * k) : sample[k];
      }
      for (auto& s : ring.samples) {
        std::copy(fill, fill + 3, s);
      }
      ring.camera = camera;
      ring.next = 0;
    }
    if (!refill && !same_tick) {
      std::copy(sample, sample + 3, ring.samples[ring.next]);
      ring.next = (ring.next + 1) % kFocusSamples;
    }
    ring.last_tick = t_tick_count;
    for (int k = 0; k < 3; ++k) {
      double sum = 0.0;
      for (const auto& s : ring.samples) {
        sum += s[k];
      }
      StoreF32(base, out + 4 * k, float(sum / kFocusSamples));
    }
  }
}

namespace wwe::game_tick {

void Configure(rex::Runtime* runtime) { g_runtime = runtime; }

}  // namespace wwe::game_tick
