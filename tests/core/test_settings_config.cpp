#include <catch_amalgamated.hpp>

#include <rex/cvar.h>

#include "settings/config_file.h"

using wwe::settings::SetTomlKey;

TEST_CASE("saving a setting replaces its line and keeps every other line") {
  const std::string before =
      "# Auto-generated cvar configuration\n"
      "log_file = \"D:/x/runtime.log\"\n"
      "audio_mute = false\n"
      "wwe_tick_log = true\n";
  CHECK(SetTomlKey(before, "audio_mute", "true") ==
        "# Auto-generated cvar configuration\n"
        "log_file = \"D:/x/runtime.log\"\n"
        "audio_mute = true\n"
        "wwe_tick_log = true\n");
}

TEST_CASE("saving a setting the file does not have appends it") {
  CHECK(SetTomlKey("wwe_tick_log = true\n", "audio_mute", "true") ==
        "wwe_tick_log = true\naudio_mute = true\n");
  CHECK(SetTomlKey("wwe_tick_log = true", "audio_mute", "true") ==
        "wwe_tick_log = true\naudio_mute = true\n");
  CHECK(SetTomlKey("", "audio_mute", "true") == "audio_mute = true\n");
}

TEST_CASE("a key that only starts with the setting's name is left alone") {
  CHECK(SetTomlKey("audio_mute_all = 1\n", "audio_mute", "true") ==
        "audio_mute_all = 1\naudio_mute = true\n");
}

TEST_CASE("the line is found with any spacing and Windows line ends are kept") {
  CHECK(SetTomlKey("a = 1\r\n  audio_mute=false\r\n", "audio_mute", "true") ==
        "a = 1\r\naudio_mute = true\r\n");
}

TEST_CASE("the SDK's config writer escapes backslashes and quotes in strings") {
  // A Windows path saved from the F4 overlay made wweallstars.toml invalid,
  // so the SDK ignored the whole file (in-game settings ticket 03).
  static std::string value = R"(D:\x "y")";
  rex::cvar::RegisterFlag({
      .name = "test_toml_escape",
      .type = rex::cvar::FlagType::String,
      .category = "Test/TomlEscape",
      .setter = [](std::string_view v) { value = std::string(v); return true; },
      .getter = [] { return value; },
      .default_value = "",
  });
  CHECK(rex::cvar::SerializeToTOML("Test/TomlEscape") ==
        std::string(R"(test_toml_escape = "D:\\x \"y\"")") + "\n");
  rex::cvar::UnregisterFlag("test_toml_escape");
}

TEST_CASE("the page's settings file sits in the user data folder") {
  // Launcher ticket 21: replacing the game folder never wipes the player's settings.
  CHECK(wwe::settings::SettingsPath("D:/WWE/userdata") ==
        std::filesystem::path("D:/WWE/userdata/wweallstars.settings.toml"));
}

TEST_CASE("settings saved by the page load back through the SDK's loader") {
  // Ticket 10: the page's own file must always be valid TOML.
  static bool flag_bool = false;
  static double flag_double = 0;
  static std::string flag_string;
  rex::cvar::RegisterFlag({.name = "test_settings_bool", .type = rex::cvar::FlagType::Boolean,
                           .category = "Test/Settings",
                           .setter = [](std::string_view v) { flag_bool = v == "true"; return true; },
                           .getter = [] { return std::string(flag_bool ? "true" : "false"); },
                           .default_value = "false"});
  rex::cvar::RegisterFlag({.name = "test_settings_double", .type = rex::cvar::FlagType::Double,
                           .category = "Test/Settings",
                           .setter = [](std::string_view v) { flag_double = std::stod(std::string(v)); return true; },
                           .getter = [] { return std::to_string(flag_double); },
                           .default_value = "0"});
  rex::cvar::RegisterFlag({.name = "test_settings_string", .type = rex::cvar::FlagType::String,
                           .category = "Test/Settings",
                           .setter = [](std::string_view v) { flag_string = std::string(v); return true; },
                           .getter = [] { return flag_string; },
                           .default_value = ""});
  const auto path = std::filesystem::temp_directory_path() / "wwe_test_settings.toml";
  std::filesystem::remove(path);
  using wwe::settings::SaveSetting;
  using wwe::settings::TomlLiteral;
  REQUIRE(SaveSetting(path, "test_settings_bool", TomlLiteral("test_settings_bool", "true")));
  REQUIRE(SaveSetting(path, "test_settings_double", TomlLiteral("test_settings_double", "0.25")));
  REQUIRE(SaveSetting(path, "test_settings_string",
                      TomlLiteral("test_settings_string", R"(C:\x "y")")));
  REQUIRE(SaveSetting(path, "test_settings_double", TomlLiteral("test_settings_double", "0.5")));
  CHECK(wwe::settings::ConfigError(path).empty());
  rex::cvar::LoadConfig(path);
  CHECK(flag_bool);
  CHECK(flag_double == 0.5);
  CHECK(flag_string == R"(C:\x "y")");
  std::filesystem::remove(path);
  for (const char* name : {"test_settings_bool", "test_settings_double", "test_settings_string"}) {
    rex::cvar::UnregisterFlag(name);
  }
}

TEST_CASE("a config that does not parse is reported, a missing one is not") {
  const auto path = std::filesystem::temp_directory_path() / "wwe_test_broken.toml";
  {
    std::ofstream out(path, std::ios::binary);
    out << R"(log_file = "D:\Projects\x.log")" << "\n";  // an unescaped Windows path
  }
  CHECK_FALSE(wwe::settings::ConfigError(path).empty());
  std::filesystem::remove(path);
  CHECK(wwe::settings::ConfigError(path).empty());
}

TEST_CASE("migration: Supersampling On at 720p becomes 1440p") {
  using wwe::settings::MigrateSettingsText;
  CHECK(MigrateSettingsText("wwe_native_supersample = true\n") ==
        "wwe_native_resolution_scale = 2\n");
  CHECK(MigrateSettingsText("wwe_native_resolution_scale = 1\r\nwwe_native_supersample = true\r\n") ==
        "wwe_native_resolution_scale = 2\r\n");
}

TEST_CASE("migration: a chosen render resolution wins over Supersampling") {
  using wwe::settings::MigrateSettingsText;
  CHECK(MigrateSettingsText("wwe_native_supersample = true\nwwe_native_resolution_scale = 3\n") ==
        "wwe_native_resolution_scale = 3\n");
  CHECK(MigrateSettingsText("wwe_native_resolution_scale = 2\nwwe_native_supersample = false\n") ==
        "wwe_native_resolution_scale = 2\n");
}

TEST_CASE("migration: Supersampling Off drops the key and keeps 720p") {
  using wwe::settings::MigrateSettingsText;
  CHECK(MigrateSettingsText("wwe_native_cas = 0\nwwe_native_supersample = false\n") ==
        "wwe_native_cas = 0\nwwe_native_resolution_scale = 1\n");
}

// The same cases as the launcher's SettingsFileTests, so both migrate a file alike.
TEST_CASE("migration reads the old value as the launcher does: comments, quotes, empty") {
  using wwe::settings::MigrateSettingsText;
  CHECK(MigrateSettingsText("wwe_native_supersample = true # set in game\n") ==
        "wwe_native_resolution_scale = 2\n");
  CHECK(MigrateSettingsText("wwe_native_supersample = \"true\"\n") ==
        "wwe_native_resolution_scale = 2\n");
  CHECK(MigrateSettingsText("wwe_native_supersample =\n") == "wwe_native_resolution_scale = 1\n");
}

TEST_CASE("migration: a file without the old key is left as it is") {
  const std::string text = "wwe_native_cas = 0.25\nwwe_native_resolution_scale = 1\n";
  CHECK(wwe::settings::MigrateSettingsText(text) == text);
}

TEST_CASE("migration rewrites the file once") {
  const auto path = std::filesystem::temp_directory_path() / "wwe_test_migrate.toml";
  {
    std::ofstream out(path, std::ios::binary);
    out << "wwe_native_supersample = true\n";
  }
  CHECK(wwe::settings::MigrateSettingsFile(path));
  CHECK_FALSE(wwe::settings::MigrateSettingsFile(path));
  std::ifstream in(path, std::ios::binary);
  std::ostringstream text;
  text << in.rdbuf();
  CHECK(text.str() == "wwe_native_resolution_scale = 2\n");
  in.close();
  std::filesystem::remove(path);
}

TEST_CASE("a removed or unknown key in a config is ignored, the rest still loads") {
  static double flag_double = 0;
  rex::cvar::RegisterFlag({.name = "test_settings_known", .type = rex::cvar::FlagType::Double,
                           .category = "Test/Settings",
                           .setter = [](std::string_view v) { flag_double = std::stod(std::string(v)); return true; },
                           .getter = [] { return std::to_string(flag_double); },
                           .default_value = "0"});
  const auto path = std::filesystem::temp_directory_path() / "wwe_test_unknown.toml";
  {
    std::ofstream out(path, std::ios::binary);
    out << "test_settings_removed_key = true\ntest_settings_known = 0.75\n";
  }
  CHECK(wwe::settings::ConfigError(path).empty());
  rex::cvar::LoadConfig(path);
  CHECK(flag_double == 0.75);
  std::filesystem::remove(path);
  rex::cvar::UnregisterFlag("test_settings_known");
}
