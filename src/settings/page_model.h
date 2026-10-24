// The in-game settings page as a pure state machine: controller snapshots and
// the time in, page state, setting changes and the game's input hold out. No
// ImGui or SDK types, so it is tested on its own (tests/core/
// test_settings_page.cpp). Navigation follows wwe-svr2011-pc's GRAPHICS page
// (KaikoClanworth1), used with the author's permission.

#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace wwe::settings {

// One controller snapshot, in XInput's layout.
struct Pad {
  uint16_t buttons = 0;
  int16_t thumb_lx = 0;
  int16_t thumb_ly = 0;
};

struct Choice {
  std::string value;  // as the cvar parses it
  std::string label;  // as the page shows it
  // A game data file the choice needs (a language's Coalesced.*), relative and
  // '/'-separated; the choice is offered only when the disc has it. Usually empty.
  std::string file;
};

// Choices that depend on the player's PC or disc (settings/registry.h WithRuntimeChoices).
enum class RuntimeChoices {
  kNone,
  kGpus,       // each GPU Vulkan reports joins the fixed choices, by index
  kLanguages,  // a choice stays only when the disc has its file
};

// The rule a text row's value follows (settings/text_rules.h); kNone for a
// choice row.
enum class TextRule {
  kNone,
  kGamertag,       // the player name
  kServerAddress,  // the game server
};

// One named set of values for a group row's members (a Graphics preset).
struct Preset {
  std::string value;                // the group row's choice value
  std::string label;
  std::vector<std::string> values;  // one per member, in member order
};

// A row that sets several settings at once: the Graphics preset (low-end-preset
// spec, "Presets"). It is derived, never saved: the row shows the preset whose
// values all match its members, else Custom, which is shown but never picked.
struct Group {
  std::vector<std::string> members;  // the members' cvar names
  std::vector<Preset> presets;
};

// The choice a group row shows when no preset matches.
inline constexpr std::string_view kCustomChoice = "custom";

struct Setting {
  std::string name;  // cvar; a group row's own name names no cvar
  std::string label;
  std::string tab;
  std::vector<Choice> choices;
  std::string help;
  // Takes effect when the game starts, so the launcher shows it (launcher spec,
  // "Settings hand-off"); live settings stay on this page only.
  bool startup = false;
  bool native_only = false;  // does nothing on Emulated
  RuntimeChoices runtime_choices = RuntimeChoices::kNone;
  // The game reads the cvar once, at boot, whatever the cvar's lifecycle says
  // (user_language), so a change applies at the next start.
  bool read_at_boot = false;
  bool restart = false;  // applies at the next start (from the cvar's lifecycle)
  bool pinned = false;   // set on the command line, which outranks the saved value
  Group group;           // members empty: an ordinary row
  // A text row has a value instead of choices; the page shows it read-only
  // (typing in game is out of scope), the launcher edits it.
  TextRule text = TextRule::kNone;
  size_t max_length = 0;
  // A sub-heading the launcher shows above the first row of a run of rows that
  // share it, inside their tab ("Faster starts"); empty for most rows.
  std::string heading;

  bool is_group() const { return !group.members.empty(); }
  bool is_text() const { return text != TextRule::kNone; }
};

// A group row's choices: one per preset, then Custom.
inline std::vector<Choice> GroupChoices(const Group& group) {
  std::vector<Choice> choices;
  for (const Preset& preset : group.presets) choices.push_back({preset.value, preset.label});
  choices.push_back({std::string(kCustomChoice), "Custom"});
  return choices;
}

struct Change {
  std::string name;
  std::string value;
};

class PageModel {
 public:
  static constexpr uint16_t kUp = 0x0001, kDown = 0x0002, kLeft = 0x0004, kRight = 0x0008;
  static constexpr uint16_t kStart = 0x0010, kBack = 0x0020, kA = 0x1000, kB = 0x2000;
  static constexpr uint16_t kLB = 0x0100, kRB = 0x0200;
  // Both sticks clicked: opens and closes the page. The game uses neither
  // stick click (no help or control string mentions one).
  static constexpr uint16_t kCombo = 0x0040 | 0x0080;
  static constexpr int kStickDeadZone = 16000;
  static constexpr uint64_t kOpenGuardMs = 150;
  static constexpr uint64_t kRepeatDelayMs = 400;
  static constexpr uint64_t kRepeatIntervalMs = 130;

  explicit PageModel(std::vector<Setting> settings) : settings_(std::move(settings)) {
    DeriveGroupMarks(settings_);
    choices_.resize(settings_.size());
    applied_.resize(settings_.size());
    members_.resize(settings_.size());
    for (size_t i = 0; i < settings_.size(); ++i) {
      bool known = false;
      for (const std::string& tab : tabs_) known |= tab == settings_[i].tab;
      if (!known) tabs_.push_back(settings_[i].tab);
      for (const std::string& name : settings_[i].group.members) members_[i].push_back(Index(name));
    }
  }

  // A group row takes its marks from its members: Native only when all are,
  // pinned when any is, and a next-start row when any member a preset changes
  // is one.
  static void DeriveGroupMarks(std::vector<Setting>& settings) {
    for (Setting& setting : settings) {
      if (!setting.is_group()) continue;
      setting.native_only = true;
      setting.pinned = false;
      setting.restart = false;
      for (size_t m = 0; m < setting.group.members.size(); ++m) {
        for (const Setting& member : settings) {
          if (member.name != setting.group.members[m]) continue;
          setting.native_only &= member.native_only;
          setting.pinned |= member.pinned;
          bool varies = false;
          for (const Preset& preset : setting.group.presets) {
            varies |= !SameValue(preset.values[m], setting.group.presets[0].values[m]);
          }
          setting.restart |= varies && member.restart;
        }
      }
    }
  }

  // Opens on the first row, showing values (one per setting, as saved). applied
  // holds the values in effect now (default: values); a restart setting whose
  // shown value differs is marked. A value no choice names is added as its own
  // choice, so it shows as it is and stays until the player changes it. Input
  // stays held from the game until the page closes and every button is released.
  void Open(const std::vector<std::string>& values, uint64_t now_ms,
            const std::vector<std::string>& applied = {}) {
    for (size_t i = 0; i < settings_.size(); ++i) {
      choices_[i] = 0;
      applied_[i] = i < applied.size() ? applied[i] : i < values.size() ? values[i] : "";
      if (i >= values.size() || settings_[i].is_group()) continue;
      if (settings_[i].is_text()) {
        settings_[i].choices = {{values[i], values[i].empty() ? "Not set" : values[i]}};
        continue;
      }
      bool found = false;
      for (size_t c = 0; c < settings_[i].choices.size(); ++c) {
        if (SameValue(settings_[i].choices[c].value, values[i])) {
          choices_[i] = c;
          found = true;
          break;
        }
      }
      if (!found) {
        settings_[i].choices.insert(settings_[i].choices.begin(), {values[i], values[i]});
      }
    }
    SyncGroups();
    open_ = true;
    holding_ = true;
    waiting_release_ = true;
    opened_ms_ = now_ms;
    tab_ = 0;
    row_ = 0;
    previous_ = 0;
  }

  // Closes the page from outside (its key pressed again). Input stays held
  // until every button is released, as when B closes it.
  void Close() { open_ = false; }

  // One poll of the pad. Returns the setting changes it made.
  std::vector<Change> Update(const Pad& pad, uint64_t now_ms) {
    const uint16_t held = Held(pad);
    std::vector<Change> changes;
    if (!holding_) {
      return changes;
    }
    if (waiting_release_) {
      if (held == 0 && now_ms >= opened_ms_ + kOpenGuardMs) {
        waiting_release_ = false;
      }
      previous_ = held;
      return changes;
    }
    if (!open_) {
      if (held == 0) {
        holding_ = false;  // released after closing: the game gets the pad back
      }
      previous_ = held;
      return changes;
    }
    const uint16_t pressed = held & ~previous_;
    const bool combo = (held & kCombo) == kCombo && (previous_ & kCombo) != kCombo;
    if (combo || (pressed & (kB | kStart | kBack))) {
      open_ = false;
      previous_ = held;
      return changes;
    }
    const uint16_t directions = held & (kUp | kDown | kLeft | kRight);
    uint16_t act = pressed & (kUp | kDown | kLeft | kRight | kA);
    if (directions != (previous_ & (kUp | kDown | kLeft | kRight))) {
      repeat_at_ms_ = now_ms + kRepeatDelayMs;  // a new direction starts its delay
    } else if (directions && now_ms >= repeat_at_ms_) {
      act |= directions;
      repeat_at_ms_ = now_ms + kRepeatIntervalMs;
    }
    previous_ = held;
    if (settings_.empty()) {
      return changes;
    }
    if (pressed & (kLB | kRB)) {
      if (pressed & kLB) tab_ = (tab_ + tabs_.size() - 1) % tabs_.size();
      if (pressed & kRB) tab_ = (tab_ + 1) % tabs_.size();
      row_ = 0;
      return changes;
    }
    const std::vector<size_t> tab_rows = rows();
    if (act & kUp) row_ = (row_ + tab_rows.size() - 1) % tab_rows.size();
    if (act & kDown) row_ = (row_ + 1) % tab_rows.size();
    const size_t selected = tab_rows[row_];
    const size_t count = settings_[selected].choices.size();
    if (count == 0 || settings_[selected].is_text()) {
      return changes;
    }
    if (settings_[selected].is_group()) {
      if (act & (kLeft | kRight | kA)) changes = Pick(selected, (act & kLeft) != 0);
      return changes;
    }
    size_t& choice = choices_[selected];
    const size_t before = choice;
    if (act & kLeft) choice = (choice + count - 1) % count;
    if (act & (kRight | kA)) choice = (choice + 1) % count;
    if (choice != before) {
      changes.push_back({settings_[selected].name, settings_[selected].choices[choice].value});
      SyncGroups();
    }
    return changes;
  }

  bool is_open() const { return open_; }
  // Whether the game must see an idle controller.
  bool holds_input() const { return holding_; }
  const std::vector<std::string>& tabs() const { return tabs_; }
  size_t tab() const { return tab_; }
  // The settings on the current tab, in registry order.
  std::vector<size_t> rows() const {
    std::vector<size_t> result;
    for (size_t i = 0; i < settings_.size(); ++i) {
      if (!tabs_.empty() && settings_[i].tab == tabs_[tab_]) result.push_back(i);
    }
    return result;
  }
  // The selected setting.
  size_t row() const { return rows().empty() ? 0 : rows()[row_]; }
  size_t choice(size_t setting) const { return choices_[setting]; }
  // What the row shows as its value.
  const std::string& ValueLabel(size_t setting) const {
    static const std::string kNotSet = "Not set";
    const auto& choices = settings_[setting].choices;
    return choices.empty() ? kNotSet : choices[choices_[setting]].label;
  }
  // When a change to the setting applies, for the help line; empty for a live
  // setting nothing pins.
  std::string ApplyNote(size_t setting) const {
    if (settings_[setting].is_text()) {
      return "Change in the launcher.";
    }
    if (settings_[setting].pinned) {
      return settings_[setting].restart
                 ? "Set by launch options: a change here does not apply at the next start."
                 : "Set by launch options: a change here lasts until the game closes.";
    }
    if (!settings_[setting].restart) {
      return {};
    }
    return needs_restart(setting) ? "* Applies the next time the game starts."
                                  : "Applies the next time the game starts.";
  }

  // A restart setting whose shown value is not the one in effect; for a group
  // row, any member that is.
  bool needs_restart(size_t setting) const {
    if (settings_[setting].is_group()) {
      for (size_t member : members_[setting]) {
        if (member < settings_.size() && needs_restart(member)) return true;
      }
      return false;
    }
    return settings_[setting].restart && !settings_[setting].choices.empty() &&
           !SameValue(settings_[setting].choices[choices_[setting]].value, applied_[setting]);
  }
  const std::vector<Setting>& settings() const { return settings_; }

  // The setting a change names, for its restart mark; null when none does.
  const Setting* Find(std::string_view name) const {
    const size_t i = Index(name);
    return i < settings_.size() ? &settings_[i] : nullptr;
  }

  // Equal as text, or as numbers ("0.5" and the cvar's "0.500000").
  static bool SameValue(std::string_view a, std::string_view b) {
    if (a == b) return true;
    const std::string sa(a), sb(b);
    char* end_a = nullptr;
    char* end_b = nullptr;
    const double na = std::strtod(sa.c_str(), &end_a);
    const double nb = std::strtod(sb.c_str(), &end_b);
    return !sa.empty() && !sb.empty() && *end_a == 0 && *end_b == 0 && na == nb;
  }

 private:
  size_t Index(std::string_view name) const {
    for (size_t i = 0; i < settings_.size(); ++i) {
      if (settings_[i].name == name) return i;
    }
    return settings_.size();
  }

  // The setting's choice that names value, or none.
  std::optional<size_t> ChoiceOf(size_t setting, std::string_view value) const {
    for (size_t c = 0; c < settings_[setting].choices.size(); ++c) {
      if (SameValue(settings_[setting].choices[c].value, value)) return c;
    }
    return std::nullopt;
  }

  // Each group row shows the preset its members match, else Custom (its last choice).
  void SyncGroups() {
    for (size_t i = 0; i < settings_.size(); ++i) {
      if (!settings_[i].is_group()) continue;
      const Group& group = settings_[i].group;
      choices_[i] = settings_[i].choices.size() - 1;
      for (size_t p = 0; p < group.presets.size(); ++p) {
        bool all = true;
        for (size_t m = 0; m < members_[i].size(); ++m) {
          const size_t member = members_[i][m];
          all &= member < settings_.size() &&
                 SameValue(settings_[member].choices[choices_[member]].value,
                           group.presets[p].values[m]);
        }
        if (all) {
          choices_[i] = p;
          break;
        }
      }
    }
  }

  // Steps a group row to the next (or previous) preset, never to Custom, and
  // sets the members that differ.
  std::vector<Change> Pick(size_t row, bool back) {
    std::vector<Change> changes;
    const Group& group = settings_[row].group;
    const size_t count = group.presets.size();
    if (count == 0) return changes;
    const size_t now = choices_[row];  // count when Custom
    const size_t preset = back ? (now == 0 ? count - 1 : now - 1) : (now >= count - 1 ? 0 : now + 1);
    for (size_t m = 0; m < members_[row].size(); ++m) {
      const size_t member = members_[row][m];
      if (member == settings_.size()) continue;
      const std::string& value = group.presets[preset].values[m];
      if (SameValue(settings_[member].choices[choices_[member]].value, value)) continue;
      if (const auto choice = ChoiceOf(member, value)) {
        choices_[member] = *choice;
      } else {
        settings_[member].choices.push_back({value, value});
        choices_[member] = settings_[member].choices.size() - 1;
      }
      changes.push_back({settings_[member].name, value});
    }
    SyncGroups();
    return changes;
  }

  // Buttons plus the left stick as D-pad directions.
  static uint16_t Held(const Pad& pad) {
    uint16_t held = pad.buttons;
    if (pad.thumb_ly > kStickDeadZone) held |= kUp;
    if (pad.thumb_ly < -kStickDeadZone) held |= kDown;
    if (pad.thumb_lx < -kStickDeadZone) held |= kLeft;
    if (pad.thumb_lx > kStickDeadZone) held |= kRight;
    return held;
  }

  std::vector<Setting> settings_;
  std::vector<size_t> choices_;
  std::vector<std::string> applied_;
  std::vector<std::vector<size_t>> members_;  // a group row's member indices (size(): missing)
  std::vector<std::string> tabs_;
  size_t tab_ = 0;
  bool open_ = false;
  bool holding_ = false;
  bool waiting_release_ = false;
  uint64_t opened_ms_ = 0;
  uint64_t repeat_at_ms_ = 0;
  uint16_t previous_ = 0;
  size_t row_ = 0;
};

// Watches the pad while the page is closed: true on the poll where both
// sticks become clicked together.
class ComboWatch {
 public:
  bool Update(const Pad& pad) {
    const bool both = (pad.buttons & PageModel::kCombo) == PageModel::kCombo;
    const bool fire = both && !was_both_;
    was_both_ = both;
    return fire;
  }

 private:
  bool was_both_ = false;
};

}  // namespace wwe::settings
