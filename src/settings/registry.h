// The player settings the in-game page shows: the one list the page, the
// config file and a later launcher read (spec: "Settings registry").

#pragma once

#include <functional>
#include <string>
#include <vector>

#include <rex/cvar.h>

#include "settings/page_model.h"
#include "settings/text_rules.h"

namespace wwe::settings {

// The Graphics preset's members and value sets (low-end-preset spec,
// "Presets"): the one place the page, the launcher (through the schema export)
// and the tests read them. High is the cvar defaults (graphics-remaster spec).
inline Group GraphicsPresets() {
  return {.members = {"wwe_native_resolution_scale", "wwe_native_cas", "wwe_native_anisotropy"},
          .presets = {{"low", "Low", {"1", "0", "2"}},
                      {"medium", "Medium", {"1", "0.5", "3"}},
                      {"high", "High", {"2", "0.5", "3"}},
                      {"ultra", "Ultra", {"3", "0.25", "5"}}}};
}

// What each preset turns on and off, from the table: per preset, the members
// whose value differs from the preset before it, as "Label Value".
inline std::string PresetHelp(const Group& group, const std::vector<Setting>& settings) {
  std::string help;
  for (size_t p = 0; p < group.presets.size(); ++p) {
    std::string parts;
    for (size_t m = 0; m < group.members.size(); ++m) {
      const std::string& value = group.presets[p].values[m];
      if (p > 0 && PageModel::SameValue(value, group.presets[p - 1].values[m])) continue;
      for (const Setting& setting : settings) {
        if (setting.name != group.members[m]) continue;
        std::string label = value;
        for (const Choice& choice : setting.choices) {
          if (PageModel::SameValue(choice.value, value)) label = choice.label;
        }
        // The same in every preset: says nothing about the difference.
        bool varies = false;
        for (const Preset& other : group.presets) {
          varies |= !PageModel::SameValue(other.values[m], value);
        }
        if (varies) parts += (parts.empty() ? "" : ", ") + setting.label + " " + label;
      }
    }
    help += (help.empty() ? "" : " ") + group.presets[p].label + ": " + parts + ".";
  }
  return help;
}

inline std::vector<Setting> PlayerSettings() {
  std::vector<Setting> settings = {
      {"fullscreen", "Display mode", "DISPLAY", {{"false", "Windowed"}, {"true", "Fullscreen"}},
       "Play in a window or fill the screen.", /*startup=*/true},
      {"present_vsync", "VSync", "DISPLAY", {{"true", "On"}, {"false", "Off"}},
       "On shows every frame on a display refresh, so the picture never tears. Off gives the "
       "lowest input latency."},
      {"vblank_follows_display", "Lock to display", "DISPLAY", {{"true", "On"}, {"false", "Off"}},
       "On times the game to your display's refresh when it is a multiple of 60 Hz, so no "
       "frame is held or dropped."},
      // The SDK's device index (src/ui/vulkan/vulkan_provider.cpp); -1 lets it
      // pick the most capable GPU.
      {.name = "vulkan_device",
       .label = "GPU",
       .tab = "DISPLAY",
       .choices = {{"-1", "Automatic"}},
       .help = "The graphics card that runs the game. Automatic picks the most capable one.",
       .startup = true,
       .runtime_choices = RuntimeChoices::kGpus},
      // The SDK's XConfig language. The game turns it into its language files
      // at boot (0x826099B0): 3 DEU, 4 FRA, 5 SPA, 6 ITA, anything else INT.
      // Japanese reads INT too, so it is not offered.
      {.name = "user_language",
       .label = "Language",
       .tab = "DISPLAY",
       .choices = {{"1", "English", "Game/Localization/Coalesced.int"},
                   {"3", "German", "Game/Localization/Coalesced.deu"},
                   {"4", "French", "Game/Localization/Coalesced.fra"},
                   {"5", "Spanish", "Game/Localization/Coalesced.spa"},
                   {"6", "Italian", "Game/Localization/Coalesced.ita"}},
       .help = "The language of the game's menus and text. Only the languages on your disc are "
               "offered.",
       .startup = true,
       .runtime_choices = RuntimeChoices::kLanguages,
       .read_at_boot = true},
      // The Graphics preset opens the GRAPHICS tab; its help is filled in below.
      {.name = "graphics_preset",
       .label = "Graphics preset",
       .tab = "GRAPHICS",
       .choices = GroupChoices(GraphicsPresets()),
       .startup = true,
       .native_only = true,
       .group = GraphicsPresets()},
      {"wwe_native_renderer", "Renderer", "GRAPHICS", {{"true", "Native"}, {"false", "Emulated"}},
       "Native draws the game directly with Vulkan. Emulated runs the Xbox 360 GPU in emulation: "
       "slower, but the reference if Native shows a problem.",
       /*startup=*/true},
      {"wwe_native_cas", "Sharpening", "GRAPHICS",
       {{"0", "Off"}, {"0.25", "Low"}, {"0.5", "Medium"}, {"1", "High"}},
       "Sharpens the final picture. Native only.", /*startup=*/false, /*native_only=*/true},
      {"wwe_native_resolution_scale", "Render resolution", "GRAPHICS",
       {{"1", "720p"}, {"2", "1440p"}, {"3", "2160p"}, {"4", "2880p"}},
       "The resolution the game draws at. The picture is fitted to your window: a resolution "
       "above it is filtered down and smooths jagged edges, one below it is scaled up and "
       "sharpened. Higher needs a stronger GPU. Native only.",
       /*startup=*/true, /*native_only=*/true},
      {"wwe_native_anisotropy", "Texture filtering", "GRAPHICS",
       {{"0", "Off"}, {"2", "2x"}, {"3", "4x"}, {"4", "8x"}, {"5", "16x"}},
       "Keeps floors, ropes and crowds sharp at an angle. Native only.",
       /*startup=*/true, /*native_only=*/true},
      {"wwe_native_color_mode", "Colour", "GRAPHICS", {{"0", "Original"}, {"1", "Natural"}},
       "Original shows the Xbox 360's colours, richer, with deeper shadows, as in Xenia. "
       "Natural is softer, with more detail in dark areas. Native only.",
       /*startup=*/true, /*native_only=*/true},
      {"wwe_60fps_entrances", "60 fps entrances", "GRAPHICS", {{"false", "Off"}, {"true", "On"}},
       "Runs wrestler entrances and the arena intro at 60 fps instead of the original 30.",
       /*startup=*/true},
      {"wwe_120fps_matches", "120 fps matches", "GRAPHICS", {{"false", "Off"}, {"true", "On"}},
       "Experimental: some moves can look wrong at 120, such as a wrestler briefly out of "
       "place on a running throw. Runs offline matches at 120 fps on a 120 Hz or faster "
       "display, or with variable refresh. Menus and entrances keep their rates; online "
       "matches stay at 60. Needs a strong GPU: lower the render resolution if frames stutter.",
       /*startup=*/true},
      {"audio_mute", "Mute", "AUDIO", {{"false", "Off"}, {"true", "On"}},
       "Silences all game audio."},
      {"wwe_vibration_mode", "Vibration", "CONTROLS",
       {{"0", "Off"}, {"1", "Original"}, {"2", "Enhanced"}},
       "Enhanced pulses the pad on every hit you take and rumbles harder on finishers and at "
       "critical health. Original rumbles only on aerial hits, as on the Xbox 360."},
      {"wwe_vibration_strength", "Vibration strength", "CONTROLS",
       {{"0", "0 %"}, {"25", "25 %"}, {"50", "50 %"}, {"75", "75 %"}, {"100", "100 %"}},
       "Scales every rumble, in every vibration mode. Lower it for a gentler pad."},
      {"wwe_unlock_everything", "Unlock everything", "GAMEPLAY",
       {{"false", "Off"}, {"true", "On"}},
       "Unlocks every wrestler, arena, attire, Create a Superstar move, entrance and Fantasy "
       "Warfare match. Progress you earn while it is on is still saved; turn it off to see "
       "only what you have unlocked yourself.",
       /*startup=*/true},
      // Faster starts (.scratch/faster-starts/spec.md): all off by default.
      {.name = "wwe_short_loading_screens",
       .label = "Shorter loading screens",
       .tab = "GAMEPLAY",
       .choices = {{"false", "Off"}, {"true", "On"}},
       .help = "Cuts the fixed waits before a match where nothing is loading: about 10 seconds "
               "less, and the tips screen passes too quickly to read. Ignored in online matches.",
       .startup = true,
       .heading = "Faster starts"},
      // The launcher applies this one: its Coalesced merge drops the logo movies.
      {.name = "wwe_skip_startup_logos",
       .label = "Skip startup logos",
       .tab = "GAMEPLAY",
       .choices = {{"false", "Off"}, {"true", "On"}},
       .help = "Skips the THQ and WWE logo movies when the game starts, so the menu comes "
               "about 12 seconds sooner. The disclaimer still shows.",
       .startup = true,
       .heading = "Faster starts"},
      // The SDK's online client (patches/rexglue-online.patch) reads these when
      // the game first asks for Xbox LIVE, so a change applies at the next start.
      // The developer switches (forced relay, dropped direct traffic, the bind
      // address, the net trace) stay off this list (launcher-online spec).
      {.name = "wwe_online",
       .label = "Online play",
       .tab = "ONLINE",
       .choices = {{"false", "Off"}, {"true", "On"}},
       .help = "Experimental. On signs in to the game server, so the Xbox LIVE menu finds "
               "Player Matches. Off contacts no server.",
       .startup = true,
       .read_at_boot = true},
      {.name = "wwe_online_name",
       .label = "Player name",
       .tab = "ONLINE",
       .help = "The name other players see in the match lobby: 1 to 15 letters, digits and "
               "spaces, starting with a letter.",
       .startup = true,
       .read_at_boot = true,
       .text = TextRule::kGamertag,
       .max_length = 15},
      // The default follows the SDK's cvar: the owner's server.
      {.name = "wwe_online_server",
       .label = "Game server",
       .tab = "ONLINE",
       .help = "Where online play signs in and finds matches. The default is the project's "
               "server; type another (a host, host:port or an http(s) URL) to play on a "
               "friend's.",
       .startup = true,
       .read_at_boot = true,
       .text = TextRule::kServerAddress,
       .max_length = 253},
      {.name = "wwe_online_upnp",
       .label = "UPnP",
       .tab = "ONLINE",
       .choices = {{"false", "Off"}, {"true", "On"}},
       .help = "Optional: asks your router to open the game's ports while it runs, so players "
               "elsewhere can reach you directly. Matches work without it.",
       .startup = true,
       .read_at_boot = true},
  };
  for (Setting& setting : settings) {
    if (setting.is_group()) setting.help = PresetHelp(setting.group, settings);
  }
  return settings;
}

// What the game logs when online play is on but the saved player name breaks
// the gamertag rule (a hand-edited file; the launcher never saves one): it
// signs in with the profile's name instead. Empty when nothing is wrong.
inline std::string OnlineNameWarning(bool online, const std::string& name) {
  if (!online) return {};
  const std::string error = TextRuleError(TextRule::kGamertag, name);
  if (error.empty()) return {};
  const std::string fallback =
      "signing in with the profile's name. Choose one in the launcher's Settings > ONLINE.";
  if (name.empty()) return "online play is on without a player name: " + fallback;
  return "the player name \"" + name + "\" is refused (" + error + "): " + fallback;
}

// What the game logs when online play is on but the saved game server breaks
// the server-address rule (a hand-edited file). The game still tries it, so a
// friend's unusual address is never second-guessed. Empty when nothing is wrong.
inline std::string OnlineServerWarning(bool online, const std::string& server) {
  if (!online) return {};
  const std::string error = TextRuleError(TextRule::kServerAddress, server);
  if (error.empty()) return {};
  return "the game server \"" + server + "\" is refused (" + error +
         "): online play may not sign in. Fix it in the launcher's Settings > ONLINE.";
}

// The first-setup rule the game and the launcher share (low-end-preset spec,
// "When it applies"): the suggested preset applies only while none of the
// group's members is chosen, that is saved or set in the launch options. A
// saved value of any member means the player has chosen.
inline bool IsFirstSetup(const Group& group, const std::function<bool(const std::string&)>& chosen) {
  for (const std::string& member : group.members) {
    if (chosen(member)) return false;
  }
  return true;
}

// The settings with the Graphics preset's help naming the suggestion for the
// GPU in use (a preset value, settings/gpu_suggestion.h); unchanged when
// suggested is empty (no GPU known).
inline std::vector<Setting> WithSuggestedPreset(std::vector<Setting> settings,
                                                const std::string& suggested) {
  for (Setting& setting : settings) {
    if (!setting.is_group() || suggested.empty()) continue;
    for (const Preset& preset : setting.group.presets) {
      if (preset.value == suggested) setting.help += " Suggested for this GPU: " + preset.label + ".";
    }
  }
  return settings;
}

// The settings with each restart mark taken from its cvar's lifecycle (so the
// page and the developer overlay, F6, agree) and pinned when the command line
// set the cvar. A group row takes both from its members.
inline std::vector<Setting> WithLifecycles(std::vector<Setting> settings) {
  for (Setting& setting : settings) {
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.name);
    setting.restart =
        setting.read_at_boot || (flag && flag->lifecycle != rex::cvar::Lifecycle::kHotReload);
    setting.pinned = rex::cvar::GetFlagSource(setting.name) == rex::cvar::Source::kCommandLine;
  }
  PageModel::DeriveGroupMarks(settings);
  return settings;
}

// The settings with the choices that depend on the player's PC and disc
// filled in: each of gpus (as Vulkan lists them) after the GPU's fixed
// choices, and only the choices whose file has_file finds. A setting's cvar
// default always stays (the game falls back to English). The launcher does
// the same from the schema export (Launcher.Core SettingsSchema.WithRuntimeChoices).
inline std::vector<Setting> WithRuntimeChoices(
    std::vector<Setting> settings, const std::vector<std::string>& gpus,
    const std::function<bool(const std::string& file)>& has_file) {
  for (Setting& setting : settings) {
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.name);
    std::vector<Choice> kept;
    for (Choice& choice : setting.choices) {
      if (choice.file.empty() || has_file(choice.file) ||
          (flag && PageModel::SameValue(choice.value, flag->default_value))) {
        kept.push_back(std::move(choice));
      }
    }
    if (setting.runtime_choices == RuntimeChoices::kGpus) {
      for (size_t i = 0; i < gpus.size(); ++i) kept.push_back({std::to_string(i), gpus[i]});
    }
    setting.choices = std::move(kept);
  }
  return settings;
}

}  // namespace wwe::settings
