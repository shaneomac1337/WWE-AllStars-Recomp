// SDK settings whose default the port changes (keyboard spec: the keyboard
// works out of the box). The compiled-in default itself changes, so the
// settings file, the environment and the command line still win over it, and
// the schema export and the page's default marks show the port's value.

#pragma once

#include <string_view>
#include <utility>

#include <rex/cvar.h>

namespace wwe::settings {

inline constexpr std::pair<std::string_view, std::string_view> kPortDefaults[] = {
    {"mnk_mode", "true"},
    // The keyboard layout (docs/research/keyboard-layout.md §4): WASD moves, Space runs, the
    // four attacks are a 2x2 block on J/K/U/I, the counters on L and ;. The launcher's Keys
    // page carries the same values (Launcher.Core KeyBinds.Inputs).
    {"keybind_a", "K"},
    {"keybind_b", "I,Backspace"},
    {"keybind_x", "J"},
    {"keybind_y", "U"},
    {"keybind_left_shoulder", "L"},
    {"keybind_right_shoulder", "Semicolon"},
    {"keybind_left_trigger", "F,E"},
    {"keybind_right_trigger", "Space"},
    {"keybind_lstick_up", "W"},
    {"keybind_lstick_down", "S"},
    {"keybind_lstick_left", "A"},
    {"keybind_lstick_right", "D"},
    {"keybind_lstick_press", "C"},
    {"keybind_rstick_up", "9"},
    {"keybind_rstick_down", "0"},
    {"keybind_rstick_left", "O"},
    {"keybind_rstick_right", "P"},
    {"keybind_rstick_press", "N"},
    {"keybind_dpad_up", "Up"},
    {"keybind_dpad_down", "Down"},
    {"keybind_dpad_left", "Left"},
    {"keybind_dpad_right", "Right"},
    {"keybind_start", "Tab,Return"},
    {"keybind_back", "Z"},
};

// Call once at startup, before any config load or schema export, while the
// process has one thread.
inline void ApplyPortDefaults() {
  for (rex::cvar::FlagEntry& entry : rex::cvar::GetRegistry()) {
    for (const auto& [name, value] : kPortDefaults) {
      if (entry.name != name || entry.source != rex::cvar::Source::kDefault) continue;
      entry.default_value = value;
      entry.setter(value);
    }
  }
}

}  // namespace wwe::settings
