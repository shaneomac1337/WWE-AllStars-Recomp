#include "native/vk_context.h"

#include <algorithm>
#include <vector>

#include <rex/logging.h>
#include <rex/system/xmemory.h>
#include <rex/ui/vulkan/instance.h>
#include <rex/ui/vulkan/util.h>

namespace wwe::native {

Context g_vk;

namespace {

namespace vk_util = rex::ui::vulkan::util;

struct UploadRing {
  VkBuffer buffer = VK_NULL_HANDLE;
  VkDeviceMemory memory = VK_NULL_HANDLE;
  uint8_t* mapped = nullptr;
  VkDeviceAddress address = 0;
  VkDeviceSize size = 0;
  VkDeviceSize used = 0;
};

// Per frame slot: a list of chunks. A frame that outgrows the first chunk
// gets more (kept for later frames), so work is never dropped.
struct UploadSlot {
  std::vector<UploadRing> chunks;
  size_t current = 0;
};
std::vector<UploadSlot> g_slots;
UploadSlot* g_slot = nullptr;
VkDeviceSize g_chunk_size = 0;
uint32_t g_next_descriptor[kHeapCount] = {};
std::vector<uint32_t> g_free_descriptors[kHeapCount];
std::vector<std::pair<uint64_t, Retired>> g_retired;
bool g_ring_full_logged = false;

bool CreateHeaps() {
  const auto& dfn = *g_vk.dfn;
  constexpr VkDescriptorType kTypes[kHeapCount] = {
      VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE,
      VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, VK_DESCRIPTOR_TYPE_SAMPLER};

  for (uint32_t heap = 0; heap < kHeapCount; ++heap) {
    VkDescriptorSetLayoutBinding binding = {};
    binding.binding = 0;
    binding.descriptorType = kTypes[heap];
    binding.descriptorCount = kHeapCapacity[heap];
    binding.stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    // Textures appear while frames that index the heap are in flight.
    const VkDescriptorBindingFlags binding_flags =
        VK_DESCRIPTOR_BINDING_PARTIALLY_BOUND_BIT | VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT |
        VK_DESCRIPTOR_BINDING_UPDATE_UNUSED_WHILE_PENDING_BIT;
    VkDescriptorSetLayoutBindingFlagsCreateInfo flags_info = {
        VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_BINDING_FLAGS_CREATE_INFO};
    flags_info.bindingCount = 1;
    flags_info.pBindingFlags = &binding_flags;
    VkDescriptorSetLayoutCreateInfo layout_info = {
        VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};
    layout_info.pNext = &flags_info;
    layout_info.flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_UPDATE_AFTER_BIND_POOL_BIT;
    layout_info.bindingCount = 1;
    layout_info.pBindings = &binding;
    if (dfn.vkCreateDescriptorSetLayout(g_vk.vk_device, &layout_info, nullptr,
                                        &g_vk.set_layouts[heap]) != VK_SUCCESS) {
      return false;
    }
  }

  const VkDescriptorPoolSize pool_sizes[] = {
      {VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE,
       kHeapCapacity[kHeapTexture2D] + kHeapCapacity[kHeapTexture3D] +
           kHeapCapacity[kHeapTextureCube]},
      {VK_DESCRIPTOR_TYPE_SAMPLER, kHeapCapacity[kHeapSampler]}};
  VkDescriptorPoolCreateInfo pool_info = {VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
  pool_info.flags = VK_DESCRIPTOR_POOL_CREATE_UPDATE_AFTER_BIND_BIT;
  pool_info.maxSets = kHeapCount;
  pool_info.poolSizeCount = uint32_t(std::size(pool_sizes));
  pool_info.pPoolSizes = pool_sizes;
  if (dfn.vkCreateDescriptorPool(g_vk.vk_device, &pool_info, nullptr, &g_vk.descriptor_pool) !=
      VK_SUCCESS) {
    return false;
  }
  VkDescriptorSetAllocateInfo allocate_info = {VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
  allocate_info.descriptorPool = g_vk.descriptor_pool;
  allocate_info.descriptorSetCount = kHeapCount;
  allocate_info.pSetLayouts = g_vk.set_layouts;
  if (dfn.vkAllocateDescriptorSets(g_vk.vk_device, &allocate_info, g_vk.sets) != VK_SUCCESS) {
    return false;
  }

  VkPushConstantRange push_range = {};
  push_range.stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
  push_range.size = sizeof(PushConstants);
  VkPipelineLayoutCreateInfo pipeline_layout_info = {
      VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};
  pipeline_layout_info.setLayoutCount = kHeapCount;
  pipeline_layout_info.pSetLayouts = g_vk.set_layouts;
  pipeline_layout_info.pushConstantRangeCount = 1;
  pipeline_layout_info.pPushConstantRanges = &push_range;
  return dfn.vkCreatePipelineLayout(g_vk.vk_device, &pipeline_layout_info, nullptr,
                                    &g_vk.pipeline_layout) == VK_SUCCESS;
}

bool CreateUploadRing(UploadRing& ring, VkDeviceSize size) {
  const auto& dfn = *g_vk.dfn;
  VkBufferCreateInfo buffer_info = {VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
  buffer_info.size = size;
  buffer_info.usage = VK_BUFFER_USAGE_SHADER_DEVICE_ADDRESS_BIT |
                      VK_BUFFER_USAGE_VERTEX_BUFFER_BIT | VK_BUFFER_USAGE_INDEX_BUFFER_BIT |
                      VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
  buffer_info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
  if (dfn.vkCreateBuffer(g_vk.vk_device, &buffer_info, nullptr, &ring.buffer) != VK_SUCCESS) {
    return false;
  }
  VkMemoryRequirements requirements;
  dfn.vkGetBufferMemoryRequirements(g_vk.vk_device, ring.buffer, &requirements);
  // Host-coherent so the CPU writes need no flush before the submission.
  const uint32_t memory_type = vk_util::ChooseHostMemoryType(
      g_vk.device->memory_types(),
      requirements.memoryTypeBits & g_vk.device->memory_types().host_coherent, false);
  if (memory_type == UINT32_MAX) {
    return false;
  }
  VkMemoryAllocateFlagsInfo flags_info = {VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_FLAGS_INFO};
  flags_info.flags = VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT;
  VkMemoryAllocateInfo allocate_info = {VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
  allocate_info.pNext = &flags_info;
  allocate_info.allocationSize = requirements.size;
  allocate_info.memoryTypeIndex = memory_type;
  if (dfn.vkAllocateMemory(g_vk.vk_device, &allocate_info, nullptr, &ring.memory) != VK_SUCCESS ||
      dfn.vkBindBufferMemory(g_vk.vk_device, ring.buffer, ring.memory, 0) != VK_SUCCESS ||
      dfn.vkMapMemory(g_vk.vk_device, ring.memory, 0, VK_WHOLE_SIZE, 0,
                      reinterpret_cast<void**>(&ring.mapped)) != VK_SUCCESS) {
    return false;
  }
  VkBufferDeviceAddressInfo address_info = {VK_STRUCTURE_TYPE_BUFFER_DEVICE_ADDRESS_INFO};
  address_info.buffer = ring.buffer;
  ring.address = g_vk.vkGetBufferDeviceAddress(g_vk.vk_device, &address_info);
  ring.size = size;
  return ring.address != 0;
}

}  // namespace

bool InitializeContext(const rex::ui::vulkan::VulkanDevice* device,
                       rex::memory::Memory* memory) {
  const auto& properties = device->properties();
  if (!properties.bufferDeviceAddress || !properties.runtimeDescriptorArray ||
      !properties.descriptorBindingPartiallyBound ||
      !properties.descriptorBindingSampledImageUpdateAfterBind ||
      !properties.descriptorBindingUpdateUnusedWhilePending || !properties.shaderInt64 ||
      !properties.dynamicRendering) {
    REXLOG_ERROR("native renderer: the Vulkan device lacks buffer device address, "
                 "bindless descriptors, shaderInt64 or dynamic rendering");
    return false;
  }
  // The bindless heaps (kHeapCapacity) need these; a device below them would misbehave
  // rather than fail, so a bug report has to show them.
  const auto& instance_functions = device->vulkan_instance()->functions();
  if (instance_functions.vkGetPhysicalDeviceProperties2) {
    VkPhysicalDeviceDescriptorIndexingProperties indexing = {
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_INDEXING_PROPERTIES};
    VkPhysicalDeviceProperties2 properties_2 = {VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
    properties_2.pNext = &indexing;
    instance_functions.vkGetPhysicalDeviceProperties2(device->physical_device(), &properties_2);
    REXLOG_INFO(
        "native renderer: update-after-bind limits: {} sampled images and {} samplers per stage, "
        "{} descriptors in all pools; non-uniform image indexing native {}",
        indexing.maxPerStageDescriptorUpdateAfterBindSampledImages,
        indexing.maxPerStageDescriptorUpdateAfterBindSamplers,
        indexing.maxUpdateAfterBindDescriptorsInAllPools,
        bool(indexing.shaderSampledImageArrayNonUniformIndexingNative));
  }
  g_vk.device = device;
  g_vk.dfn = &device->functions();
  g_vk.vk_device = device->device();
  g_vk.memory = memory;
  const auto get_proc = device->vulkan_instance()->functions().vkGetDeviceProcAddr;
  g_vk.vkGetBufferDeviceAddress = reinterpret_cast<PFN_vkGetBufferDeviceAddress>(
      get_proc(g_vk.vk_device, "vkGetBufferDeviceAddress"));
  if (!g_vk.vkGetBufferDeviceAddress) {
    g_vk.vkGetBufferDeviceAddress = reinterpret_cast<PFN_vkGetBufferDeviceAddress>(
        get_proc(g_vk.vk_device, "vkGetBufferDeviceAddressKHR"));
  }
  g_vk.vkCmdBlitImage =
      reinterpret_cast<PFN_vkCmdBlitImage>(get_proc(g_vk.vk_device, "vkCmdBlitImage"));
  g_vk.vkCmdClearDepthStencilImage = reinterpret_cast<PFN_vkCmdClearDepthStencilImage>(
      get_proc(g_vk.vk_device, "vkCmdClearDepthStencilImage"));
  g_vk.vkCreatePipelineCache = reinterpret_cast<PFN_vkCreatePipelineCache>(
      get_proc(g_vk.vk_device, "vkCreatePipelineCache"));
  g_vk.vkGetPipelineCacheData = reinterpret_cast<PFN_vkGetPipelineCacheData>(
      get_proc(g_vk.vk_device, "vkGetPipelineCacheData"));
  if (!g_vk.vkGetBufferDeviceAddress || !g_vk.vkCmdBlitImage ||
      !g_vk.vkCmdClearDepthStencilImage || !g_vk.vkCreatePipelineCache ||
      !g_vk.vkGetPipelineCacheData) {
    REXLOG_ERROR("native renderer: could not load device functions missing from the SDK table");
    return false;
  }
  return CreateHeaps();
}

const uint8_t* TranslatePhysical(uint32_t physical_address) {
  return g_vk.memory->TranslatePhysical(physical_address);
}

bool CreateUploadRings(uint32_t count, VkDeviceSize size_each) {
  g_chunk_size = size_each;
  g_slots.resize(count);
  for (UploadSlot& slot : g_slots) {
    slot.chunks.resize(1);
    if (!CreateUploadRing(slot.chunks[0], size_each)) {
      return false;
    }
  }
  g_slot = &g_slots[0];
  return true;
}

void BeginUploadFrame(uint32_t slot) {
  g_slot = &g_slots[slot];
  g_slot->current = 0;
  for (UploadRing& chunk : g_slot->chunks) {
    chunk.used = 0;
  }
}

Upload AllocateUpload(VkDeviceSize size, VkDeviceSize alignment) {
  UploadSlot& slot = *g_slot;
  for (;;) {
    UploadRing& ring = slot.chunks[slot.current];
    const VkDeviceSize offset = (ring.used + alignment - 1) & ~(alignment - 1);
    if (offset + size <= ring.size) {
      ring.used = offset + size;
      Upload upload;
      upload.data = ring.mapped + offset;
      upload.buffer = ring.buffer;
      upload.offset = offset;
      upload.address = ring.address + offset;
      return upload;
    }
    if (slot.current + 1 < slot.chunks.size()) {
      ++slot.current;
      continue;
    }
    UploadRing chunk;
    const VkDeviceSize chunk_size = std::max(g_chunk_size, size + alignment);
    if (!CreateUploadRing(chunk, chunk_size)) {
      if (!g_ring_full_logged) {
        g_ring_full_logged = true;
        REXLOG_WARN("native renderer: could not grow the upload ring; dropping work");
      }
      return {};
    }
    REXLOG_INFO("native renderer: upload ring grew to {} chunks", slot.chunks.size() + 1);
    slot.chunks.push_back(chunk);
    slot.current = slot.chunks.size() - 1;
  }
}

std::optional<uint32_t> AllocateDescriptor(DescriptorHeap heap) {
  if (!g_free_descriptors[heap].empty()) {
    const uint32_t index = g_free_descriptors[heap].back();
    g_free_descriptors[heap].pop_back();
    return index;
  }
  if (g_next_descriptor[heap] >= kHeapCapacity[heap]) {
    return std::nullopt;
  }
  return g_next_descriptor[heap]++;
}

void WriteImageDescriptor(DescriptorHeap heap, uint32_t index, VkImageView view) {
  VkDescriptorImageInfo image_info = {};
  image_info.imageView = view;
  image_info.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
  VkWriteDescriptorSet write = {VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
  write.dstSet = g_vk.sets[heap];
  write.dstArrayElement = index;
  write.descriptorCount = 1;
  write.descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
  write.pImageInfo = &image_info;
  g_vk.dfn->vkUpdateDescriptorSets(g_vk.vk_device, 1, &write, 0, nullptr);
}

void WriteSamplerDescriptor(uint32_t index, VkSampler sampler) {
  VkDescriptorImageInfo image_info = {};
  image_info.sampler = sampler;
  VkWriteDescriptorSet write = {VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
  write.dstSet = g_vk.sets[kHeapSampler];
  write.dstArrayElement = index;
  write.descriptorCount = 1;
  write.descriptorType = VK_DESCRIPTOR_TYPE_SAMPLER;
  write.pImageInfo = &image_info;
  g_vk.dfn->vkUpdateDescriptorSets(g_vk.vk_device, 1, &write, 0, nullptr);
}

void Retire(Retired&& objects) { g_retired.emplace_back(g_vk.frame, std::move(objects)); }

void DestroyRetired(uint64_t completed_frame) {
  const auto& dfn = *g_vk.dfn;
  size_t kept = 0;
  for (size_t i = 0; i < g_retired.size(); ++i) {
    if (g_retired[i].first > completed_frame) {
      if (kept != i) {
        g_retired[kept] = std::move(g_retired[i]);
      }
      ++kept;
      continue;
    }
    const Retired& objects = g_retired[i].second;
    for (VkImageView view : objects.views) {
      dfn.vkDestroyImageView(g_vk.vk_device, view, nullptr);
    }
    if (objects.image) {
      dfn.vkDestroyImage(g_vk.vk_device, objects.image, nullptr);
    }
    if (objects.buffer) {
      dfn.vkDestroyBuffer(g_vk.vk_device, objects.buffer, nullptr);
    }
    if (objects.memory) {
      dfn.vkFreeMemory(g_vk.vk_device, objects.memory, nullptr);
    }
    for (const auto& [heap, index] : objects.descriptors) {
      if (index) {
        g_free_descriptors[heap].push_back(index);
      }
    }
  }
  g_retired.resize(kept);
}

void BindDescriptorHeaps(VkCommandBuffer command_buffer) {
  g_vk.dfn->vkCmdBindDescriptorSets(command_buffer, VK_PIPELINE_BIND_POINT_GRAPHICS,
                                    g_vk.pipeline_layout, 0, kHeapCount, g_vk.sets, 0, nullptr);
}

}  // namespace wwe::native
