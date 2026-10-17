// Counts calls to the game's XDK Direct3D functions that build GPU draw
// packets, so a session log can be compared with the PM4 draws the command
// processor executes (--gpu_draw_census). Equal totals mean every draw the GPU
// sees came through a function a native renderer can hook.
//
// Addresses are in the statically linked D3D library (0x82DA9AA0-0x82DCEAA0);
// see scripts/scan_pm4.py for how they were found.

#include "generated/default/wweallstars_pch.h"

#include "d3d_census.h"
#include "crash_report.h"
#include "native/native_renderer.h"
#include "native/shader_library.h"
#include "native/shader_topup.h"
#include "native/topup_converter.h"
#include "frame_clock.h"
#include "resolve_watch.h"
#include "xma_stats.h"
#ifdef WWE_D3D_SWEEP
#include "d3d_sweep.h"
#endif

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <algorithm>
#include <vector>

#include <fmt/format.h>
#include <rex/cvar.h>
#include <rex/hook.h>
#include <rex/logging.h>

REXCVAR_DEFINE_BOOL(wwe_d3d_census, false, "WWEAllStars",
                    "Log cumulative D3D draw-function call counts every 60 presents");
REXCVAR_DEFINE_INT32(wwe_exit_after_frame, -1, "WWEAllStars",
                     "Scene runs: after presenting this guest frame (and dumping it), write the "
                     "native report, save the pipeline cache and exit");
REXCVAR_DEFINE_BOOL(wwe_d3d_probe, false, "WWEAllStars",
                    "Log the arguments of the first calls to key D3D functions");
REXCVAR_DEFINE_STRING(wwe_shader_dump_dir, "", "WWEAllStars",
                      "Save each unique shader container passed to CreateVertexShader or "
                      "CreatePixelShader to this directory, for offline conversion");

namespace {

enum Counter : int {
  kDrawIndexedVertices,  // 82DB2060, PM4 DRAW_INDX
  kDrawVerticesUP,       // 82DB1160, PM4 DRAW_INDX (quad lists, 2D and UI)
  kBeginIndexedVertices,  // 82DB1668, PM4 DRAW_INDX (DrawIndexedVerticesUP)
  kDrawVertices,          // 82DB1C48, PM4 DRAW_INDX, not hooked by the native renderer
  kClearRect,            // 82DBC4B0, PM4 DRAW_INDX_2 after each Clear
  kDraw14F68,            // unnamed DRAW_INDX_2 builders, not seen per frame so far
  kDraw150C0,
  kDraw1A468,
  kDraw21230,
  kDraw29158,
  kDraw2F338,
  kIndirectBuffer,  // 82DB7CE0, PM4 INDIRECT_BUFFER
  kResolve,         // 82DBF120
  kSetRenderTarget,  // 82DB5548
  kCreateVertexShader,  // 82DADDE8
  kCreatePixelShader,   // 82DADBC0
  kRegisterVertexShader,  // 82DADCF0, every vertex shader object
  kRegisterPixelShader,   // 82DAC148, every pixel shader object
  kCount
};

std::atomic<uint64_t> g_counts[kCount];
std::atomic<uint64_t> g_presents;

void Count(Counter c) { g_counts[c].fetch_add(1, std::memory_order_relaxed); }

// Guest callers (the return address in lr) of each DRAW_INDX builder, so the
// census can say which game code issues the draws (--wwe_d3d_census only).
struct Callers {
  const char* name;
  std::mutex mutex;
  std::unordered_map<uint32_t, uint64_t> counts;
};
Callers g_callers[] = {{"DrawIndexedVertices"}, {"BeginVertices"}, {"BeginIndexedVertices"},
                       {"DrawVertices"}};
enum CallerTable { kCallersIndexed, kCallersUP, kCallersIndexedUP, kCallersDrawVertices };

void CountCaller(CallerTable table, uint64_t lr) {
  if (!REXCVAR_GET(wwe_d3d_census)) {
    return;
  }
  Callers& callers = g_callers[table];
  std::lock_guard lock(callers.mutex);
  ++callers.counts[uint32_t(lr)];
}

// Cumulative counts, busiest first; census_report.py keeps the latest per caller.
void LogCallers() {
  for (Callers& callers : g_callers) {
    std::vector<std::pair<uint64_t, uint32_t>> top;
    {
      std::lock_guard lock(callers.mutex);
      for (const auto& [caller, n] : callers.counts) top.emplace_back(n, caller);
    }
    if (top.empty()) continue;
    std::sort(top.rbegin(), top.rend());
    std::string line;
    for (size_t i = 0; i < top.size() && i < 24; ++i) {
      line += fmt::format(" {:08X}={}", top[i].second, top[i].first);
    }
    REXLOG_INFO("draw-census callers {}:{}", callers.name, line);
  }
}

uint64_t Get(Counter c) { return g_counts[c].load(std::memory_order_relaxed); }

void LogCensus(uint64_t presents) {
  const uint64_t other = Get(kDraw14F68) + Get(kDraw150C0) + Get(kDraw1A468) +
                         Get(kDraw21230) + Get(kDraw29158) + Get(kDraw2F338);
  REXLOG_INFO(
      "draw-census guest presents={} indexed={} draw_vertices={} up={} begin_indexed={} "
      "clear_rect={} other_draws={} "
      "(14F68={} 150C0={} 1A468={} 21230={} 29158={} 2F338={}) indirect={} resolve={} "
      "set_rt={} create_vs={} create_ps={} register_vs={} register_ps={}",
      presents, Get(kDrawIndexedVertices), Get(kDrawVertices), Get(kDrawVerticesUP),
      Get(kBeginIndexedVertices), Get(kClearRect), other,
      Get(kDraw14F68), Get(kDraw150C0), Get(kDraw1A468), Get(kDraw21230), Get(kDraw29158),
      Get(kDraw2F338), Get(kIndirectBuffer), Get(kResolve), Get(kSetRenderTarget),
      Get(kCreateVertexShader), Get(kCreatePixelShader), Get(kRegisterVertexShader),
      Get(kRegisterPixelShader));
}

uint32_t LoadBE32(const uint8_t* p) {
  return uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
}

// Shader containers start with flags 0x102A11xx, then the virtual (header and
// constant tables) and physical (microcode) sizes; on disc the two parts are
// contiguous. Shader objects keep a copy of the header and a pointer to the
// microcode, so the container is rebuilt from both (see ShaderRegistered).
bool ValidContainerHeader(const uint8_t* header) {
  const uint32_t flags = LoadBE32(header);
  const uint32_t size = LoadBE32(header + 4) + LoadBE32(header + 8);
  return (flags & 0xFFFFFF00) == 0x102A1100 && size != 0 && size <= (1u << 20);
}

void DumpShaderContainer(const std::vector<uint8_t>& container, const char* kind) {
  const std::string dir = REXCVAR_GET(wwe_shader_dump_dir);
  if (dir.empty()) {
    return;
  }
  uint64_t hash = 14695981039346656037ull;  // FNV-1a
  for (uint8_t byte : container) {
    hash = (hash ^ byte) * 1099511628211ull;
  }
  static std::mutex mutex;
  static std::unordered_set<uint64_t> seen;
  std::lock_guard lock(mutex);
  if (!seen.insert(hash).second) {
    return;
  }
  std::error_code error;
  std::filesystem::create_directories(dir, error);
  const auto path =
      std::filesystem::path(dir) / fmt::format("{}_{:016x}.bin", kind, hash);
  if (FILE* file = std::fopen(path.string().c_str(), "wb")) {
    std::fwrite(container.data(), 1, container.size(), file);
    std::fclose(file);
  }
}

// Both creation paths end in the XDK's shader register functions: the
// Create*Shader calls, and UE3's in-place path (0x828E8270 for vertex shaders,
// 0x828E8418 for pixel shaders), which builds the object itself and never
// calls Create*Shader. The object holds the container header at header_offset
// and the microcode address at microcode_offset.
// Objects built by Create*Shader, registered from the caller's container
// (ShaderCreated), with a hash of the header copy the object holds. D3D
// registers them again later; the register hook's rebuild of these objects is
// not the shader (ticket 29), so it must not replace them. Once the game frees
// an object and its memory holds another shader, the header differs and the
// register hook takes the new shader as usual.
std::mutex g_created_mutex;
std::unordered_map<uint32_t, uint64_t> g_created_headers;

uint64_t HeaderHash(const uint8_t* header) {
  uint64_t hash = 14695981039346656037ull;  // FNV-1a
  const uint32_t size = std::min<uint32_t>(LoadBE32(header + 4), 1u << 16);
  for (uint32_t i = 0; i < size; ++i) {
    hash = (hash ^ header[i]) * 1099511628211ull;
  }
  return hash;
}

void ShaderRegistered(const uint8_t* base, uint32_t object, uint32_t header_offset,
                      uint32_t microcode_offset, bool is_pixel_shader) {
  {
    std::lock_guard lock(g_created_mutex);
    const auto created = g_created_headers.find(object);
    if (created != g_created_headers.end()) {
      if (ValidContainerHeader(base + object + header_offset) &&
          HeaderHash(base + object + header_offset) == created->second) {
        return;  // the same Create*Shader shader, registered again
      }
      g_created_headers.erase(created);
    }
  }
  const uint8_t* header = base + object + header_offset;
  if (!ValidContainerHeader(header)) {
    REXLOG_WARN("shader register: {} object {:08X} has no container header (flags {:08X})",
                is_pixel_shader ? "pixel" : "vertex", object, LoadBE32(header));
    return;
  }
  const uint32_t header_size = LoadBE32(header + 4);
  const uint32_t microcode_size = LoadBE32(header + 8);
  const uint32_t microcode = LoadBE32(base + object + microcode_offset);
  std::vector<uint8_t> container(header, header + header_size);
  container.insert(container.end(), base + microcode, base + microcode + microcode_size);
  DumpShaderContainer(container, is_pixel_shader ? "ps" : "vs");
  wwe::native::shader_library::OnShaderCreated(container.data(), object, is_pixel_shader);
}

// --wwe_d3d_probe: log the arguments of the first calls to a D3D function, and
// of a few more once the game is past its menus, with the first 64 bytes of
// any argument that points into guest heap memory.
std::string DumpGuest(const uint8_t* base, uint32_t address) {
  std::string out;
  for (uint32_t i = 0; i < 64; i += 4) {
    out += fmt::format(" {:08X}", LoadBE32(base + address + i));
  }
  return out;
}

void Probe(const char* name, const PPCContext& ctx, const uint8_t* base, uint32_t& calls) {
  const uint64_t presents = g_presents.load(std::memory_order_relaxed);
  const bool early = calls < 4;
  const bool late = presents >= 1500 && calls < 12;
  if (!REXCVAR_GET(wwe_d3d_probe) || !(early || late)) {
    return;
  }
  ++calls;
  const uint32_t args[] = {ctx.r3.u32, ctx.r4.u32, ctx.r5.u32, ctx.r6.u32,
                           ctx.r7.u32, ctx.r8.u32, ctx.r9.u32, ctx.r10.u32};
  REXLOG_INFO("probe {} frame={} r3..r10={:08X} {:08X} {:08X} {:08X} {:08X} {:08X} {:08X} {:08X} "
              "f1={} f2={}",
              name, presents, args[0], args[1], args[2], args[3], args[4], args[5], args[6],
              args[7], ctx.f1.f64, ctx.f2.f64);
  for (int i = 1; i < 8; ++i) {
    if (args[i] >= 0x40000000 && args[i] < 0xC0000000) {
      REXLOG_INFO("probe {}   r{} ->{}", name, i + 3, DumpGuest(base, args[i]));
    }
  }
}

#ifdef WWE_D3D_SWEEP
// Busiest D3D library functions since start, with calls per present.
void LogSweep(uint64_t presents) {
  std::vector<std::pair<uint64_t, uint32_t>> calls;
  std::string draws;
  for (int i = 0; i < wwe::sweep::kCount; ++i) {
    const uint64_t n = wwe::sweep::g_calls[i].load(std::memory_order_relaxed);
    if (n) calls.emplace_back(n, wwe::sweep::kAddresses[i]);
    const uint64_t d = wwe::sweep::g_draws[i].load(std::memory_order_relaxed);
    if (d) draws += fmt::format(" {:08X}={:.1f}", wwe::sweep::kAddresses[i], double(d) / double(presents));
  }
  std::sort(calls.rbegin(), calls.rend());
  std::string line;
  for (size_t i = 0; i < calls.size() && i < 48; ++i) {
    line += fmt::format(" {:08X}={:.1f}", calls[i].second, double(calls[i].first) / double(presents));
  }
  REXLOG_INFO("d3d-sweep presents={} per-present:{}", presents, line);
  REXLOG_INFO("d3d-sweep draws-written per-present:{}", draws);
}
#endif

}  // namespace

#define WWE_PROBE_HOOK(address, name) \
  DECLARE_REX_FUNC(sub_##address);    \
  REX_HOOK_RAW(sub_##address) {       \
    static uint32_t calls = 0;        \
    Probe(name, ctx, base, calls);    \
    __imp__sub_##address(ctx, base);  \
  }

WWE_PROBE_HOOK(82DB4D80, "SetIndices")
WWE_PROBE_HOOK(82DAC510, "SetVertexDeclaration")
WWE_PROBE_HOOK(82DAA2F8, "SetTexture")

#define WWE_COUNTING_HOOK(address, counter) \
  DECLARE_REX_FUNC(sub_##address);          \
  REX_HOOK_RAW(sub_##address) {             \
    Count(counter);                         \
    __imp__sub_##address(ctx, base);        \
  }

// D3DDevice_SetStreamSource: r4 = stream, r5 = vertex buffer, r6 = offset,
// r7 = stride. It writes the stream's vertex fetch constant into the device.
DECLARE_REX_FUNC(sub_82DB4BD8);
REX_HOOK_RAW(sub_82DB4BD8) {
  static uint32_t probe_calls = 0;
  Probe("SetStreamSource", ctx, base, probe_calls);
  const uint32_t stream = ctx.r4.u32;
  const uint32_t stride = ctx.r7.u32;
  __imp__sub_82DB4BD8(ctx, base);
  wwe::native::OnSetStreamSource(stream, stride);
}

// D3DDevice_DrawIndexedVertices: r3 = device, r4 = primitive, r5 = base
// vertex, r6 = start index, r7 = index count. The device's register mirror
// holds the draw's complete state once the original has committed it.
DECLARE_REX_FUNC(sub_82DB2060);
REX_HOOK_RAW(sub_82DB2060) {
  Count(kDrawIndexedVertices);
  CountCaller(kCallersIndexed, ctx.lr);
  static uint32_t probe_calls = 0;
  Probe("DrawIndexedVertices", ctx, base, probe_calls);
  const uint32_t device = ctx.r3.u32;
  const uint32_t primitive = ctx.r4.u32;
  const int32_t base_vertex = ctx.r5.s32;
  const uint32_t start_index = ctx.r6.u32;
  const uint32_t index_count = ctx.r7.u32;
  __imp__sub_82DB2060(ctx, base);
  wwe::native::OnDrawIndexed(base, device, primitive, base_vertex, start_index, index_count);
}

// D3DDevice_BeginVertices: r4 = primitive, r5 = vertex count, r6 = stride;
// returns the guest pointer the game copies the vertices into. The draw is
// complete in EndVertices.
namespace {
struct PendingVertices {
  uint32_t device = 0;
  uint32_t primitive = 0;
  uint32_t count = 0;
  uint32_t stride = 0;
  uint32_t vertices = 0;
  uint32_t indices = 0;
  uint32_t index_count = 0;
  bool index32 = false;
  int32_t base_vertex = 0;
} g_pending_vertices;

void FinishPendingVertices(const uint8_t* base) {
  const PendingVertices pending = g_pending_vertices;
  g_pending_vertices = {};
  if (pending.vertices) {
    wwe::native::OnDrawVertices(base, pending.device, pending.primitive, pending.count,
                                pending.stride, pending.vertices, pending.indices,
                                pending.index_count, pending.index32, pending.base_vertex);
  }
}
}  // namespace

DECLARE_REX_FUNC(sub_82DB1160);
REX_HOOK_RAW(sub_82DB1160) {
  Count(kDrawVerticesUP);
  CountCaller(kCallersUP, ctx.lr);
  static uint32_t probe_calls = 0;
  Probe("BeginVertices", ctx, base, probe_calls);
  g_pending_vertices = {ctx.r3.u32, ctx.r4.u32, ctx.r5.u32, ctx.r6.u32, 0};
  __imp__sub_82DB1160(ctx, base);
  g_pending_vertices.vertices = ctx.r3.u32;
}

// D3DDevice_EndVertices: r3 = device.
DECLARE_REX_FUNC(sub_82DB1150);
REX_HOOK_RAW(sub_82DB1150) {
  __imp__sub_82DB1150(ctx, base);
  FinishPendingVertices(base);
}

// DrawVerticesUP copies into BeginVertices' allocation and commits the ring
// inline at 0x82DB1658, without calling the hooked EndVertices entry point.
DECLARE_REX_FUNC(sub_82DB1620);
REX_HOOK_RAW(sub_82DB1620) {
  __imp__sub_82DB1620(ctx, base);
  FinishPendingVertices(base);
}

// BeginIndexedVertices returns indices through r10 and vertices through
// stack argument 9 (stores at 0x82DB1B7C/E0). EndVertices commits both.
DECLARE_REX_FUNC(sub_82DB1668);
REX_HOOK_RAW(sub_82DB1668) {
  Count(kBeginIndexedVertices);
  CountCaller(kCallersIndexedUP, ctx.lr);
  PendingVertices pending;
  pending.device = ctx.r3.u32;
  pending.primitive = ctx.r4.u32;
  pending.base_vertex = ctx.r5.s32;
  pending.count = ctx.r6.u32;
  pending.index_count = ctx.r7.u32;
  pending.index32 = (ctx.r8.u32 & 4) != 0;
  pending.stride = ctx.r9.u32;
  const uint32_t out_indices = ctx.r10.u32;
  const uint32_t out_vertices = LoadBE32(base + ctx.r1.u32 + 0x54);
  __imp__sub_82DB1668(ctx, base);
  g_pending_vertices = {};
  if (ctx.r3.u32 == 0 && out_vertices && out_indices) {
    pending.vertices = LoadBE32(base + out_vertices);
    pending.indices = LoadBE32(base + out_indices);
    g_pending_vertices = pending;
  }
}

// DrawIndexedVerticesUP also commits inline (0x82DB1C38).
DECLARE_REX_FUNC(sub_82DB1BA8);
REX_HOOK_RAW(sub_82DB1BA8) {
  __imp__sub_82DB1BA8(ctx, base);
  FinishPendingVertices(base);
}

// D3DDevice_Clear: r4 = flags, r5 = -1, r6..r9 = rect, r10 = float4 colour
// pointer, f1 = depth, stencil on the caller's stack.
DECLARE_REX_FUNC(sub_82DBC9D0);
REX_HOOK_RAW(sub_82DBC9D0) {
  static uint32_t probe_calls = 0;
  Probe("Clear", ctx, base, probe_calls);
  const uint32_t device = ctx.r3.u32;
  const uint32_t flags = ctx.r4.u32;
  const int32_t rect[4] = {ctx.r6.s32, ctx.r7.s32, ctx.r8.s32, ctx.r9.s32};
  const uint32_t color = ctx.r10.u32;
  const float depth = float(ctx.f1.f64);
  const uint32_t stencil = LoadBE32(base + ctx.r1.u32 + 0x5C);
  __imp__sub_82DBC9D0(ctx, base);
  wwe::native::OnClear(base, device, flags, rect, color, depth, stencil);
}

// D3D internal: copies one register group from the device's register mirror
// into the command buffer (r3 = device, r5 = first register, r6 = mirror).
// Logging each distinct pair once gives the mirror layout.
DECLARE_REX_FUNC(sub_82DC1B50);
REX_HOOK_RAW(sub_82DC1B50) {
  if (REXCVAR_GET(wwe_d3d_census)) {
    static std::mutex mutex;
    static std::unordered_set<uint64_t> seen;
    const uint64_t key = uint64_t(ctx.r5.u32) << 32 | (ctx.r6.u32 - ctx.r3.u32);
    std::lock_guard lock(mutex);
    if (seen.insert(key).second) {
      REXLOG_INFO("register-mirror first_reg={:04X} device_offset={:04X} r4={:08X}", ctx.r5.u32,
                  ctx.r6.u32 - ctx.r3.u32, ctx.r4.u32);
    }
  }
  __imp__sub_82DC1B50(ctx, base);
}

// D3DDevice_DrawVertices: r3 = device, r4 = primitive, r5 = start vertex,
// r6 = vertex count. An auto-index DRAW_INDX over the bound streams.
DECLARE_REX_FUNC(sub_82DB1C48);
REX_HOOK_RAW(sub_82DB1C48) {
  Count(kDrawVertices);
  CountCaller(kCallersDrawVertices, ctx.lr);
  const uint32_t device = ctx.r3.u32;
  const uint32_t primitive = ctx.r4.u32;
  const uint32_t start_vertex = ctx.r5.u32;
  const uint32_t vertex_count = ctx.r6.u32;
  __imp__sub_82DB1C48(ctx, base);
  wwe::native::OnDrawNonIndexed(base, device, primitive, start_vertex, vertex_count);
}

WWE_COUNTING_HOOK(82DBC4B0, kClearRect)
WWE_COUNTING_HOOK(82DB6118, kDraw14F68)
WWE_COUNTING_HOOK(82DB6270, kDraw150C0)
WWE_COUNTING_HOOK(82DB9828, kDraw1A468)
WWE_COUNTING_HOOK(82DADA00, kDraw21230)
WWE_COUNTING_HOOK(82DC7F88, kDraw29158)
WWE_COUNTING_HOOK(82DCE1A8, kDraw2F338)
WWE_COUNTING_HOOK(82DB7CE0, kIndirectBuffer)
DECLARE_REX_FUNC(sub_82DBF120);
REX_HOOK_RAW(sub_82DBF120) {
  Count(kResolve);
  static uint32_t probe_calls = 0;
  Probe("Resolve", ctx, base, probe_calls);
  const uint32_t device = ctx.r3.u32;
  const uint32_t flags = ctx.r4.u32;
  const uint32_t destination = ctx.r8.u32;
  if (REXCVAR_GET(wwe_d3d_census)) {
    // Distinct argument sets, with the 16 bytes behind each pointer argument.
    static std::mutex mutex;
    static std::unordered_set<uint64_t> seen;
    const uint32_t args[] = {ctx.r4.u32, ctx.r5.u32, ctx.r6.u32, ctx.r7.u32,
                             ctx.r8.u32, ctx.r9.u32, ctx.r10.u32};
    std::string text;
    uint64_t hash = 14695981039346656037ull;
    for (uint32_t i = 0; i < 7; ++i) {
      text += fmt::format(" r{}={:08X}", i + 4, args[i]);
      if (args[i] >= 0x40000000 && args[i] < 0xC0000000 && i != 4) {
        text += " [";
        for (uint32_t j = 0; j < 16; j += 4) {
          const uint32_t v = LoadBE32(base + args[i] + j);
          text += fmt::format(" {:08X}", v);
          hash = (hash ^ v) * 1099511628211ull;
        }
        text += " ]";
      }
      hash = (hash ^ args[i]) * 1099511628211ull;
    }
    std::lock_guard lock(mutex);
    if (seen.size() < 200 && seen.insert(hash).second) {
      REXLOG_INFO("resolve-args{} f1={}", text, ctx.f1.f64);
    }
  }
  __imp__sub_82DBF120(ctx, base);
  wwe::native::OnResolve(base, device, flags, destination);
  wwe::resolve_watch::OnResolve(base, destination, g_presents.load(std::memory_order_relaxed));
}

DECLARE_REX_FUNC(sub_82DB5548);
REX_HOOK_RAW(sub_82DB5548) {
  Count(kSetRenderTarget);
  static uint32_t probe_calls = 0;
  Probe("SetRenderTarget", ctx, base, probe_calls);
  const uint32_t index = ctx.r4.u32;
  const uint32_t surface = ctx.r5.u32;
  __imp__sub_82DB5548(ctx, base);
  wwe::native::OnSetRenderTarget(base, index, surface);
}

// D3DDevice_CreateVertexShader / CreatePixelShader: r3 = the caller's shader
// container (header, then microcode at header size), returns the object. They
// call the register functions below, but for these objects the microcode the
// register hook reads is not the shader's (ticket 29: it held heap data, so
// the rebuilt containers did not convert). The caller's container is the
// shader itself, so it replaces what the register hook recorded.
// They are also the only path for the shaders the game compiles at runtime,
// which the launcher's disc reader cannot put in the pack: a miss goes to the
// shader top-up folder.
std::unique_ptr<wwe::native::ShaderTopUp> g_topup;

void ShaderCreated(const uint8_t* base, uint32_t container, uint32_t object,
                   bool is_pixel_shader) {
  const uint8_t* header = base + container;
  if (!object || !ValidContainerHeader(header)) {
    return;
  }
  const uint32_t size = LoadBE32(header + 4) + LoadBE32(header + 8);
  const std::vector<uint8_t> bytes(header, header + size);
  DumpShaderContainer(bytes, is_pixel_shader ? "ps" : "vs");
  const bool packed =
      wwe::native::shader_library::OnShaderCreated(bytes.data(), object, is_pixel_shader);
  if (g_topup && g_topup->Offer(bytes.data(), bytes.size(), is_pixel_shader, packed)) {
    REXLOG_INFO("shader top-up: wrote {}",
                wwe::native::ShaderTopUp::FileName(bytes.data(), bytes.size(), is_pixel_shader));
  }
  // Converted in the background and used once ready (ticket 22); a file an
  // earlier session wrote is converted too.
  if (!packed && wwe::native::IsEnabled()) {
    wwe::native::topup_converter::Offer(bytes.data(), bytes.size(), is_pixel_shader);
  }
  const uint8_t* object_header = base + object + (is_pixel_shader ? 0x28 : 0x368);
  if (ValidContainerHeader(object_header)) {
    std::lock_guard lock(g_created_mutex);
    g_created_headers[object] = HeaderHash(object_header);
  }
}

DECLARE_REX_FUNC(sub_82DADDE8);
REX_HOOK_RAW(sub_82DADDE8) {
  Count(kCreateVertexShader);
  const uint32_t container = ctx.r3.u32;
  __imp__sub_82DADDE8(ctx, base);
  ShaderCreated(base, container, ctx.r3.u32, false);
}

DECLARE_REX_FUNC(sub_82DADBC0);
REX_HOOK_RAW(sub_82DADBC0) {
  Count(kCreatePixelShader);
  const uint32_t container = ctx.r3.u32;
  __imp__sub_82DADBC0(ctx, base);
  ShaderCreated(base, container, ctx.r3.u32, true);
}

// Vertex shader register: r3 = object, r4 = microcode (stored at object+0x20).
// The header copy sits at object+0x368, after the object's own state.
DECLARE_REX_FUNC(sub_82DADCF0);
REX_HOOK_RAW(sub_82DADCF0) {
  Count(kRegisterVertexShader);
  const uint32_t object = ctx.r3.u32;
  __imp__sub_82DADCF0(ctx, base);
  ShaderRegistered(base, object, 0x368, 0x20, false);
}

// Pixel shader register: r3 = object, r4 = microcode (stored at object+0x18).
// The header copy sits at object+0x28.
DECLARE_REX_FUNC(sub_82DAC148);
REX_HOOK_RAW(sub_82DAC148) {
  Count(kRegisterPixelShader);
  const uint32_t object = ctx.r3.u32;
  __imp__sub_82DAC148(ctx, base);
  ShaderRegistered(base, object, 0x28, 0x18, true);
}

void wwe::ConfigureShaderTopUp(const std::filesystem::path& cache_root) {
  g_topup = std::make_unique<wwe::native::ShaderTopUp>(cache_root);
}

uint64_t wwe::PresentCount() { return g_presents.load(std::memory_order_relaxed); }

// D3DDevice_Present: the only caller of VdSwap, once per frame.
DECLARE_REX_FUNC(sub_82DBDFF8);
REX_HOOK_RAW(sub_82DBDFF8) {
  static uint32_t probe_calls = 0;
  Probe("Present", ctx, base, probe_calls);
#ifdef WWE_D3D_SWEEP
  wwe::sweep::SetDevice(ctx.r3.u32);
#endif
  wwe::crash::CrashIfAsked();
  wwe::native::OnPresent(base, ctx.r3.u32, ctx.r4.u32);
  wwe::native::DumpGuestFrontBuffer(base, ctx.r4.u32,
                                    g_presents.load(std::memory_order_relaxed));
  __imp__sub_82DBDFF8(ctx, base);
  const uint64_t presents = g_presents.fetch_add(1, std::memory_order_relaxed) + 1;
  wwe::frame_clock::OnPresent(presents);
  if (int64_t(presents) - 1 == REXCVAR_GET(wwe_exit_after_frame)) {
    // Frame numbers count presents before this one, as the dumps do.
    REXLOG_INFO("exit after frame {}", presents - 1);
    wwe::xma::LogStats("final");
    wwe::native::OnExit();
    wwe::native::Shutdown();
    rex::FlushLogging();
    std::_Exit(0);
  }
  wwe::resolve_watch::LogPeriodic(presents);
  if (presents % wwe::xma::kLogEveryPresents == 0) {
    wwe::xma::LogStats("periodic");
  }
  if (presents % 60 == 0 && REXCVAR_GET(wwe_d3d_census)) {
    LogCensus(presents);
    if (presents % 600 == 0) LogCallers();
#ifdef WWE_D3D_SWEEP
    if (presents % 600 == 0) LogSweep(presents);
#endif
  }
}
