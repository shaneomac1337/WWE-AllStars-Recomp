// Saving one setting into the flat TOML config (wweallstars.toml) without
// touching the rest of the file, unlike the SDK's SaveConfig, which rewrites
// every non-default cvar (debug and command-line values included).

#pragma once

#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>

#include <rex/cvar.h>
#include <toml++/toml.hpp>

namespace wwe::settings {

// The page's own settings file, in the user data folder so that replacing the
// game folder keeps it (launcher ticket 21). Loaded after the main config, so
// the page's settings apply whatever state that file is in (in-game settings
// ticket 10).
inline std::filesystem::path SettingsPath(const std::filesystem::path& user_data_root) {
  return user_data_root / "wweallstars.settings.toml";
}

// Why a config file does not parse; empty if it parses or does not exist.
// The SDK loads the main config before logging starts, so its own error
// message is lost.
inline std::string ConfigError(const std::filesystem::path& path) {
  std::error_code error;
  if (!std::filesystem::exists(path, error)) {
    return {};
  }
  try {
    toml::parse_file(path.string());
  } catch (const toml::parse_error& parse_error) {
    return std::string(parse_error.description()) + " (line " +
           std::to_string(parse_error.source().begin.line) + ")";
  }
  return {};
}

// The value as a TOML literal for the cvar: strings quoted with backslashes
// and quotes escaped, numbers and booleans bare.
inline std::string TomlLiteral(const std::string& name, const std::string& value) {
  const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(name);
  if (!flag || flag->type != rex::cvar::FlagType::String) {
    return value;
  }
  std::string quoted = "\"";
  for (char c : value) {
    if (c == '"' || c == '\\') quoted += '\\';
    quoted += c;
  }
  return quoted + "\"";
}

// The config text with key set to the TOML literal value: its line replaced
// (keeping the line end), or appended if the file has none.
inline std::string SetTomlKey(std::string_view text, std::string_view key,
                              std::string_view value) {
  const std::string line = std::string(key) + " = " + std::string(value);
  std::string out;
  bool found = false;
  size_t start = 0;
  while (start < text.size()) {
    size_t end = text.find('\n', start);
    end = end == std::string_view::npos ? text.size() : end + 1;
    std::string_view current = text.substr(start, end - start);
    std::string_view ending;
    if (current.ends_with("\r\n")) {
      ending = "\r\n";
    } else if (current.ends_with('\n')) {
      ending = "\n";
    }
    std::string_view body = current.substr(0, current.size() - ending.size());
    const size_t first = body.find_first_not_of(" \t");
    std::string_view rest = first == std::string_view::npos ? "" : body.substr(first);
    bool match = false;
    if (!found && rest.starts_with(key)) {
      std::string_view after = rest.substr(key.size());
      const size_t equals = after.find_first_not_of(" \t");
      match = equals != std::string_view::npos && after[equals] == '=';
    }
    if (match) {
      out += line;
      out += ending;
      found = true;
    } else {
      out += current;
    }
    start = end;
  }
  if (!found) {
    if (!out.empty() && out.back() != '\n') {
      out += '\n';
    }
    out += line;
    out += '\n';
  }
  return out;
}

// The text without the line that sets key, and that line's value (trimmed);
// the value is empty when no line sets it.
// The value a TOML literal holds: a basic string unquoted, anything else up to
// a comment. As the launcher's SettingsFile.LiteralValue, so both migrate a
// file alike.
inline std::string TomlLiteralValue(std::string_view literal) {
  const size_t first = literal.find_first_not_of(" \t");
  literal = first == std::string_view::npos ? std::string_view() : literal.substr(first);
  if (!literal.starts_with('"')) {
    literal = literal.substr(0, literal.find('#'));
    const size_t last = literal.find_last_not_of(" \t\r\n");
    return std::string(last == std::string_view::npos ? std::string_view() : literal.substr(0, last + 1));
  }
  std::string value;
  for (size_t i = 1; i < literal.size() && literal[i] != '"'; ++i) {
    if (literal[i] == '\\' && i + 1 < literal.size()) ++i;
    value += literal[i];
  }
  return value;
}

// Removes the first line that sets key from the text and returns its value;
// nullopt (text unchanged) when no line sets it.
inline std::optional<std::string> TakeTomlKey(std::string& text, std::string_view key) {
  size_t start = 0;
  while (start < text.size()) {
    size_t end = text.find('\n', start);
    end = end == std::string::npos ? text.size() : end + 1;
    std::string_view line = std::string_view(text).substr(start, end - start);
    const size_t first = line.find_first_not_of(" \t");
    if (first != std::string_view::npos && line.substr(first).starts_with(key)) {
      std::string_view after = line.substr(first + key.size());
      const size_t equals = after.find_first_not_of(" \t");
      if (equals != std::string_view::npos && after[equals] == '=') {
        std::string taken = TomlLiteralValue(after.substr(equals + 1));
        text.erase(start, end - start);
        return taken;
      }
    }
    start = end;
  }
  return std::nullopt;
}

// Graphics remaster: the Supersampling setting is gone. On means 1440p unless a
// render resolution above 720p was chosen (it won then too); Off keeps 720p,
// written out because the default is 1440p now.
inline std::string MigrateSettingsText(std::string text) {
  const std::optional<std::string> supersample = TakeTomlKey(text, "wwe_native_supersample");
  if (!supersample) {
    return text;
  }
  std::string copy = text;  // read only: SetTomlKey keeps the line's place
  const std::optional<std::string> scale = TakeTomlKey(copy, "wwe_native_resolution_scale");
  const int old_scale = std::max(scale ? std::atoi(scale->c_str()) : 1, 1);
  const int new_scale = *supersample == "true" && old_scale == 1 ? 2 : old_scale;
  return SetTomlKey(text, "wwe_native_resolution_scale", std::to_string(new_scale));
}

// Reads text, or empty if the file cannot be read.
inline std::string ReadText(const std::filesystem::path& path) {
  std::ifstream in{path, std::ios::binary};
  if (!in) {
    return {};
  }
  std::ostringstream buffer;
  buffer << in.rdbuf();
  return buffer.str();
}

// Writes the text through a temporary file and a rename, so a crash never
// leaves the file half written.
inline bool WriteTextAtomically(const std::filesystem::path& path, const std::string& text) {
  const std::filesystem::path temporary = path.string() + ".tmp";
  {
    std::ofstream out{temporary, std::ios::binary | std::ios::trunc};
    if (!out) {
      return false;
    }
    out << text;
    if (!out) {
      return false;
    }
  }
  std::error_code error;
  std::filesystem::rename(temporary, path, error);
  if (error) {
    std::filesystem::remove(temporary, error);
    return false;
  }
  return true;
}

// Migrates a config file in place (MigrateSettingsText); true if it changed.
inline bool MigrateSettingsFile(const std::filesystem::path& path) {
  const std::string text = ReadText(path);
  const std::string migrated = MigrateSettingsText(text);
  return migrated != text && WriteTextAtomically(path, migrated);
}

// Writes key = value into the config file.
inline bool SaveSetting(const std::filesystem::path& path, std::string_view key,
                        std::string_view value) {
  return WriteTextAtomically(path, SetTomlKey(ReadText(path), key, value));
}

}  // namespace wwe::settings
