#include <catch_amalgamated.hpp>

#include <map>
#include <sstream>
#include <string>
#include <vector>

#include "settings/registry.h"
#include "settings/schema.h"

using wwe::settings::PlayerSettings;
using wwe::settings::SchemaJson;
using wwe::settings::Setting;
using wwe::settings::WithLifecycles;

namespace {

// The export's setting lines, one object per line, by key.
std::map<std::string, std::string> SettingLines(const std::string& json) {
  std::map<std::string, std::string> lines;
  std::istringstream in(json);
  for (std::string line; std::getline(in, line);) {
    const std::string marker = "{\"key\": \"";
    const size_t at = line.find(marker);
    if (at == std::string::npos) continue;
    const size_t start = at + marker.size();
    lines[line.substr(start, line.find('"', start) - start)] = line;
  }
  return lines;
}

bool Has(const std::string& line, const std::string& text) {
  return line.find(text) != std::string::npos;
}

}  // namespace

TEST_CASE("the schema export lists every registry entry with its startup flag") {
  const std::map<std::string, std::string> lines =
      SettingLines(SchemaJson(WithLifecycles(PlayerSettings())));
  REQUIRE(lines.size() == PlayerSettings().size());
  // The launcher shows the settings that take effect when the game starts (spec, "Settings").
  for (const char* name : {"fullscreen", "wwe_native_renderer", "wwe_native_resolution_scale",
                           "wwe_native_anisotropy"}) {
    INFO(name);
    REQUIRE(lines.contains(name));
    CHECK(Has(lines.at(name), "\"applies_at_startup\": true"));
  }
  // Live settings stay on the in-game page only.
  for (const char* name : {"present_vsync", "vblank_follows_display", "wwe_native_cas",
                           "audio_mute", "wwe_vibration_mode", "wwe_vibration_strength"}) {
    INFO(name);
    REQUIRE(lines.contains(name));
    CHECK(Has(lines.at(name), "\"applies_at_startup\": false"));
  }
}

TEST_CASE("the schema export marks the Native-only settings") {
  const std::map<std::string, std::string> lines =
      SettingLines(SchemaJson(WithLifecycles(PlayerSettings())));
  for (const char* name : {"wwe_native_cas", "wwe_native_resolution_scale", "wwe_native_anisotropy"}) {
    INFO(name);
    CHECK(Has(lines.at(name), "\"native_only\": true"));
  }
  for (const char* name : {"fullscreen", "wwe_native_renderer", "audio_mute"}) {
    INFO(name);
    CHECK(Has(lines.at(name), "\"native_only\": false"));
  }
}

TEST_CASE("the schema export gives each setting its cvar type, default choice and restart mark") {
  const std::map<std::string, std::string> lines =
      SettingLines(SchemaJson(WithLifecycles(PlayerSettings())));
  INFO(lines.at("fullscreen"));
  // The SDK starts fullscreen by default (src/ui/window.cpp).
  CHECK(Has(lines.at("fullscreen"),
            "\"type\": \"bool\", \"default\": \"true\", \"applies_at_startup\": true, "
            "\"native_only\": false, \"next_start\": false"));
  CHECK(Has(lines.at("wwe_native_resolution_scale"), "\"type\": \"int\", \"default\": \"2\""));
  // The cvar's default 0.5 is spelled as its choice, so the launcher can select it.
  CHECK(Has(lines.at("wwe_native_cas"), "\"type\": \"double\", \"default\": \"0.5\""));
  CHECK(Has(lines.at("wwe_native_renderer"), "\"next_start\": true"));
  CHECK(Has(lines.at("wwe_native_renderer"),
            "\"choices\": [{\"value\": \"true\", \"label\": \"Native\"}, "
            "{\"value\": \"false\", \"label\": \"Emulated\"}]"));
}

TEST_CASE("the schema export carries the ONLINE rows as startup choices") {
  const std::map<std::string, std::string> lines =
      SettingLines(SchemaJson(WithLifecycles(PlayerSettings())));
  for (const char* name : {"wwe_online", "wwe_online_upnp"}) {
    INFO(name);
    REQUIRE(lines.contains(name));
    CHECK(Has(lines.at(name), "\"tab\": \"ONLINE\", \"type\": \"bool\", \"default\": \"false\", "
                              "\"applies_at_startup\": true, \"native_only\": false, "
                              "\"next_start\": true"));
    CHECK(Has(lines.at(name), "\"choices\": [{\"value\": \"false\", \"label\": \"Off\"}, "
                              "{\"value\": \"true\", \"label\": \"On\"}]"));
  }
}

TEST_CASE("a settings file that names no renderer starts on Native (ADR-0020)") {
  const std::map<std::string, std::string> lines =
      SettingLines(SchemaJson(WithLifecycles(PlayerSettings())));
  INFO(lines.at("wwe_native_renderer"));
  CHECK(Has(lines.at("wwe_native_renderer"), "\"type\": \"bool\", \"default\": \"true\""));
}

TEST_CASE("the schema export carries the Graphics preset row with its members and value sets") {
  const std::map<std::string, std::string> lines =
      SettingLines(SchemaJson(WithLifecycles(PlayerSettings())));
  REQUIRE(lines.contains("graphics_preset"));
  const std::string& line = lines.at("graphics_preset");
  INFO(line);
  // No cvar of its own; its default is the preset the members' defaults match.
  CHECK(Has(line, "\"type\": \"\", \"default\": \"high\", \"applies_at_startup\": true, "
                  "\"native_only\": true, \"next_start\": true"));
  CHECK(Has(line, "\"members\": [\"wwe_native_resolution_scale\", \"wwe_native_cas\", "
                  "\"wwe_native_anisotropy\"]"));
  CHECK(Has(line, "\"presets\": [{\"value\": \"low\", \"label\": \"Low\", "
                  "\"values\": [\"1\", \"0\", \"2\"]}, "
                  "{\"value\": \"medium\", \"label\": \"Medium\", "
                  "\"values\": [\"1\", \"0.5\", \"3\"]}, "
                  "{\"value\": \"high\", \"label\": \"High\", "
                  "\"values\": [\"2\", \"0.5\", \"3\"]}, "
                  "{\"value\": \"ultra\", \"label\": \"Ultra\", "
                  "\"values\": [\"3\", \"0.25\", \"5\"]}]"));
  CHECK(Has(line, "{\"value\": \"custom\", \"label\": \"Custom\"}"));
  // Ordinary rows carry neither field.
  CHECK_FALSE(Has(lines.at("fullscreen"), "\"members\""));
  CHECK_FALSE(Has(lines.at("fullscreen"), "\"presets\""));
}

TEST_CASE("the schema export escapes quotes, backslashes and control characters") {
  Setting setting{"test_escape", "Say \"hi\"", "TAB", {{"a\\b", "Tab\there"}}, "Line\nbreak"};
  const std::string json = SchemaJson({setting});
  CHECK(Has(json, R"("label": "Say \"hi\"")"));
  CHECK(Has(json, R"({"value": "a\\b", "label": "Tab\there"})"));
  CHECK(Has(json, R"("help": "Line\nbreak")"));
  CHECK(Has(json, "\"format\": 2,"));
}

TEST_CASE("only the exact switch asks for the schema export") {
  using wwe::settings::SchemaRequested;
  CHECK(SchemaRequested({"wweallstars.exe", "--print-settings-schema"}));
  CHECK_FALSE(SchemaRequested({"wweallstars.exe"}));
  CHECK_FALSE(SchemaRequested({"wweallstars.exe", "--game_data_root=D:/x"}));
  CHECK_FALSE(SchemaRequested({"wweallstars.exe", "--print-settings-schema-now"}));
}
