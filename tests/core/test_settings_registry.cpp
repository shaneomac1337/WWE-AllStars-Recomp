#include <catch_amalgamated.hpp>

#include <set>
#include <string>
#include <vector>

#include <rex/cvar.h>

#include "settings/registry.h"

using wwe::settings::PlayerSettings;
using wwe::settings::Setting;

namespace {

// Settings defined in the GPU plugin, which the tests do not load.
bool InGpuPlugin(const std::string& name) { return name == "vblank_follows_display"; }

// A cvar value as a number (booleans as 0 and 1), so "0.5" equals "0.500000".
double AsNumber(const std::string& value) {
  if (value == "true") return 1;
  if (value == "false") return 0;
  return std::stod(value);
}

}  // namespace

TEST_CASE("the page shows the spec's settings on six tabs") {
  std::set<std::string> names;
  std::set<std::string> tabs;
  for (const Setting& setting : PlayerSettings()) {
    names.insert(setting.name);
    tabs.insert(setting.tab);
  }
  CHECK(tabs == std::set<std::string>{"DISPLAY", "GRAPHICS", "AUDIO", "CONTROLS", "GAMEPLAY", "ONLINE"});
  CHECK(names == std::set<std::string>{
                     "fullscreen", "present_vsync", "vblank_follows_display",
                     "wwe_native_renderer", "wwe_native_cas",
                     "wwe_native_resolution_scale", "wwe_native_anisotropy",
                     "wwe_native_color_mode", "wwe_60fps_entrances", "wwe_120fps_matches",
                     "audio_mute",
                     "wwe_vibration_mode", "wwe_vibration_strength", "mnk_mode", "wwe_unlock_everything", "wwe_short_loading_screens", "wwe_skip_startup_logos", "vulkan_device",
                     "user_language", "graphics_preset", "wwe_online", "wwe_online_name", "wwe_online_server",
                     "wwe_online_upnp"});
}

TEST_CASE("vibration strength sits next to the vibration mode on CONTROLS, in quarter steps") {
  const std::vector<Setting> settings = PlayerSettings();
  size_t mode = settings.size(), strength = settings.size();
  for (size_t i = 0; i < settings.size(); ++i) {
    if (settings[i].name == "wwe_vibration_mode") mode = i;
    if (settings[i].name == "wwe_vibration_strength") strength = i;
  }
  REQUIRE(strength < settings.size());
  CHECK(strength == mode + 1);
  CHECK(settings[strength].tab == "CONTROLS");
  CHECK(settings[strength].label == "Vibration strength");
  std::vector<std::string> labels;
  for (const auto& choice : settings[strength].choices) labels.push_back(choice.label);
  CHECK(labels == std::vector<std::string>{"0 %", "25 %", "50 %", "75 %", "100 %"});
}

TEST_CASE("online play and UPnP are next-start choices on ONLINE, both Off by default") {
  // Launcher-online spec, "Online settings are registry settings".
  for (const Setting& setting : wwe::settings::WithLifecycles(PlayerSettings())) {
    if (setting.name != "wwe_online" && setting.name != "wwe_online_upnp") continue;
    INFO(setting.name);
    CHECK(setting.tab == "ONLINE");
    CHECK(setting.startup);
    CHECK(setting.restart);
    std::vector<std::string> labels;
    for (const auto& choice : setting.choices) labels.push_back(choice.label);
    CHECK(labels == std::vector<std::string>{"Off", "On"});
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.name);
    REQUIRE(flag);
    CHECK(std::string(flag->default_value) == "false");
  }
  const std::vector<Setting> settings = PlayerSettings();
  const auto help = [&](const std::string& name) {
    for (const Setting& setting : settings) {
      if (setting.name == name) return setting.help;
    }
    return std::string();
  };
  CHECK(help("wwe_online").find("Experimental") != std::string::npos);
  CHECK(help("wwe_online_upnp").find("work without it") != std::string::npos);
}

TEST_CASE("the developer online switches stay off the player's settings") {
  for (const Setting& setting : PlayerSettings()) {
    INFO(setting.name);
    CHECK(setting.name != "wwe_online_force_relay");
    CHECK(setting.name != "wwe_online_drop_direct");
    CHECK(setting.name != "wwe_online_ip");
    CHECK(setting.name != "wwe_net_trace");
  }
}

TEST_CASE("every setting names a cvar, offers values it accepts, and includes its default") {
  for (const Setting& setting : PlayerSettings()) {
    INFO(setting.name);
    CHECK_FALSE(setting.label.empty());
    CHECK_FALSE(setting.help.empty());
    // A text row (the player name) has a value, not choices (test_text_settings.cpp).
    if (setting.is_text()) continue;
    // The GPU's other choices are the GPUs found at runtime.
    CHECK(setting.choices.size() >= (setting.runtime_choices == wwe::settings::RuntimeChoices::kGpus ? 1u : 2u));
    // A group row names no cvar (test_graphics_preset.cpp).
    if (InGpuPlugin(setting.name) || setting.is_group()) {
      continue;
    }
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.name);
    REQUIRE(flag);
    const std::string default_value = flag->default_value;
    bool has_default = false;
    for (const auto& choice : setting.choices) {
      INFO(choice.value);
      CHECK(rex::cvar::SetFlagByName(setting.name, choice.value));
      has_default |= AsNumber(choice.value) == AsNumber(default_value);
    }
    rex::cvar::ResetToDefault(setting.name);
    CHECK(has_default);
  }
}

TEST_CASE("restart marks come from each cvar's lifecycle") {
  for (const Setting& setting : wwe::settings::WithLifecycles(PlayerSettings())) {
    INFO(setting.name);
    if (setting.name == "audio_mute" || setting.name == "fullscreen" ||
        setting.name == "wwe_vibration_mode" || setting.name == "wwe_vibration_strength" ||
        setting.name == "mnk_mode" ||
        setting.name == "wwe_native_cas" || setting.name == "present_vsync" ||
        setting.name == "wwe_native_anisotropy" || setting.name == "wwe_60fps_entrances" ||
        setting.name == "wwe_120fps_matches") {
      CHECK_FALSE(setting.restart);
    } else if (!InGpuPlugin(setting.name)) {
      CHECK(setting.restart);
    }
  }
}

TEST_CASE("Colour offers Original and Natural on GRAPHICS, Original by default, at next start") {
  // Colour-original spec: Native only, outside the Graphics preset (a look, not a cost).
  bool found = false;
  for (const Setting& setting : wwe::settings::WithLifecycles(PlayerSettings())) {
    if (setting.name != "wwe_native_color_mode") continue;
    found = true;
    CHECK(setting.label == "Colour");
    CHECK(setting.tab == "GRAPHICS");
    CHECK(setting.native_only);
    CHECK(setting.startup);
    CHECK(setting.restart);
    std::vector<std::string> labels, values;
    for (const auto& choice : setting.choices) {
      labels.push_back(choice.label);
      values.push_back(choice.value);
    }
    CHECK(labels == std::vector<std::string>{"Original", "Natural"});
    CHECK(values == std::vector<std::string>{"0", "1"});
    CHECK(setting.help.find("Xbox 360") != std::string::npos);
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.name);
    REQUIRE(flag);
    CHECK(std::string(flag->default_value) == "0");
  }
  CHECK(found);
  for (const std::string& member : wwe::settings::GraphicsPresets().members) {
    CHECK(member != "wwe_native_color_mode");
  }
}
