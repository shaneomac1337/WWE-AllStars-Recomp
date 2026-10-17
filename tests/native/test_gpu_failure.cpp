// GPU failure (gpu-robustness ticket 01): the log line and exit code the
// launcher reads (launcher/src/Launcher.Core/GameExitReport.cs).

#include <catch_amalgamated.hpp>

#include "native/gpu_failure.h"

using wwe::native::GpuFailureLine;
using wwe::native::kGpuFailureExitCode;
using wwe::native::VkResultName;

TEST_CASE("the GPU failure exit code is the one the launcher reads") {
  CHECK(kGpuFailureExitCode == 0x47505546);
}

TEST_CASE("the GPU failure line names the Vulkan result and the call in the launcher's format") {
  CHECK(GpuFailureLine(-4, "vkQueueSubmit") ==
        "native renderer: gpu failure VK_ERROR_DEVICE_LOST (vkQueueSubmit); ending the session. "
        "Switch the renderer to Emulated in the launcher if this repeats.");
}

TEST_CASE("Vulkan results by name, and an unknown one by number") {
  CHECK(VkResultName(-2) == "VK_ERROR_OUT_OF_DEVICE_MEMORY");
  CHECK(VkResultName(-1000001004) == "VK_ERROR_OUT_OF_DATE_KHR");
  CHECK(VkResultName(-12345) == "VkResult -12345");
}

TEST_CASE("the GPU failure reason names the Vulkan result and the call") {
  CHECK(wwe::native::GpuFailureReason(-4, "vkWaitForFences") ==
        "VK_ERROR_DEVICE_LOST (vkWaitForFences)");
}
