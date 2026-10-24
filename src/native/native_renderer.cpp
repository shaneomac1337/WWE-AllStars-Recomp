#include "native/native_renderer.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <thread>
#include <unordered_set>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <memory>
#include <unordered_map>
#include <vector>

#define XXH_INLINE_ALL
#include <xxhash.h>

#include <ctime>

#include <fmt/chrono.h>
#include <fmt/format.h>
#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/runtime.h>
#include <rex/system/external_frame.h>
#include <rex/thread.h>
#include <rex/ui/present_pacing.h>
#include <rex/ui/vulkan/provider.h>
#include <rex/ui/vulkan/util.h>
#include <rex/ui/presenter.h>

#include "crash_report.h"
#include "native/debug_labels.h"
#include "native/geometry_cache.h"
#include "native/gpu_timer.h"
#include "native/guest_decode.h"
#include "native/pass_names.h"
#include "native/pipeline_parts.h"
#include "native/pipelines.h"
#include "native/prepare_output.h"
#include "native/texture_layout.h"
#include "frame_capture.h"
#include "native/draw_skips.h"
#include "native/frame_dumps.h"
#include "native/renderer_fallback.h"
#include "native/gpu_failure.h"
#include "native/shader_pack.h"
#include "native/shader_library.h"
#include "native/shader_topup.h"
#include "native/live_topup.h"
#include "native/topup_converter.h"
#include "native/texture_formats.h"
#include "native/textures.h"
#include "native/vk_context.h"
#include "native/write_watch.h"
#include "settings/headless.h"
#include "vibration/mode.h"

REXCVAR_DECLARE(bool, wwe_native_renderer);  // settings_cvars.cpp
REXCVAR_DECLARE(bool, present_pacing_thread_priority);  // SDK presenter.cpp
// Developer measurement (low-end-preset ticket 01); off by default, and no
// query pool exists while it is off.
REXCVAR_DEFINE_STRING(wwe_native_gpu_timer_csv, "", "WWEAllStars",
                      "Native renderer: write GPU ms per frame and per pass, and the renderer's "
                      "CPU ms, to this CSV (empty = off; relative paths start in the exe's folder)");
REXCVAR_DEFINE_INT32(wwe_native_debug_target, 0, "WWEAllStars",
                     "Native renderer debugging: present the Nth render target created (1-based) "
                     "instead of the front buffer");
REXCVAR_DEFINE_STRING(wwe_native_report, "", "WWEAllStars",
                      "Write the session's renderer state and draw skips as JSON to this file "
                      "at exit");
REXCVAR_DEFINE_INT32(wwe_native_dump_frame, -1, "WWEAllStars",
                     "Native renderer debugging: write the presented image of this frame "
                     "number to wwe_native_dump_path (binary PPM)");
REXCVAR_DEFINE_STRING(wwe_native_dump_path, "native-frame.ppm", "WWEAllStars",
                      "Native renderer debugging: file wwe_native_dump_frame writes");
REXCVAR_DEFINE_STRING(wwe_native_dump_frames, "", "WWEAllStars",
                      "Comma-separated guest frame numbers to dump in one run (Emulated: the "
                      "front buffer with --readback_resolve, else the presenter output), each to wwe_native_dump_dir/frame-NNNNNN.ppm");
REXCVAR_DEFINE_STRING(wwe_native_dump_dir, "", "WWEAllStars",
                      "Directory wwe_native_dump_frames writes to (absolute; relative paths start "
                      "in the executable's directory)");
REXCVAR_DEFINE_INT32(wwe_native_trace_frame, -1, "WWEAllStars",
                     "Native renderer debugging: log every event of this frame number");
REXCVAR_DEFINE_UINT32(wwe_native_debug_resolve, 0, "WWEAllStars",
                      "Native renderer debugging: present the resolve target at this physical "
                      "address instead of the front buffer");
REXCVAR_DEFINE_UINT32(wwe_native_debug_null_slots, 0, "WWEAllStars",
                      "Native renderer debugging: bitmask of texture fetch slots bound to the "
                      "default texture");
REXCVAR_DEFINE_BOOL(wwe_native_debug_no_blend, false, "WWEAllStars",
                    "Native renderer debugging: disable blending");
REXCVAR_DEFINE_BOOL(wwe_native_debug_fail_submit, false, "WWEAllStars",
                    "Native renderer debugging: fail the next queue submit with "
                    "VK_ERROR_DEVICE_LOST, which ends the session as a GPU failure");
REXCVAR_DEFINE_UINT64(wwe_native_trace_ps, 0, "WWEAllStars",
                      "Native renderer debugging: in traced frames, dump the constants of draws "
                      "using this pixel shader hash");
REXCVAR_DEFINE_UINT64(wwe_native_debug_skip_ps, 0, "WWEAllStars",
                      "Native renderer debugging: skip draws using this pixel shader hash");
REXCVAR_DEFINE_BOOL(wwe_native_half_pixel_offset, true, "WWEAllStars",
                    "Native renderer: shift geometry by half a pixel for D3D9-style pixel "
                    "centres, like the emulated path's half_pixel_offset");
REXCVAR_DEFINE_BOOL(wwe_native_reuse_constants, true, "WWEAllStars",
                    "Native renderer: upload shader constants only when they differ from the "
                    "previous draw's");
REXCVAR_DEFINE_BOOL(wwe_native_bind_used_slots, true, "WWEAllStars",
                    "Native renderer: bind textures only in the fetch slots the draw's shaders "
                    "read (from the shader pack)");
// Player settings, defined in settings_cvars.cpp.
REXCVAR_DECLARE(double, wwe_native_cas);
REXCVAR_DECLARE(int32_t, wwe_native_resolution_scale);
REXCVAR_DECLARE(bool, wwe_60fps_entrances);
REXCVAR_DECLARE(bool, wwe_120fps_matches);
REXCVAR_DECLARE(bool, wwe_unlock_everything);
REXCVAR_DECLARE(bool, wwe_short_loading_screens);
REXCVAR_DECLARE(int32_t, wwe_native_color_mode);
REXCVAR_DEFINE_STRING(wwe_shader_pack, "", "WWEAllStars",
                      "Native renderer: the shader pack to load (empty: wweallstars.shaderpack in "
                      "the cache_root folder)");
REXCVAR_DEFINE_BOOL(wwe_native_resolve_readback, false, "WWEAllStars",
                    "Native renderer: copy the first resolve of each run of frames back to guest "
                    "memory, so CPU code can read render-to-texture results (off: the copies cost "
                    "100+ ms per entrance frame and no CPU reader was found)");
REXCVAR_DEFINE_BOOL(wwe_native_swap_half2, true, "WWEAllStars",
                    "Native renderer: 16-bit texcoords have their halves swapped after the "
                    "32-bit vertex byte swap");

namespace wwe::native {

// Defined below; any Vulkan call whose failure means the device is gone ends here.
[[noreturn]] void EndSessionWithGpuFailure(VkResult result, const char* call);

namespace {

using namespace guest;
using rex::ui::vulkan::VulkanDevice;
namespace vk_util = rex::ui::vulkan::util;

// The plugin samples a published frame after our submission, so three slots
// keep a frame we are writing apart from the one being presented.
constexpr uint32_t kFramesInFlight = 3;
constexpr VkDeviceSize kUploadRingSize = 64ull << 20;

// Guest D3DDevice layout (SvR 2011's XDK), verified with --wwe_d3d_probe.
constexpr uint32_t kDeviceFetchConstants = 0x480;  // 32 x 6 dwords
constexpr uint32_t kDeviceVertexConstants = 0x780;  // 256 float4
constexpr uint32_t kDevicePixelConstants = 0x1780;  // 256 float4
constexpr uint32_t kDeviceBooleans = 0x2780;        // VS 4 dwords, PS 4 dwords
constexpr uint32_t kDeviceVertexDeclaration = 0x2ED8;
constexpr uint32_t kDeviceStreamFetch = 0x778;  // stream 0; stream s at -8 s
constexpr uint32_t kDeviceIndexBuffer = 0x3144;
constexpr uint32_t kDevicePixelShader = 0x3244;
constexpr uint32_t kDeviceVertexShader = 0x3248;
// Texture objects keep their fetch constant here.
constexpr uint32_t kTextureFetchConstant = 0x1C;
// Surface objects: packed size (DecodeSurfaceSize).
constexpr uint32_t kSurfaceSize = 0x24;

enum Reg : uint32_t {
  RB_SURFACE_INFO = 0x2000,
  RB_COLOR_INFO = 0x2001,
  RB_DEPTH_INFO = 0x2002,
  PA_SC_SCREEN_SCISSOR_TL = 0x200E,
  PA_SC_SCREEN_SCISSOR_BR = 0x200F,
  // D3D keeps the window scissor (SetScissorRect, already clamped to the
  // target) in these mirror slots, at the end of the 0x2000 group.
  PA_SC_WINDOW_SCISSOR_TL = 0x2011,
  PA_SC_WINDOW_SCISSOR_BR = 0x2012,
  RB_COLOR_MASK = 0x2104,
  RB_BLEND_RED = 0x2105,
  RB_STENCILREFMASK_BF = 0x210C,
  RB_STENCILREFMASK = 0x210D,
  RB_ALPHA_REF = 0x210E,
  PA_CL_VPORT_XSCALE = 0x210F,
  PA_CL_VPORT_XOFFSET = 0x2110,
  PA_CL_VPORT_YSCALE = 0x2111,
  PA_CL_VPORT_YOFFSET = 0x2112,
  PA_CL_VPORT_ZSCALE = 0x2113,
  PA_CL_VPORT_ZOFFSET = 0x2114,
  RB_DEPTHCONTROL = 0x2200,
  RB_BLENDCONTROL0 = 0x2201,
  RB_COLORCONTROL = 0x2202,
  PA_SU_SC_MODE_CNTL = 0x2205,
  PA_CL_VTE_CNTL = 0x2206,
  PA_SU_VTX_CNTL = 0x2302,
  RB_COPY_CONTROL = 0x2318,
  RB_COPY_DEST_INFO = 0x231B,
  RB_DEPTH_CLEAR = 0x231D,
  RB_COLOR_CLEAR = 0x231E,
};

bool Tracing();

#define TRACE(...)                                              \
  do {                                                          \
    if (Tracing()) REXLOG_INFO("native trace: " __VA_ARGS__); \
  } while (0)

// --- Vertex declarations ----------------------------------------------------

struct VertexLayout {
  uint32_t attribute_count = 0;
  VertexAttribute attributes[kMaxAttributes] = {};
  uint32_t dummy_locations = 0;  // bitmask of shader locations fed zeros
  uint32_t swapped_texcoords = 0;
  uint32_t stream_mask = 0;
  bool packed_normal = false;
  bool byte_normal = false;
  // Byte strides from the shader's vfetches (layouts built without a D3D
  // declaration); a stream's SetStreamSource stride takes precedence.
  uint32_t fetch_strides[kMaxStreams] = {};
};

struct DeclEntry {
  bool valid = false;
  VertexLayout layout;
};

// Declaration objects (verified): element count at +0x18, D3DVERTEXELEMENT9
// array at +0x34 (12 bytes each, big-endian: stream, offset, type, method,
// usage, usage index).
constexpr uint32_t kDeclarationCount = 0x18;
constexpr uint32_t kDeclarationElements = 0x34;

const DeclEntry* GetDeclaration(const uint8_t* base, uint32_t object) {
  static std::unordered_map<uint64_t, DeclEntry> cache;
  static uint32_t logged = 0;
  if (!object) {
    return nullptr;
  }
  const uint8_t* p = base + object;
  const uint32_t count = std::min<uint32_t>(LoadBE32(p + kDeclarationCount), kMaxAttributes);
  const uint8_t* elements = p + kDeclarationElements;
  const uint64_t key = uint64_t(object) << 32 ^ XXH3_64bits(elements, count * 12);
  auto [it, inserted] = cache.try_emplace(key);
  DeclEntry& entry = it->second;
  if (!inserted) {
    return entry.valid ? &entry : nullptr;
  }
  VertexLayout& layout = entry.layout;
  std::string description;
  for (uint32_t i = 0; i < count; ++i) {
    const uint8_t* e = elements + i * 12;
    const uint16_t stream = LoadBE16(e);
    const uint16_t offset = LoadBE16(e + 2);
    const uint32_t type = LoadBE32(e + 4);
    const uint8_t usage = e[9];
    const uint8_t usage_index = e[10];
    description += fmt::format(" {}.{}@{}+{}:{:X}", usage, usage_index, stream, offset, type);
    const DeclType* decl_type = FindDeclType(type);
    const InputLocation* location = FindLocation(usage, usage_index);
    if (!decl_type || stream >= kMaxStreams) {
      REXLOG_WARN("native renderer: declaration {:08X} element {} has unsupported type {:X} "
                  "(stream {})",
                  object, i, type, stream);
      continue;
    }
    if (!location) {
      continue;
    }
    VertexAttribute& attribute = layout.attributes[layout.attribute_count++];
    attribute.location = location->location;
    attribute.format = location->is_uint ? decl_type->uint_format : decl_type->float_format;
    attribute.offset = offset;
    attribute.stream = stream;
    layout.stream_mask |= 1u << stream;
    if (decl_type->packed_normal && location->is_uint) {
      layout.packed_normal = true;
    }
    if ((usage == 3 || usage == 6 || usage == 7) && (type & 0x3F) == 6) {
      layout.byte_normal = true;
    }
    if (REXCVAR_GET(wwe_native_swap_half2)) {
      layout.swapped_texcoords |= HalfSwapBit(usage, usage_index, decl_type->sixteen_bit,
                                              FourComponent16(type & 0x3F));
    }
  }
  entry.valid = layout.attribute_count > 0;
  for (const InputLocation& l : kInputLocations) {
    bool present = false;
    for (uint32_t i = 0; i < entry.layout.attribute_count; ++i) {
      present |= entry.layout.attributes[i].location == l.location;
    }
    if (!present) {
      entry.layout.dummy_locations |= 1u << l.location;
    }
  }
  if (logged++ < 64) {
    REXLOG_INFO("native renderer: declaration {:08X} (usage.index@stream+offset:type):{}",
                object, description);
  }
  return entry.valid ? &entry : nullptr;
}

// --- Render targets ---------------------------------------------------------

struct RenderTarget {
  VkImage image = VK_NULL_HANDLE;
  VkDeviceMemory memory = VK_NULL_HANDLE;
  VkImageView view = VK_NULL_HANDLE;
  // sRGB view of the same image, for draws through the k_8_8_8_8_GAMMA view of
  // the surface (ticket 39); null for formats without a gamma twin.
  VkImageView gamma_view = VK_NULL_HANDLE;
  VkFormat format = VK_FORMAT_UNDEFINED;
  // Host size: the guest size times the resolution scale.
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t guest_width = 0;
  uint32_t guest_height = 0;
  uint32_t scale = 1;  // host size / guest size (guest::TargetScale)
  VkImageLayout layout = VK_IMAGE_LAYOUT_UNDEFINED;
  bool is_depth = false;
};

constexpr VkFormat kDepthFormat = VK_FORMAT_D32_SFLOAT_S8_UINT;

// --- Renderer state ---------------------------------------------------------

struct FrameSlot {
  VkImage image = VK_NULL_HANDLE;
  VkDeviceMemory memory = VK_NULL_HANDLE;
  VkImageView view = VK_NULL_HANDLE;
  uint32_t width = 0;  // the output image's size (guest::PlanOutput)
  uint32_t height = 0;
  VkCommandPool command_pool = VK_NULL_HANDLE;
  VkCommandBuffer upload_cb = VK_NULL_HANDLE;
  VkCommandBuffer main_cb = VK_NULL_HANDLE;
  VkFence fence = VK_NULL_HANDLE;
};

// Per 300 frames; draw skips are counted for the whole session in g.skips.
struct Stats {
  uint64_t drawn = 0;
  uint64_t drawn_indexed = 0;
  uint64_t drawn_non_indexed = 0;
  uint64_t clears = 0;
  uint64_t resolves = 0;
};

struct State {
  const VulkanDevice* device = nullptr;
  FrameSlot slots[kFramesInFlight];
  // Render size: the guest front buffer times the render resolution scale.
  // The output (each slot's image) is that, or the window's 16:9 area when
  // the window is smaller (guest::PlanOutput).
  uint32_t scale = 1;
  ColourMode colour = ColourMode::kNatural;  // --wwe_native_color_mode, an Enhancement
  uint32_t width = 1280;
  uint32_t height = 720;
  // 2:1 steps of the downsample, largest first; kept while the sizes match.
  struct HalfImage {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    uint32_t width = 0;
    uint32_t height = 0;
  };
  std::vector<HalfImage> halves;
  uint64_t frame = 0;
  bool recording = false;
  // Blocks of this frame's upload ring that later draws reuse while their
  // contents do not change (reset with the ring each frame).
  struct ReusedBlock {
    uint8_t last[4096];
    VkDeviceAddress address = 0;
  };
  ReusedBlock vertex_constants, pixel_constants;
  SharedConstants last_shared = {};
  VkDeviceAddress shared_address = 0;
  PushConstants last_push = {};
  Upload zeros;
  FrameSlot* slot = nullptr;

  std::unordered_map<uint64_t, std::unique_ptr<RenderTarget>> render_targets;
  bool rendering = false;
  RenderTarget* active_color = nullptr;
  bool active_gamma = false;  // rendering through active_color's gamma_view
  RenderTarget* active_depth = nullptr;
  // The depth target last rendered with, for depth resolves whose registers
  // name no target the renderer knows.
  RenderTarget* last_depth = nullptr;
  VkPipeline bound_pipeline = VK_NULL_HANDLE;
  // The bound pipeline takes strides, culling and depth/stencil as dynamic
  // state (the library path), and the values last set while one was bound.
  bool bound_dynamic = false;
  bool dynamic_set = false;
  parts::DynamicState last_dynamic = {};
  // Size of the surfaces set with SetRenderTarget, per index.
  uint32_t surface_width[4] = {1280, 1280, 1280, 1280};
  uint32_t surface_height[4] = {720, 720, 720, 720};

  uint32_t stream_strides[kMaxStreams] = {};
  // Resolve destinations: frame of the last resolve and the start of the
  // current run of consecutive frames.
  struct ResolveRun {
    uint64_t last_frame = 0;
    uint64_t run_start = 0;
  };
  std::unordered_map<uint32_t, ResolveRun> resolve_runs;
  struct PendingReadback {
    VkBuffer buffer = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    textures::FetchConstant fetch;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t scale = 1;  // the resolve target's host scale
    uint32_t bytes_per_pixel = 0;
    bool average_bytes = false;
    bool swap_red_blue = false;  // 8888 resolve the guest stores as ARGB
    uint64_t frame = 0;
  };
  std::vector<PendingReadback> pending_readbacks;
  // Staging for depth resolves (depth aspect -> buffer -> R32 image).
  VkBuffer depth_staging = VK_NULL_HANDLE;
  VkDeviceMemory depth_staging_memory = VK_NULL_HANDLE;
  VkDeviceSize depth_staging_size = 0;
  uint64_t readbacks_delivered = 0;
  std::vector<RenderTarget*> render_target_order;
  std::vector<uint8_t> readback_scratch;
  Stats stats;
  // Written on the render thread; OnExit may read it from the UI thread.
  std::mutex skips_mutex;
  DrawSkips skips;
  Fallback fallback = Fallback::kNone;
  // The VkResult of a GPU failure, for the session report; 0 if none.
  std::atomic<int32_t> gpu_failure{0};
  // What the current frame spent its time on, logged when it runs long.
  struct Hitch {
    std::chrono::steady_clock::time_point last_present;
    uint64_t pending = 0;
    uint32_t render_targets = 0;
    uint64_t renderer_ns = 0;    // in the hooks below, fence waits included
    uint64_t fence_wait_ns = 0;  // waiting for the GPU to free a frame slot
    uint32_t readbacks_queued = 0;
    uint64_t readback_queue_ns = 0;    // buffer allocation and copy commands
    uint32_t readbacks_delivered = 0;
    uint64_t readback_bytes = 0;
    uint64_t readback_deliver_ns = 0;  // map, convert and write into guest memory
  } hitch;
  // Since the last statistics line.
  uint64_t readback_ns_total = 0;
  // Session totals of compiles on the render thread, for the smoothness
  // report's first-run bar; logged when they change.
  uint64_t render_thread_pipelines = 0;
  uint64_t render_thread_waits = 0;  // pipelines it waited for a worker to build
  uint64_t logged_pipelines = 0;
  uint64_t logged_modules = 0;
  uint64_t logged_waits = 0;
} g;

// The GPU timer (--wwe_native_gpu_timer_csv): a timestamp query pool per frame
// slot, read once the slot's fence has signalled, where the renderer waits
// anyway, so reading adds no stall.
struct GpuTimer {
  std::FILE* csv = nullptr;
  bool timestamps = false;  // the graphics queue writes timestamps
  double period_ns = 1;
  uint32_t valid_bits = 64;
  static constexpr uint32_t kQueries = 1024;  // per frame: start, end, then a pair per span
  VkQueryPool pools[kFramesInFlight] = {};
  gpu_timer::FrameQueries frames[kFramesInFlight];
  bool submitted[kFramesInFlight] = {};  // the slot holds a frame whose rows are not written
  std::vector<uint64_t> results;
  PFN_vkCmdWriteTimestamp vkCmdWriteTimestamp = nullptr;
  PFN_vkGetQueryPoolResults vkGetQueryPoolResults = nullptr;
} g_timer;

// Labels on passes, resolves and the present, and names on the main images,
// when the instance has VK_EXT_debug_utils (low-end-preset ticket 04).
DebugLabels g_labels;

int64_t g_trace_frame = -1;

uint64_t NsSince(std::chrono::steady_clock::time_point start) {
  return uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(
                      std::chrono::steady_clock::now() - start)
                      .count());
}

// Adds the time a hook spends to the frame's renderer time.
struct HookTimer {
  std::chrono::steady_clock::time_point start = std::chrono::steady_clock::now();
  ~HookTimer() { g.hitch.renderer_ns += NsSince(start); }
};

bool Tracing() {
  return int64_t(g.frame) == int64_t(REXCVAR_GET(wwe_native_trace_frame)) ||
         int64_t(g.frame) == g_trace_frame;
}

// Debugging: creating native_trace_request next to the executable traces the
// next frame (checked twice a second).
void PollTraceRequest() {
  if (g.frame % 30 != 0) {
    return;
  }
  std::error_code error;
  if (std::filesystem::remove("native_trace_request", error)) {
    g_trace_frame = int64_t(g.frame) + 1;
    REXLOG_INFO("native renderer: tracing frame {}", g_trace_frame);
  }
}

void ImageBarrier(VkCommandBuffer cb, VkImage image, VkImageAspectFlags aspect,
                  VkImageLayout old_layout, VkImageLayout new_layout) {
  // Conservative full barrier; the frame has few layout changes.
  VkImageMemoryBarrier barrier = {VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
  barrier.srcAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
  barrier.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
  barrier.oldLayout = old_layout;
  barrier.newLayout = new_layout;
  barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
  barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
  barrier.image = image;
  barrier.subresourceRange = vk_util::InitializeSubresourceRange(aspect);
  g_vk.dfn->vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                                 VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 1,
                                 &barrier);
}

void TransitionTarget(RenderTarget& target, VkImageLayout layout) {
  if (target.layout == layout) {
    return;
  }
  ImageBarrier(g.slot->main_cb, target.image,
               target.is_depth ? VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT
                               : VK_IMAGE_ASPECT_COLOR_BIT,
               target.layout, layout);
  target.layout = layout;
}

// A transfer image for the output or a downsample step.
bool CreateTransferImage(uint32_t width, uint32_t height, VkImageUsageFlags usage,
                         VkImage& image, VkDeviceMemory& memory) {
  VkImageCreateInfo image_info = {VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
  image_info.imageType = VK_IMAGE_TYPE_2D;
  image_info.format = VK_FORMAT_R8G8B8A8_UNORM;
  image_info.extent = {width, height, 1};
  image_info.mipLevels = 1;
  image_info.arrayLayers = 1;
  image_info.samples = VK_SAMPLE_COUNT_1_BIT;
  image_info.tiling = VK_IMAGE_TILING_OPTIMAL;
  image_info.usage = usage;
  image_info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
  image_info.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
  return vk_util::CreateDedicatedAllocationImage(g.device, image_info,
                                                 vk_util::MemoryPurpose::kDeviceLocal, image,
                                                 memory);
}

// The slot's output image at width x height, the old one retired.
bool CreateOutputImage(FrameSlot& slot, uint32_t width, uint32_t height) {
  if (slot.image) {
    Retired retired;
    retired.image = slot.image;
    retired.memory = slot.memory;
    retired.views.push_back(slot.view);
    Retire(std::move(retired));
    slot.image = VK_NULL_HANDLE;
    slot.memory = VK_NULL_HANDLE;
    slot.view = VK_NULL_HANDLE;
    slot.width = slot.height = 0;
  }
  if (!CreateTransferImage(width, height,
                           VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT |
                               VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                               VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
                           slot.image, slot.memory)) {
    return false;
  }
  VkImageViewCreateInfo view_info = {VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
  view_info.image = slot.image;
  view_info.viewType = VK_IMAGE_VIEW_TYPE_2D;
  view_info.format = VK_FORMAT_R8G8B8A8_UNORM;
  view_info.subresourceRange = vk_util::InitializeSubresourceRange();
  if (g_vk.dfn->vkCreateImageView(g_vk.vk_device, &view_info, nullptr, &slot.view) !=
      VK_SUCCESS) {
    return false;
  }
  slot.width = width;
  slot.height = height;
  g_labels.Name(g_vk.vk_device, VK_OBJECT_TYPE_IMAGE, uint64_t(slot.image),
                fmt::format("native output {}x{}", width, height));
  return true;
}

bool CreateFrameSlot(FrameSlot& slot) {
  const VulkanDevice::Functions& dfn = *g_vk.dfn;
  const VkDevice device = g_vk.vk_device;
  if (!CreateOutputImage(slot, g.width, g.height)) {
    return false;
  }

  // One pool per slot, reset whole once the slot's fence has signalled.
  VkCommandPoolCreateInfo pool_info = {VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
  pool_info.flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT;
  pool_info.queueFamilyIndex = g.device->queue_family_graphics_compute();
  if (dfn.vkCreateCommandPool(device, &pool_info, nullptr, &slot.command_pool) != VK_SUCCESS) {
    return false;
  }
  VkCommandBuffer command_buffers[2];
  VkCommandBufferAllocateInfo allocate_info = {VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
  allocate_info.commandPool = slot.command_pool;
  allocate_info.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
  allocate_info.commandBufferCount = 2;
  if (dfn.vkAllocateCommandBuffers(device, &allocate_info, command_buffers) != VK_SUCCESS) {
    return false;
  }
  slot.upload_cb = command_buffers[0];
  slot.main_cb = command_buffers[1];

  VkFenceCreateInfo fence_info = {VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
  fence_info.flags = VK_FENCE_CREATE_SIGNALED_BIT;
  return dfn.vkCreateFence(device, &fence_info, nullptr, &slot.fence) == VK_SUCCESS;
}

// Writes resolve copies whose frame the GPU has finished into guest memory,
// in submission order, so the game's CPU code sees render-to-texture results
// (created attire bakes read them back). Never waits.
void DeliverReadbacks() {
  const VulkanDevice::Functions& dfn = *g_vk.dfn;
  const auto start = std::chrono::steady_clock::now();
  size_t delivered = 0;
  for (const State::PendingReadback& p : g.pending_readbacks) {
    // A slot's fence is only reset when the slot records again, three frames
    // on, so until then its status is that of frame p.frame.
    if (g.frame < p.frame + kFramesInFlight) {
      const VkResult status =
          dfn.vkGetFenceStatus(g_vk.vk_device, g.slots[p.frame % kFramesInFlight].fence);
      if (status < 0) {
        EndSessionWithGpuFailure(status, "vkGetFenceStatus");
      }
      if (status != VK_SUCCESS) {
        break;
      }
    }
    void* mapped = nullptr;
    if (dfn.vkMapMemory(g_vk.vk_device, p.memory, 0, VK_WHOLE_SIZE, 0, &mapped) == VK_SUCCESS) {
      VkMappedMemoryRange range = {VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE};
      range.memory = p.memory;
      range.size = VK_WHOLE_SIZE;
      dfn.vkInvalidateMappedMemoryRanges(g_vk.vk_device, 1, &range);
      auto* pixels = static_cast<uint8_t*>(mapped);
      if (p.swap_red_blue && p.bytes_per_pixel == 4) {
        // The copy is RGBA; the guest's resolve stored red and blue swapped.
        for (size_t i = 0; i + 3 < size_t(p.width) * p.height * 4; i += 4) {
          std::swap(pixels[i], pixels[i + 2]);
        }
      }
      if (p.scale > 1) {
        // Scaled resolve targets go back to guest memory at guest size.
        std::vector<uint8_t>& guest_pixels = g.readback_scratch;
        guest_pixels.resize(size_t(p.width / p.scale) * (p.height / p.scale) * p.bytes_per_pixel);
        texture_layout::Downsample(pixels, p.width, p.height, p.scale, p.bytes_per_pixel,
                                   p.average_bytes, guest_pixels.data());
        textures::WriteToGuest(p.fetch, guest_pixels.data(), p.width / p.scale,
                               p.height / p.scale);
      } else {
        textures::WriteToGuest(p.fetch, pixels, p.width, p.height);
      }
      dfn.vkUnmapMemory(g_vk.vk_device, p.memory);
    }
    dfn.vkDestroyBuffer(g_vk.vk_device, p.buffer, nullptr);
    dfn.vkFreeMemory(g_vk.vk_device, p.memory, nullptr);
    g.hitch.readback_bytes += uint64_t(p.width) * p.height * p.bytes_per_pixel;
    ++delivered;
  }
  g.pending_readbacks.erase(g.pending_readbacks.begin(),
                            g.pending_readbacks.begin() + delivered);
  g.readbacks_delivered += delivered;
  if (delivered) {
    const uint64_t ns = NsSince(start);
    g.hitch.readbacks_delivered += uint32_t(delivered);
    g.hitch.readback_deliver_ns += ns;
    g.readback_ns_total += ns;
  }
}

// Copies the first resolve in a run of consecutive frames to a readback
// buffer (burst readback): one-off render-to-texture results reach guest
// memory, per-frame targets cost nothing after their first frame.
void QueueReadback(const textures::FetchConstant& fetch, const textures::ResolveTarget& target,
                   VkImageLayout& layout) {
  const uint32_t address = fetch.dwords[1] & 0xFFFFF000;
  State::ResolveRun& run = g.resolve_runs[address];
  if (run.last_frame + 1 < g.frame || run.last_frame == 0) {
    run.run_start = g.frame;
  }
  run.last_frame = g.frame;
  const uint32_t bytes_per_pixel = textures::GuestBytesPerPixel(fetch);
  if (run.run_start != g.frame || !bytes_per_pixel || !REXCVAR_GET(wwe_native_resolve_readback)) {
    return;
  }
  const auto start = std::chrono::steady_clock::now();
  State::PendingReadback pending;
  pending.fetch = fetch;
  pending.width = target.width;
  pending.height = target.height;
  pending.scale = target.scale;
  pending.bytes_per_pixel = bytes_per_pixel;
  pending.average_bytes = textures::BytewiseUnorm(fetch);
  pending.swap_red_blue = textures::BytewiseUnorm(fetch) && textures::ResolvesRedBlueSwapped(fetch);
  pending.frame = g.frame;
  if (!vk_util::CreateDedicatedAllocationBuffer(
          g.device, VkDeviceSize(target.width) * target.height * bytes_per_pixel,
          VK_BUFFER_USAGE_TRANSFER_DST_BIT, vk_util::MemoryPurpose::kReadback, pending.buffer,
          pending.memory)) {
    return;
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  ImageBarrier(cb, target.image, VK_IMAGE_ASPECT_COLOR_BIT, layout,
               VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
  layout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
  VkBufferImageCopy region = {};
  region.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
  region.imageExtent = {target.width, target.height, 1};
  g_vk.dfn->vkCmdCopyImageToBuffer(cb, target.image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                   pending.buffer, 1, &region);
  g.pending_readbacks.push_back(pending);
  const uint64_t ns = NsSince(start);
  ++g.hitch.readbacks_queued;
  g.hitch.readback_queue_ns += ns;
  g.readback_ns_total += ns;
}

// --- GPU timer ----------------------------------------------------------------

void InitializeGpuTimer() {
  const std::string path = REXCVAR_GET(wwe_native_gpu_timer_csv);
  if (path.empty()) {
    return;
  }
  g_timer.csv = std::fopen(path.c_str(), "w");
  if (!g_timer.csv) {
    REXLOG_WARN("native renderer: cannot write GPU timer CSV {}", path);
    return;
  }
  std::fputs(gpu_timer::CsvHeader().c_str(), g_timer.csv);
  const auto& ifn = g.device->vulkan_instance()->functions();
  VkPhysicalDeviceProperties properties = {};
  ifn.vkGetPhysicalDeviceProperties(g.device->physical_device(), &properties);
  uint32_t family_count = 0;
  ifn.vkGetPhysicalDeviceQueueFamilyProperties(g.device->physical_device(), &family_count, nullptr);
  std::vector<VkQueueFamilyProperties> families(family_count);
  ifn.vkGetPhysicalDeviceQueueFamilyProperties(g.device->physical_device(), &family_count,
                                               families.data());
  const uint32_t family = g.device->queue_family_graphics_compute();
  g_timer.valid_bits = family < families.size() ? families[family].timestampValidBits : 0;
  g_timer.period_ns = properties.limits.timestampPeriod;
  const auto get_proc = ifn.vkGetDeviceProcAddr;
  g_timer.vkCmdWriteTimestamp = reinterpret_cast<PFN_vkCmdWriteTimestamp>(
      get_proc(g_vk.vk_device, "vkCmdWriteTimestamp"));
  g_timer.vkGetQueryPoolResults = reinterpret_cast<PFN_vkGetQueryPoolResults>(
      get_proc(g_vk.vk_device, "vkGetQueryPoolResults"));
  g_timer.timestamps = g_timer.valid_bits > 0 && g_timer.period_ns > 0 &&
                       g_timer.vkCmdWriteTimestamp && g_timer.vkGetQueryPoolResults;
  for (uint32_t i = 0; g_timer.timestamps && i < kFramesInFlight; ++i) {
    VkQueryPoolCreateInfo pool_info = {VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO};
    pool_info.queryType = VK_QUERY_TYPE_TIMESTAMP;
    pool_info.queryCount = GpuTimer::kQueries;
    g_timer.timestamps =
        g_vk.dfn->vkCreateQueryPool(g_vk.vk_device, &pool_info, nullptr, &g_timer.pools[i]) ==
        VK_SUCCESS;
  }
  for (auto& frame : g_timer.frames) {
    frame = gpu_timer::FrameQueries(g_timer.timestamps ? GpuTimer::kQueries : 0);
  }
  REXLOG_INFO("native renderer: GPU timer CSV {} ({})", path,
              g_timer.timestamps
                  ? fmt::format("{} timestamp bits, {} ns per tick", g_timer.valid_bits,
                                g_timer.period_ns)
                  : std::string("no timestamps on the graphics queue: CPU only"));
}

// After the slot's fence: writes the rows of the frame it held, then starts
// the new frame's queries at the front of the upload command buffer, which is
// submitted first.
void BeginGpuTimerFrame(uint32_t slot_index, VkCommandBuffer upload_cb) {
  if (!g_timer.csv) {
    return;
  }
  gpu_timer::FrameQueries& frame = g_timer.frames[slot_index];
  if (g_timer.submitted[slot_index]) {
    std::span<const uint64_t> stamps;
    if (frame.timed()) {
      g_timer.results.resize(frame.used());
      if (g_timer.vkGetQueryPoolResults(g_vk.vk_device, g_timer.pools[slot_index], 0, frame.used(),
                                        g_timer.results.size() * sizeof(uint64_t),
                                        g_timer.results.data(), sizeof(uint64_t),
                                        VK_QUERY_RESULT_64_BIT) == VK_SUCCESS) {
        stamps = g_timer.results;
      }
    }
    std::fputs(gpu_timer::CsvRows(frame, stamps, g_timer.period_ns, g_timer.valid_bits).c_str(),
               g_timer.csv);
    g_timer.submitted[slot_index] = false;
  }
  frame.Reset(g.frame);
  if (frame.timed()) {
    g_vk.dfn->vkCmdResetQueryPool(upload_cb, g_timer.pools[slot_index], 0, GpuTimer::kQueries);
    g_timer.vkCmdWriteTimestamp(upload_cb, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                                g_timer.pools[slot_index], gpu_timer::FrameQueries::kFrameBegin);
  }
}

// Spans stamp when all earlier work has finished (bottom of pipe), so they
// never overlap and their sum stays within the frame.
bool GpuTimerOn() { return g_timer.csv && g.slot; }

void BeginGpuSpan(gpu_timer::Span span, std::string label) {
  if (!GpuTimerOn()) return;
  const uint32_t slot_index = uint32_t(g.slot - g.slots);
  if (const auto query = g_timer.frames[slot_index].Begin(span, std::move(label))) {
    g_timer.vkCmdWriteTimestamp(g.slot->main_cb, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                                g_timer.pools[slot_index], *query);
  }
}

void EndGpuSpan() {
  if (!GpuTimerOn()) return;
  const uint32_t slot_index = uint32_t(g.slot - g.slots);
  if (const auto query = g_timer.frames[slot_index].End()) {
    g_timer.vkCmdWriteTimestamp(g.slot->main_cb, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                                g_timer.pools[slot_index], *query);
  }
}

// At the end of the frame's last command buffer.
void EndGpuTimerFrame() {
  if (!GpuTimerOn()) return;
  const uint32_t slot_index = uint32_t(g.slot - g.slots);
  if (g_timer.frames[slot_index].timed()) {
    g_timer.vkCmdWriteTimestamp(g.slot->main_cb, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                                g_timer.pools[slot_index], gpu_timer::FrameQueries::kFrameEnd);
  }
}

// Once the frame is submitted, with the renderer's CPU ms the hitch timer measured.
void GpuTimerFrameSubmitted(uint32_t slot_index, double cpu_ms) {
  if (!g_timer.csv) return;
  g_timer.frames[slot_index].set_cpu_ms(cpu_ms);
  // Local time with milliseconds, as the runtime log and a capture's marks write it.
  const auto now = std::chrono::system_clock::now();
  const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;
  const std::time_t seconds = std::chrono::system_clock::to_time_t(now);
  std::tm local = {};
#ifdef _WIN32
  localtime_s(&local, &seconds);
#else
  localtime_r(&seconds, &local);
#endif
  g_timer.frames[slot_index].set_time(fmt::format("{:%Y-%m-%d %H:%M:%S}.{:03}", local, ms.count()));
  g_timer.submitted[slot_index] = true;
  if (g.frame % 60 == 0) {
    std::fflush(g_timer.csv);
  }
}

void InitializeDebugLabels() {
  const auto& instance = *g.device->vulkan_instance();
  if (!instance.extensions().ext_EXT_debug_utils) {
    return;
  }
  const auto get_proc = instance.functions().vkGetInstanceProcAddr;
  g_labels = DebugLabels({reinterpret_cast<PFN_vkCmdBeginDebugUtilsLabelEXT>(
                              get_proc(instance.instance(), "vkCmdBeginDebugUtilsLabelEXT")),
                          reinterpret_cast<PFN_vkCmdEndDebugUtilsLabelEXT>(
                              get_proc(instance.instance(), "vkCmdEndDebugUtilsLabelEXT")),
                          instance.functions().vkSetDebugUtilsObjectNameEXT});
  REXLOG_INFO("native renderer: debug labels {}", g_labels.enabled() ? "on" : "unavailable");
}

// Whether passes, resolves and the present need a name (the GPU timer's CSV
// or a debug label), so nothing is formatted otherwise.
bool NamingWork() { return (g_timer.csv || g_labels.enabled()) && g.slot; }

// One stretch of GPU work: a debug label and a timer span around it.
void BeginWork(gpu_timer::Span span, std::string name) {
  if (!g.slot) return;
  g_labels.Begin(g.slot->main_cb, name);
  BeginGpuSpan(span, std::move(name));
}

void EndWork() {
  if (!g.slot) return;
  EndGpuSpan();
  g_labels.End(g.slot->main_cb);
}

void CloseGpuTimer() {
  if (g_timer.csv) {
    std::fclose(g_timer.csv);
    g_timer.csv = nullptr;
  }
}

void SkipDraw(Skip reason, uint64_t key) {
  std::lock_guard lock(g.skips_mutex);
  g.skips.Count(reason, g.frame, key);
}

void BeginFrameIfNeeded() {
  if (g.recording) {
    return;
  }
  const VulkanDevice::Functions& dfn = *g_vk.dfn;
  const uint32_t slot_index = uint32_t(g.frame % kFramesInFlight);
  FrameSlot& slot = g.slots[slot_index];
  const auto wait_start = std::chrono::steady_clock::now();
  const VkResult waited = dfn.vkWaitForFences(g_vk.vk_device, 1, &slot.fence, VK_TRUE, UINT64_MAX);
  if (waited < 0) {
    EndSessionWithGpuFailure(waited, "vkWaitForFences");
  }
  g.hitch.fence_wait_ns += NsSince(wait_start);
  // After the wait: readbacks of the slot's previous frame are complete.
  DeliverReadbacks();
  dfn.vkResetFences(g_vk.vk_device, 1, &slot.fence);
  // The slot's previous frame, and every frame before it, has completed.
  g_vk.frame = g.frame;
  if (g.frame >= kFramesInFlight) {
    DestroyRetired(g.frame - kFramesInFlight);
  }
  textures::BeginFrame(g.frame);
  if (g.frame % 300 == 0) {
    textures::EvictUnused(g.frame);
  }
  dfn.vkResetCommandPool(g_vk.vk_device, slot.command_pool, 0);
  VkCommandBufferBeginInfo begin_info = {VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
  begin_info.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
  dfn.vkBeginCommandBuffer(slot.upload_cb, &begin_info);
  dfn.vkBeginCommandBuffer(slot.main_cb, &begin_info);
  BeginGpuTimerFrame(slot_index, slot.upload_cb);
  BeginUploadFrame(slot_index);
  g.vertex_constants.address = g.pixel_constants.address = g.shared_address = 0;
  g.last_push = {};
  g.zeros = {};
  BindDescriptorHeaps(slot.main_cb);
  g.slot = &slot;
  g.recording = true;
  g.bound_pipeline = VK_NULL_HANDLE;
  g.dynamic_set = false;
}

void EndRendering() {
  if (g.rendering) {
    g_vk.dfn->vkCmdEndRendering(g.slot->main_cb);
    g.rendering = false;
    EndWork();
  }
}

// width and height are the guest size.
RenderTarget* GetRenderTarget(uint64_t key, VkFormat format, uint32_t guest_width,
                              uint32_t guest_height, bool is_depth) {
  const uint32_t scale = g.scale;
  const uint32_t width = guest_width * scale;
  const uint32_t height = guest_height * scale;
  std::unique_ptr<RenderTarget>& slot = g.render_targets[key];
  // A surface reused at another scale is a new image.
  if (slot && (slot->width < width || slot->height < height || slot->scale != scale)) {
    // Grows in place of the old one, which frames in flight may still use.
    if (g.active_color == slot.get() || g.active_depth == slot.get()) {
      EndRendering();
      g.active_color = g.active_depth = nullptr;
    }
    if (g.last_depth == slot.get()) {
      g.last_depth = nullptr;
    }
    std::erase(g.render_target_order, slot.get());
    Retired retired;
    retired.image = slot->image;
    retired.memory = slot->memory;
    retired.views.push_back(slot->view);
    if (slot->gamma_view) {
      retired.views.push_back(slot->gamma_view);
    }
    Retire(std::move(retired));
    slot.reset();
  }
  if (slot) {
    return slot.get();
  }
  auto target = std::make_unique<RenderTarget>();
  VkImageCreateInfo image_info = {VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
  image_info.imageType = VK_IMAGE_TYPE_2D;
  image_info.format = format;
  image_info.extent = {width, height, 1};
  image_info.mipLevels = 1;
  image_info.arrayLayers = 1;
  image_info.samples = VK_SAMPLE_COUNT_1_BIT;
  image_info.tiling = VK_IMAGE_TILING_OPTIMAL;
  image_info.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT |
                     VK_IMAGE_USAGE_SAMPLED_BIT |
                     (is_depth ? VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT
                               : VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT);
  image_info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
  image_info.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
  // A colour surface with a gamma twin can be drawn through either view.
  const VkFormat gamma_format = is_depth ? VK_FORMAT_UNDEFINED : GammaFormat(format);
  const VkFormat view_formats[2] = {format, gamma_format};
  VkImageFormatListCreateInfo format_list = {VK_STRUCTURE_TYPE_IMAGE_FORMAT_LIST_CREATE_INFO};
  if (gamma_format != VK_FORMAT_UNDEFINED) {
    image_info.flags |= VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
    format_list.viewFormatCount = 2;
    format_list.pViewFormats = view_formats;
    image_info.pNext = &format_list;
  }
  if (!vk_util::CreateDedicatedAllocationImage(g.device, image_info,
                                               vk_util::MemoryPurpose::kDeviceLocal,
                                               target->image, target->memory)) {
    REXLOG_ERROR("native renderer: could not create a {}x{} render target", width, height);
    g.render_targets.erase(key);
    return nullptr;
  }
  VkImageViewCreateInfo view_info = {VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
  view_info.image = target->image;
  view_info.viewType = VK_IMAGE_VIEW_TYPE_2D;
  view_info.format = format;
  view_info.subresourceRange = vk_util::InitializeSubresourceRange(
      is_depth ? VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT
               : VK_IMAGE_ASPECT_COLOR_BIT);
  g_vk.dfn->vkCreateImageView(g_vk.vk_device, &view_info, nullptr, &target->view);
  if (gamma_format != VK_FORMAT_UNDEFINED) {
    view_info.format = gamma_format;
    g_vk.dfn->vkCreateImageView(g_vk.vk_device, &view_info, nullptr, &target->gamma_view);
  }
  target->format = format;
  target->width = width;
  target->height = height;
  target->guest_width = guest_width;
  target->guest_height = guest_height;
  target->scale = scale;
  target->is_depth = is_depth;
  g_labels.Name(g_vk.vk_device, VK_OBJECT_TYPE_IMAGE, uint64_t(target->image),
                fmt::format("{} target {}x{} {}", is_depth ? "depth" : "colour", width, height,
                            FormatName(format)));
  slot = std::move(target);
  g.render_target_order.push_back(slot.get());
  ++g.hitch.render_targets;
  REXLOG_INFO("native renderer: {} render target {}x{} format {} (key {:X})",
              is_depth ? "depth" : "colour", width, height, uint32_t(format), key);
  return slot.get();
}

RenderTarget* CurrentColorTarget(const uint8_t* d3d, bool create) {
  const uint32_t surface_info = ReadReg(d3d, RB_SURFACE_INFO);
  const uint32_t color_info = ReadReg(d3d, RB_COLOR_INFO);
  const uint32_t pitch = surface_info & 0x3FFF;
  const uint64_t key = ColorTargetKey(surface_info, color_info);
  const uint32_t width = pitch ? pitch : g.surface_width[0];
  const uint32_t height = g.surface_height[0];
  if (!create) {
    const auto it = g.render_targets.find(key);
    return it != g.render_targets.end() ? it->second.get() : nullptr;
  }
  return GetRenderTarget(key, ColorTargetFormat((color_info >> 16) & 0xF), width, height, false);
}

RenderTarget* CurrentDepthTarget(const uint8_t* d3d, uint32_t width, uint32_t height) {
  const uint32_t surface_info = ReadReg(d3d, RB_SURFACE_INFO);
  const uint32_t depth_info = ReadReg(d3d, RB_DEPTH_INFO);
  const uint64_t key =
      uint64_t(depth_info & 0x10FFF) | uint64_t(surface_info & 0x3FFF) << 32 | 1ull << 63;
  return GetRenderTarget(key, kDepthFormat, width, height, true);
}

// Begins dynamic rendering into the guest's current render targets unless it
// is already active with them.
bool EnsureRendering(const uint8_t* d3d, bool need_depth) {
  RenderTarget* color = CurrentColorTarget(d3d, true);
  if (!color) {
    return false;
  }
  RenderTarget* depth =
      need_depth ? CurrentDepthTarget(d3d, color->guest_width, color->guest_height) : nullptr;
  // Draws through the k_8_8_8_8_GAMMA view render into the sRGB view, which
  // blends in linear and encodes on write as Xenos does.
  const bool gamma =
      color->gamma_view && ColorTargetIsGamma((ReadReg(d3d, RB_COLOR_INFO) >> 16) & 0xF);
  if (g.rendering && g.active_color == color && g.active_gamma == gamma &&
      (!need_depth || g.active_depth == depth)) {
    return true;
  }
  EndRendering();
  TransitionTarget(*color, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
  if (depth) {
    TransitionTarget(*depth, VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL);
  }
  VkRenderingAttachmentInfo color_attachment = {VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO};
  color_attachment.imageView = gamma ? color->gamma_view : color->view;
  color_attachment.imageLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
  color_attachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
  color_attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
  VkRenderingAttachmentInfo depth_attachment = {VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO};
  if (depth) {
    depth_attachment.imageView = depth->view;
    depth_attachment.imageLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    depth_attachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    depth_attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
  }
  VkRenderingInfo rendering_info = {VK_STRUCTURE_TYPE_RENDERING_INFO};
  rendering_info.renderArea = {{0, 0}, {color->width, color->height}};
  rendering_info.layerCount = 1;
  rendering_info.colorAttachmentCount = 1;
  rendering_info.pColorAttachments = &color_attachment;
  rendering_info.pDepthAttachment = depth ? &depth_attachment : nullptr;
  rendering_info.pStencilAttachment = depth ? &depth_attachment : nullptr;
  if (NamingWork()) {
    BeginWork(gpu_timer::Span::kPass,
              PassName({color->width, color->height, color->format, gamma, depth != nullptr}));
  }
  g_vk.dfn->vkCmdBeginRendering(g.slot->main_cb, &rendering_info);
  g.rendering = true;
  g.active_color = color;
  g.active_gamma = gamma;
  g.active_depth = depth;
  if (depth) {
    g.last_depth = depth;
  }
  TRACE("begin rendering colour {}x{} fmt {} info {:08X} surface {:08X} depth {}", color->width,
        color->height, uint32_t(color->format), ReadReg(d3d, RB_COLOR_INFO),
        ReadReg(d3d, RB_SURFACE_INFO), depth ? "yes" : "no");
  g.bound_pipeline = VK_NULL_HANDLE;
  return true;
}

// The draw's clip transform (guest_decode.h) for the active target. Pixel
// centres at .0 (PA_SU_VTX_CNTL pix_center 0, as in D3D9) shift geometry by
// half a pixel right and down, as the emulated path does.
ClipTransform DrawClipTransform(const uint8_t* d3d) {
  const bool half_pixel =
      REXCVAR_GET(wwe_native_half_pixel_offset) && !(ReadReg(d3d, PA_SU_VTX_CNTL) & 1);
  return ComputeClipTransform(
      ReadReg(d3d, PA_CL_VTE_CNTL), ReadRegFloat(d3d, PA_CL_VPORT_XSCALE),
      ReadRegFloat(d3d, PA_CL_VPORT_XOFFSET), ReadRegFloat(d3d, PA_CL_VPORT_YSCALE),
      ReadRegFloat(d3d, PA_CL_VPORT_YOFFSET), float(g.active_color->guest_width),
      float(g.active_color->guest_height), half_pixel);
}

void SetDynamicState(const uint8_t* d3d, VkCommandBuffer cb) {
  const auto& dfn = *g_vk.dfn;
  const ClipTransform clip = DrawClipTransform(d3d);
  const float x_scale = clip.x_scale, x_offset = clip.x_offset;
  const float y_scale = clip.y_scale, y_offset = clip.y_offset;
  const uint32_t vte = ReadReg(d3d, PA_CL_VTE_CNTL);
  const float z_scale = (vte & 16) ? ReadRegFloat(d3d, PA_CL_VPORT_ZSCALE) : 1.0f;
  const float z_offset = (vte & 32) ? ReadRegFloat(d3d, PA_CL_VPORT_ZOFFSET) : 0.0f;
  const float scale = float(g.active_color->scale);
  // The converted vertex shaders negate y (-fvk-invert-y); this viewport undoes
  // it, reproducing D3D's transform for either sign of the y scale.
  VkViewport viewport;
  viewport.x = (x_offset - x_scale) * scale;
  viewport.width = 2.0f * x_scale * scale;
  viewport.y = (y_offset + y_scale) * scale;
  viewport.height = -2.0f * y_scale * scale;
  viewport.minDepth = std::clamp(z_offset, 0.0f, 1.0f);
  viewport.maxDepth = std::clamp(z_offset + z_scale, 0.0f, 1.0f);
  if (viewport.width <= 0.0f) {
    viewport.x = 0.0f;
    viewport.width = float(g.active_color->width);
  }
  dfn.vkCmdSetViewport(cb, 0, 1, &viewport);

  // The screen scissor intersected with the window scissor, which clips
  // UI such as the menus' scrolling tickers to their boxes.
  const uint32_t tl = ReadReg(d3d, PA_SC_SCREEN_SCISSOR_TL);
  const uint32_t br = ReadReg(d3d, PA_SC_SCREEN_SCISSOR_BR);
  int32_t left = int32_t(tl & 0x7FFF), top = int32_t((tl >> 16) & 0x7FFF);
  int32_t right = int32_t(br & 0x7FFF), bottom = int32_t((br >> 16) & 0x7FFF);
  if (!(right > left && bottom > top)) {
    left = top = 0;
    right = bottom = INT32_MAX;
  }
  const uint32_t window_tl = ReadReg(d3d, PA_SC_WINDOW_SCISSOR_TL);
  const uint32_t window_br = ReadReg(d3d, PA_SC_WINDOW_SCISSOR_BR);
  if (window_br) {
    left = std::max(left, int32_t(window_tl & 0x7FFF));
    top = std::max(top, int32_t((window_tl >> 16) & 0x7FFF));
    right = std::min(right, int32_t(window_br & 0x7FFF));
    bottom = std::min(bottom, int32_t((window_br >> 16) & 0x7FFF));
  }
  const int32_t s = int32_t(g.active_color->scale);
  left = std::min<int32_t>(left * s, int32_t(g.active_color->width));
  top = std::min<int32_t>(top * s, int32_t(g.active_color->height));
  right = std::min<int64_t>(int64_t(right) * s, int32_t(g.active_color->width));
  bottom = std::min<int64_t>(int64_t(bottom) * s, int32_t(g.active_color->height));
  // An empty rectangle draws nothing, as on the console.
  const VkRect2D scissor = {{left, top},
                            {uint32_t(std::max(right - left, 0)),
                             uint32_t(std::max(bottom - top, 0))}};
  dfn.vkCmdSetScissor(cb, 0, 1, &scissor);
  TRACE("  viewport {:.1f},{:.1f} {:.1f}x{:.1f} scissor {},{} {}x{} (screen {:08X} {:08X} "
        "window {:08X} {:08X})",
        viewport.x, viewport.y, viewport.width, viewport.height, scissor.offset.x,
        scissor.offset.y, scissor.extent.width, scissor.extent.height, tl, br, window_tl,
        window_br);

  float blend_constants[4];
  for (uint32_t i = 0; i < 4; ++i) {
    blend_constants[i] = ReadRegFloat(d3d, RB_BLEND_RED + i);
  }
  dfn.vkCmdSetBlendConstants(cb, blend_constants);
  const uint32_t ref_mask = ReadReg(d3d, RB_STENCILREFMASK);
  // With two-sided stencil disabled, both faces use the front reference and masks.
  const uint32_t ref_mask_bf = (ReadReg(d3d, RB_DEPTHCONTROL) & 0x80)
                                 ? ReadReg(d3d, RB_STENCILREFMASK_BF)
                                 : ref_mask;
  dfn.vkCmdSetStencilReference(cb, VK_STENCIL_FACE_FRONT_BIT, ref_mask & 0xFF);
  dfn.vkCmdSetStencilCompareMask(cb, VK_STENCIL_FACE_FRONT_BIT, (ref_mask >> 8) & 0xFF);
  dfn.vkCmdSetStencilWriteMask(cb, VK_STENCIL_FACE_FRONT_BIT, (ref_mask >> 16) & 0xFF);
  dfn.vkCmdSetStencilReference(cb, VK_STENCIL_FACE_BACK_BIT, ref_mask_bf & 0xFF);
  dfn.vkCmdSetStencilCompareMask(cb, VK_STENCIL_FACE_BACK_BIT, (ref_mask_bf >> 8) & 0xFF);
  dfn.vkCmdSetStencilWriteMask(cb, VK_STENCIL_FACE_BACK_BIT, (ref_mask_bf >> 16) & 0xFF);
}

// The library path's dynamic state (parts::DynamicStateOf), set where it
// changed since the last draw with such a pipeline.
void SetLibraryDynamicState(VkCommandBuffer cb, const parts::DynamicState& state) {
  const auto& dfn = *g_vk.dfn;
  const parts::DynamicState& last = g.last_dynamic;
  const bool all = !g.dynamic_set;
  if (all || state.cull_mode != last.cull_mode) {
    dfn.vkCmdSetCullMode(cb, state.cull_mode);
  }
  if (all || state.front_face != last.front_face) {
    dfn.vkCmdSetFrontFace(cb, state.front_face);
  }
  if (all || state.depth_test != last.depth_test) {
    dfn.vkCmdSetDepthTestEnable(cb, state.depth_test);
  }
  if (all || state.depth_write != last.depth_write) {
    dfn.vkCmdSetDepthWriteEnable(cb, state.depth_write);
  }
  if (all || state.depth_compare != last.depth_compare) {
    dfn.vkCmdSetDepthCompareOp(cb, state.depth_compare);
  }
  if (all || state.stencil_test != last.stencil_test) {
    dfn.vkCmdSetStencilTestEnable(cb, state.stencil_test);
  }
  if (all || !(state.front == last.front)) {
    dfn.vkCmdSetStencilOp(cb, VK_STENCIL_FACE_FRONT_BIT, state.front.fail, state.front.pass,
                          state.front.depth_fail, state.front.compare);
  }
  if (all || !(state.back == last.back)) {
    dfn.vkCmdSetStencilOp(cb, VK_STENCIL_FACE_BACK_BIT, state.back.fail, state.back.pass,
                          state.back.depth_fail, state.back.compare);
  }
  g.last_dynamic = state;
  g.dynamic_set = true;
}

// Binds a vertex buffer; with a library-path pipeline bound its stride is
// dynamic state, given here.
void BindVertexBuffer(uint32_t binding, VkBuffer buffer, VkDeviceSize offset, uint32_t stride) {
  const auto& dfn = *g_vk.dfn;
  if (g.bound_dynamic) {
    const VkDeviceSize vk_stride = stride;
    dfn.vkCmdBindVertexBuffers2(g.slot->main_cb, binding, 1, &buffer, &offset, nullptr,
                                &vk_stride);
  } else {
    dfn.vkCmdBindVertexBuffers(g.slot->main_cb, binding, 1, &buffer, &offset);
  }
}

// Vertex, pixel and shared constants for a draw; false if the ring is full.
// 256 float4 constants (big-endian in the device), converted into the upload
// ring unless the previous draw's block holds the same values.
bool UploadConstantBlock(State::ReusedBlock& block, const uint8_t* big_endian,
                         VkDeviceAddress& address) {
  if (block.address && REXCVAR_GET(wwe_native_reuse_constants) &&
      std::memcmp(block.last, big_endian, sizeof(block.last)) == 0) {
    address = block.address;
    return true;
  }
  const Upload upload = AllocateUpload(sizeof(block.last), 16);
  if (!upload.data) {
    return false;
  }
  SwapCopy32(reinterpret_cast<uint32_t*>(upload.data), big_endian, sizeof(block.last) / 4);
  std::memcpy(block.last, big_endian, sizeof(block.last));
  block.address = address = upload.address;
  return true;
}

// wait: textures still loading are waited for rather than drawn without.
// False when the draw must be skipped, with the reason and key set.
bool UploadConstants(const uint8_t* d3d, uint32_t swapped_texcoords, uint32_t fetch_slots,
                     bool wait, PushConstants& push, Skip& reason, uint64_t& key) {
  reason = Skip::kConstantUpload;
  if (!UploadConstantBlock(g.vertex_constants, d3d + kDeviceVertexConstants,
                           push.vertex_constants) ||
      !UploadConstantBlock(g.pixel_constants, d3d + kDevicePixelConstants,
                           push.pixel_constants)) {
    return false;
  }
  SharedConstants shared_values = {};
  SharedConstants* shared = &shared_values;
  for (uint32_t slot = 0; slot < kFetchSlots; ++slot) {
    const uint8_t* fetch_data = d3d + kDeviceFetchConstants + slot * 24;
    if (!(fetch_slots & (1u << slot)) ||
        (REXCVAR_GET(wwe_native_debug_null_slots) & (1u << slot))) {
      continue;
    }
    if (!IsTextureFetch(LoadBE32(fetch_data), LoadBE32(fetch_data + 4))) {
      // A slot the shaders read holds no texture fetch constant: the draw
      // would sample the default image. Unknown masks (all slots) skip it.
      if (fetch_slots == UINT32_MAX) {
        continue;
      }
      reason = Skip::kNoTexture;
      key = uint64_t(slot) << 32 | LoadBE32(fetch_data);
      return false;
    }
    TRACE("  slot {} fetch {:08X} {:08X} {:08X} {:08X} {:08X} {:08X}", slot, LoadBE32(fetch_data),
          LoadBE32(fetch_data + 4), LoadBE32(fetch_data + 8), LoadBE32(fetch_data + 12),
          LoadBE32(fetch_data + 16), LoadBE32(fetch_data + 20));
    const textures::Binding binding = textures::Bind(textures::LoadFetchConstant(fetch_data),
                                                     g.slot->upload_cb, g.frame, wait);
    if (!binding.supported) {
      reason = Skip::kNoTexture;
      key = binding.base_address;
      return false;
    }
    switch (binding.dimension) {
      case 2: shared->texture_3d[slot] = binding.texture_index; break;
      case 3: shared->texture_cube[slot] = binding.texture_index; break;
      default: shared->texture_2d[slot] = binding.texture_index; break;
    }
    shared->sampler[slot] = binding.sampler_index;
  }
  SwapCopy32(shared->booleans, d3d + kDeviceBooleans, 8);
  shared->swapped_texcoords = swapped_texcoords;
  shared->alpha_threshold = ReadRegFloat(d3d, RB_ALPHA_REF);
  // Clip transform: pre-transformed positions to clip space, half-pixel shift.
  const ClipTransform clip = DrawClipTransform(d3d);
  shared->half_pixel_offset[0] = clip.offset[0];
  shared->half_pixel_offset[1] = clip.offset[1];
  shared->ndc_scale[0] = clip.ndc_scale[0];
  shared->ndc_scale[1] = clip.ndc_scale[1];
  if (g.shared_address && REXCVAR_GET(wwe_native_reuse_constants) &&
      std::memcmp(&g.last_shared, shared, sizeof(*shared)) == 0) {
    push.shared_constants = g.shared_address;
    return true;
  }
  const Upload upload = AllocateUpload(sizeof(SharedConstants), 16);
  if (!upload.data) {
    return false;
  }
  std::memcpy(upload.data, shared, sizeof(*shared));
  g.last_shared = *shared;
  g.shared_address = push.shared_constants = upload.address;
  return true;
}

// scripts/analyze_hitches.py reads this line (RENDER_THREAD_TOTALS).
void LogRenderThreadTotals(bool always) {
  const uint64_t modules = shader_library::RenderThreadModules();
  if (always || g.render_thread_pipelines != g.logged_pipelines || modules != g.logged_modules ||
      g.render_thread_waits != g.logged_waits) {
    REXLOG_INFO("native renderer: render thread totals at frame {}: pipelines compiled {}, "
                "shader modules created {}, pipeline waits {}",
                g.frame, g.render_thread_pipelines, modules, g.render_thread_waits);
    g.logged_pipelines = g.render_thread_pipelines;
    g.logged_modules = modules;
    g.logged_waits = g.render_thread_waits;
  }
}

}  // namespace

// Emulated frame captures without --readback_resolve go through its presenter.
rex::Runtime* g_runtime = nullptr;

bool g_cas_applied = false;
// Whether Native owns the presenter's effect: Native is requested and the user
// chose no present_effect of their own (theirs always wins).
bool g_cas_owns_effect = false;

void ApplyPresenterSettings() {
  if (!REXCVAR_GET(wwe_native_renderer)) {
    return;
  }
  const CasSetting cas = CasEffect(REXCVAR_GET(wwe_native_cas));
  if (rex::cvar::GetFlagByName("present_effect") != "bilinear") {
    if (cas.enabled) {
      REXLOG_INFO("native renderer: --present_effect is set; --wwe_native_cas is not applied");
    }
    return;
  }
  g_cas_owns_effect = true;
  if (!cas.enabled) {
    return;
  }
  // The presenter reads these when it is created; later changes go through
  // ApplyLiveSharpening.
  rex::cvar::SetFlagByName("present_effect", "cas");
  rex::cvar::SetFlagByName("present_cas_additional_sharpness", fmt::format("{}", cas.sharpness));
  g_cas_applied = true;
  REXLOG_INFO("native renderer: CAS sharpening {}", cas.sharpness);
}

namespace {

// A sharpening change after startup (the PC SETTINGS page, the F6 overlay):
// the presenter's effect follows at once. Change callbacks run on the thread
// that sets the cvar (after the new value is stored); the page and the
// overlay draw on the UI thread, the one the presenter's paint config belongs to.
void ApplyLiveSharpening() {
  auto* graphics = g_runtime ? g_runtime->graphics_system() : nullptr;
  rex::ui::Presenter* presenter = graphics ? graphics->presenter() : nullptr;
  if (!presenter) {
    return;
  }
  const CasSetting cas = CasEffect(REXCVAR_GET(wwe_native_cas));
  // The game is built without REX_HAS_FIDELITYFX_SDK, so it never touches the
  // presenter's paint config itself (its layout differs here): the SDK switches it.
  presenter->SetCasFromUIThread(cas.enabled, float(cas.sharpness));
  g_cas_applied = g_cas_applied || cas.enabled;
  REXLOG_INFO("native renderer: CAS sharpening {} (live)", cas.enabled ? cas.sharpness : 0.0);
}

}  // namespace

void Configure(rex::Runtime* runtime) {
  g_runtime = runtime;
  // Guest memory access works without the renderer too (Emulated frame dumps).
  if (runtime) {
    g_vk.memory = runtime->memory();
  }
  if (!REXCVAR_GET(wwe_native_renderer)) {
    return;
  }
  if (g_cas_owns_effect) {
    rex::cvar::RegisterChangeCallback(
        "wwe_native_cas", [](std::string_view, std::string_view) { ApplyLiveSharpening(); });
  }
  auto* graphics_system = runtime ? runtime->graphics_system() : nullptr;
  // This build only has the Vulkan backend, so the provider is a VulkanProvider.
  auto* provider = graphics_system
                       ? static_cast<rex::ui::vulkan::VulkanProvider*>(graphics_system->provider())
                       : nullptr;
  // A missing, damaged or stale shader pack starts the session on Emulated.
  std::filesystem::path pack_path = REXCVAR_GET(wwe_shader_pack);
  if (pack_path.empty()) {
    pack_path = (runtime ? runtime->cache_root() : std::filesystem::path()) / kShaderPackFileName;
  }
  ShaderPackLoad pack = LoadShaderPackFile(pack_path, WWE_EXPECTED_SHADER_CONVERTER_ID);
  g.fallback = pack.fallback;
  if (g.fallback != Fallback::kNone) {
    REXLOG_ERROR("native renderer: renderer fallback {} ({}); running on Emulated. Rebuild the "
                 "shader pack with the launcher or pwsh scripts/convert-shaders.ps1.",
                 FallbackName(g.fallback), pack.detail);
    return;
  }
  const uint64_t converter_id = pack.pack.converter_id;
  // Shader top-ups an earlier session converted live (ticket 22) join the pack.
  if (runtime) {
    std::vector<std::string> skipped;
    const std::vector<ShaderPack> converted = live_topup::LoadConverted(
        runtime->cache_root() / ShaderTopUp::kFolderName, converter_id, skipped);
    if (!converted.empty()) {
      pack.pack = MergeShaderPacks(std::move(pack.pack), converted);
      REXLOG_INFO("native renderer: {} shader top-ups converted earlier joined the pack",
                  converted.size());
    }
    for (const std::string& name : skipped) {
      REXLOG_WARN("native renderer: converted shader top-up {} is not used", name);
    }
  }
  g.device = provider ? provider->vulkan_device() : nullptr;
  g.scale = uint32_t(std::clamp(REXCVAR_GET(wwe_native_resolution_scale), 1, 4));
  g.colour = REXCVAR_GET(wwe_native_color_mode) == 0 ? ColourMode::kOriginal
                                                      : ColourMode::kNatural;
  g.width = 1280 * g.scale;
  g.height = 720 * g.scale;
  bool ok = g.device && InitializeContext(g.device, runtime->memory()) &&
            CreateUploadRings(kFramesInFlight, kUploadRingSize) &&
            textures::Initialize(g.scale, g.colour, LodBiasFor(g.scale)) &&
            geometry::Initialize() &&
            write_watch::Initialize(runtime->memory());
  for (FrameSlot& slot : g.slots) {
    ok = ok && CreateFrameSlot(slot);
  }
  if (!ok) {
    REXLOG_ERROR("native renderer: could not create Vulkan resources; running on Emulated");
    g.device = nullptr;
    g.fallback = Fallback::kVulkanSetup;
    return;
  }
  InitializeGpuTimer();
  InitializeDebugLabels();
  shader_library::Initialize(g.device, std::move(pack.pack));
  pipelines::Initialize(converter_id, runtime->cache_root(), g.colour);
  if (!pipelines::PrepareMode()) {
    topup_converter::Configure(runtime->cache_root(), converter_id);
  }
  rex::system::external_frame::SetEnabled(true);
  REXLOG_INFO("native renderer: enabled, render size {}x{}", g.width, g.height);
}

bool IsEnabled() { return g.device != nullptr; }

// The window size SetWindowSize last gave, width in the high half; 0 before the first.
std::atomic<uint64_t> g_window_size{0};

void SetWindowSize(uint32_t width, uint32_t height) {
  // A minimised window has no area: the last plan's size stays.
  if (width && height) {
    g_window_size.store((uint64_t(width) << 32) | height, std::memory_order_relaxed);
  }
}

bool PrepareRequested() { return pipelines::PrepareMode(); }

void PreparePipelinesAndExit() {
  const auto start = std::chrono::steady_clock::now();
  const auto elapsed_ms = [&start] {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() -
                                                                 start)
        .count();
  };
  settings::UseStandardOutput();
  const auto print = [](const std::string& line) {
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fflush(stdout);
  };
  print(prepare::HeaderLine());
  prepare::Result result;
  if (!IsEnabled()) {
    result.reason = g.fallback != Fallback::kNone ? FallbackName(g.fallback) : "native_off";
  } else {
    prepare::StageTracker tracker;
    for (;;) {
      const pipelines::PrebuildStatus status = pipelines::Status();
      const prepare::Snapshot snapshot = {status.modules_built,   status.modules_total,
                                          status.libraries_built, status.libraries_total,
                                          status.pipelines_done,  status.pipelines_total};
      for (const prepare::Progress& progress : tracker.Report(snapshot)) {
        print(prepare::ProgressLine(progress));
      }
      // On the library path every shader library is built too, so the driver
      // cache saved below holds them and the next start loads them warm.
      if (prepare::Finished(snapshot)) {
        result.modules = status.modules_total;
        result.failed = status.pipelines_failed;
        result.pipelines = status.pipelines_done - status.pipelines_failed;
        result.library_path = status.libraries_total != 0;
        result.libraries = status.libraries_built - status.libraries_failed;
        result.libraries_failed = status.libraries_failed;
        break;
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
    pipelines::Save(true);
    pipelines::Shutdown();
    pipelines::ClearStaleMarker();
  }
  result.ms = elapsed_ms();
  const std::string line = prepare::ResultLine(result);
  REXLOG_INFO("native renderer: prepare-pipelines {}", line.substr(0, line.size() - 1));
  print(line);
  rex::FlushLogging();
  std::_Exit(prepare::ExitCode(result));
}

void Shutdown() {
  if (g.device) {
    pipelines::Shutdown();
    textures::Shutdown();
  }
}

// Enhancements (CONTEXT.md): Native image options beyond the oracle frame, and
// Enhanced vibration and colour (scene runs force Original).
// Each one is listed here, so the session report shows whether it actually
// ran and the scene runner fails a run that had one on (its ENHANCEMENTS list
// in scripts/scene_runner.py names the same cvars).
struct Enhancement {
  const char* name;
  bool (*on)();
};
constexpr std::array<Enhancement, 7> kEnhancements = {{
    {"wwe_native_cas", [] { return g.device != nullptr && g_cas_applied; }},
    {"wwe_vibration_mode",
     [] { return wwe::vibration::CurrentMode() == wwe::vibration::Mode::kEnhanced; }},
    {"wwe_native_color_mode",
     [] { return g.device != nullptr && g.colour == ColourMode::kNatural; }},
    {"wwe_60fps_entrances", [] { return REXCVAR_GET(wwe_60fps_entrances); }},
    {"wwe_120fps_matches", [] { return REXCVAR_GET(wwe_120fps_matches); }},
    {"wwe_unlock_everything", [] { return REXCVAR_GET(wwe_unlock_everything); }},
    {"wwe_short_loading_screens", [] { return REXCVAR_GET(wwe_short_loading_screens); }},
}};

void OnExit() {
  static std::atomic<bool> done{false};
  if (done.exchange(true)) {
    return;
  }
  std::lock_guard lock(g.skips_mutex);
  CloseGpuTimer();
  if (g.device) {
    REXLOG_INFO("native renderer: session draw skips over {} frames: {}", g.frame,
                g.skips.Summary());
    LogRenderThreadTotals(true);
  }
  const std::string path = REXCVAR_GET(wwe_native_report);
  if (path.empty()) {
    return;
  }
  if (FILE* file = std::fopen(path.c_str(), "w")) {
    // Debug cvars change what Native draws, so a scene run with one set fails.
    std::string debug_cvars;
    for (const std::string& name : rex::cvar::ListModifiedFlags()) {
      if (name.starts_with("wwe_native_debug_")) {
        debug_cvars += fmt::format(R"({}"{}")", debug_cvars.empty() ? "" : ", ", name);
      }
    }
    std::string enhancements;
    for (const Enhancement& e : kEnhancements) {
      enhancements += fmt::format(R"({}{{"name": "{}", "on": {}}})",
                                  enhancements.empty() ? "" : ", ", e.name,
                                  e.on() ? "true" : "false");
    }
    std::fputs(fmt::format(R"({{"native_requested": {}, "native_active": {}, "fallback": "{}", )"
                           R"("frames": {}, "debug_cvars": [{}], "enhancements": [{}], )"
                           R"("draw_skips": {}, "gpu_failure": "{}"}})",
                           REXCVAR_GET(wwe_native_renderer) ? "true" : "false",
                           g.device ? "true" : "false", FallbackName(g.fallback), g.frame,
                           debug_cvars, enhancements, g.skips.Json(),
                           g.gpu_failure ? VkResultName(g.gpu_failure) : std::string())
                   .c_str(),
               file);
    std::fputc('\n', file);
    std::fclose(file);
  } else {
    REXLOG_WARN("native renderer: cannot write report {} (relative paths start in the "
                "executable's directory)",
                path);
  }
}

// A GPU failure ends the session (ADR-0025): no device recreation, no switch
// to Emulated mid-session. The launcher offers Emulated for the next start.
// Call it with no queue held: the report's message box stays up a while. A
// lost device often fails on two threads at once (render and present); the
// second waits here for the first to end the process.
[[noreturn]] void EndSessionWithGpuFailure(VkResult result, const char* call) {
  int32_t none = 0;
  if (!g.gpu_failure.compare_exchange_strong(none, int32_t(result))) {
    for (;;) std::this_thread::sleep_for(std::chrono::hours(1));
  }
  REXLOG_ERROR("{}", GpuFailureLine(int32_t(result), call));
  OnExit();
  wwe::crash::WriteGpuFailureReport(GpuFailureReason(int32_t(result), call));
  rex::FlushLogging();
  std::_Exit(kGpuFailureExitCode);
}

void OnHostGpuLoss() {
  if (IsEnabled()) {
    // The SDK reports a loss for VK_ERROR_DEVICE_LOST from the presenter's
    // acquire or present, or the GPU plugin's fence wait or submit.
    EndSessionWithGpuFailure(VK_ERROR_DEVICE_LOST, "SDK");
  }
}

void OnSetRenderTarget(const uint8_t* base, uint32_t index, uint32_t surface) {
  if (!g.device || index >= 4 || !surface) {
    return;
  }
  const SurfaceSize size = DecodeSurfaceSize(LoadBE32(base + surface + kSurfaceSize));
  g.surface_width[index] = size.width;
  g.surface_height[index] = size.height;
}

void OnClear(const uint8_t* base, uint32_t device, uint32_t flags, const int32_t rect[4],
             uint32_t color, float depth, uint32_t stencil) {
  const HookTimer timer;
  if (!g.device) {
    return;
  }
  BeginFrameIfNeeded();
  const uint8_t* d3d = base + device;
  const bool clear_color = (flags & 0xF) != 0;
  const bool clear_depth_stencil = (flags & 0x30) != 0;
  if (!EnsureRendering(d3d, clear_depth_stencil)) {
    return;
  }
  ++g.stats.clears;
  TRACE("clear flags {:X} rect {} {} {} {} colour {} depth {}", flags, rect[0], rect[1], rect[2],
        rect[3], color ? LoadBEFloat(base + color) : -1.0f, depth);
  VkClearAttachment attachments[2] = {};
  uint32_t count = 0;
  if (clear_color) {
    VkClearAttachment& a = attachments[count++];
    a.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    a.colorAttachment = 0;
    for (uint32_t i = 0; i < 4; ++i) {
      a.clearValue.color.float32[i] = color ? LoadBEFloat(base + color + i * 4) : 0.0f;
    }
  }
  if (clear_depth_stencil && g.active_depth) {
    VkClearAttachment& a = attachments[count++];
    a.aspectMask = ((flags & 0x10) ? VK_IMAGE_ASPECT_DEPTH_BIT : 0) |
                   ((flags & 0x20) ? VK_IMAGE_ASPECT_STENCIL_BIT : 0);
    a.clearValue.depthStencil = {depth, stencil & 0xFF};
  }
  if (!count) {
    return;
  }
  const RenderTarget& target = *g.active_color;
  VkClearRect clear_rect = {};
  clear_rect.layerCount = 1;
  const int32_t s = int32_t(target.scale);
  const int32_t left = rect[0] * s, top = rect[1] * s;
  const int32_t right = std::min<int32_t>(rect[2] * s, int32_t(target.width));
  const int32_t bottom = std::min<int32_t>(rect[3] * s, int32_t(target.height));
  if (right > left && bottom > top && left >= 0 && top >= 0) {
    clear_rect.rect = {{left, top}, {uint32_t(right - left), uint32_t(bottom - top)}};
  } else {
    clear_rect.rect = {{0, 0}, {target.width, target.height}};
  }
  g_vk.dfn->vkCmdClearAttachments(g.slot->main_cb, count, attachments, 1, &clear_rect);
}

namespace {

// Copies the current depth target's depth into the destination as R32 float,
// through a staging buffer since Vulkan cannot blit depth to colour.
void ResolveDepth(const uint8_t* d3d, const textures::FetchConstant& fetch) {
  const uint64_t key = uint64_t(ReadReg(d3d, RB_DEPTH_INFO) & 0x10FFF) |
                       uint64_t(ReadReg(d3d, RB_SURFACE_INFO) & 0x3FFF) << 32 | 1ull << 63;
  const auto it = g.render_targets.find(key);
  RenderTarget* found = it != g.render_targets.end() ? it->second.get() : g.last_depth;
  if (it == g.render_targets.end()) {
    // Otherwise the texture would read as transparent black (format 22).
    static uint32_t logged = 0;
    if (logged++ < 8) {
      REXLOG_WARN("native renderer: depth resolve names no known depth target (key {:X}); "
                  "using the last one rendered with ({})",
                  key, found ? fmt::format("{}x{}", found->width, found->height) : "none");
    }
  }
  if (!found) {
    return;
  }
  RenderTarget& depth = *found;
  const textures::ResolveTarget target = textures::GetResolveTarget(fetch, false, depth.scale);
  if (!target.image || target.format != VK_FORMAT_R32_SFLOAT) {
    return;
  }
  const uint32_t width = std::min(depth.width, target.width);
  const uint32_t height = std::min(depth.height, target.height);
  const VkDeviceSize size = VkDeviceSize(depth.width) * depth.height * 4;
  if (g.depth_staging_size < size) {
    if (g.depth_staging) {
      // Frames still in flight may use the old buffer.
      Retired retired;
      retired.buffer = g.depth_staging;
      retired.memory = g.depth_staging_memory;
      Retire(std::move(retired));
      g.depth_staging = VK_NULL_HANDLE;
      g.depth_staging_memory = VK_NULL_HANDLE;
    }
    if (!vk_util::CreateDedicatedAllocationBuffer(
            g.device, size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT,
            vk_util::MemoryPurpose::kDeviceLocal, g.depth_staging, g.depth_staging_memory)) {
      g.depth_staging_size = 0;
      return;
    }
    g.depth_staging_size = size;
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  const auto& dfn = *g_vk.dfn;
  TransitionTarget(depth, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
  VkBufferImageCopy region = {};
  region.bufferRowLength = depth.width;
  region.imageSubresource = {VK_IMAGE_ASPECT_DEPTH_BIT, 0, 0, 1};
  region.imageExtent = {width, height, 1};
  dfn.vkCmdCopyImageToBuffer(cb, depth.image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                             g.depth_staging, 1, &region);
  VkBufferMemoryBarrier buffer_barrier = {VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER};
  buffer_barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
  buffer_barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
  buffer_barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
  buffer_barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
  buffer_barrier.buffer = g.depth_staging;
  buffer_barrier.size = VK_WHOLE_SIZE;
  dfn.vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0,
                           0, nullptr, 1, &buffer_barrier, 0, nullptr);
  ImageBarrier(cb, target.image, VK_IMAGE_ASPECT_COLOR_BIT, *target.layout,
               VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
  region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
  dfn.vkCmdCopyBufferToImage(cb, g.depth_staging, target.image,
                             VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
  ImageBarrier(cb, target.image, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
               VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
  *target.layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
}

}  // namespace

void OnResolve(const uint8_t* base, uint32_t device, uint32_t flags, uint32_t destination) {
  const HookTimer timer;
  if (!g.device) {
    return;
  }
  BeginFrameIfNeeded();
  const uint8_t* d3d = base + device;
  const uint32_t copy_control = ReadReg(d3d, RB_COPY_CONTROL);
  const uint32_t dest_info = ReadReg(d3d, RB_COPY_DEST_INFO);
  const uint32_t source_select = copy_control & 7;
  const uint32_t copy_command = (copy_control >> 20) & 3;
  // Resolve flags 0..3 pick a colour target, 4 the depth-stencil surface
  // (copied into a 24_8 texture as float depth).
  const bool depth_resolve = (flags & 7) == 4;
  RenderTarget* source =
      source_select < 4 && !depth_resolve ? CurrentColorTarget(d3d, false) : nullptr;
  // First resolve into each destination: whether Native found its source.
  static std::unordered_set<uint32_t> logged_destinations;
  if (logged_destinations.insert(destination).second) {
    REXLOG_INFO("native renderer: resolve dest={:08X} fetch_base={:08X} copy_control={:08X} "
                "dest_info={:08X} flags={:X} source={}",
                destination,
                destination ? LoadBE32(base + destination + kTextureFetchConstant + 4) & ~0xFFFu : 0,
                copy_control, dest_info, flags,
                source ? "found" : depth_resolve ? "depth" : "none");
  }
  ++g.stats.resolves;
  if (Tracing()) {
    const uint8_t* dest_fetch = base + destination + kTextureFetchConstant;
    TRACE("resolve dest {:08X} fetch {:08X} {:08X} {:08X} copy_control {:08X} dest_info {:08X} "
          "source {} {}x{}",
          destination, destination ? LoadBE32(dest_fetch) : 0,
          destination ? LoadBE32(dest_fetch + 4) : 0, destination ? LoadBE32(dest_fetch + 8) : 0,
          copy_control, dest_info, source ? "found" : "none", source ? source->width : 0,
          source ? source->height : 0);
  }
  EndRendering();
  // One span for the whole resolve: copies, readback copy and clears.
  struct ResolveSpan {
    ~ResolveSpan() { EndWork(); }
  } resolve_span;
  if (NamingWork()) {
    const RenderTarget* timed = depth_resolve ? g.last_depth : source;
    BeginWork(gpu_timer::Span::kResolve,
              timed ? ResolveName(timed->width, timed->height, timed->format, depth_resolve)
                    : std::string("resolve"));
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  if (destination && depth_resolve) {
    ResolveDepth(d3d, textures::LoadFetchConstant(base + destination + kTextureFetchConstant));
  }
  if (destination && source && copy_command != 3) {
    const textures::FetchConstant fetch =
        textures::LoadFetchConstant(base + destination + kTextureFetchConstant);
    const textures::ResolveTarget target =
        textures::GetResolveTarget(fetch,
                                   ((dest_info >> 24) & 1) || textures::ResolvesRedBlueSwapped(fetch),
                                   source->scale);
    if (target.image) {
      TransitionTarget(*source, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
      ImageBarrier(cb, target.image, VK_IMAGE_ASPECT_COLOR_BIT, *target.layout,
                   VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
      const int32_t width = int32_t(std::min(source->width, target.width));
      const int32_t height = int32_t(std::min(source->height, target.height));
      VkImageBlit blit = {};
      blit.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
      blit.srcOffsets[1] = {width, height, 1};
      blit.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
      blit.dstOffsets[1] = {width, height, 1};
      g_vk.vkCmdBlitImage(cb, source->image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, target.image,
                          VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &blit, VK_FILTER_NEAREST);
      VkImageLayout layout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
      QueueReadback(fetch, target, layout);
      ImageBarrier(cb, target.image, VK_IMAGE_ASPECT_COLOR_BIT, layout,
                   VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
      *target.layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    }
  }
  // Resolves can clear the source afterwards (RB_COPY_CONTROL bits 8 and 9).
  // This writes RB_COLOR_CLEAR's raw bits, as the EDRAM clear does; a guest
  // Clear is a draw and goes through the gamma view like any other draw.
  if ((copy_control >> 8) & 1) {
    if (RenderTarget* color = CurrentColorTarget(d3d, false)) {
      TransitionTarget(*color, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
      const uint32_t c = ReadReg(d3d, RB_COLOR_CLEAR);
      VkClearColorValue value;
      value.float32[0] = float((c >> 16) & 0xFF) / 255.0f;
      value.float32[1] = float((c >> 8) & 0xFF) / 255.0f;
      value.float32[2] = float(c & 0xFF) / 255.0f;
      value.float32[3] = float(c >> 24) / 255.0f;
      const VkImageSubresourceRange range = vk_util::InitializeSubresourceRange();
      g_vk.dfn->vkCmdClearColorImage(cb, color->image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                     &value, 1, &range);
    }
  }
  if ((copy_control >> 9) & 1) {
    const auto it = g.render_targets.find(
        uint64_t(ReadReg(d3d, RB_DEPTH_INFO) & 0x10FFF) |
        uint64_t(ReadReg(d3d, RB_SURFACE_INFO) & 0x3FFF) << 32 | 1ull << 63);
    if (it != g.render_targets.end()) {
      RenderTarget& depth = *it->second;
      TransitionTarget(depth, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
      const uint32_t d = ReadReg(d3d, RB_DEPTH_CLEAR);
      const VkClearDepthStencilValue value = {float(d >> 8) / float(0xFFFFFF), d & 0xFF};
      const VkImageSubresourceRange range = vk_util::InitializeSubresourceRange(
          VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT);
      g_vk.vkCmdClearDepthStencilImage(cb, depth.image,
                                            VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &value, 1,
                                            &range);
    }
  }
}

namespace {

bool Topology(uint32_t primitive, VkPrimitiveTopology& topology, bool& quads) {
  quads = false;
  switch (primitive) {
    case 1: topology = VK_PRIMITIVE_TOPOLOGY_POINT_LIST; return true;
    case 2: topology = VK_PRIMITIVE_TOPOLOGY_LINE_LIST; return true;
    case 3: topology = VK_PRIMITIVE_TOPOLOGY_LINE_STRIP; return true;
    case 4: topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST; return true;
    case 5: topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_FAN; return true;
    case 6: topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP; return true;
    case 13:
      topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
      quads = true;
      return true;
    default:
      return false;
  }
}

// The layout of a vertex shader's own fetches, for draws without a D3D
// declaration (most of UE3's). The vfetches are read from the live microcode
// (object+0x20), which D3D patched when it bound the shader to a declaration;
// layouts are cached by the decoded fetches.
constexpr uint32_t kVertexShaderMicrocode = 0x20;

const DeclEntry* GetShaderLayout(const uint8_t* base, uint32_t object, const NativeShader& shader) {
  static std::unordered_map<uint64_t, DeclEntry> cache;
  if (shader.elements.empty() || shader.elements.size() > kMaxAttributes) {
    return nullptr;
  }
  const uint32_t microcode = LoadBE32(base + object + kVertexShaderMicrocode);
  if (!microcode) {
    return nullptr;
  }
  const uint8_t* code = base + microcode + shader.code_offset;
  guest::VertexFetchInput inputs[kMaxAttributes] = {};
  const size_t count = shader.elements.size();
  for (size_t i = 0; i < count; ++i) {
    auto element = shader.elements[i];
    if (!guest::LocateVertexFetch(code, shader.code_size, element) ||
        !guest::DecodeVertexFetch(code, element, inputs[i]) || !inputs[i].format) {
      return nullptr;  // not a fetch, or not bound to a declaration yet
    }
  }
  const uint64_t key = XXH3_64bits_withSeed(inputs, count * sizeof(inputs[0]), shader.hash);
  auto [it, inserted] = cache.try_emplace(key);
  DeclEntry& entry = it->second;
  if (!inserted) {
    return entry.valid ? &entry : nullptr;
  }
  VertexLayout& layout = entry.layout;
  std::string description;
  for (size_t i = 0; i < count; ++i) {
    const guest::VertexFetchInput& input = inputs[i];
    const uint32_t stream = 95u - input.fetch_constant;
    const DeclType* decl_type = FindDeclTypeForFetch(input);
    const InputLocation* location = FindLocation(input.usage, input.usage_index);
    description += fmt::format(" {}.{}@{}+{}/{}:{}{}{}e{}", input.usage, input.usage_index, stream,
                               input.offset_dwords * 4, input.stride_dwords * 4, input.format,
                               input.is_signed ? "s" : "u", input.is_normalized ? "n" : "i",
                               input.exp_adjust);
    if (!decl_type || stream >= kMaxStreams || !location) {
      continue;
    }
    bool duplicate = false;
    for (uint32_t i = 0; i < layout.attribute_count; ++i) {
      duplicate |= layout.attributes[i].location == location->location;
    }
    if (duplicate) {
      continue;  // the same element fetched twice (UE3 skinning)
    }
    VertexAttribute& attribute = layout.attributes[layout.attribute_count++];
    attribute.location = location->location;
    attribute.format = location->is_uint ? decl_type->uint_format : decl_type->float_format;
    attribute.offset = input.offset_dwords * 4;
    attribute.stream = stream;
    layout.stream_mask |= 1u << stream;
    layout.fetch_strides[stream] = input.stride_dwords * 4;
    if (decl_type->packed_normal && location->is_uint) {
      layout.packed_normal = true;
    }
    if ((input.usage == 3 || input.usage == 6 || input.usage == 7) && input.format == 6) {
      layout.byte_normal = true;
    }
    if (REXCVAR_GET(wwe_native_swap_half2)) {
      layout.swapped_texcoords |=
          HalfSwapBit(input.usage, input.usage_index, decl_type->sixteen_bit,
                      FourComponent16(input.format));
    }
  }
  entry.valid = layout.attribute_count > 0;
  for (const InputLocation& l : kInputLocations) {
    bool present = false;
    for (uint32_t i = 0; i < layout.attribute_count; ++i) {
      present |= layout.attributes[i].location == l.location;
    }
    if (!present) {
      layout.dummy_locations |= 1u << l.location;
    }
  }
  static uint32_t logged = 0;
  if (logged++ < 64) {
    REXLOG_INFO("native renderer: shader layout {:016X} (usage.index@stream+offset/stride:fmt):{}",
                shader.hash, description);
  }
  return entry.valid ? &entry : nullptr;
}

// The bytes past a vertex's start that its stream's attributes reach.
uint32_t StreamExtent(const VertexLayout& layout, uint32_t stream) {
  uint32_t extent = 0;
  for (uint32_t i = 0; i < layout.attribute_count; ++i) {
    const VertexAttribute& a = layout.attributes[i];
    if (a.stream == stream) {
      extent = std::max(extent, a.offset + guest::VertexFormatBytes(a.format));
    }
  }
  return extent;
}

// Shaders, declaration, render targets, pipeline, constants and dynamic
// state shared by both draw kinds. Leaves the pipeline bound; returns the
// declaration's layout, or nullptr when the draw is skipped. widen_strides:
// the caller repacks its vertices, so a stride short of the attributes is
// widened to cover them (guest::HostVertexStride).
const VertexLayout* PrepareDraw(const uint8_t* base, const uint8_t* d3d,
                                VkPrimitiveTopology topology,
                                uint32_t strides[kMaxStreams], bool widen_strides = false) {
  const NativeShader* vertex_shader =
      shader_library::Find(LoadBE32(d3d + kDeviceVertexShader));
  // No pixel shader bound: a depth-only draw (ticket 38), not a missing shader.
  static const NativeShader kNoPixelShader = [] {
    NativeShader none;
    none.is_pixel_shader = true;
    none.fetch_slots = 0;
    return none;
  }();
  const uint32_t pixel_object = LoadBE32(d3d + kDevicePixelShader);
  const NativeShader* pixel_shader =
      pixel_object ? shader_library::Find(pixel_object) : &kNoPixelShader;
  if (!vertex_shader || !pixel_shader || vertex_shader->is_pixel_shader ||
      !pixel_shader->is_pixel_shader) {
    SkipDraw(Skip::kNoShader, uint64_t(LoadBE32(d3d + kDeviceVertexShader)) << 32 |
                                  LoadBE32(d3d + kDevicePixelShader));
    return nullptr;
  }
  const DeclEntry* declaration =
      GetDeclaration(base, LoadBE32(d3d + kDeviceVertexDeclaration));
  if (!declaration) {
    declaration = GetShaderLayout(base, LoadBE32(d3d + kDeviceVertexShader), *vertex_shader);
  }
  if (declaration) {
    for (uint32_t stream = 0; stream < kMaxStreams; ++stream) {
      if (!strides[stream]) {
        strides[stream] = declaration->layout.fetch_strides[stream];
      }
    }
  }
  if (!declaration) {
    static uint32_t logged = 0;
    if (logged++ < 8) {
      const uint32_t object = LoadBE32(d3d + kDeviceVertexDeclaration);
      REXLOG_WARN("native renderer: no declaration at draw (object {:08X}, count {:08X})", object,
                  object ? LoadBE32(base + object + kDeclarationCount) : 0);
    }
    SkipDraw(Skip::kNoDeclaration, vertex_shader->hash);
    return nullptr;
  }
  const VertexLayout& layout = declaration->layout;
  if (widen_strides) {
    for (uint32_t stream = 0; stream < kMaxStreams; ++stream) {
      strides[stream] = guest::HostVertexStride(strides[stream], StreamExtent(layout, stream));
    }
  }
  const uint32_t depth_control = ReadReg(d3d, RB_DEPTHCONTROL);
  if (!EnsureRendering(d3d, (depth_control & 7) != 0)) {
    SkipDraw(Skip::kNoRenderTarget, pixel_shader->hash);
    return nullptr;
  }

  if (REXCVAR_GET(wwe_native_debug_skip_ps) &&
      pixel_shader->hash == REXCVAR_GET(wwe_native_debug_skip_ps)) {
    SkipDraw(Skip::kDebugSkipPs, pixel_shader->hash);
    return nullptr;
  }

  PipelineKey key;
  std::memset(&key, 0, sizeof(key));
  key.vertex_shader = vertex_shader->hash;
  key.pixel_shader = pixel_shader->hash;
  const uint32_t color_control = ReadReg(d3d, RB_COLORCONTROL);
  uint32_t spec = 0;
  if (layout.packed_normal) {
    spec |= 1;  // SPEC_CONSTANT_R11G11B10_NORMAL
  }
  if (layout.byte_normal) {
    spec |= 4;  // SPEC_CONSTANT_BYTE_NORMAL
  }
  if ((color_control & 8) && (color_control & 7) != 7) {
    spec |= 2;  // SPEC_CONSTANT_ALPHA_TEST
  }
  spec |= GammaOutputSpec(g.colour, g.active_gamma);
  key.spec_constants = spec & (vertex_shader->spec_constants_mask |
                               pixel_shader->spec_constants_mask);
  key.topology = topology;
  key.color_format =
      g.active_gamma ? GammaFormat(g.active_color->format) : g.active_color->format;
  key.depth_format = g.active_depth ? g.active_depth->format : VK_FORMAT_UNDEFINED;
  key.blend_control = ReadReg(d3d, RB_BLENDCONTROL0);
  key.color_mask = ReadReg(d3d, RB_COLOR_MASK);
  key.depth_control = g.active_depth ? depth_control : 0;
  key.cull = ReadReg(d3d, PA_SU_SC_MODE_CNTL) & 7;
  for (uint32_t stream = 0; stream < kMaxStreams; ++stream) {
    key.strides[stream] = (layout.stream_mask & (1u << stream)) ? strides[stream] : 0;
  }
  key.dummy_locations = layout.dummy_locations;
  if (pixel_shader == &kNoPixelShader) {
    MakeDepthOnly(key);
  }
  if (Tracing()) {
    std::string attributes;
    for (uint32_t i = 0; i < layout.attribute_count; ++i) {
      const VertexAttribute& a = layout.attributes[i];
      attributes += fmt::format(" loc{}=fmt{}@s{}+{}", a.location, uint32_t(a.format), a.stream,
                                a.offset);
    }
    TRACE("  layout{} strides {} swap_mask {:08X} dummy {:08X}", attributes, strides[0],
          layout.swapped_texcoords, layout.dummy_locations);
  }
  key.attribute_count = layout.attribute_count;
  std::memcpy(key.attributes, layout.attributes, sizeof(key.attributes));
  // Draws into the main scene skip a frame or two while a new pipeline
  // compiles on the workers; off-screen targets (shadow maps, attire bakes)
  // may be drawn only once, so they wait for a worker to build it first.
  const bool main_scene = g.active_color->guest_width * g.scale == g.width &&
                          g.active_color->guest_height * g.scale == g.height;
  bool pending, dynamic;
  const VkPipeline pipeline = pipelines::Get(key, !main_scene, pending, dynamic);
  if (!pipeline) {
    SkipDraw(pending ? Skip::kPipelinePending : Skip::kPipelineFailed, pixel_shader->hash);
    g.hitch.pending += pending;
    return nullptr;
  }

  PushConstants push;
  const uint32_t fetch_slots = REXCVAR_GET(wwe_native_bind_used_slots)
                                   ? vertex_shader->fetch_slots | pixel_shader->fetch_slots
                                   : UINT32_MAX;
  Skip reason;
  uint64_t reason_key = pixel_shader->hash;
  if (!UploadConstants(d3d, layout.swapped_texcoords, fetch_slots, !main_scene, push, reason,
                       reason_key)) {
    SkipDraw(reason, reason_key);
    return nullptr;
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  const auto& dfn = *g_vk.dfn;
  if (g.bound_pipeline != pipeline) {
    dfn.vkCmdBindPipeline(cb, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline);
    g.bound_pipeline = pipeline;
    g.bound_dynamic = dynamic;
    // A pipeline with this state baked in leaves the dynamic values undefined.
    g.dynamic_set = g.dynamic_set && dynamic;
  }
  SetDynamicState(d3d, cb);
  if (dynamic) {
    SetLibraryDynamicState(cb, parts::DynamicStateOf(key));
  }
  if (std::memcmp(&push, &g.last_push, sizeof(push)) != 0) {
    dfn.vkCmdPushConstants(cb, g_vk.pipeline_layout,
                           VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT, 0,
                           sizeof(push), &push);
    g.last_push = push;
  }
  if (layout.dummy_locations) {
    // Zeros for shader inputs the declaration does not feed, once per frame.
    if (!g.zeros.data) {
      g.zeros = AllocateUpload(64, 16);
      if (!g.zeros.data) {
        SkipDraw(Skip::kVertexUpload, 64);
        return nullptr;
      }
      std::memset(g.zeros.data, 0, 64);
    }
    BindVertexBuffer(kDummyBinding, g.zeros.buffer, g.zeros.offset, 0);
  }
  return &layout;
}

// Binds the layout's vertex streams for vertices [first_vertex, + vertex_count)
// from the fetch constants SetStreamSource wrote (stream s at device +0x778 -
// 8 s, slot 95 - s; dword 0 = physical address | type 3). Returns -1, or the
// stream that could not be bound after counting the draw as bad_stream.
int BindStreams(const uint8_t* d3d, const VertexLayout& layout, const uint32_t* strides,
                uint32_t first_vertex, uint32_t vertex_count) {
  for (uint32_t stream = 0; stream < kMaxStreams; ++stream) {
    if (!(layout.stream_mask & (1u << stream))) {
      continue;
    }
    const uint32_t fetch = LoadBE32(d3d + kDeviceStreamFetch - 8 * stream);
    const uint32_t stride = strides[stream];
    geometry::Region vertices;
    if ((fetch & 3) != 3 || !stride ||
        !geometry::Vertices((fetch & ~3u) + first_vertex * stride, vertex_count * stride,
                            g.slot->upload_cb, vertices)) {
      SkipDraw(Skip::kBadStream, fetch);
      return int(stream);
    }
    BindVertexBuffer(stream, vertices.buffer, vertices.offset, stride);
  }
  return -1;
}

// Copies guest vertex data, which is fetched as 32-bit big-endian words.
bool UploadVertices(const uint8_t* source, uint32_t bytes, uint32_t binding, uint32_t stride) {
  const Upload upload = AllocateUpload((bytes + 3) & ~3u, 16);
  if (!upload.data) {
    return false;
  }
  SwapCopy32(reinterpret_cast<uint32_t*>(upload.data), source, (bytes + 3) / 4);
  BindVertexBuffer(binding, upload.buffer, upload.offset, stride);
  return true;
}

}  // namespace

void OnSetStreamSource(uint32_t stream, uint32_t stride) {
  if (stream < kMaxStreams) {
    g.stream_strides[stream] = stride;
  }
}

void OnDrawVertices(const uint8_t* base, uint32_t device, uint32_t primitive,
                    uint32_t vertex_count, uint32_t stride, uint32_t vertices,
                    uint32_t indices, uint32_t index_count, bool index32, int32_t base_vertex) {
  const HookTimer timer;
  if (!g.device || !vertex_count) {
    return;
  }
  BeginFrameIfNeeded();
  if (!vertices || !stride || stride > 256 || (stride & 3)) {
    SkipDraw(Skip::kBadUserVertices, stride);
    return;
  }
  VkPrimitiveTopology topology;
  bool quads;
  if (!Topology(primitive, topology, quads)) {
    SkipDraw(Skip::kUnsupportedPrimitive, primitive);
    return;
  }
  uint32_t strides[kMaxStreams] = {stride};
  const uint8_t* d3d = base + device;
  if (!PrepareDraw(base, d3d, topology, strides, true)) {
    return;
  }
  const uint32_t host_stride = strides[0];
  if (host_stride == stride) {
    if (!UploadVertices(base + vertices, vertex_count * stride, 0, stride)) {
      SkipDraw(Skip::kVertexUpload, vertex_count * stride);
      return;
    }
  } else {
    // An element past the game's stride (issue #3): each vertex gets the
    // guest bytes Xenos fetches for it, the next vertex's included.
    const Upload upload = AllocateUpload(size_t(vertex_count) * host_stride, 16);
    if (!upload.data) {
      SkipDraw(Skip::kVertexUpload, vertex_count * host_stride);
      return;
    }
    guest::RepackVertices(reinterpret_cast<uint32_t*>(upload.data), base + vertices,
                          vertex_count, stride, host_stride);
    BindVertexBuffer(0, upload.buffer, upload.offset, host_stride);
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  const auto& dfn = *g_vk.dfn;
  if (indices && index_count) {
    std::vector<uint32_t> source_indices(index_count);
    const auto range = ScanIndices(base + indices, index_count, index32, source_indices.data());
    if (range.empty() || int64_t(range.min) + base_vertex < 0 ||
        int64_t(range.max) + base_vertex >= vertex_count) {
      SkipDraw(Skip::kIndexRange, uint32_t(base_vertex));
      return;
    }
    const uint32_t count = quads ? index_count / 4 * 6 : index_count;
    const Upload upload = AllocateUpload(size_t(count) * 4, 16);
    if (!upload.data) {
      SkipDraw(Skip::kIndexUpload, count);
      return;
    }
    const uint32_t written = RebaseIndices(source_indices.data(), index_count, index32, quads, 0,
                                           reinterpret_cast<uint32_t*>(upload.data));
    dfn.vkCmdBindIndexBuffer(cb, upload.buffer, upload.offset, VK_INDEX_TYPE_UINT32);
    dfn.vkCmdDrawIndexed(cb, written, 1, 0, base_vertex, 0);
  } else if (quads) {
    const uint32_t quad_count = vertex_count / 4;
    const Upload index_upload = AllocateUpload(quad_count * 6 * sizeof(uint32_t), 16);
    if (!index_upload.data) {
      SkipDraw(Skip::kIndexUpload, quad_count * 6);
      return;
    }
    QuadListIndices(vertex_count, reinterpret_cast<uint32_t*>(index_upload.data));
    dfn.vkCmdBindIndexBuffer(cb, index_upload.buffer, index_upload.offset, VK_INDEX_TYPE_UINT32);
    dfn.vkCmdDrawIndexed(cb, quad_count * 6, 1, 0, 0, 0);
  } else {
    dfn.vkCmdDraw(cb, vertex_count, 1, 0, 0);
  }
  ++g.stats.drawn;
  TRACE("draw vertices rt_fmt {} prim {} count {} stride {} blend {:08X} depthctl {:08X} "
        "colorctl {:08X} "
        "mask {:08X} target {}x{} vs {:016x} ps {:016x}",
        (ReadReg(d3d, RB_COLOR_INFO) >> 16) & 0xF, primitive, vertex_count, stride, ReadReg(d3d, RB_BLENDCONTROL0),
        ReadReg(d3d, RB_DEPTHCONTROL), ReadReg(d3d, RB_COLORCONTROL), ReadReg(d3d, RB_COLOR_MASK),
        g.active_color ? g.active_color->guest_width : 0,
        g.active_color ? g.active_color->guest_height : 0,
        shader_library::Hash(LoadBE32(d3d + kDeviceVertexShader)),
        shader_library::Hash(LoadBE32(d3d + kDevicePixelShader)));
  TRACE("  vte {:08X} vport {:.1f} {:.1f} {:.1f} {:.1f} screen scissor {:08X} {:08X} window "
        "scissor {:08X} {:08X} mode {:08X}",
        ReadReg(d3d, PA_CL_VTE_CNTL), ReadRegFloat(d3d, PA_CL_VPORT_XSCALE),
        ReadRegFloat(d3d, PA_CL_VPORT_XOFFSET), ReadRegFloat(d3d, PA_CL_VPORT_YSCALE),
        ReadRegFloat(d3d, PA_CL_VPORT_YOFFSET), ReadReg(d3d, PA_SC_SCREEN_SCISSOR_TL),
        ReadReg(d3d, PA_SC_SCREEN_SCISSOR_BR), ReadReg(d3d, PA_SC_WINDOW_SCISSOR_TL),
        ReadReg(d3d, PA_SC_WINDOW_SCISSOR_BR), ReadReg(d3d, PA_SU_SC_MODE_CNTL));
  TRACE("  ps c0 {:.6g} {:.6g} {:.6g} {:.6g} c1 {:.6g} {:.6g} {:.6g} {:.6g}",
        LoadBEFloat(d3d + kDevicePixelConstants), LoadBEFloat(d3d + kDevicePixelConstants + 4),
        LoadBEFloat(d3d + kDevicePixelConstants + 8), LoadBEFloat(d3d + kDevicePixelConstants + 12),
        LoadBEFloat(d3d + kDevicePixelConstants + 16), LoadBEFloat(d3d + kDevicePixelConstants + 20),
        LoadBEFloat(d3d + kDevicePixelConstants + 24), LoadBEFloat(d3d + kDevicePixelConstants + 28));
}

void OnDrawNonIndexed(const uint8_t* base, uint32_t device, uint32_t primitive,
                      uint32_t start_vertex, uint32_t vertex_count) {
  const HookTimer timer;
  if (!g.device || !vertex_count) {
    return;
  }
  BeginFrameIfNeeded();
  VkPrimitiveTopology topology;
  bool quads;
  if (!Topology(primitive, topology, quads)) {
    SkipDraw(Skip::kUnsupportedPrimitive, primitive);
    return;
  }
  const uint8_t* d3d = base + device;
  uint32_t strides[kMaxStreams];
  std::memcpy(strides, g.stream_strides, sizeof(strides));
  const VertexLayout* layout = PrepareDraw(base, d3d, topology, strides);
  if (!layout || BindStreams(d3d, *layout, strides, start_vertex, vertex_count) >= 0) {
    return;
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  const auto& dfn = *g_vk.dfn;
  if (quads) {
    const uint32_t index_count = vertex_count / 4 * 6;
    const Upload index_upload = AllocateUpload(size_t(index_count) * sizeof(uint32_t), 16);
    if (!index_upload.data) {
      SkipDraw(Skip::kIndexUpload, index_count);
      return;
    }
    QuadListIndices(vertex_count, reinterpret_cast<uint32_t*>(index_upload.data));
    dfn.vkCmdBindIndexBuffer(cb, index_upload.buffer, index_upload.offset, VK_INDEX_TYPE_UINT32);
    dfn.vkCmdDrawIndexed(cb, index_count, 1, 0, 0, 0);
  } else {
    dfn.vkCmdDraw(cb, vertex_count, 1, 0, 0);
  }
  ++g.stats.drawn_non_indexed;
  TRACE("draw non-indexed prim {} start {} count {} vs {:016x} ps {:016x}", primitive,
        start_vertex, vertex_count, shader_library::Hash(LoadBE32(d3d + kDeviceVertexShader)),
        shader_library::Hash(LoadBE32(d3d + kDevicePixelShader)));
}

void OnDrawIndexed(const uint8_t* base, uint32_t device, uint32_t primitive,
                   int32_t base_vertex, uint32_t start_index, uint32_t index_count) {
  const HookTimer timer;
  if (!g.device || !index_count) {
    return;
  }
  BeginFrameIfNeeded();
  VkPrimitiveTopology topology;
  bool quads;
  if (!Topology(primitive, topology, quads)) {
    SkipDraw(Skip::kUnsupportedPrimitive, primitive);
    return;
  }
  const uint8_t* d3d = base + device;
  const uint32_t index_buffer = LoadBE32(d3d + kDeviceIndexBuffer);
  if (!index_buffer) {
    SkipDraw(Skip::kNoIndexBuffer, 0);
    return;
  }
  // Index buffer object: Common bit 31 = 32-bit indices, +0x18 = address.
  const bool index32 = (LoadBE32(base + index_buffer) & 0x80000000) != 0;
  const uint32_t index_address = (LoadBE32(base + index_buffer + 0x18) & ~3u) +
                                 start_index * (index32 ? 4 : 2);
  geometry::Indices indices;
  if (!geometry::IndexBuffer(ToPhysical(index_address), index_count, index32, quads,
                             g.slot->upload_cb, indices)) {
    SkipDraw(Skip::kIndexBufferUpload, index_address);
    return;
  }
  const uint32_t min_index = indices.min_index, max_index = indices.max_index;

  uint32_t strides[kMaxStreams];
  std::memcpy(strides, g.stream_strides, sizeof(strides));
  const VertexLayout* layout = PrepareDraw(base, d3d, topology, strides);
  if (!layout) {
    return;
  }
  const uint32_t first_vertex = uint32_t(int32_t(min_index) + base_vertex);
  const uint32_t vertex_count = max_index - min_index + 1;
  if (const int stream = BindStreams(d3d, *layout, strides, first_vertex, vertex_count);
      stream >= 0) {
    static uint32_t logged = 0;
    if (logged++ < 16) {
      REXLOG_WARN("native renderer: indexed draw stream {} fetch={:08X} {:08X} stride={} "
                  "indices {}..{} base_vertex={} count={} index32={}",
                  stream, LoadBE32(d3d + kDeviceStreamFetch - 8 * stream),
                  LoadBE32(d3d + kDeviceStreamFetch - 8 * stream + 4), strides[stream],
                  min_index, max_index, base_vertex, index_count, index32);
      std::string ib;
      for (uint32_t i = 0; i < 32; i += 4) {
        ib += fmt::format(" {:08X}", LoadBE32(base + index_buffer + i));
      }
      REXLOG_WARN("native renderer:   index buffer {:08X}:{} start={}", index_buffer, ib,
                  start_index);
    }
    return;
  }
  const VkCommandBuffer cb = g.slot->main_cb;
  const auto& dfn = *g_vk.dfn;
  dfn.vkCmdBindIndexBuffer(cb, indices.region.buffer, indices.region.offset,
                           VK_INDEX_TYPE_UINT32);
  dfn.vkCmdDrawIndexed(cb, indices.count, 1, 0, 0, 0);
  ++g.stats.drawn_indexed;
  TRACE("draw indexed prim {} indices {} blend {:08X} depthctl {:08X} rt_fmt {} ps {:016x}",
        primitive, index_count, ReadReg(d3d, RB_BLENDCONTROL0), ReadReg(d3d, RB_DEPTHCONTROL),
        (ReadReg(d3d, RB_COLOR_INFO) >> 16) & 0xF,
        shader_library::Hash(LoadBE32(d3d + kDevicePixelShader)));
  if (Tracing()) {
    std::string c;
    for (uint32_t i = 0; i < 52; ++i) {
      c += fmt::format("{}{:.4g}", i % 4 ? " " : " | ",
                       LoadBEFloat(d3d + kDeviceVertexConstants + i * 4));
    }
    TRACE("  vte {:08X} vport {:.1f} {:.1f} {:.1f} {:.1f} {:.3f} {:.3f} mode {:08X} vs c0-12:{}",
          ReadReg(d3d, PA_CL_VTE_CNTL), ReadRegFloat(d3d, PA_CL_VPORT_XSCALE),
          ReadRegFloat(d3d, PA_CL_VPORT_XOFFSET), ReadRegFloat(d3d, PA_CL_VPORT_YSCALE),
          ReadRegFloat(d3d, PA_CL_VPORT_YOFFSET), ReadRegFloat(d3d, PA_CL_VPORT_ZSCALE),
          ReadRegFloat(d3d, PA_CL_VPORT_ZOFFSET), ReadReg(d3d, PA_SU_SC_MODE_CNTL), c);
    if (shader_library::Hash(LoadBE32(d3d + kDevicePixelShader)) == REXCVAR_GET(wwe_native_trace_ps)) {
      for (uint32_t r = 0; r < 256; r += 4) {
        std::string c;
        for (uint32_t i = 0; i < 16; ++i) {
          c += fmt::format(" {:.4g}", LoadBEFloat(d3d + kDevicePixelConstants + r * 16 + i * 4));
        }
        TRACE("  ps c{}:{}", r, c);
      }
      std::string b;
      for (uint32_t i = 0; i < 8; ++i) {
        b += fmt::format(" {:08X}", LoadBE32(d3d + kDeviceBooleans + i * 4));
      }
      TRACE("  booleans:{} colorcontrol {:08X} colormask {:08X}", b, ReadReg(d3d, RB_COLORCONTROL),
            ReadReg(d3d, RB_COLOR_MASK));
    }
    TRACE("  vs {:016x} ps {:016x} first vertex {} verts {} streams {:X} fetch0 {:08X} {:08X}",
          shader_library::Hash(LoadBE32(d3d + kDeviceVertexShader)),
          shader_library::Hash(LoadBE32(d3d + kDevicePixelShader)), first_vertex,
          vertex_count, layout->stream_mask, LoadBE32(d3d + kDeviceStreamFetch), LoadBE32(d3d + kDeviceStreamFetch + 4));
    if (Tracing()) {
      // First vertices of stream 0 as stored (big-endian words).
      const uint32_t fetch = LoadBE32(d3d + kDeviceStreamFetch) & ~3u;
      std::string raw;
      for (uint32_t w = 0; w < 8; ++w) {
        raw += fmt::format(" {:08X}", LoadBE32(TranslatePhysical(fetch + first_vertex * strides[0]) +
                                               w * 4));
      }
      TRACE("  stream0 raw:{}", raw);
    }
  }
}

namespace {

// Logs frames that took much longer than a 60 Hz frame, with what the
// renderer did in them, so stutter can be pinned on its cause.
void LogHitch() {
  constexpr double kHitchMs = 25.0;
  const auto now = std::chrono::steady_clock::now();
  const double ms =
      std::chrono::duration<double, std::milli>(now - g.hitch.last_present).count();
  g.hitch.last_present = now;
  uint64_t pipelines_compiled, pipeline_ns;
  pipelines::TakeSyncStats(pipelines_compiled, pipeline_ns);
  uint64_t pipeline_waits, pipeline_wait_ns;
  pipelines::TakeWaitStats(pipeline_waits, pipeline_wait_ns);
  g.render_thread_waits += pipeline_waits;
  uint64_t modules_created, module_ns;
  shader_library::TakeRenderThreadModules(modules_created, module_ns);
  textures::UploadStats tex;
  textures::TakeUploadStats(tex);
  const uint64_t geometry_bytes = geometry::TakeUploadBytes();
  g.render_thread_pipelines += pipelines_compiled;
  if (g.frame > 60 && ms > kHitchMs) {
    // renderer = time in the hooks (the rest of the frame is the game and
    // the emulator); fence = of that, waiting for the GPU.
    REXLOG_INFO("native renderer: hitch at frame {}: {:.1f} ms, renderer {:.1f} ms, GPU fence "
                "{:.1f} ms; pipelines compiled on this thread {} ({:.1f} ms), pipelines "
                "waited for on a worker {} ({:.1f} ms), shader modules "
                "created on this thread {} ({:.1f} ms), draws waiting "
                "for pipelines {}; textures uploaded {} ({:.2f} MB), render thread on textures "
                "{:.1f} ms (waiting {:.1f}), reading on any thread {:.1f} ms (write-watch "
                "{:.1f}, hash {:.1f}); geometry {:.2f} MB; new render targets {}; readbacks "
                "queued {} ({:.1f} ms), delivered {} ({:.2f} MB, {:.1f} ms)",
                g.frame, ms, g.hitch.renderer_ns / 1e6, g.hitch.fence_wait_ns / 1e6,
                pipelines_compiled, pipeline_ns / 1e6, pipeline_waits, pipeline_wait_ns / 1e6,
                modules_created, module_ns / 1e6,
                g.hitch.pending, tex.count,
                tex.bytes / 1048576.0, tex.ns / 1e6, tex.wait_ns / 1e6, tex.read_ns / 1e6,
                tex.watch_ns / 1e6, tex.hash_ns / 1e6, geometry_bytes / 1048576.0,
                g.hitch.render_targets, g.hitch.readbacks_queued, g.hitch.readback_queue_ns / 1e6,
                g.hitch.readbacks_delivered, g.hitch.readback_bytes / 1048576.0,
                g.hitch.readback_deliver_ns / 1e6);
  }
  GpuTimerFrameSubmitted(uint32_t(g.frame % kFramesInFlight), g.hitch.renderer_ns / 1e6);
  g.hitch.readbacks_queued = 0;
  g.hitch.readback_queue_ns = 0;
  g.hitch.readbacks_delivered = 0;
  g.hitch.readback_bytes = 0;
  g.hitch.readback_deliver_ns = 0;
  g.hitch.pending = 0;
  g.hitch.render_targets = 0;
  g.hitch.renderer_ns = 0;
  g.hitch.fence_wait_ns = 0;
}

}  // namespace

// The file to dump a frame to: wwe_native_dump_frames into wwe_native_dump_dir,
// or the single wwe_native_dump_frame into wwe_native_dump_path; empty if none.
std::string DumpTarget(uint64_t frame) {
  if (std::string capture = frame_capture::TakeRequest(frame); !capture.empty()) {
    return capture;
  }
  static const FrameDumps dumps = [] {
    FrameDumps parsed;
    if (!parsed.Parse(REXCVAR_GET(wwe_native_dump_frames))) {
      REXLOG_ERROR("native renderer: bad --wwe_native_dump_frames list '{}'",
                   REXCVAR_GET(wwe_native_dump_frames));
    }
    if (!parsed.Empty()) {
      std::error_code error;
      std::filesystem::create_directories(REXCVAR_GET(wwe_native_dump_dir), error);
    }
    return parsed;
  }();
  if (dumps.Contains(frame) && !REXCVAR_GET(wwe_native_dump_dir).empty()) {
    return FrameDumps::Path(REXCVAR_GET(wwe_native_dump_dir), frame);
  }
  if (int64_t(frame) == REXCVAR_GET(wwe_native_dump_frame)) {
    return REXCVAR_GET(wwe_native_dump_path);
  }
  return {};
}

// Writes packed RGB rows as a PNG when the path ends in .png (F12 captures),
// else as a binary PPM (frame dumps).
void WriteRgbImage(const std::string& path, const uint8_t* rgb, uint32_t width, uint32_t height) {
  FILE* file = std::fopen(path.c_str(), "wb");
  if (!file) {
    REXLOG_WARN("native renderer: cannot write {}", path);
    return;
  }
  if (path.ends_with(".png")) {
    const std::vector<uint8_t> png = frame_capture::EncodePngRgb(rgb, width, height);
    std::fwrite(png.data(), 1, png.size(), file);
  } else {
    std::fprintf(file, "P6\n%u %u\n255\n", width, height);
    std::fwrite(rgb, 1, size_t(width) * height * 3, file);
  }
  std::fclose(file);
}

// Waits for the frame and writes the copied image as a binary PPM (RGB),
// logging its channel ranges so a black frame shows in the log too.
void WriteFrameDump(VkBuffer buffer, VkDeviceMemory memory, VkFence fence,
                    const std::string& path, uint32_t width, uint32_t height) {
  const VulkanDevice::Functions& dfn = *g_vk.dfn;
  const VkResult waited = dfn.vkWaitForFences(g_vk.vk_device, 1, &fence, VK_TRUE, UINT64_MAX);
  if (waited < 0) {
    EndSessionWithGpuFailure(waited, "vkWaitForFences");
  }
  void* mapped = nullptr;
  if (dfn.vkMapMemory(g_vk.vk_device, memory, 0, VK_WHOLE_SIZE, 0, &mapped) == VK_SUCCESS) {
    VkMappedMemoryRange range = {VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE};
    range.memory = memory;
    range.size = VK_WHOLE_SIZE;
    dfn.vkInvalidateMappedMemoryRanges(g_vk.vk_device, 1, &range);
    const auto* rgba = static_cast<const uint8_t*>(mapped);
    const size_t pixels = size_t(width) * height;
    uint8_t max_rgb[3] = {};
    std::vector<uint8_t> rgb(pixels * 3);
    for (size_t i = 0; i < pixels; ++i) {
      for (int c = 0; c < 3; ++c) {
        rgb[i * 3 + c] = rgba[i * 4 + c];
        max_rgb[c] = std::max(max_rgb[c], rgba[i * 4 + c]);
      }
    }
    WriteRgbImage(path, rgb.data(), width, height);
    REXLOG_INFO("native renderer: dumped frame {} to {} (max rgb {} {} {})", g.frame, path,
                max_rgb[0], max_rgb[1], max_rgb[2]);
    dfn.vkUnmapMemory(g_vk.vk_device, memory);
  }
  dfn.vkDestroyBuffer(g_vk.vk_device, buffer, nullptr);
  dfn.vkFreeMemory(g_vk.vk_device, memory, nullptr);
}

void DumpGuestFrontBuffer(const uint8_t* base, uint32_t front_buffer, uint64_t frame) {
  if (IsEnabled() || !front_buffer) {
    return;
  }
  const std::string path = DumpTarget(frame);
  if (path.empty()) {
    return;
  }
  if (rex::cvar::GetFlagByName("readback_resolve") == "none") {
    // The front buffer stays on the host GPU (the launcher's sessions): save
    // the presenter's guest output instead, the frame on screen after the
    // gamma ramp, from the latest swap.
    auto* graphics = g_runtime ? g_runtime->graphics_system() : nullptr;
    rex::ui::Presenter* presenter = graphics ? graphics->presenter() : nullptr;
    rex::ui::RawImage image;
    if (!presenter || !presenter->CaptureGuestOutput(image)) {
      REXLOG_WARN("native renderer: Emulated frame {} not saved to {}: no presenter output",
                  frame, path);
      return;
    }
    std::vector<uint8_t> rgb(size_t(image.width) * image.height * 3);
    for (uint32_t y = 0; y < image.height; ++y) {
      for (uint32_t x = 0; x < image.width; ++x) {
        std::memcpy(&rgb[(size_t(y) * image.width + x) * 3], &image.data[y * image.stride + x * 4],
                    3);
      }
    }
    WriteRgbImage(path, rgb.data(), image.width, image.height);
    REXLOG_INFO("native renderer: dumped presenter output of frame {} ({}x{}) to {}", frame,
                image.width, image.height, path);
    return;
  }
  // Emulated draws on the emulated Xenos GPU; with --readback_resolve the front
  // buffer's resolve is in guest memory, in the same layout as any texture.
  const textures::FetchConstant fetch =
      textures::LoadFetchConstant(base + front_buffer + kTextureFetchConstant);
  const uint32_t width = (fetch.dwords[2] & 0x1FFF) + 1;
  const uint32_t height = ((fetch.dwords[2] >> 13) & 0x1FFF) + 1;
  std::vector<uint8_t> rgba;
  if (!textures::ReadFromGuest(fetch, width, height, rgba)) {
    REXLOG_WARN("native renderer: front buffer format not dumpable");
    return;
  }
  // The front buffer is k_8_8_8_8 stored as ARGB: after the endian swap the
  // bytes read B, G, R, A.
  std::vector<uint8_t> rgb(size_t(width) * height * 3);
  for (size_t i = 0; i < size_t(width) * height; ++i) {
    rgb[i * 3 + 0] = rgba[i * 4 + 2];
    rgb[i * 3 + 1] = rgba[i * 4 + 1];
    rgb[i * 3 + 2] = rgba[i * 4];
  }
  WriteRgbImage(path, rgb.data(), width, height);
  REXLOG_INFO("native renderer: dumped guest front buffer of frame {} ({}x{}) to {}", frame,
              width, height, path);
}

// The render thread (the guest thread that draws and presents) keeps its
// time when other programs run, as the pacing thread does: MMCSS Games, else
// highest priority. Once per thread that presents.
static void RaiseRenderThreadPriority() {
  thread_local bool raised = false;
  if (raised) {
    return;
  }
  raised = true;
  namespace pacing = rex::ui::pacing;
  const bool raise = REXCVAR_GET(present_pacing_thread_priority);
  const bool joined = raise && rex::thread::JoinMmcssGames();
  const pacing::ThreadPriorityChoice choice = pacing::ChoosePacingThreadPriority(raise, joined);
  if (choice == pacing::ThreadPriorityChoice::kHighest) {
    rex::thread::SetCurrentThreadPriorityHighest();
  }
  REXLOG_INFO("native renderer: render thread priority {}",
              pacing::ThreadPriorityChoiceName(choice));
}

// The output plan for the window as it is now, so a resize shows on the next frame.
OutputPlan PlanForWindow() {
  const uint64_t size = g_window_size.load(std::memory_order_relaxed);
  return PlanOutput(g.scale, uint32_t(size >> 32), uint32_t(size));
}

void BlitImage(VkCommandBuffer cb, VkImage from, uint32_t from_width, uint32_t from_height,
               VkImage to, uint32_t to_width, uint32_t to_height) {
  VkImageBlit blit = {};
  blit.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
  blit.srcOffsets[1] = {int32_t(from_width), int32_t(from_height), 1};
  blit.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
  blit.dstOffsets[1] = {int32_t(to_width), int32_t(to_height), 1};
  g_vk.vkCmdBlitImage(cb, from, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, to,
                      VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &blit, VK_FILTER_LINEAR);
}

// Downsample step `index` at width x height, made again when its size changes.
State::HalfImage& HalfImageAt(size_t index, uint32_t width, uint32_t height) {
  if (g.halves.size() <= index) {
    g.halves.resize(index + 1);
  }
  State::HalfImage& half = g.halves[index];
  if (half.width != width || half.height != height) {
    if (half.image) {
      Retired retired;
      retired.image = half.image;
      retired.memory = half.memory;
      Retire(std::move(retired));
      half = {};
    }
    if (!CreateTransferImage(width, height,
                             VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                             half.image, half.memory)) {
      EndSessionWithGpuFailure(VK_ERROR_OUT_OF_DEVICE_MEMORY, "downsample image");
    }
    half.width = width;
    half.height = height;
  }
  return half;
}

// Copies the front buffer into the slot's output image, filtered down to the
// output size when the plan says so: 2:1 box steps (a linear blit at exactly
// half size averages 2x2 texels), then one bilinear blit for any rest.
void BlitToOutput(VkCommandBuffer cb, const textures::ResolveTarget& source,
                  const OutputPlan& plan, FrameSlot& slot) {
  VkImage from = source.image;
  uint32_t from_width = source.width, from_height = source.height;
  const uint32_t steps = plan.downsample ? plan.halvings : 0;
  for (uint32_t i = 0; i < steps; ++i) {
    const bool last_box = i + 1 == steps && plan.filter == DownsampleFilter::kBox;
    if (last_box) {
      BlitImage(cb, from, from_width, from_height, slot.image, slot.width, slot.height);
      return;
    }
    State::HalfImage& half = HalfImageAt(i, from_width / 2, from_height / 2);
    ImageBarrier(cb, half.image, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_UNDEFINED,
                 VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    BlitImage(cb, from, from_width, from_height, half.image, half.width, half.height);
    ImageBarrier(cb, half.image, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                 VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    from = half.image;
    from_width = half.width;
    from_height = half.height;
  }
  BlitImage(cb, from, from_width, from_height, slot.image, slot.width, slot.height);
}

void OnPresent(const uint8_t* base, uint32_t device, uint32_t front_buffer) {
  const HookTimer timer;
  if (!g.device) {
    return;
  }
  RaiseRenderThreadPriority();
  BeginFrameIfNeeded();
  EndRendering();
  const VulkanDevice::Functions& dfn = *g_vk.dfn;
  FrameSlot& slot = *g.slot;
  const VkCommandBuffer cb = slot.main_cb;

  if (g.frame % 300 == 0) {
    const Stats& s = g.stats;
    REXLOG_INFO("native renderer: frame {}: drawn={} drawn_indexed={} drawn_non_indexed={} "
                "clears={} resolves={} pipelines={} render_thread_modules={} render_targets={} "
                "readbacks={} readback_ms={:.1f} {}",
                g.frame, s.drawn, s.drawn_indexed, s.drawn_non_indexed, s.clears, s.resolves,
                pipelines::Count(), shader_library::RenderThreadModules(),
                g.render_targets.size(), g.readbacks_delivered, g.readback_ns_total / 1e6,
                pipelines::TakeLinkStats());
    g.stats = {};
    g.readback_ns_total = 0;
  }
  if (std::lock_guard lock(g.skips_mutex); g.frame % 60 == 0 && g.skips.TakeChanged()) {
    REXLOG_INFO("native renderer: draw skips at frame {}: {}", g.frame, g.skips.Summary());
  }
  if (g.frame % 60 == 0) {
    LogRenderThreadTotals(false);
  }

  const OutputPlan plan = PlanForWindow();
  if ((slot.width != plan.output_width || slot.height != plan.output_height) &&
      !CreateOutputImage(slot, plan.output_width, plan.output_height)) {
    EndSessionWithGpuFailure(VK_ERROR_OUT_OF_DEVICE_MEMORY, "output image");
  }
  if (NamingWork()) BeginWork(gpu_timer::Span::kPresent, PresentName(slot.width, slot.height));
  ImageBarrier(cb, slot.image, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_UNDEFINED,
               VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
  textures::ResolveTarget source;
  if (front_buffer) {
    const textures::FetchConstant fetch =
        textures::LoadFetchConstant(base + front_buffer + kTextureFetchConstant);
    source = textures::FindResolveTarget(fetch.dwords[1] & 0xFFFFF000);
  }
  if (g.frame % 300 == 0 || g.frame < 3) {
    REXLOG_INFO("native renderer: present front_buffer={:08X} base={:08X} source={}x{} found={}",
                front_buffer,
                front_buffer ? textures::LoadFetchConstant(base + front_buffer +
                                                           kTextureFetchConstant).dwords[1]
                             : 0,
                source.width, source.height, source.image != VK_NULL_HANDLE);
  }
  if (const uint32_t debug_resolve = REXCVAR_GET(wwe_native_debug_resolve)) {
    const textures::ResolveTarget target = textures::FindResolveTarget(debug_resolve);
    if (target.image) {
      source = target;
    }
  }
  const int32_t debug_target = REXCVAR_GET(wwe_native_debug_target);
  if (debug_target > 0 && size_t(debug_target) <= g.render_target_order.size()) {
    RenderTarget& target = *g.render_target_order[debug_target - 1];
    if (!target.is_depth) {
      TransitionTarget(target, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
      source = {target.image, target.format, target.width, target.height, &target.layout};
    }
  }
  if (source.image) {
    ImageBarrier(cb, source.image, VK_IMAGE_ASPECT_COLOR_BIT, *source.layout,
                 VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    BlitToOutput(cb, source, plan, slot);
    ImageBarrier(cb, source.image, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                 VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
    *source.layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
  } else {
    const VkClearColorValue black = {};
    const VkImageSubresourceRange range = vk_util::InitializeSubresourceRange();
    dfn.vkCmdClearColorImage(cb, slot.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &black, 1,
                             &range);
  }
  // --wwe_native_dump_frame(s): copy the presented image out for a file.
  VkBuffer dump_buffer = VK_NULL_HANDLE;
  VkDeviceMemory dump_memory = VK_NULL_HANDLE;
  VkImageLayout output_layout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
  const std::string dump_path = DumpTarget(g.frame);
  if (!dump_path.empty() &&
      vk_util::CreateDedicatedAllocationBuffer(g.device, VkDeviceSize(slot.width) * slot.height * 4,
                                               VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                                               vk_util::MemoryPurpose::kReadback, dump_buffer,
                                               dump_memory)) {
    ImageBarrier(cb, slot.image, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                 VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    output_layout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    VkBufferImageCopy region = {};
    region.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.imageExtent = {slot.width, slot.height, 1};
    dfn.vkCmdCopyImageToBuffer(cb, slot.image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, dump_buffer,
                               1, &region);
  }
  // Later submissions on the same queue (the plugin's present) fall in the
  // barrier's second scope.
  ImageBarrier(cb, slot.image, VK_IMAGE_ASPECT_COLOR_BIT, output_layout,
               VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
  EndWork();
  EndGpuTimerFrame();
  geometry::EndFrame(slot.upload_cb);
  dfn.vkEndCommandBuffer(slot.upload_cb);
  dfn.vkEndCommandBuffer(slot.main_cb);

  const VkCommandBuffer command_buffers[2] = {slot.upload_cb, slot.main_cb};
  VkSubmitInfo submit_info = {VK_STRUCTURE_TYPE_SUBMIT_INFO};
  submit_info.commandBufferCount = 2;
  submit_info.pCommandBuffers = command_buffers;
  g.recording = false;
  VkResult submitted;
  {
    VulkanDevice::Queue::Acquisition queue =
        g.device->AcquireQueue(g.device->queue_family_graphics_compute(), 0);
    submitted = REXCVAR_GET(wwe_native_debug_fail_submit)
                    ? VK_ERROR_DEVICE_LOST
                    : dfn.vkQueueSubmit(queue.queue(), 1, &submit_info, slot.fence);
  }
  if (submitted != VK_SUCCESS) {
    EndSessionWithGpuFailure(submitted, "vkQueueSubmit");
  }

  if (dump_buffer) {
    WriteFrameDump(dump_buffer, dump_memory, slot.fence, dump_path, slot.width, slot.height);
  }

  rex::system::external_frame::Frame frame;
  frame.image_view = uint64_t(reinterpret_cast<uintptr_t>(slot.view));
  frame.width = slot.width;
  frame.height = slot.height;
  frame.sequence = g.frame;
  rex::system::external_frame::Publish(frame);
  LogHitch();
  ++g.frame;
  PollTraceRequest();
  if (g.frame % 600 == 0) {
    pipelines::RequestSave();
  }
}

}  // namespace wwe::native
