// Guest textures and samplers for the native renderer: fetch constants
// decoded into bindless image and sampler indices, and images that resolves
// write, standing in for the guest memory the emulator no longer fills.

#pragma once

#include <cstdint>
#include <vector>

#include <rex/ui/vulkan/api.h>

#include "native/texture_formats.h"

namespace wwe::native::textures {

// scale: the native resolution scale; resolve targets are that much larger
// than their guest textures.
// colour: how gamma fetches are decoded (texture_formats.h, GammaFetch).
// lod_bias: the render resolution's texture LOD bias (guest::PlanOutput);
// --wwe_native_lod_bias, when set, replaces it.
bool Initialize(uint32_t scale, ColourMode colour, float lod_bias);

// A texture fetch constant as the guest stores it (six big-endian dwords).
struct FetchConstant {
  uint32_t dwords[6];
};
FetchConstant LoadFetchConstant(const uint8_t* big_endian);

struct Binding {
  uint32_t texture_index = 0;  // in the heap matching the dimension
  uint32_t sampler_index = 0;
  uint32_t dimension = 1;  // xenos::DataDimension: 1 = 2D, 2 = 3D / stacked, 3 = cube
  // False when the fetch constant cannot be drawn (1D/3D, unknown format, no
  // data); the draw is skipped as no_texture rather than drawn with the default.
  bool supported = true;
  uint32_t base_address = 0;
};

// Records guest-memory uploads into upload_cb when a texture is new or its
// data changed. Reading and converting guest data runs on worker threads;
// without wait, a texture still being read binds its previous content (or
// the default image when new), with wait the read is waited for.
// Unsupported textures bind the default image and report supported = false.
Binding Bind(const FetchConstant& fetch, VkCommandBuffer upload_cb, uint64_t frame, bool wait);

// Stops the worker threads; call before the device goes away.
void Shutdown();

// The image a resolve writes for a destination texture fetch constant,
// created on first use in TRANSFER_DST-compatible form. Later Binds of a
// fetch constant at the same address and format sample it.
struct ResolveTarget {
  VkImage image = VK_NULL_HANDLE;
  VkFormat format = VK_FORMAT_UNDEFINED;
  uint32_t width = 0;
  uint32_t height = 0;
  VkImageLayout* layout = nullptr;
  uint32_t scale = 1;  // host size / guest size
};
// scale: the source render target's host scale; the image is the guest size
// times it.
ResolveTarget GetResolveTarget(const FetchConstant& fetch, bool red_blue_swapped,
                               uint32_t scale);

// Whether a colour resolve into this texture stores red and blue swapped
// (RB_COPY_DEST_INFO copy_dest_swap, which the mirror does not hold): D3D
// swaps for ARGB textures, whose ZYXW fetch swizzle swaps them back, so the
// destination's swizzle tells. Resolve copies stay RGBA, so their views
// undo the swizzle's swap.
bool ResolvesRedBlueSwapped(const FetchConstant& fetch);

// Textures uploaded since the last call, and the CPU time checking them took.
struct UploadStats {
  uint64_t count;
  uint64_t bytes;
  uint64_t ns;        // render thread time on textures
  uint64_t wait_ns;   // of which waiting for workers
  uint64_t read_ns;   // reading guest data, any thread
  uint64_t watch_ns;  // of which write-protecting pages
  uint64_t hash_ns;   // of which hashing guest data
};
void TakeUploadStats(UploadStats& stats);

// At each frame's start, before any Bind: takes this frame's Texture filtering
// (wwe_native_anisotropy). A change reaches the samplers draws look up from
// this frame on; the old ones stay for frames still in flight.
void BeginFrame(uint64_t frame);

// Destroys guest-memory textures no draw has used for a while, so memory does
// not grow with every arena and attire the session loads.
void EvictUnused(uint64_t frame);

// The resolve target at a guest physical address, or an empty result.
ResolveTarget FindResolveTarget(uint32_t base_address);

// Bytes per pixel of a resolve destination's guest format, or 0 when its
// guest layout is not written back (compressed or unknown formats).
uint32_t GuestBytesPerPixel(const FetchConstant& fetch);

// Physical address and byte length of level 0 of a 2D texture; length 0 for
// formats GuestBytesPerPixel does not cover.
struct GuestRange {
  uint32_t physical = 0;
  uint32_t length = 0;
};
GuestRange Level0Range(const FetchConstant& fetch);

// Whether every byte of the format is an 8-bit unorm channel, so scaled
// readbacks can average bytes.
bool BytewiseUnorm(const FetchConstant& fetch);

// Writes resolved pixels (host order, tightly packed rows) into guest
// memory in the destination's layout: tiled or linear, endian-swapped.
// Un-tiles a level-0 32-bit-per-pixel image from guest memory into packed
// host-order rows (debug frame dumps). False for other formats.
bool ReadFromGuest(const FetchConstant& raw, uint32_t width, uint32_t height,
                   std::vector<uint8_t>& pixels);

void WriteToGuest(const FetchConstant& fetch, const uint8_t* pixels, uint32_t width,
                  uint32_t height);

}  // namespace wwe::native::textures
