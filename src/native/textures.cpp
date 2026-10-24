#include "native/textures.h"
#include "native/texture_formats.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

#define XXH_INLINE_ALL
#include <xxhash.h>

#include <fmt/format.h>
#include <rex/cvar.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>
#include <rex/logging.h>
#include <rex/ui/vulkan/util.h>

#include "native/guest_decode.h"
#include "native/sampler_choice.h"
#include "native/texture_layout.h"
#include "native/thread_budget.h"
#include "native/vk_context.h"
#include "native/write_watch.h"

REXCVAR_DECLARE(int32_t, wwe_native_anisotropy);  // settings_cvars.cpp
REXCVAR_DEFINE_BOOL(wwe_native_texture_write_watch, true, "WWEAllStars",
                    "Native renderer: skip re-checking textures whose guest memory was not "
                    "written since the last upload (off: hash every texture every frame)");
REXCVAR_DEFINE_BOOL(wwe_native_async_textures, true, "WWEAllStars",
                    "Native renderer: read and convert textures on worker threads; main-scene "
                    "draws show the previous content (or nothing) for a frame or two instead of "
                    "stalling while new textures load");
REXCVAR_DEFINE_DOUBLE(wwe_native_lod_bias, 0.0, "WWEAllStars",
                      "Native renderer debugging: added to the mip level of mipmapped textures; "
                      "negative values keep distant surfaces sharper at the cost of some shimmer "
                      "(left unset: follows the render resolution)");
REXCVAR_DEFINE_UINT32(wwe_native_dump_texture, 0, "WWEAllStars",
                      "Native renderer debugging: write the texture at this physical address "
                      "(1: every texture) to native_texture_<address>_<size>.dds when it is "
                      "uploaded");

namespace wwe::native::textures {

namespace {

// Writes decoded (host-order) blocks as a DDS for inspection; DXT1/3/5 and
// RGBA8 only.
void DumpDds(uint32_t address, VkFormat format, uint32_t width, uint32_t height,
             const uint8_t* data, size_t size) {
  uint32_t header[32] = {};
  header[0] = 0x20534444;  // "DDS "
  header[1] = 124;
  header[2] = 0x1 | 0x2 | 0x4 | 0x1000;
  header[3] = height;
  header[4] = width;
  header[19] = 32;
  switch (format) {
    case VK_FORMAT_BC1_RGBA_UNORM_BLOCK: header[20] = 0x4; header[21] = 0x31545844; break;
    case VK_FORMAT_BC2_UNORM_BLOCK: header[20] = 0x4; header[21] = 0x33545844; break;
    case VK_FORMAT_BC3_UNORM_BLOCK: header[20] = 0x4; header[21] = 0x35545844; break;
    case VK_FORMAT_R8G8B8A8_UNORM:
      header[20] = 0x41;
      header[22] = 32;
      header[23] = 0xFF;
      header[24] = 0xFF00;
      header[25] = 0xFF0000;
      header[26] = 0xFF000000;
      break;
    default:
      return;
  }
  header[27] = 0x1000;
  const std::string path =
      fmt::format("native_texture_{:08X}_{}x{}.dds", address, width, height);
  if (FILE* file = std::fopen(path.c_str(), "wb")) {
    std::fwrite(header, 1, sizeof(header), file);
    std::fwrite(data, 1, size, file);
    std::fclose(file);
    REXLOG_INFO("native renderer: dumped texture to {}", path);
  }
}

namespace xenos = rex::graphics::xenos;
namespace texture_util = rex::graphics::texture_util;
namespace vk_util = rex::ui::vulkan::util;

using guest::ByteSwap32;
using guest::ToPhysical;
using texture_layout::EndianSwap;

struct Texture {
  VkImage image = VK_NULL_HANDLE;
  VkDeviceMemory memory = VK_NULL_HANDLE;
  VkFormat format = VK_FORMAT_UNDEFINED;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t levels = 1;
  uint32_t layers = 1;
  VkImageLayout layout = VK_IMAGE_LAYOUT_UNDEFINED;
  // Guest swizzle (12 bits) plus the gamma bit (bit 12) -> a view with that
  // mapping and format, and its bindless index.
  struct View {
    VkImageView view;
    DescriptorHeap heap;
    uint32_t index;
  };
  std::unordered_map<uint32_t, View> views;
  uint64_t content_hash = 0;
  // Guest memory the upload read: base level and mips (physical, bytes).
  uint32_t base_range[2] = {};
  uint32_t mips_range[2] = {};
  uint32_t watch_token = 0;
  // The last check could not upload: check again next frame.
  bool upload_failed = false;
  uint64_t checked_frame = UINT64_MAX;
  uint64_t last_used = 0;
  // Written in consecutive frames: not watched (hashed every frame) until
  // dynamic_until, so its writes cost no page faults.
  uint64_t dirty_frame = UINT64_MAX;
  uint32_t dirty_streak = 0;
  uint64_t dynamic_until = 0;
  // A read of guest memory in progress on a worker (or done, to apply).
  std::shared_ptr<struct TextureJob> job;
  bool uploaded = false;
  bool is_resolve_target = false;
  bool red_blue_swapped = false;
};

struct State {
  // Guest-memory textures by fetch constant (address, format, size, layout).
  std::unordered_map<uint64_t, std::unique_ptr<Texture>> textures;
  // Resolve destinations by physical base address.
  std::unordered_map<uint32_t, std::unique_ptr<Texture>> resolve_targets;
  std::unordered_map<uint64_t, uint32_t> samplers;  // SamplerChoice key -> bindless index
  // wwe_native_anisotropy as of this frame's start (BeginFrame), so one frame
  // never mixes two settings; read first in Initialize.
  int32_t texture_filtering = 0;
  Texture defaults[3];  // 2D, 3D, cube; bindless index 0 of each heap
  bool defaults_ready = false;
  uint32_t unsupported_logged = 0;
  uint32_t scale = 1;
  ColourMode colour = ColourMode::kNatural;  // fixed for the session
  float lod_bias = 0.0f;                     // fixed for the session
  // Render thread: textures uploaded, bytes, and its own time on textures
  // (applying finished reads, reading inline, waiting for workers).
  uint64_t upload_count = 0;
  uint64_t upload_bytes = 0;
  uint64_t upload_ns = 0;
  uint64_t wait_ns = 0;
  // Any thread reading guest textures.
  std::atomic<uint64_t> read_ns{0};
  std::atomic<uint64_t> watch_ns{0};  // of which write-protecting pages
  std::atomic<uint64_t> hash_ns{0};   // of which hashing guest data
  // Worker threads and their queue.
  std::mutex mutex;
  std::condition_variable_any work_ready;
  std::condition_variable_any job_done;
  std::deque<std::shared_ptr<struct TextureJob>> queue;
  std::vector<std::jthread> workers;
} g;

uint64_t NsSince(std::chrono::steady_clock::time_point start) {
  return uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(
                      std::chrono::steady_clock::now() - start)
                      .count());
}

// Guest textures unused this many frames are destroyed (reloaded if needed).
constexpr uint64_t kEvictAfterFrames = 1800;
constexpr uint32_t kDynamicStreak = 3;
constexpr uint64_t kDynamicFrames = 600;

// Destroys the texture's image, memory and views once frames in flight are
// done with them, and frees its bindless slots.
void RetireTexture(Texture& texture) {
  Retired retired;
  retired.image = texture.image;
  retired.memory = texture.memory;
  for (const auto& [swizzle, view] : texture.views) {
    retired.views.push_back(view.view);
    retired.descriptors.emplace_back(view.heap, view.index);
  }
  Retire(std::move(retired));
  texture.image = VK_NULL_HANDLE;
  texture.memory = VK_NULL_HANDLE;
  texture.views.clear();
}

bool CreateImage(Texture& texture, VkImageType type, VkFormat format, uint32_t width,
                 uint32_t height, uint32_t depth, uint32_t layers, VkImageCreateFlags flags,
                 VkImageUsageFlags usage, uint32_t levels = 1) {
  VkImageCreateInfo image_info = {VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
  image_info.flags = flags;
  // Formats with an sRGB twin can be sampled through either view: gamma
  // fetches decode on sample (ticket 39).
  const VkFormat view_formats[2] = {format, GammaFormat(format)};
  VkImageFormatListCreateInfo format_list = {VK_STRUCTURE_TYPE_IMAGE_FORMAT_LIST_CREATE_INFO};
  if (view_formats[1] != VK_FORMAT_UNDEFINED) {
    image_info.flags |= VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
    format_list.viewFormatCount = 2;
    format_list.pViewFormats = view_formats;
    image_info.pNext = &format_list;
  }
  image_info.imageType = type;
  image_info.format = format;
  image_info.extent = {width, height, depth};
  image_info.mipLevels = levels;
  image_info.arrayLayers = layers;
  image_info.samples = VK_SAMPLE_COUNT_1_BIT;
  image_info.tiling = VK_IMAGE_TILING_OPTIMAL;
  image_info.usage = usage;
  image_info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
  image_info.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
  if (!vk_util::CreateDedicatedAllocationImage(g_vk.device, image_info,
                                               vk_util::MemoryPurpose::kDeviceLocal,
                                               texture.image, texture.memory)) {
    return false;
  }
  texture.format = format;
  texture.width = width;
  texture.height = height;
  texture.levels = levels;
  texture.layers = layers;
  return true;
}

VkComponentSwizzle Swizzle(uint32_t selector, bool red_blue_swapped) {
  if (red_blue_swapped && (selector == 0 || selector == 2)) {
    selector ^= 2;
  }
  switch (selector) {
    case 0: return VK_COMPONENT_SWIZZLE_R;
    case 1: return VK_COMPONENT_SWIZZLE_G;
    case 2: return VK_COMPONENT_SWIZZLE_B;
    case 3: return VK_COMPONENT_SWIZZLE_A;
    case 4: return VK_COMPONENT_SWIZZLE_ZERO;
    default: return VK_COMPONENT_SWIZZLE_ONE;
  }
}

// gamma: sample through the sRGB view (Xenos gamma fetch), when the format has one.
uint32_t ViewIndex(Texture& texture, uint32_t swizzle, DescriptorHeap heap,
                   VkImageViewType view_type, bool gamma = false) {
  gamma = gamma && GammaFormat(texture.format) != VK_FORMAT_UNDEFINED;
  const uint32_t view_key = swizzle | uint32_t(gamma) << 12;
  const auto it = texture.views.find(view_key);
  if (it != texture.views.end()) {
    return it->second.index;
  }
  VkImageViewCreateInfo view_info = {VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
  view_info.image = texture.image;
  view_info.viewType = view_type;
  view_info.format = gamma ? GammaFormat(texture.format) : texture.format;
  view_info.components.r = Swizzle(swizzle & 7, texture.red_blue_swapped);
  view_info.components.g = Swizzle((swizzle >> 3) & 7, texture.red_blue_swapped);
  view_info.components.b = Swizzle((swizzle >> 6) & 7, texture.red_blue_swapped);
  view_info.components.a = Swizzle((swizzle >> 9) & 7, texture.red_blue_swapped);
  view_info.subresourceRange = vk_util::InitializeSubresourceRange();
  if (view_type == VK_IMAGE_VIEW_TYPE_CUBE) {
    view_info.subresourceRange.layerCount = 6;
  }
  VkImageView view;
  if (g_vk.dfn->vkCreateImageView(g_vk.vk_device, &view_info, nullptr, &view) != VK_SUCCESS) {
    return 0;
  }
  const uint32_t index = AllocateDescriptor(heap);
  if (index) {
    WriteImageDescriptor(heap, index, view);
  }
  texture.views.emplace(view_key, Texture::View{view, heap, index});
  return index;
}

// A fetch's view index: Enhanced samples gamma through the sRGB view, Original
// flags the index so the shader PWL-decodes after filtering (GammaFetch).
uint32_t FetchViewIndex(Texture& texture, uint32_t swizzle, DescriptorHeap heap,
                        VkImageViewType view_type, const FetchConstant& raw) {
  const GammaFetchPath path =
      GammaFetch(g.colour, FetchSamplesGamma(raw.dwords[0]), texture.format);
  return ViewIndex(texture, swizzle, heap, view_type, path.srgb_view) |
         (path.shader_decode ? kPwlFetchFlag : 0u);
}

void Barrier(VkCommandBuffer cb, Texture& texture, VkImageLayout new_layout,
             VkPipelineStageFlags src_stage, VkAccessFlags src_access,
             VkPipelineStageFlags dst_stage, VkAccessFlags dst_access, uint32_t layers = 1) {
  VkImageMemoryBarrier barrier = {VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
  barrier.srcAccessMask = src_access;
  barrier.dstAccessMask = dst_access;
  barrier.oldLayout = texture.layout;
  barrier.newLayout = new_layout;
  barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
  barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
  barrier.image = texture.image;
  // All levels and layers.
  barrier.subresourceRange = vk_util::InitializeSubresourceRange();
  g_vk.dfn->vkCmdPipelineBarrier(cb, src_stage, dst_stage, 0, 0, nullptr, 0, nullptr, 1,
                                 &barrier);
  texture.layout = new_layout;
}

void PrepareDefaults(VkCommandBuffer cb) {
  const uint32_t layer_counts[3] = {1, 1, 6};
  for (uint32_t i = 0; i < 3; ++i) {
    Texture& texture = g.defaults[i];
    Barrier(cb, texture, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
            0, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_ACCESS_TRANSFER_WRITE_BIT, layer_counts[i]);
    const VkClearColorValue transparent_black = {};
    VkImageSubresourceRange range = vk_util::InitializeSubresourceRange();
    range.layerCount = layer_counts[i];
    g_vk.dfn->vkCmdClearColorImage(cb, texture.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                   &transparent_black, 1, &range);
    Barrier(cb, texture, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_PIPELINE_STAGE_VERTEX_SHADER_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
            VK_ACCESS_SHADER_READ_BIT, layer_counts[i]);
  }
  g.defaults_ready = true;
}

VkFilter Filter(uint32_t filter) { return filter == 0 ? VK_FILTER_NEAREST : VK_FILTER_LINEAR; }

VkSamplerAddressMode AddressMode(uint32_t clamp) {
  switch (xenos::ClampMode(clamp)) {
    case xenos::ClampMode::kRepeat:
      return VK_SAMPLER_ADDRESS_MODE_REPEAT;
    case xenos::ClampMode::kMirroredRepeat:
      return VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT;
    case xenos::ClampMode::kClampToBorder:
    case xenos::ClampMode::kMirrorClampToBorder:
      return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER;
    default:
      return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
  }
}

uint32_t SamplerIndex(const xenos::xe_gpu_texture_fetch_t& fetch, uint32_t min_level = 0,
                      uint32_t max_level = 15) {
  const auto& properties = g_vk.device->properties();
  const SamplerChoice choice =
      ChooseSampler(fetch, min_level, max_level, g.texture_filtering,
                    properties.samplerAnisotropy ? properties.maxSamplerAnisotropy : 0.0f);
  const float anisotropy = choice.anisotropy;
  const auto it = g.samplers.find(choice.key);
  if (it != g.samplers.end()) {
    return it->second;
  }
  VkSamplerCreateInfo sampler_info = {VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
  sampler_info.magFilter = Filter(uint32_t(fetch.mag_filter));
  sampler_info.minFilter = Filter(uint32_t(fetch.min_filter));
  sampler_info.mipmapMode = uint32_t(fetch.mip_filter) == 1 ? VK_SAMPLER_MIPMAP_MODE_LINEAR
                                                             : VK_SAMPLER_MIPMAP_MODE_NEAREST;
  sampler_info.addressModeU = AddressMode(uint32_t(fetch.clamp_x));
  sampler_info.addressModeV = AddressMode(uint32_t(fetch.clamp_y));
  sampler_info.addressModeW = AddressMode(uint32_t(fetch.clamp_z));
  // The image holds levels 0..max_level; kBaseMap samples the first only.
  sampler_info.minLod = float(min_level);
  sampler_info.maxLod = uint32_t(fetch.mip_filter) == 2 ? float(min_level) : float(max_level);
  if (sampler_info.maxLod > sampler_info.minLod) {
    // Vulkan guarantees |bias| <= 2 on every device.
    sampler_info.mipLodBias = std::clamp(g.lod_bias, -2.0f, 2.0f);
  }
  if (anisotropy > 1.0f) {
    // As in the emulated path, anisotropic filtering is fully linear.
    sampler_info.magFilter = sampler_info.minFilter = VK_FILTER_LINEAR;
    sampler_info.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR;
    sampler_info.anisotropyEnable = VK_TRUE;
    sampler_info.maxAnisotropy = anisotropy;
  }
  switch (uint32_t(fetch.border_color)) {
    case 1: sampler_info.borderColor = VK_BORDER_COLOR_FLOAT_OPAQUE_BLACK; break;
    case 2: sampler_info.borderColor = VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE; break;
    default: sampler_info.borderColor = VK_BORDER_COLOR_FLOAT_TRANSPARENT_BLACK; break;
  }
  VkSampler sampler;
  uint32_t index = 0;
  if (g_vk.dfn->vkCreateSampler(g_vk.vk_device, &sampler_info, nullptr, &sampler) ==
      VK_SUCCESS) {
    index = AllocateDescriptor(kHeapSampler);
    if (index) {
      WriteSamplerDescriptor(index, sampler);
    }
  }
  g.samplers.emplace(choice.key, index);
  return index;
}

// Guest texture: 2D or cube, all mip levels (base from base_page, the rest
// from mip_page, with the packed mip tail) per the SDK's guest layout.
struct GuestTexture {
  xenos::xe_gpu_texture_fetch_t fetch;
  FormatInfo info;
  uint32_t width;
  uint32_t height;
  uint32_t layers;
  uint32_t base_page;
  uint32_t mip_page;
  uint32_t min_level;
  uint32_t max_level;
};

// One read of a guest texture: done by a worker (or inline), then applied on
// the render thread, which records the copy into the image.
struct TextureJob {
  GuestTexture t;
  bool cube = false;
  bool create_image = false;  // the texture has no image yet
  bool watch = false;
  bool had_upload = false;
  uint64_t previous_hash = 0;
  std::atomic<bool> done{false};
  // Results.
  Texture image;  // image and memory when create_image
  bool image_failed = false;
  bool changed = false;
  uint64_t hash = 0;
  uint32_t watch_token = 0;
  uint32_t base_range[2] = {};
  uint32_t mips_range[2] = {};
  std::vector<uint8_t> data;  // host-order levels, tightly packed
  VkBufferImageCopy regions[16] = {};
  uint32_t region_count = 0;
};

// Any thread: creates the image if needed, reads, hashes and converts the
// guest data. Only touches the job.
void ReadTexture(TextureJob& job) {
  const auto start = std::chrono::steady_clock::now();
  const GuestTexture& t = job.t;
  if (job.create_image &&
      !CreateImage(job.image, VK_IMAGE_TYPE_2D, t.info.format, t.width, t.height, 1, t.layers,
                   job.cube ? VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT : 0,
                   VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                   t.max_level + 1)) {
    job.image_failed = true;
    g.read_ns += NsSince(start);
    return;
  }
  const xenos::xe_gpu_texture_fetch_t& fetch = t.fetch;
  const uint32_t block = t.info.block_size;
  const uint32_t bpb_log2 = t.info.bytes_per_block_log2;
  const uint32_t bytes_per_block = 1u << bpb_log2;
  const texture_util::TextureGuestLayout layout = texture_util::GetGuestTextureLayout(
      fetch.dimension, uint32_t(fetch.pitch), t.width, t.height, t.layers, fetch.tiled,
      fetch.format, fetch.packed_mips, t.base_page != 0, t.max_level);
  const uint8_t* base = t.base_page ? TranslatePhysical(t.base_page << 12) : nullptr;
  const uint8_t* mips = t.mip_page ? TranslatePhysical(t.mip_page << 12) : nullptr;
  job.base_range[0] = t.base_page << 12;
  job.base_range[1] = base ? layout.base.level_data_extent_bytes : 0;
  job.mips_range[0] = t.mip_page << 12;
  job.mips_range[1] = mips && t.max_level ? layout.mips_total_extent_bytes : 0;
  if (job.watch) {
    // Before reading: a write after this makes the texture dirty again.
    const auto watch_start = std::chrono::steady_clock::now();
    job.watch_token = std::min(write_watch::Watch(job.base_range[0], job.base_range[1]),
                               write_watch::Watch(job.mips_range[0], job.mips_range[1]));
    g.watch_ns += NsSince(watch_start);
  }
  const auto hash_start = std::chrono::steady_clock::now();
  uint64_t hash = 0;
  if (base) {
    hash = XXH3_64bits(base, layout.base.level_data_extent_bytes);
  }
  if (mips && t.max_level) {
    hash ^= XXH3_64bits(mips, layout.mips_total_extent_bytes) * 31;
  }
  g.hash_ns += NsSince(hash_start);
  job.hash = hash;
  job.changed = !job.had_upload || hash != job.previous_hash;
  if (!job.changed) {
    g.read_ns += NsSince(start);
    return;
  }

  // Host copy: level by level, each layer tightly packed.
  size_t total = 0;
  for (uint32_t level = 0; level <= t.max_level; ++level) {
    const uint32_t bx = (std::max(t.width >> level, 1u) + block - 1) / block;
    const uint32_t by = (std::max(t.height >> level, 1u) + block - 1) / block;
    total += size_t(bx) * by * bytes_per_block * t.layers;
  }
  job.data.resize(total);
  size_t offset = 0;
  for (uint32_t level = 0; level <= t.max_level; ++level) {
    const uint32_t level_width = std::max(t.width >> level, 1u);
    const uint32_t level_height = std::max(t.height >> level, 1u);
    const uint32_t bx = (level_width + block - 1) / block;
    const uint32_t by = (level_height + block - 1) / block;
    const size_t row_bytes = size_t(bx) * bytes_per_block;
    // Where the level is stored: the base, a mip, or inside the packed tail.
    const bool from_base = level == 0;
    const uint8_t* source = from_base ? base : mips;
    const uint32_t stored_level = std::min(level, layout.packed_level);
    const texture_util::TextureGuestLayout::Level& stored =
        from_base ? layout.base : layout.mips[stored_level];
    if (!from_base) {
      source = mips ? mips + layout.mip_offsets_bytes[stored_level] : nullptr;
    }
    uint32_t tail_x = 0, tail_y = 0, tail_z = 0;
    if (level >= layout.packed_level) {
      texture_util::GetPackedMipOffset(t.width, t.height, 1, fetch.format, level, tail_x, tail_y,
                                       tail_z);
    }
    for (uint32_t layer = 0; layer < t.layers; ++layer) {
      uint8_t* dest = job.data.data() + offset + size_t(layer) * row_bytes * by;
      if (!source) {
        std::memset(dest, 0, row_bytes * by);
        continue;
      }
      texture_layout::CopyLevelFromGuest(
          dest, source + size_t(layer) * stored.array_slice_stride_bytes, bx, by, bpb_log2,
          stored.row_pitch_bytes, fetch.tiled, tail_x, tail_y);
    }
    VkBufferImageCopy& region = job.regions[job.region_count++];
    region.bufferOffset = offset;  // relative; the upload offset is added when applied
    region.bufferRowLength = bx * block;
    region.bufferImageHeight = by * block;
    region.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, level, 0, t.layers};
    region.imageExtent = {level_width, level_height, 1};
    offset += row_bytes * by * t.layers;
  }
  EndianSwap(job.data.data(), total, fetch.endianness);
  if (const uint32_t dump = REXCVAR_GET(wwe_native_dump_texture);
      dump == 1 || (dump && dump == t.base_page << 12)) {
    DumpDds(t.base_page << 12, t.info.format, t.width, t.height, job.data.data(),
            size_t((t.width + block - 1) / block) * ((t.height + block - 1) / block) *
                bytes_per_block);
  }
  g.read_ns += NsSince(start);
}

// Render thread: takes a finished read into the texture, recording the copy.
void ApplyJob(Texture& texture, TextureJob& job, VkCommandBuffer cb) {
  if (job.create_image) {
    if (job.image_failed) {
      REXLOG_ERROR("native renderer: could not create a {}x{} texture", job.t.width,
                   job.t.height);
      return;
    }
    texture.image = job.image.image;
    texture.memory = job.image.memory;
    texture.format = job.image.format;
    texture.width = job.image.width;
    texture.height = job.image.height;
    texture.levels = job.image.levels;
    texture.layers = job.image.layers;
    texture.layout = VK_IMAGE_LAYOUT_UNDEFINED;
  }
  texture.base_range[0] = job.base_range[0];
  texture.base_range[1] = job.base_range[1];
  texture.mips_range[0] = job.mips_range[0];
  texture.mips_range[1] = job.mips_range[1];
  texture.watch_token = job.watch_token;
  texture.upload_failed = false;
  if (!job.changed) {
    return;
  }
  const Upload upload = AllocateUpload(job.data.size(), 16);
  if (!upload.data) {
    texture.upload_failed = true;
    return;
  }
  std::memcpy(upload.data, job.data.data(), job.data.size());
  for (uint32_t i = 0; i < job.region_count; ++i) {
    job.regions[i].bufferOffset += upload.offset;
  }
  texture.content_hash = job.hash;
  texture.uploaded = true;
  ++g.upload_count;
  g.upload_bytes += job.data.size();
  Barrier(cb, texture, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
          VK_PIPELINE_STAGE_VERTEX_SHADER_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0,
          VK_PIPELINE_STAGE_TRANSFER_BIT, VK_ACCESS_TRANSFER_WRITE_BIT);
  g_vk.dfn->vkCmdCopyBufferToImage(cb, upload.buffer, texture.image,
                                   VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, job.region_count,
                                   job.regions);
  Barrier(cb, texture, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_PIPELINE_STAGE_TRANSFER_BIT,
          VK_ACCESS_TRANSFER_WRITE_BIT,
          VK_PIPELINE_STAGE_VERTEX_SHADER_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
          VK_ACCESS_SHADER_READ_BIT);
}

void Work(std::stop_token stop) {
  for (;;) {
    std::shared_ptr<TextureJob> job;
    {
      std::unique_lock lock(g.mutex);
      g.work_ready.wait(lock, stop, [] { return !g.queue.empty(); });
      if (stop.stop_requested()) {
        return;
      }
      job = std::move(g.queue.front());
      g.queue.pop_front();
    }
    ReadTexture(*job);
    {
      std::lock_guard lock(g.mutex);
      job->done = true;
    }
    g.job_done.notify_all();
  }
}

// Render thread: applies the texture's read once it is done. With wait, a
// read still queued or running is waited for.
void FinishJob(Texture& texture, VkCommandBuffer cb, bool wait) {
  TextureJob* job = texture.job.get();
  if (!job) {
    return;
  }
  if (!job->done) {
    if (!wait) {
      return;
    }
    const auto start = std::chrono::steady_clock::now();
    std::unique_lock lock(g.mutex);
    g.job_done.wait(lock, [job] { return job->done.load(); });
    g.wait_ns += NsSince(start);
  }
  ApplyJob(texture, *job, cb);
  texture.job.reset();
}

xenos::xe_gpu_texture_fetch_t Decode(const FetchConstant& fetch) {
  xenos::xe_gpu_texture_fetch_t decoded;
  std::memcpy(&decoded, fetch.dwords, sizeof(decoded));
  return decoded;
}

}  // namespace

FetchConstant LoadFetchConstant(const uint8_t* big_endian) {
  FetchConstant fetch;
  for (uint32_t i = 0; i < 6; ++i) {
    uint32_t v;
    std::memcpy(&v, big_endian + i * 4, 4);
    fetch.dwords[i] = ByteSwap32(v);
  }
  return fetch;
}

bool Initialize(uint32_t scale, ColourMode colour, float lod_bias) {
  g.scale = scale;
  g.colour = colour;
  g.lod_bias = rex::cvar::HasNonDefaultValue("wwe_native_lod_bias")
                   ? float(REXCVAR_GET(wwe_native_lod_bias))
                   : lod_bias;
  g.texture_filtering = REXCVAR_GET(wwe_native_anisotropy);
  // A prepare starts no texture work, so play's count serves both.
  const uint32_t workers =
      thread_budget::ForPlay(std::thread::hardware_concurrency()).textures;
  REXLOG_INFO("native renderer: {} texture workers", workers);
  for (uint32_t i = 0; i < workers; ++i) {
    g.workers.emplace_back(Work);
  }
  const VkImageUsageFlags usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
  if (!CreateImage(g.defaults[0], VK_IMAGE_TYPE_2D, VK_FORMAT_R8G8B8A8_UNORM, 1, 1, 1, 1, 0,
                   usage) ||
      !CreateImage(g.defaults[1], VK_IMAGE_TYPE_3D, VK_FORMAT_R8G8B8A8_UNORM, 1, 1, 1, 1, 0,
                   usage) ||
      !CreateImage(g.defaults[2], VK_IMAGE_TYPE_2D, VK_FORMAT_R8G8B8A8_UNORM, 1, 1, 1, 6,
                   VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT, usage)) {
    return false;
  }
  // Index 0 of every heap.
  ViewIndex(g.defaults[0], 0x688, kHeapTexture2D, VK_IMAGE_VIEW_TYPE_2D);
  ViewIndex(g.defaults[1], 0x688, kHeapTexture3D, VK_IMAGE_VIEW_TYPE_3D);
  ViewIndex(g.defaults[2], 0x688, kHeapTextureCube, VK_IMAGE_VIEW_TYPE_CUBE);
  xenos::xe_gpu_texture_fetch_t linear_clamp = {};
  linear_clamp.clamp_x = linear_clamp.clamp_y = linear_clamp.clamp_z =
      xenos::ClampMode::kClampToEdge;
  linear_clamp.mag_filter = linear_clamp.min_filter = xenos::TextureFilter::kLinear;
  SamplerIndex(linear_clamp);
  return true;
}

Binding Bind(const FetchConstant& raw, VkCommandBuffer upload_cb, uint64_t frame, bool wait) {
  if (!g.defaults_ready) {
    PrepareDefaults(upload_cb);
  }
  GuestTexture t;
  t.fetch = Decode(raw);
  const xenos::xe_gpu_texture_fetch_t& fetch = t.fetch;
  Binding binding;
  if (!guest::IsTextureFetch(raw.dwords[0], raw.dwords[1])) {
    return binding;
  }
  binding.dimension = uint32_t(fetch.dimension);
  const bool cube = fetch.dimension == xenos::DataDimension::kCube;
  if (fetch.dimension != xenos::DataDimension::k2DOrStacked && !cube) {
    // No traced scene uses 1D or 3D textures; say so if one ever does.
    if (g.unsupported_logged++ < 16) {
      REXLOG_WARN("native renderer: {}D texture at {:08X} not supported yet",
                  fetch.dimension == xenos::DataDimension::k1D ? 1 : 3,
                  ToPhysical(uint32_t(fetch.base_address) << 12));
    }
    binding.sampler_index = SamplerIndex(fetch);
    binding.supported = false;
    binding.base_address = ToPhysical(uint32_t(fetch.base_address) << 12);
    return binding;
  }
  const uint32_t base_address = ToPhysical(uint32_t(fetch.base_address) << 12);
  binding.base_address = base_address;
  const uint32_t swizzle = uint32_t(fetch.swizzle);

  if (!cube) {
    const auto resolved = g.resolve_targets.find(base_address);
    if (resolved != g.resolve_targets.end()) {
      binding.sampler_index = SamplerIndex(fetch, 0, 0);
      binding.texture_index =
          FetchViewIndex(*resolved->second, swizzle, kHeapTexture2D, VK_IMAGE_VIEW_TYPE_2D, raw);
      return binding;
    }
  }

  t.info = GetFormatInfo(fetch.format);
  if (t.info.format == VK_FORMAT_UNDEFINED) {
    binding.sampler_index = SamplerIndex(fetch);
    if (g.unsupported_logged++ < 16) {
      // 24_8 textures exist only as depth resolve targets; one that is not
      // means the resolve that should have written it found no depth target.
      REXLOG_WARN("native renderer: texture format {} at {:08X} not supported yet",
                  uint32_t(fetch.format), base_address);
    }
    binding.supported = false;
    return binding;
  }
  uint32_t width_minus_1, height_minus_1, depth_minus_1;
  texture_util::GetSubresourcesFromFetchConstant(fetch, &width_minus_1, &height_minus_1,
                                                 &depth_minus_1, &t.base_page, &t.mip_page,
                                                 &t.min_level, &t.max_level);
  t.width = width_minus_1 + 1;
  t.height = height_minus_1 + 1;
  // Stacked 2D arrays use their first layer for now (no traced scene has one).
  t.layers = cube ? 6 : 1;
  if (!cube && depth_minus_1 && g.unsupported_logged++ < 16) {
    REXLOG_WARN("native renderer: stacked texture at {:08X} ({} layers) draws its first layer",
                base_address, depth_minus_1 + 1);
  }
  if (t.base_page) {
    t.base_page = ToPhysical(t.base_page << 12) >> 12;
  }
  if (t.mip_page) {
    t.mip_page = ToPhysical(t.mip_page << 12) >> 12;
  }
  binding.sampler_index = SamplerIndex(fetch, t.min_level, t.max_level);
  if (!t.base_page && !t.mip_page) {
    binding.supported = false;
    return binding;
  }

  const uint64_t key =
      XXH3_64bits(&fetch, sizeof(fetch.dword_0) * 3) ^
      (uint64_t(t.mip_page) << 40 | uint64_t(t.max_level) << 32 | uint64_t(fetch.packed_mips) << 36);
  std::unique_ptr<Texture>& slot = g.textures[key];
  if (!slot) {
    slot = std::make_unique<Texture>();
  }
  Texture& texture = *slot;
  texture.last_used = frame;
  const auto start = std::chrono::steady_clock::now();
  FinishJob(texture, upload_cb, wait);
  if (texture.checked_frame != frame && !texture.job) {
    texture.checked_frame = frame;
    const bool watch =
        REXCVAR_GET(wwe_native_texture_write_watch) && frame >= texture.dynamic_until;
    const bool clean =
        watch && texture.uploaded && !texture.upload_failed &&
        (!texture.base_range[1] ||
         !write_watch::IsDirty(texture.base_range[0], texture.base_range[1],
                               texture.watch_token)) &&
        (!texture.mips_range[1] ||
         !write_watch::IsDirty(texture.mips_range[0], texture.mips_range[1],
                               texture.watch_token));
    if (!clean) {
      if (watch && texture.uploaded) {
        texture.dirty_streak = texture.dirty_frame + 1 == frame ? texture.dirty_streak + 1 : 1;
        texture.dirty_frame = frame;
        if (texture.dirty_streak >= kDynamicStreak) {
          texture.dynamic_until = frame + kDynamicFrames;
          texture.dirty_streak = 0;
        }
      }
      auto job = std::make_shared<TextureJob>();
      job->t = t;
      job->cube = cube;
      job->create_image = !texture.image;
      job->watch = watch && frame >= texture.dynamic_until;
      job->had_upload = texture.uploaded;
      job->previous_hash = texture.content_hash;
      texture.job = job;
      if (wait || !REXCVAR_GET(wwe_native_async_textures) || g.workers.empty()) {
        ReadTexture(*job);
        job->done = true;
        FinishJob(texture, upload_cb, true);
      } else {
        std::lock_guard lock(g.mutex);
        g.queue.push_back(std::move(job));
        g.work_ready.notify_one();
      }
    }
  }
  g.upload_ns += NsSince(start);
  if (!texture.image) {
    if (!texture.job) {
      g.textures.erase(key);  // the image could not be created; try again later
    }
    return binding;
  }
  if (!texture.uploaded) {
    return binding;
  }
  binding.texture_index =
      cube ? FetchViewIndex(texture, swizzle, kHeapTextureCube, VK_IMAGE_VIEW_TYPE_CUBE, raw)
           : FetchViewIndex(texture, swizzle, kHeapTexture2D, VK_IMAGE_VIEW_TYPE_2D, raw);
  return binding;
}

ResolveTarget GetResolveTarget(const FetchConstant& raw, bool red_blue_swapped,
                               uint32_t scale) {
  const xenos::xe_gpu_texture_fetch_t fetch = Decode(raw);
  const uint32_t base_address = ToPhysical(uint32_t(fetch.base_address) << 12);
  const uint32_t width = (fetch.size_2d.width + 1) * scale;
  const uint32_t height = (fetch.size_2d.height + 1) * scale;
  FormatInfo info = GetFormatInfo(fetch.format);
  if (fetch.format == xenos::TextureFormat::k_24_8 ||
      fetch.format == xenos::TextureFormat::k_24_8_FLOAT) {
    // Depth resolves: the depth value as a float in X.
    info.format = VK_FORMAT_R32_SFLOAT;
  } else if (info.format == VK_FORMAT_UNDEFINED || info.block_size != 1) {
    info.format = VK_FORMAT_R8G8B8A8_UNORM;
  }
  std::unique_ptr<Texture>& slot = g.resolve_targets[base_address];
  if (slot && (slot->width != width || slot->height != height || slot->format != info.format)) {
    RetireTexture(*slot);
    slot.reset();
  }
  if (!slot) {
    slot = std::make_unique<Texture>();
    slot->is_resolve_target = true;
    if (!CreateImage(*slot, VK_IMAGE_TYPE_2D, info.format, width, height, 1, 1, 0,
                     VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT |
                         VK_IMAGE_USAGE_TRANSFER_SRC_BIT)) {
      slot.reset();
      return {};
    }
  }
  if (slot->red_blue_swapped != red_blue_swapped) {
    slot->red_blue_swapped = red_blue_swapped;
    Retired retired;
    for (const auto& [swizzle, view] : slot->views) {
      retired.views.push_back(view.view);
      retired.descriptors.emplace_back(view.heap, view.index);
    }
    Retire(std::move(retired));
    slot->views.clear();
  }
  return {slot->image, slot->format, slot->width, slot->height, &slot->layout, scale};
}

uint32_t GuestBytesPerPixel(const FetchConstant& raw) {
  const FormatInfo info = GetFormatInfo(Decode(raw).format);
  return info.format != VK_FORMAT_UNDEFINED && info.block_size == 1
             ? 1u << info.bytes_per_block_log2
             : 0;
}

GuestRange Level0Range(const FetchConstant& raw) {
  const xenos::xe_gpu_texture_fetch_t fetch = Decode(raw);
  const FormatInfo info = GetFormatInfo(fetch.format);
  if (info.format == VK_FORMAT_UNDEFINED || info.block_size != 1 || !fetch.base_address) {
    return {};
  }
  return {ToPhysical(uint32_t(fetch.base_address) << 12),
          texture_layout::Level0Bytes(uint32_t(fetch.pitch) << 5, fetch.size_2d.height + 1,
                                      info.bytes_per_block_log2, fetch.tiled)};
}

bool ResolvesRedBlueSwapped(const FetchConstant& raw) {
  const xenos::xe_gpu_texture_fetch_t fetch = Decode(raw);
  switch (fetch.format) {
    case xenos::TextureFormat::k_8_8_8_8:
    case xenos::TextureFormat::k_8_8_8_8_AS_16_16_16_16:
    case xenos::TextureFormat::k_2_10_10_10:
    case xenos::TextureFormat::k_16_16_16_16_FLOAT:
      return (uint32_t(fetch.swizzle) & 7) == 2;  // X reads Z
    default:
      return false;
  }
}

bool BytewiseUnorm(const FetchConstant& raw) {
  switch (Decode(raw).format) {
    case xenos::TextureFormat::k_8_8_8_8:
    case xenos::TextureFormat::k_8_8_8_8_A:
    case xenos::TextureFormat::k_8_8_8_8_AS_16_16_16_16:
    case xenos::TextureFormat::k_8:
    case xenos::TextureFormat::k_8_A:
    case xenos::TextureFormat::k_8_B:
    case xenos::TextureFormat::k_8_8:
      return true;
    default:
      return false;
  }
}

void WriteToGuest(const FetchConstant& raw, const uint8_t* pixels, uint32_t width,
                  uint32_t height) {
  const xenos::xe_gpu_texture_fetch_t fetch = Decode(raw);
  const FormatInfo info = GetFormatInfo(fetch.format);
  if (info.format == VK_FORMAT_UNDEFINED || info.block_size != 1) {
    return;
  }
  const uint32_t bpp = 1u << info.bytes_per_block_log2;
  const uint32_t pitch = uint32_t(fetch.pitch) << 5;
  const uint32_t linear_row_bytes = texture_layout::LinearRowBytes(pitch, info.bytes_per_block_log2);
  const uint32_t physical = ToPhysical(uint32_t(fetch.base_address) << 12);
  auto* dest = const_cast<uint8_t*>(TranslatePhysical(physical));
  // These writes bypass the guest's page protection; cached copies of the
  // pages must still see them.
  write_watch::MarkWritten(
      physical, texture_layout::Level0Bytes(pitch, height, info.bytes_per_block_log2, fetch.tiled));
  std::vector<uint8_t> row(size_t(width) * bpp);
  for (uint32_t y = 0; y < height; ++y) {
    std::memcpy(row.data(), pixels + size_t(y) * width * bpp, row.size());
    // The same swap converts host order to guest order and back.
    EndianSwap(row.data(), row.size(), fetch.endianness);
    if (fetch.tiled) {
      for (uint32_t x = 0; x < width; ++x) {
        std::memcpy(dest + texture_layout::GuestOffset(x, y, pitch, info.bytes_per_block_log2,
                                                       true, linear_row_bytes),
                    row.data() + size_t(x) * bpp, bpp);
      }
    } else {
      std::memcpy(dest + size_t(y) * linear_row_bytes, row.data(), row.size());
    }
  }
}

// The inverse of WriteToGuest: un-tiles a level-0 image of a 32-bit-per-pixel
// format from guest memory into packed host-order rows. False for other formats.
bool ReadFromGuest(const FetchConstant& raw, uint32_t width, uint32_t height,
                   std::vector<uint8_t>& pixels) {
  const xenos::xe_gpu_texture_fetch_t fetch = Decode(raw);
  const FormatInfo info = GetFormatInfo(fetch.format);
  if (info.format == VK_FORMAT_UNDEFINED || info.block_size != 1 ||
      info.bytes_per_block_log2 != 2) {
    return false;
  }
  const uint32_t bpp = 4;
  const uint32_t pitch = uint32_t(fetch.pitch) << 5;
  const uint32_t linear_row_bytes = texture_layout::LinearRowBytes(pitch, info.bytes_per_block_log2);
  const uint8_t* source =
      TranslatePhysical(ToPhysical(uint32_t(fetch.base_address) << 12));
  pixels.resize(size_t(width) * height * bpp);
  for (uint32_t y = 0; y < height; ++y) {
    uint8_t* row = pixels.data() + size_t(y) * width * bpp;
    for (uint32_t x = 0; x < width; ++x) {
      const uint32_t offset =
          fetch.tiled ? texture_layout::GuestOffset(x, y, pitch, info.bytes_per_block_log2, true,
                                                    linear_row_bytes)
                      : y * linear_row_bytes + x * bpp;
      std::memcpy(row + size_t(x) * bpp, source + offset, bpp);
    }
    EndianSwap(row, size_t(width) * bpp, fetch.endianness);
  }
  return true;
}

void TakeUploadStats(UploadStats& stats) {
  stats = {g.upload_count,         g.upload_bytes,         g.upload_ns,         g.wait_ns,
           g.read_ns.exchange(0), g.watch_ns.exchange(0), g.hash_ns.exchange(0)};
  g.upload_count = g.upload_bytes = g.upload_ns = g.wait_ns = 0;
}

void Shutdown() {
  for (std::jthread& worker : g.workers) {
    worker.request_stop();
  }
  g.work_ready.notify_all();
  g.workers.clear();  // joins
}

void BeginFrame(uint64_t frame) {
  const int32_t texture_filtering = REXCVAR_GET(wwe_native_anisotropy);
  if (texture_filtering != g.texture_filtering) {
    // Old samplers stay for frames in flight; each level used adds its own to
    // the sampler heap (kHeapCapacity), so the count shows the headroom left.
    REXLOG_INFO("native renderer: texture filtering {} -> {} at frame {}; samplers are rebuilt as "
                "draws need them ({} samplers so far)",
                g.texture_filtering, texture_filtering, frame, g.samplers.size());
  }
  g.texture_filtering = texture_filtering;
}

void EvictUnused(uint64_t frame) {
  for (auto it = g.textures.begin(); it != g.textures.end();) {
    if (it->second->last_used + kEvictAfterFrames < frame && !it->second->job) {
      RetireTexture(*it->second);
      it = g.textures.erase(it);
    } else {
      ++it;
    }
  }
}

ResolveTarget FindResolveTarget(uint32_t base_address) {
  const auto it = g.resolve_targets.find(ToPhysical(base_address));
  if (it == g.resolve_targets.end()) {
    return {};
  }
  Texture& texture = *it->second;
  return {texture.image, texture.format, texture.width, texture.height, &texture.layout};
}

}  // namespace wwe::native::textures
