// The player settings list as JSON, for the launcher (`wweallstars.exe
// --print-settings-schema`; launcher spec, "Settings hand-off"). The launcher
// builds its Settings screen from a committed copy of this export
// (launcher/src/Launcher.Core/SettingsSchema.json), and a launcher test fails
// when the two drift apart.

#pragma once

#include <cstdio>
#include <string>
#include <string_view>
#include <vector>

#include <rex/cvar.h>

#include "settings/headless.h"
#include "settings/page_model.h"
#include "settings/text_rules.h"

namespace wwe::settings {

// The export's format; the launcher refuses any other.
inline constexpr int kSchemaFormat = 2;

// The command-line switch that prints the schema and exits before the runtime starts.
inline constexpr std::string_view kPrintSchemaSwitch = "--print-settings-schema";

inline bool SchemaRequested(const std::vector<std::string>& args) {
  return HasSwitch(args, kPrintSchemaSwitch);
}

// The text as a JSON string literal.
inline std::string JsonString(std::string_view text) {
  std::string out = "\"";
  for (char c : text) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20) {
          char escaped[8];
          std::snprintf(escaped, sizeof(escaped), "\\u%04x", static_cast<unsigned char>(c));
          out += escaped;
        } else {
          out += c;
        }
    }
  }
  return out + "\"";
}

namespace schema_detail {

// The cvar's type as the launcher writes its TOML literal; empty if unknown.
inline std::string TypeName(const rex::cvar::FlagEntry* flag) {
  if (!flag) return {};
  switch (flag->type) {
    case rex::cvar::FlagType::Boolean: return "bool";
    case rex::cvar::FlagType::Int32:
    case rex::cvar::FlagType::Int64:
    case rex::cvar::FlagType::Uint32:
    case rex::cvar::FlagType::Uint64: return "int";
    case rex::cvar::FlagType::Double: return "double";
    case rex::cvar::FlagType::String: return "string";
    default: return {};
  }
}

// The cvar's default, spelled as the choice that names it (so the launcher can
// select it); the raw default if no choice does.
inline std::string DefaultValue(const Setting& setting, const rex::cvar::FlagEntry* flag) {
  if (!flag) return {};
  for (const Choice& choice : setting.choices) {
    if (PageModel::SameValue(choice.value, flag->default_value)) return choice.value;
  }
  return flag->default_value;
}

inline const char* Bool(bool value) { return value ? "true" : "false"; }

// A group row's default: the preset its members' cvar defaults match, else Custom.
inline std::string GroupDefault(const Setting& setting) {
  for (const Preset& preset : setting.group.presets) {
    bool all = true;
    for (size_t m = 0; m < setting.group.members.size(); ++m) {
      const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.group.members[m]);
      all &= flag && PageModel::SameValue(preset.values[m], flag->default_value);
    }
    if (all) return preset.value;
  }
  return std::string(kCustomChoice);
}

inline std::string JsonStrings(const std::vector<std::string>& values) {
  std::string out = "[";
  for (size_t i = 0; i < values.size(); ++i) out += (i ? ", " : "") + JsonString(values[i]);
  return out + "]";
}

// How the launcher fills the setting's choices at runtime; null for fixed choices.
inline const char* RuntimeChoicesName(RuntimeChoices runtime) {
  switch (runtime) {
    case RuntimeChoices::kGpus: return "gpus";
    case RuntimeChoices::kLanguages: return "languages";
    default: return nullptr;
  }
}

}  // namespace schema_detail

// With --print-settings-schema on the command line: prints SchemaJson of the
// player settings to standard output and exits. Otherwise returns. Defined in
// schema_export.cpp (the game only).
void PrintSchemaAndExitIfAsked();

// The settings (with their lifecycles applied) as JSON: one object per line,
// in registry order, so the committed copy diffs line by line. A cvar the GPU
// plugin defines (vblank_follows_display) registers only once the plugin is
// loaded (settings/gpu_plugin_settings.h); before that its type and default
// are empty and its next_start is false. A group row (the
// Graphics preset) names no cvar: its type is empty, its default is the preset
// the members' defaults match, and it adds the optional "members" and
// "presets" fields. A text row (format 2) adds "kind": "text", its
// "max_length" and its "rule" (settings/text_rules.h), with no choices. A row
// with a sub-heading adds the optional "heading" field.
inline std::string SchemaJson(const std::vector<Setting>& settings) {
  using namespace schema_detail;
  std::string out = "{\n  \"format\": " + std::to_string(kSchemaFormat) + ",\n  \"settings\": [\n";
  for (size_t i = 0; i < settings.size(); ++i) {
    const Setting& setting = settings[i];
    const rex::cvar::FlagEntry* flag = rex::cvar::GetFlagInfo(setting.name);
    out += "    {\"key\": " + JsonString(setting.name);
    out += ", \"label\": " + JsonString(setting.label);
    out += ", \"tab\": " + JsonString(setting.tab);
    out += ", \"type\": " + JsonString(TypeName(flag));
    out += ", \"default\": " + JsonString(setting.is_group() ? GroupDefault(setting)
                                                              : DefaultValue(setting, flag));
    out += std::string(", \"applies_at_startup\": ") + Bool(setting.startup);
    out += std::string(", \"native_only\": ") + Bool(setting.native_only);
    out += std::string(", \"next_start\": ") + Bool(setting.restart);
    if (!setting.heading.empty()) out += ", \"heading\": " + JsonString(setting.heading);
    if (setting.is_text()) {
      out += ", \"kind\": \"text\", \"max_length\": " + std::to_string(setting.max_length);
      out += ", \"rule\": " + JsonString(TextRuleName(setting.text));
    }
    if (const char* runtime = RuntimeChoicesName(setting.runtime_choices)) {
      out += ", \"runtime_choices\": " + JsonString(runtime);
    }
    if (setting.is_group()) {
      out += ", \"members\": " + JsonStrings(setting.group.members);
      out += ", \"presets\": [";
      for (size_t p = 0; p < setting.group.presets.size(); ++p) {
        const Preset& preset = setting.group.presets[p];
        out += (p ? ", " : "") + std::string("{\"value\": ") + JsonString(preset.value) +
               ", \"label\": " + JsonString(preset.label) +
               ", \"values\": " + JsonStrings(preset.values) + "}";
      }
      out += "]";
    }
    out += ", \"choices\": [";
    for (size_t c = 0; c < setting.choices.size(); ++c) {
      const Choice& choice = setting.choices[c];
      if (c) out += ", ";
      out += "{\"value\": " + JsonString(choice.value) + ", \"label\": " + JsonString(choice.label);
      if (!choice.file.empty()) out += ", \"file\": " + JsonString(choice.file);
      out += "}";
    }
    out += "], \"help\": " + JsonString(setting.help) + "}";
    out += i + 1 < settings.size() ? ",\n" : "\n";
  }
  return out + "  ]\n}\n";
}

}  // namespace wwe::settings
