// Vulkan objects the native renderer shares between its parts: per-frame
// upload rings addressed by buffer device address, the bindless descriptor
// heaps the converted shaders index, and their pipeline layout.

#pragma once

#include <cstdint>
#include <optional>
#include <utility>
#include <vector>

#include <rex/ui/vulkan/device.h>

namespace rex::memory {
class Memory;
}

namespace wwe::native {

// Descriptor sets as XenosRecomp's shader_common.h declares them.
enum DescriptorHeap : uint32_t {
  kHeapTexture2D,
  kHeapTexture3D,
  kHeapTextureCube,
  kHeapSampler,
  kHeapCount
};

constexpr uint32_t kHeapCapacity[kHeapCount] = {8192, 512, 512, 1024};

// Push constants the converted shaders read (g_PushConstants).
struct PushConstants {
  uint64_t vertex_constants;
  uint64_t pixel_constants;
  uint64_t shared_constants;
};

// Shared constants layout for SVR2011_RECOMP (patches/xenosrecomp-svr2011.patch):
// per 32 fetch slots, 2D / 3D / cube texture indices, then sampler indices,
// then the unified boolean file and the remaining shader parameters.
constexpr uint32_t kFetchSlots = 32;
struct SharedConstants {
  uint32_t texture_2d[kFetchSlots];    // 0
  uint32_t texture_3d[kFetchSlots];    // 128
  uint32_t texture_cube[kFetchSlots];  // 256
  uint32_t sampler[kFetchSlots];       // 384
  uint32_t booleans[8];                // 512, VS bits 0..127, PS bits 128..255
  uint32_t swapped_texcoords;          // 544
  float half_pixel_offset[2];          // 548
  float alpha_threshold;               // 556
  float ndc_scale[2];                  // 560, g_NdcScale
};
static_assert(sizeof(SharedConstants) == 568);

struct Upload {
  uint8_t* data = nullptr;
  VkBuffer buffer = VK_NULL_HANDLE;
  VkDeviceSize offset = 0;
  VkDeviceAddress address = 0;
};

struct Context {
  const rex::ui::vulkan::VulkanDevice* device = nullptr;
  const rex::ui::vulkan::VulkanDevice::Functions* dfn = nullptr;
  VkDevice vk_device = VK_NULL_HANDLE;
  rex::memory::Memory* memory = nullptr;

  // Not in the SDK's function tables.
  PFN_vkGetBufferDeviceAddress vkGetBufferDeviceAddress = nullptr;
  PFN_vkCmdBlitImage vkCmdBlitImage = nullptr;
  PFN_vkCmdClearDepthStencilImage vkCmdClearDepthStencilImage = nullptr;
  PFN_vkCreatePipelineCache vkCreatePipelineCache = nullptr;
  PFN_vkGetPipelineCacheData vkGetPipelineCacheData = nullptr;

  // The frame being recorded; resources retired now are destroyed once it
  // has completed on the GPU.
  uint64_t frame = 0;

  VkDescriptorSetLayout set_layouts[kHeapCount] = {};
  VkDescriptorPool descriptor_pool = VK_NULL_HANDLE;
  VkDescriptorSet sets[kHeapCount] = {};
  VkPipelineLayout pipeline_layout = VK_NULL_HANDLE;
};

extern Context g_vk;

// Loads the extra functions and creates the heaps and the pipeline layout.
bool InitializeContext(const rex::ui::vulkan::VulkanDevice* device, rex::memory::Memory* memory);

// Host pointer to guest physical memory (fetch constants hold physical addresses).
const uint8_t* TranslatePhysical(uint32_t physical_address);

// Upload rings, one per frame slot; the caller waits for the slot's fence
// before BeginUploadFrame reuses it.
bool CreateUploadRings(uint32_t count, VkDeviceSize size_each);
void BeginUploadFrame(uint32_t slot);
// Space in the current frame's ring; data is nullptr when the ring is full.
Upload AllocateUpload(VkDeviceSize size, VkDeviceSize alignment = 16);

// Bindless slots. Index 0 of each heap holds a default resource, so unbound
// fetch slots read something valid. Freed slots are reused.
// Empty when the heap is full. Index 0 goes to the first caller, the defaults (textures.cpp);
// DestroyRetired never frees it, so a view on a full heap can keep sampling them.
std::optional<uint32_t> AllocateDescriptor(DescriptorHeap heap);
void WriteImageDescriptor(DescriptorHeap heap, uint32_t index, VkImageView view);
void WriteSamplerDescriptor(uint32_t index, VkSampler sampler);
void BindDescriptorHeaps(VkCommandBuffer command_buffer);

// Objects frames in flight may still use: Retire queues them with the frame
// being recorded, DestroyRetired destroys those whose frame has completed.
struct Retired {
  VkImage image = VK_NULL_HANDLE;
  VkBuffer buffer = VK_NULL_HANDLE;
  VkDeviceMemory memory = VK_NULL_HANDLE;
  std::vector<VkImageView> views;
  std::vector<std::pair<DescriptorHeap, uint32_t>> descriptors;
};
void Retire(Retired&& objects);
void DestroyRetired(uint64_t completed_frame);

}  // namespace wwe::native
