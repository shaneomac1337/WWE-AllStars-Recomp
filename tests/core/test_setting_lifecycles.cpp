#include <catch_amalgamated.hpp>

#include <rex/cvar.h>
#include <rex/platform/dynlib.h>

#include "settings/registry.h"

using rex::cvar::Lifecycle;

namespace {

Lifecycle LifecycleOf(const char* name) {
  const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(name);
  REQUIRE(flag);
  return flag->lifecycle;
}

// The GPU plugin's cvars register when its DLL loads. The SDK's loader
// (settings/gpu_plugin_settings.h in the game) finds it beside the exe through
// the SDK entry point, which tests do not run; the tests run in the build
// folder, where the plugin is staged.
bool LoadGpuPluginCvars() {
  static rex::platform::DynamicLibrary plugin;
  return static_cast<bool>(plugin) ||
         plugin.Load("rexgpu-xenosrd.dll", rex::platform::SymbolResolution::kImmediate) ||
         plugin.Load("rexgpu-xenos.dll", rex::platform::SymbolResolution::kImmediate);
}

}  // namespace

// The player-facing settings (in-game settings ticket 01): each declares when a
// change takes effect, from where the code reads it.
TEST_CASE("settings read once at startup apply at the next start") {
  // Read when the native renderer starts.
  CHECK(LifecycleOf("wwe_native_renderer") == Lifecycle::kRequiresRestart);
  CHECK(LifecycleOf("wwe_native_resolution_scale") == Lifecycle::kRequiresRestart);
}

TEST_CASE("settings read every frame or with a change callback apply live") {
  CHECK(LifecycleOf("fullscreen") == Lifecycle::kHotReload);   // window callback
  CHECK(LifecycleOf("audio_mute") == Lifecycle::kHotReload);   // read per audio buffer
  // A change callback hands it to the presenter (low-end-preset spec, story 16).
  CHECK(LifecycleOf("wwe_native_cas") == Lifecycle::kHotReload);
  // A change callback rebuilds the swapchain on the UI thread (live-settings 01).
  CHECK(LifecycleOf("present_vsync") == Lifecycle::kHotReload);
  // The display vblank thread always runs and reads it once a second (live-settings 02).
  REQUIRE(LoadGpuPluginCvars());
  CHECK(LifecycleOf("vblank_follows_display") == Lifecycle::kHotReload);
  // Part of the sampler cache key, read at each frame's start (live-settings 03).
  CHECK(LifecycleOf("wwe_native_anisotropy") == Lifecycle::kHotReload);
}

// The GPU and the language (launcher ticket 08): both name SDK cvars and both
// apply at the next start, on the page and in the launcher.
TEST_CASE("the GPU and the language apply at the next start") {
  // The SDK picks the Vulkan device once, when the graphics system starts.
  CHECK(LifecycleOf("vulkan_device") == Lifecycle::kInitOnly);
  // The SDK's cvar is live, but the game picks its language files once, at
  // boot (0x826099B0), so the registry marks the setting as read at boot.
  CHECK(LifecycleOf("user_language") == Lifecycle::kHotReload);
  size_t found = 0;
  for (const wwe::settings::Setting& setting :
       wwe::settings::WithLifecycles(wwe::settings::PlayerSettings())) {
    if (setting.name == "vulkan_device" || setting.name == "user_language") {
      INFO(setting.name);
      CHECK(setting.restart);
      ++found;
    }
  }
  CHECK(found == 2);
}
