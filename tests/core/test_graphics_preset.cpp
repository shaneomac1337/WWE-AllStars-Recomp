// The Graphics preset row (low-end-preset ticket 05): its values in the
// registry and its behaviour on the page model.

#include <catch_amalgamated.hpp>

#include <map>
#include <string>
#include <vector>

#include <rex/cvar.h>

#include "settings/page_model.h"
#include "settings/registry.h"

using wwe::settings::Change;
using wwe::settings::Pad;
using wwe::settings::PageModel;
using wwe::settings::Setting;

namespace {

constexpr uint16_t kDown = PageModel::kDown, kLeft = PageModel::kLeft,
                   kRight = PageModel::kRight, kA = PageModel::kA;
const Pad kIdle = {0, 0, 0};

const Setting& Find(const std::vector<Setting>& settings, const std::string& name) {
  for (const Setting& setting : settings) {
    if (setting.name == name) return setting;
  }
  FAIL("no setting " << name);
  return settings.front();
}

// The preset's value for each member, by cvar name.
std::map<std::string, std::string> PresetValues(const Setting& row, const std::string& preset) {
  std::map<std::string, std::string> values;
  for (const auto& p : row.group.presets) {
    if (p.value != preset) continue;
    for (size_t m = 0; m < row.group.members.size(); ++m) values[row.group.members[m]] = p.values[m];
  }
  return values;
}

// A small page: the preset row over one live and one restart member.
std::vector<Setting> PresetPage() {
  Setting row{.name = "test_preset", .label = "Preset", .tab = "GRAPHICS"};
  row.group.members = {"live_member", "restart_member"};
  row.group.presets = {{"low", "Low", {"0", "false"}},
                       {"medium", "Medium", {"1", "false"}},
                       {"high", "High", {"1", "true"}}};
  row.choices = wwe::settings::GroupChoices(row.group);
  Setting live{"live_member", "Live", "GRAPHICS", {{"0", "Off"}, {"1", "On"}}, "Live."};
  live.native_only = true;
  Setting restart{"restart_member", "Restart", "GRAPHICS", {{"false", "Off"}, {"true", "On"}},
                  "Restart."};
  restart.native_only = true;
  restart.restart = true;
  return {row, live, restart};
}

// Opened with the given member values and released, on the preset row.
PageModel Opened(const std::string& live, const std::string& restart,
                 std::vector<Setting> settings = PresetPage()) {
  PageModel page(std::move(settings));
  page.Open({"", live, restart}, 0);
  page.Update(kIdle, 200);
  return page;
}

std::string Shown(const PageModel& page, size_t row) {
  return page.settings()[row].choices[page.choice(row)].value;
}

std::vector<Change> Press(PageModel& page, uint16_t button, uint64_t& now) {
  auto changes = page.Update(Pad{button, 0, 0}, now += 10);
  page.Update(kIdle, now += 10);
  return changes;
}

}  // namespace

TEST_CASE("the registry's presets match the spec's table") {
  const std::vector<Setting> settings = wwe::settings::PlayerSettings();
  const Setting& row = Find(settings, "graphics_preset");
  CHECK(row.tab == "GRAPHICS");
  CHECK(row.label == "Graphics preset");
  CHECK(row.group.members == std::vector<std::string>{"wwe_native_resolution_scale",
                                                      "wwe_native_cas", "wwe_native_anisotropy"});
  using Values = std::map<std::string, std::string>;
  CHECK(PresetValues(row, "low") == Values{{"wwe_native_resolution_scale", "1"},
                                           {"wwe_native_cas", "0"},
                                           {"wwe_native_anisotropy", "2"}});
  CHECK(PresetValues(row, "medium") == Values{{"wwe_native_resolution_scale", "1"},
                                              {"wwe_native_cas", "0.5"},
                                              {"wwe_native_anisotropy", "3"}});
  CHECK(PresetValues(row, "high") == Values{{"wwe_native_resolution_scale", "2"},
                                            {"wwe_native_cas", "0.5"},
                                            {"wwe_native_anisotropy", "3"}});
  CHECK(PresetValues(row, "ultra") == Values{{"wwe_native_resolution_scale", "3"},
                                             {"wwe_native_cas", "0.25"},
                                             {"wwe_native_anisotropy", "5"}});
  std::vector<std::string> labels;
  for (const auto& choice : row.choices) labels.push_back(choice.label);
  CHECK(labels == std::vector<std::string>{"Low", "Medium", "High", "Ultra", "Custom"});
}

TEST_CASE("High is the cvar defaults, so a player who never picks a preset keeps today's picture") {
  const std::vector<Setting> settings = wwe::settings::PlayerSettings();
  const Setting& row = Find(settings, "graphics_preset");
  for (const auto& [name, value] : PresetValues(row, "high")) {
    INFO(name);
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(name);
    REQUIRE(flag);
    CHECK(PageModel::SameValue(value, flag->default_value));
  }
}

TEST_CASE("every preset value is a choice its member offers") {
  const std::vector<Setting> settings = wwe::settings::PlayerSettings();
  const Setting& row = Find(settings, "graphics_preset");
  for (const auto& preset : row.group.presets) {
    for (size_t m = 0; m < row.group.members.size(); ++m) {
      INFO(preset.label << " " << row.group.members[m]);
      bool offered = false;
      for (const auto& choice : Find(settings, row.group.members[m]).choices) {
        offered |= PageModel::SameValue(choice.value, preset.values[m]);
      }
      CHECK(offered);
    }
  }
}

TEST_CASE("the preset row opens the GRAPHICS tab") {
  const std::vector<Setting> settings = wwe::settings::PlayerSettings();
  for (const Setting& setting : settings) {
    if (setting.tab != "GRAPHICS") continue;
    CHECK(setting.name == "graphics_preset");
    break;
  }
}

TEST_CASE("the help line says what each preset turns on and off") {
  const std::vector<Setting> settings = wwe::settings::PlayerSettings();
  const Setting& row = Find(settings, "graphics_preset");
  INFO(row.help);
  CHECK(row.help.find("Low: Render resolution 720p, Sharpening Off, Texture filtering 2x.") !=
        std::string::npos);
  CHECK(row.help.find("Medium: Sharpening Medium, Texture filtering 4x.") != std::string::npos);
  CHECK(row.help.find("High: Render resolution 1440p.") != std::string::npos);
  CHECK(row.help.find("Ultra: Render resolution 2160p, Sharpening Low, Texture filtering 16x.") !=
        std::string::npos);
}

TEST_CASE("the row shows the preset whose values all match, else Custom") {
  CHECK(Shown(Opened("0", "false"), 0) == "low");
  CHECK(Shown(Opened("1", "false"), 0) == "medium");
  CHECK(Shown(Opened("1", "true"), 0) == "high");
  CHECK(Shown(Opened("0", "true"), 0) == "custom");
}

TEST_CASE("picking a preset emits exactly the changes for members that differ") {
  PageModel page = Opened("1", "true");  // High
  uint64_t now = 300;
  std::vector<Change> changes = Press(page, kLeft, now);  // Medium
  REQUIRE(changes.size() == 1);
  CHECK(changes[0].name == "restart_member");
  CHECK(changes[0].value == "false");
  CHECK(Shown(page, 0) == "medium");
  CHECK(Shown(page, 2) == "false");

  changes = Press(page, kLeft, now);  // Low
  REQUIRE(changes.size() == 1);
  CHECK(changes[0].name == "live_member");
  CHECK(changes[0].value == "0");

  changes = Press(page, kLeft, now);  // wraps to High, past Custom
  REQUIRE(changes.size() == 2);
  CHECK(changes[0].name == "live_member");
  CHECK(changes[1].name == "restart_member");
  CHECK(Shown(page, 0) == "high");
}

TEST_CASE("a hand change to one member shows Custom; picking a preset again resets the members") {
  PageModel page = Opened("1", "true");  // High
  uint64_t now = 300;
  Press(page, kDown, now);                                // the live member
  CHECK(Press(page, kLeft, now).size() == 1);             // 1 -> 0
  CHECK(Shown(page, 0) == "custom");
  // Back on the preset row: Right from Custom picks the first preset, Low,
  // which already matches the live member and resets the restart one.
  Press(page, PageModel::kUp, now);
  std::vector<Change> changes = Press(page, kRight, now);
  REQUIRE(changes.size() == 1);
  CHECK(changes[0].name == "restart_member");
  CHECK(changes[0].value == "false");
  CHECK(Shown(page, 0) == "low");
}

TEST_CASE("Custom cannot be picked: A and Right step from High back to Low") {
  PageModel page = Opened("1", "true");  // High
  uint64_t now = 300;
  Press(page, kA, now);
  CHECK(Shown(page, 0) == "low");
  Press(page, kRight, now);
  Press(page, kRight, now);
  CHECK(Shown(page, 0) == "high");
  Press(page, kRight, now);
  CHECK(Shown(page, 0) == "low");
}

TEST_CASE("the row's restart mark is the union of the changed members' marks") {
  PageModel page = Opened("1", "false");  // Medium
  uint64_t now = 300;
  CHECK_FALSE(page.needs_restart(0));
  Press(page, kLeft, now);  // Low: only the live member changes
  CHECK_FALSE(page.needs_restart(0));
  Press(page, kLeft, now);  // High: the restart member changes too
  CHECK(page.needs_restart(0));
  CHECK(page.ApplyNote(0) == "* Applies the next time the game starts.");
  // A group whose members are all live says nothing.
  std::vector<Setting> settings = PresetPage();
  settings[2].restart = false;
  PageModel live = Opened("1", "false", settings);
  Press(live, kLeft, now);
  Press(live, kLeft, now);
  CHECK_FALSE(live.needs_restart(0));
  CHECK(live.ApplyNote(0).empty());
}

TEST_CASE("the row is Native only when all its members are") {
  CHECK(Opened("1", "true").settings()[0].native_only);
  std::vector<Setting> settings = PresetPage();
  settings[1].native_only = false;
  CHECK_FALSE(Opened("1", "true", settings).settings()[0].native_only);
  CHECK(Find(wwe::settings::PlayerSettings(), "graphics_preset").native_only);
}

TEST_CASE("a member set in the launch options gives the row the pinned note") {
  std::vector<Setting> settings = PresetPage();
  settings[2].pinned = true;
  PageModel page = Opened("1", "true", settings);
  CHECK(page.settings()[0].pinned);  // drawn in the pinned orange
  CHECK(page.ApplyNote(0) ==
        "Set by launch options: a change here does not apply at the next start.");
  CHECK_FALSE(Opened("1", "true").settings()[0].pinned);
}

TEST_CASE("first setup: the suggestion applies only while no member is saved or pinned") {
  const wwe::settings::Group group = wwe::settings::GraphicsPresets();
  using wwe::settings::IsFirstSetup;
  // Nothing chosen anywhere: a new player.
  CHECK(IsFirstSetup(group, [](const std::string&) { return false; }));
  // One member saved (settings file or config) or given in the launch options: the player chose.
  CHECK_FALSE(IsFirstSetup(group, [](const std::string& name) { return name == "wwe_native_cas"; }));
  CHECK_FALSE(IsFirstSetup(group, [](const std::string& name) {
    return name == "wwe_native_resolution_scale";
  }));
  // A setting outside the group says nothing about the preset.
  CHECK(IsFirstSetup(group, [](const std::string& name) { return name == "fullscreen"; }));
}

TEST_CASE("a page looks a change's own setting up by name, for its restart mark") {
  PageModel page = Opened("1", "true");
  REQUIRE(page.Find("restart_member"));
  CHECK(page.Find("restart_member")->restart);
  CHECK_FALSE(page.Find("live_member")->restart);
  CHECK(page.Find("missing") == nullptr);
}
