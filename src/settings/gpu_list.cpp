// `wweallstars.exe --list-gpus` and the GPU names the in-game page shows: the
// Vulkan devices in the order the SDK indexes vulkan_device
// (src/ui/vulkan/vulkan_provider.cpp enumerates the same way).

#include "settings/gpu_list.h"

#include <algorithm>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include <fmt/format.h>
#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/runtime.h>
#include <rex/ui/vulkan/instance.h>
#include <rex/ui/vulkan/provider.h>

#include "settings/gpu_suggestion.h"

namespace wwe::settings {

namespace {

std::vector<VkPhysicalDevice> Devices(const rex::ui::vulkan::VulkanInstance& instance) {
  std::vector<VkPhysicalDevice> devices;
  instance.EnumeratePhysicalDevices(devices);
  return devices;
}

std::string DeviceName(const rex::ui::vulkan::VulkanInstance& instance, VkPhysicalDevice device) {
  VkPhysicalDeviceProperties properties = {};
  instance.functions().vkGetPhysicalDeviceProperties(device, &properties);
  return properties.deviceName;
}

std::vector<std::string> Names(const rex::ui::vulkan::VulkanInstance& instance) {
  std::vector<std::string> names;
  for (VkPhysicalDevice device : Devices(instance)) names.push_back(DeviceName(instance, device));
  return names;
}

// What the suggestion rules read: device type, IDs and the largest device-local heap.
GpuFacts Facts(const rex::ui::vulkan::VulkanInstance& instance, VkPhysicalDevice device) {
  VkPhysicalDeviceProperties properties = {};
  instance.functions().vkGetPhysicalDeviceProperties(device, &properties);
  VkPhysicalDeviceMemoryProperties memory = {};
  instance.functions().vkGetPhysicalDeviceMemoryProperties(device, &memory);
  GpuFacts facts;
  facts.type = static_cast<GpuType>(properties.deviceType);
  facts.vendor_id = properties.vendorID;
  facts.device_id = properties.deviceID;
  for (uint32_t i = 0; i < memory.memoryHeapCount; ++i) {
    if (memory.memoryHeaps[i].flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) {
      facts.largest_local_heap = std::max<uint64_t>(facts.largest_local_heap, memory.memoryHeaps[i].size);
    }
  }
  return facts;
}

// The SDK's score for Automatic (src/ui/vulkan/vulkan_provider.cpp): one point per
// preferred feature the GPU supports.
uint32_t AutomaticScore(const rex::ui::vulkan::VulkanInstance& instance, VkPhysicalDevice device) {
  VkPhysicalDeviceFeatures features = {};
  instance.functions().vkGetPhysicalDeviceFeatures(device, &features);
  using rex::cvar::Query;
  return uint32_t(Query<bool>("vulkan_prefer_geometry_shader") && features.geometryShader) +
         uint32_t(Query<bool>("vulkan_prefer_fragment_stores_and_atomics") &&
                  features.fragmentStoresAndAtomics) +
         uint32_t(Query<bool>("vulkan_prefer_vertex_pipeline_stores_and_atomics") &&
                  features.vertexPipelineStoresAndAtomics) +
         uint32_t(Query<bool>("vulkan_prefer_fill_mode_non_solid") && features.fillModeNonSolid);
}

std::vector<GpuLine> Lines(const rex::ui::vulkan::VulkanInstance& instance) {
  std::vector<GpuLine> lines;
  std::vector<uint32_t> scores;
  for (VkPhysicalDevice device : Devices(instance)) {
    lines.push_back({DeviceName(instance, device), SuggestedPreset(Facts(instance, device))});
    scores.push_back(AutomaticScore(instance, device));
  }
  if (!lines.empty()) lines[AutomaticIndex(scores)].automatic = true;
  return lines;
}

rex::ui::vulkan::VulkanProvider* Provider(rex::Runtime* runtime) {
  auto* graphics_system = runtime ? runtime->graphics_system() : nullptr;
  // This build only has the Vulkan backend, so the provider is a VulkanProvider.
  return graphics_system
             ? static_cast<rex::ui::vulkan::VulkanProvider*>(graphics_system->provider())
             : nullptr;
}

}  // namespace

void PrintGpusAndExitIfAsked() {
  if (!GpusRequested(ProcessArguments())) {
    return;
  }
  // No surface and no validation: only the device list is needed.
  const std::unique_ptr<rex::ui::vulkan::VulkanInstance> instance =
      rex::ui::vulkan::VulkanInstance::Create(false, false);
  PrintAndExit(GpuListText(instance ? Lines(*instance) : std::vector<GpuLine>{}));
}

std::vector<std::string> RuntimeGpuNames(rex::Runtime* runtime) {
  auto* provider = Provider(runtime);
  return provider && provider->vulkan_instance() ? Names(*provider->vulkan_instance())
                                                 : std::vector<std::string>{};
}

void LogGpu(rex::Runtime* runtime) {
  auto* provider = Provider(runtime);
  if (!provider || !provider->vulkan_instance() || !provider->vulkan_device()) {
    return;
  }
  const std::string name = provider->vulkan_device()->properties().deviceName;
  const VkPhysicalDevice chosen = provider->vulkan_device()->physical_device();
  const std::vector<VkPhysicalDevice> devices = Devices(*provider->vulkan_instance());
  int64_t index = -1;
  for (size_t i = 0; i < devices.size(); ++i) {
    if (devices[i] == chosen) index = int64_t(i);
  }
  const int32_t wanted = rex::cvar::Query<int32_t>("vulkan_device");
  const std::string suggested = RuntimeSuggestedPreset(runtime);
  if (wanted < 0) {
    REXLOG_INFO("GPU: {} (vulkan_device {}), chosen automatically; suggested preset {}", name,
                index, suggested);
  } else if (wanted == index) {
    REXLOG_INFO("GPU: {} (vulkan_device {}), as set; suggested preset {}", name, index, suggested);
  } else {
    REXLOG_WARN("GPU: {} (vulkan_device {}), chosen automatically: the set GPU {} is missing or "
                "cannot run the game",
                name, index, wanted);
  }
}

std::pair<std::string, std::string> RuntimeGpuAndDriver(rex::Runtime* runtime) {
  auto* provider = Provider(runtime);
  if (!provider || !provider->vulkan_device()) {
    return {};
  }
  const auto& properties = provider->vulkan_device()->properties();
  return {properties.deviceName, fmt::format("0x{:08X}", properties.driverVersion)};
}

std::string RuntimeSuggestedPreset(rex::Runtime* runtime) {
  auto* provider = Provider(runtime);
  if (!provider || !provider->vulkan_instance() || !provider->vulkan_device()) {
    return {};
  }
  return SuggestedPreset(Facts(*provider->vulkan_instance(), provider->vulkan_device()->physical_device()));
}

}  // namespace wwe::settings
