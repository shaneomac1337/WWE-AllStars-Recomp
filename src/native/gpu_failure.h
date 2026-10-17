// GPU failure (CONTEXT.md, ADR-0025): Native was running and the GPU or driver
// stopped working under it. The game logs one line and exits with its own exit
// code; it does not try to recover. The launcher reads both
// (launcher/src/Launcher.Core/GameExitReport.cs): keep the wording and the code.

#pragma once

#include <cstdint>
#include <string>

namespace wwe::native {

// "GPUF" in ASCII; GameExitReport.GpuFailureExitCode in the launcher.
inline constexpr int kGpuFailureExitCode = 0x47505546;

// Takes the VkResult as an integer so this header needs no Vulkan include.
inline std::string VkResultName(int32_t result) {
  switch (result) {
    case -1: return "VK_ERROR_OUT_OF_HOST_MEMORY";
    case -2: return "VK_ERROR_OUT_OF_DEVICE_MEMORY";
    case -3: return "VK_ERROR_INITIALIZATION_FAILED";
    case -4: return "VK_ERROR_DEVICE_LOST";
    case -5: return "VK_ERROR_MEMORY_MAP_FAILED";
    case -13: return "VK_ERROR_UNKNOWN";
    case -1000000000: return "VK_ERROR_SURFACE_LOST_KHR";
    case -1000001004: return "VK_ERROR_OUT_OF_DATE_KHR";
    case -1000174001: return "VK_ERROR_NOT_PERMITTED";
    case -1000255000: return "VK_ERROR_FULL_SCREEN_EXCLUSIVE_MODE_LOST_EXT";
  }
  return "VkResult " + std::to_string(result);
}

// "<VkResult> (<call>)": the reason in the log line and the crash report.
inline std::string GpuFailureReason(int32_t result, const char* call) {
  return VkResultName(result) + " (" + call + ")";
}

// The line the game logs before it exits; the launcher looks for
// "native renderer: gpu failure ".
inline std::string GpuFailureLine(int32_t result, const char* call) {
  return "native renderer: gpu failure " + GpuFailureReason(result, call) +
         "; ending the session. Switch the renderer to Emulated in the launcher if this repeats.";
}

}  // namespace wwe::native
