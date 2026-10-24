#include "settings/settings_page.h"

#include <algorithm>
#include <atomic>
#include <cfloat>
#include <chrono>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include <imgui.h>

#include <rex/cvar.h>
#include <rex/input/input_system.h>
#include <rex/logging.h>
#include <rex/ui/imgui_dialog.h>
#include <rex/ui/keybinds.h>

#include "settings/config_file.h"
#include "settings/page_model.h"
#include "settings/registry.h"

namespace wwe::settings {

namespace {

class PageDialog;

struct State {
  rex::ui::ImGuiDrawer* drawer = nullptr;
  rex::input::InputSystem* input = nullptr;
  std::filesystem::path settings_path;  // the page's own file (ticket 10)
  std::string config_error;              // the main config's parse error, if any
  std::atomic<bool> hold = false;
  PageDialog* dialog = nullptr;  // UI thread only
  std::function<void(std::function<void()>)> run_on_ui;
  RuntimeChoiceSources sources;  // the GPU and language rows' choices
  std::map<std::string, std::string> pending;  // restart settings saved this session (UI thread)
  std::atomic<bool> open_requested = false;  // combo seen, page not yet built
  std::mutex combo_mutex;
  ComboWatch combo;
  ImFont* heading = nullptr;     // bold condensed, for the title and rows
  ImFont* body = nullptr;        // for the help line
} g;

// Colours sampled from the game's OPTIONS and SOUND screens.
constexpr ImU32 kWhite = IM_COL32(255, 255, 255, 255);
constexpr ImU32 kGold = IM_COL32(250, 200, 40, 255);
constexpr ImU32 kGrey = IM_COL32(140, 152, 175, 255);
constexpr ImU32 kPanelTop = IM_COL32(6, 16, 48, 255), kPanelBottom = IM_COL32(2, 6, 22, 255);
constexpr ImU32 kBarTop = IM_COL32(40, 120, 230, 245), kBarBottom = IM_COL32(10, 55, 150, 245);
constexpr ImU32 kBarIdle = IM_COL32(14, 32, 78, 205);
constexpr ImU32 kRuleOrange = IM_COL32(255, 140, 20, 255);

// Maps the game's 1280x720 frame into the window the way Native shows the
// game: stretched to fill it (at 16:9 that is a plain scale). x follows the
// width, y and text sizes follow the height, so text keeps its shape.
struct Frame {
  float scale_x;
  float scale;  // vertical, and for text
  ImVec2 At(float x, float y) const { return {x * scale_x, y * scale}; }
};

Frame GameFrame() {
  const ImVec2 size = ImGui::GetIO().DisplaySize;
  return {size.x / 1280.0f, size.y / 720.0f};
}

// The gold-orange rule under the game's titles: bright in the middle, fading out.
void Rule(ImDrawList* draw, const Frame& f, float y) {
  const ImU32 clear = IM_COL32(255, 140, 20, 0);
  const ImVec2 a = f.At(110, y), m = f.At(435, y + 3), b = f.At(760, y);
  draw->AddRectFilledMultiColor(a, {m.x, m.y}, clear, kRuleOrange, kRuleOrange, clear);
  draw->AddRectFilledMultiColor({m.x, a.y}, {b.x, m.y}, kRuleOrange, clear, clear, kRuleOrange);
}

void Text(ImDrawList* draw, ImFont* font, float size, const Frame& f, float x, float y, ImU32 color,
          const std::string& text, float wrap = 0) {
  draw->AddText(font ? font : ImGui::GetFont(), size * f.scale, f.At(x, y), color, text.c_str(),
                nullptr, wrap * f.scale_x);
}

// Width of text drawn with Text, in frame x units.
float TextWidth(ImFont* font, float size, const Frame& f, const std::string& text) {
  font = font ? font : ImGui::GetFont();
  return font->CalcTextSizeA(size, FLT_MAX, 0, text.c_str()).x * f.scale / f.scale_x;
}

void Arrow(ImDrawList* draw, const Frame& f, float x, float y, bool left) {
  const float w = 12, h = 13;
  const ImVec2 tip = f.At(left ? x : x + w, y);
  const ImVec2 top = f.At(left ? x + w : x, y - h), bottom = f.At(left ? x + w : x, y + h);
  draw->AddTriangleFilled(tip, top, bottom, kGold);
}

uint64_t NowMs() {
  return uint64_t(std::chrono::duration_cast<std::chrono::milliseconds>(
                      std::chrono::steady_clock::now().time_since_epoch())
                      .count());
}

// The controller (user 0's real state under the hold), plus the keyboard's
// arrows, Enter and Esc for players without one.
Pad ReadPad() {
  Pad pad;
  rex::input::X_INPUT_STATE state = {};
  if (g.input && g.input->HeldState(0, &state)) {
    pad.buttons = state.gamepad.buttons;
    pad.thumb_lx = state.gamepad.thumb_lx;
    pad.thumb_ly = state.gamepad.thumb_ly;
  }
  if (ImGui::IsKeyDown(ImGuiKey_UpArrow)) pad.buttons |= PageModel::kUp;
  if (ImGui::IsKeyDown(ImGuiKey_DownArrow)) pad.buttons |= PageModel::kDown;
  if (ImGui::IsKeyDown(ImGuiKey_LeftArrow)) pad.buttons |= PageModel::kLeft;
  if (ImGui::IsKeyDown(ImGuiKey_RightArrow)) pad.buttons |= PageModel::kRight;
  if (ImGui::IsKeyDown(ImGuiKey_Enter)) pad.buttons |= PageModel::kA;
  if (ImGui::IsKeyDown(ImGuiKey_Escape)) pad.buttons |= PageModel::kB;
  return pad;
}

// Live settings change now; restart settings are only saved and remembered
// for this session, so nothing half-applies (a render resolution change, for
// one, would reach only render targets created after it).
void Apply(const Change& change, bool restart) {
  if (restart) {
    g.pending[change.name] = change.value;
  } else if (!rex::cvar::SetFlagByName(change.name, change.value)) {
    REXLOG_WARN("settings page: {} = {} was rejected", change.name, change.value);
    return;
  }
  if (!SaveSetting(g.settings_path, change.name, TomlLiteral(change.name, change.value))) {
    REXLOG_WARN("settings page: could not save {} to {}", change.name, g.settings_path.string());
  }
  REXLOG_INFO("settings page: {} = {}{}", change.name, change.value,
              restart ? " (next start)" : "");
}

// The page's rows: the registry with its restart marks, this PC's GPUs, this
// disc's languages and the preset suggested for the GPU in use.
std::vector<Setting> PageSettings() {
  return WithRuntimeChoices(
      WithSuggestedPreset(WithLifecycles(PlayerSettings()), g.sources.suggested_preset),
      g.sources.gpus,
                            [](const std::string& file) {
                              std::error_code error;
                              return !g.sources.game_data_root.empty() &&
                                     std::filesystem::exists(g.sources.game_data_root / file, error);
                            });
}

// Registered with the ImGui drawer only while the page is open or waiting for
// the pad to be released: a registered dialog moves presents to the UI thread
// (docs/research/movement-judder.md).
class PageDialog final : public rex::ui::ImGuiDialog {
 public:
  explicit PageDialog(rex::ui::ImGuiDrawer* drawer)
      : ImGuiDialog(drawer), model_(PageSettings()) {
    std::vector<std::string> shown, applied;
    for (const Setting& setting : model_.settings()) {
      applied.push_back(rex::cvar::GetFlagByName(setting.name));
      const auto pending = g.pending.find(setting.name);
      shown.push_back(pending != g.pending.end() ? pending->second : applied.back());
    }
    model_.Open(shown, NowMs(), applied);
    g.hold = true;
  }

  void CloseFromKey() { model_.Close(); }

 protected:
  void OnDraw(ImGuiIO&) override {
    // A Graphics preset pick changes several settings, each with its own mark.
    for (const Change& change : model_.Update(ReadPad(), NowMs())) {
      const Setting* setting = model_.Find(change.name);
      Apply(change, setting && setting->restart);
    }
    g.hold = model_.holds_input();
    if (!model_.holds_input()) {
      Close();
      return;
    }
    if (model_.is_open()) {
      DrawPage();
    }
  }

  void OnClose() override {
    g.hold = false;
    g.dialog = nullptr;
  }

 private:
  // Laid out after the game's OPTIONS screens: title and rule, tab line, one
  // bar per row, rule, help line. Drawn over the game's own menu column.
  void DrawPage() {
    ImDrawList* draw = ImGui::GetForegroundDrawList();
    const Frame f = GameFrame();
    // Covers the game's own menu column (title at y 66, help text to y 590), taller
    // than it so GRAPHICS's eight rows keep full-size bars and the preset's long help
    // fits, and wide enough for all six tabs.
    draw->AddRectFilledMultiColor(f.At(90, 30), f.At(780, 714), kPanelTop, kPanelTop,
                                  kPanelBottom, kPanelBottom);
    draw->AddRect(f.At(90, 30), f.At(780, 714), IM_COL32(60, 110, 200, 160), 0, 0, 1.5f * f.scale);
    if (!g.config_error.empty()) {
      Text(draw, g.body, 16, f, 118, 50, IM_COL32(255, 120, 60, 255),
           "wweallstars.toml has an error and is ignored (details in the log).");
    }
    Text(draw, g.heading, 46, f, 118, 88, kWhite, "PC SETTINGS");
    Rule(draw, f, 140);

    float x = 130;
    Text(draw, g.heading, 24, f, x, 150, kGrey, "LB");
    x += 44;
    for (size_t t = 0; t < model_.tabs().size(); ++t) {
      const std::string& name = model_.tabs()[t];
      Text(draw, g.heading, 24, f, x, 150, t == model_.tab() ? kGold : kGrey, name);
      x += TextWidth(g.heading, 24, f, name) + 26;
    }
    Text(draw, g.heading, 24, f, x, 150, kGrey, "RB");

    const std::vector<Setting>& settings = model_.settings();
    // Rows fill y 188..564 above the rule: 56 apart, closer on a tab with more
    // than six. Text shrinks only when a bar gets too short for it.
    const std::vector<size_t> rows = model_.rows();
    const float step = std::min(56.0f, 376.0f / float(std::max<size_t>(rows.size(), 1)));
    const float bar = step - 8;
    const float label_size = std::min(32.0f, bar - 6), value_max = std::min(30.0f, bar - 8);
    const float inset = (bar - label_size - 2) / 2;
    float y = 188;
    for (size_t i : rows) {
      const bool selected = i == model_.row();
      const ImVec2 a = f.At(120, y), b = f.At(750, y + bar);
      if (selected) {
        draw->AddRectFilledMultiColor(a, b, kBarTop, kBarTop, kBarBottom, kBarBottom);
        draw->AddLine(a, {b.x, a.y}, IM_COL32(150, 210, 255, 255), 2 * f.scale);
      } else {
        draw->AddRectFilled(a, b, kBarIdle);
      }
      const ImU32 color = selected ? kGold : kGrey;
      Text(draw, g.heading, label_size, f, 140, y + inset, color, settings[i].label);
      std::string value = model_.ValueLabel(i) + (model_.needs_restart(i) ? " *" : "");
      // A text value (the game server's URL) shrinks to fit the bar, then is cut short.
      constexpr float kRoom = 205;  // the bar between the value's arrows, 495..705
      float size = value_max;
      float width = TextWidth(g.heading, size, f, value);
      if (settings[i].is_text() && width > kRoom) {
        size = std::max(20.0f, size * kRoom / width);
        const std::string full = value;
        for (size_t keep = full.size(); (width = TextWidth(g.heading, size, f, value)) > kRoom && keep > 1;) {
          value = full.substr(0, --keep) + "...";
        }
      }
      const ImU32 value_color = settings[i].pinned ? IM_COL32(255, 150, 60, 255)  // launch options
                                : selected            ? kWhite
                                                      : kGrey;
      Text(draw, g.heading, size, f, 600 - width / 2, y + inset + 1 + (value_max - size) / 2, value_color,
           value);
      // Read-only rows (text, changed in the launcher) have nothing to step through.
      if (selected && !settings[i].is_text()) {
        Arrow(draw, f, 492, y + bar / 2, true);
        Arrow(draw, f, 686, y + bar / 2, false);
      }
      y += step;
    }

    Rule(draw, f, 568);
    const size_t row = model_.row();
    Text(draw, g.body, 21, f, 118, 578, kWhite, settings[row].help, 620);
    // A long help line (the Graphics preset's) pushes the note down.
    ImFont* body = g.body ? g.body : ImGui::GetFont();
    const float help_bottom =
        578 + body->CalcTextSizeA(21 * f.scale, FLT_MAX, 620 * f.scale_x, settings[row].help.c_str())
                      .y / f.scale;
    const float note_y = std::max(640.0f, help_bottom + 6);
    if (const std::string note = model_.ApplyNote(row); !note.empty()) {
      Text(draw, g.body, 21, f, 118, note_y, model_.needs_restart(row) ? kGold : kGrey, note);
    }
  }

  PageModel model_;
};

}  // namespace

void RequestOpen();

namespace {

// UI thread. The dialog deletes itself once input is released.
void OpenPage() {
  if (!g.dialog && g.drawer) {
    g.dialog = new PageDialog(g.drawer);
  }
}

void Toggle() {
  if (g.dialog) {
    g.dialog->CloseFromKey();
  } else {
    OpenPage();
  }
}

// Guest thread, on every pad poll: holds the game's input while the page is
// open, and opens it when both sticks are clicked. The poll that sees the
// combo already returns an idle pad, so the clicks never reach the game.
bool HoldGuestInput() {
  if (!g.hold && !g.open_requested) {
    rex::input::X_INPUT_STATE state = {};
    if (g.input->HeldState(0, &state)) {
      std::lock_guard lock(g.combo_mutex);
      if (g.combo.Update(Pad{state.gamepad.buttons, 0, 0})) {
        RequestOpen();
      }
    }
  }
  return g.hold || g.open_requested;
}

}  // namespace

void RequestOpen() {
  if (!g.run_on_ui || g.open_requested.exchange(true)) {
    return;
  }
  g.run_on_ui([] {
    OpenPage();
    g.open_requested = false;
  });
}

void LoadSettingsFile(const std::filesystem::path& config_path,
                      const std::filesystem::path& user_data_root) {
  // Graphics remaster: Supersampling becomes a render resolution, once (the main
  // config migrates before the SDK loads it, in OnConfigurePaths).
  if (MigrateSettingsFile(SettingsPath(user_data_root))) {
    REXLOG_INFO("settings: {} moved from Supersampling to Render resolution",
                SettingsPath(user_data_root).string());
  }
  g.config_error = ConfigError(config_path);
  if (!g.config_error.empty()) {
    REXLOG_ERROR("{} does not parse and was ignored: {}", config_path.string(), g.config_error);
  }
  g.settings_path = SettingsPath(user_data_root);
  if (std::filesystem::exists(g.settings_path)) {
    if (const std::string error = ConfigError(g.settings_path); !error.empty()) {
      REXLOG_ERROR("{} does not parse and was ignored: {}", g.settings_path.string(), error);
    } else {
      rex::cvar::LoadConfig(g.settings_path);
      REXLOG_INFO("settings page: loaded {}", g.settings_path.string());
    }
  }
  // A name the launcher would refuse never reaches the lobby: the SDK signs in
  // with the profile's name when wwe_online_name is empty.
  if (const std::string warning = OnlineNameWarning(
          rex::cvar::GetFlagByName("wwe_online") == "true", rex::cvar::GetFlagByName("wwe_online_name"));
      !warning.empty()) {
    REXLOG_WARN("settings: {}", warning);
    rex::cvar::SetFlagByName("wwe_online_name", "");
  }
  if (const std::string warning = OnlineServerWarning(rex::cvar::GetFlagByName("wwe_online") == "true",
                                                      rex::cvar::GetFlagByName("wwe_online_server"));
      !warning.empty()) {
    REXLOG_WARN("settings: {}", warning);
  }
}

void ApplySuggestedPresetOnFirstStart(const std::string& suggested) {
  // Scene runs keep the settings their command line gives (as Configure).
  if (suggested.empty() || g.settings_path.empty() ||
      !rex::cvar::GetFlagByName("wwe_native_dump_frames").empty()) {
    return;
  }
  const Group group = GraphicsPresets();
  if (!IsFirstSetup(group, [](const std::string& name) {
        return rex::cvar::GetFlagSource(name) != rex::cvar::Source::kDefault;
      })) {
    return;
  }
  for (const Preset& preset : group.presets) {
    if (preset.value != suggested) continue;
    for (size_t m = 0; m < group.members.size(); ++m) {
      const std::string& name = group.members[m];
      // Set now, so this first session already runs on it (Native reads the
      // settings after this); sharpening reaches the presenter at the next start.
      if (!rex::cvar::SetFlagByName(name, preset.values[m]) ||
          !SaveSetting(g.settings_path, name, TomlLiteral(name, preset.values[m]))) {
        REXLOG_WARN("settings: could not apply {} = {} for the suggested preset", name,
                    preset.values[m]);
      }
    }
    REXLOG_INFO("settings: first start, applied the suggested Graphics preset {}", preset.label);
  }
}

void AddFonts(ImFontAtlas* atlas) {
  // Sizes cover a 1440p window without upscaling the glyphs much.
  const char* heading = "C:/Windows/Fonts/impact.ttf";
  const char* body = "C:/Windows/Fonts/segoeuib.ttf";
  if (std::filesystem::exists(heading)) g.heading = atlas->AddFontFromFileTTF(heading, 64.0f);
  if (std::filesystem::exists(body)) g.body = atlas->AddFontFromFileTTF(body, 40.0f);
}

void Configure(rex::ui::ImGuiDrawer* drawer, rex::input::InputSystem* input,
               const std::filesystem::path& user_data_root,
               std::function<void(std::function<void()>)> run_on_ui,
               RuntimeChoiceSources sources) {
  // Scene runs (scripts/scene_runner.py dumps frames) keep the settings their
  // command line gives; the page never opens there.
  if (!rex::cvar::GetFlagByName("wwe_native_dump_frames").empty()) {
    return;
  }
  g.drawer = drawer;
  g.input = input;
  g.settings_path = SettingsPath(user_data_root);
  g.run_on_ui = std::move(run_on_ui);
  g.sources = std::move(sources);
  if (input) {
    input->SetGuestInputHold(HoldGuestInput);
  }
  rex::ui::RegisterBind("bind_pc_settings", "F4", "Open the PC SETTINGS page", Toggle);
}

}  // namespace wwe::settings
