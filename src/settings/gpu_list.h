// The GPUs the game can run on, for the GPU setting (vulkan_device): the
// launcher asks `wweallstars.exe --list-gpus` instead of linking Vulkan itself
// (launcher spec, "New registry entries"), and the in-game page lists the same
// names.

#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "settings/headless.h"

namespace rex {
class Runtime;
}

namespace wwe::settings {

// The command-line switch that prints the GPUs and exits before the runtime starts.
inline constexpr std::string_view kListGpusSwitch = "--list-gpus";

inline bool GpusRequested(const std::vector<std::string>& args) {
  return HasSwitch(args, kListGpusSwitch);
}

struct GpuLine {
  std::string name;
  std::string suggested;   // the suggested Graphics preset (settings/gpu_suggestion.h)
  bool automatic = false;  // the GPU vulkan_device -1 (Automatic) opens
};

// One "gpu <index>: <name>\tsuggested=<preset>" line per GPU, in Vulkan's
// order, so the index is the vulkan_device value that picks it; the GPU
// Automatic opens ends in "\tautomatic". A device name never holds a tab, so
// the fields cannot run into it. The launcher reads only these lines
// (Launcher.Core GpuList.Parse), with or without the fields.
inline std::string GpuListText(const std::vector<GpuLine>& gpus) {
  std::string out;
  for (size_t i = 0; i < gpus.size(); ++i) {
    out += "gpu " + std::to_string(i) + ": " + gpus[i].name + "\tsuggested=" + gpus[i].suggested +
           (gpus[i].automatic ? "\tautomatic" : "") + "\n";
  }
  return out;
}

// The GPU Automatic opens, by each GPU's feature score in Vulkan's order: the
// SDK (src/ui/vulkan/vulkan_provider.cpp) stable-sorts by score, best first,
// and keeps Vulkan's order on a tie. That GPU could still fail to start; the
// SDK then tries the next, which no list can know.
inline size_t AutomaticIndex(const std::vector<uint32_t>& scores) {
  size_t best = 0;
  for (size_t i = 1; i < scores.size(); ++i) {
    if (scores[i] > scores[best]) best = i;
  }
  return best;
}

// The rest needs Vulkan and is defined in gpu_list.cpp (the game only).

// With --list-gpus on the command line: prints GpuListText of the GPUs Vulkan
// reports and exits. Otherwise returns.
void PrintGpusAndExitIfAsked();

// The names of the GPUs the running game's Vulkan instance reports, in
// vulkan_device order; empty without one.
std::vector<std::string> RuntimeGpuNames(rex::Runtime* runtime);

// The name and driver version (hex, as Vulkan reports it) of the GPU the game
// runs on, for the crash report; empty without one.
std::pair<std::string, std::string> RuntimeGpuAndDriver(rex::Runtime* runtime);

// Logs the GPU the game runs on, with its vulkan_device index and suggested preset.
void LogGpu(rex::Runtime* runtime);

// The suggested Graphics preset (a registry preset value) for the GPU the game
// runs on; empty without one.
std::string RuntimeSuggestedPreset(rex::Runtime* runtime);

}  // namespace wwe::settings
