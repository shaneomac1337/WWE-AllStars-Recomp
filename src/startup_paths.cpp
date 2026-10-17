#include "startup_paths.h"

#include <cstdint>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>

#include <nlohmann/json.hpp>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <rex/system.h>
#endif

namespace wwe::startup {

namespace {

// The game data folder chosen at setup, from the launcher's state file; none
// when the file is absent, unreadable or names no folder.
std::optional<std::filesystem::path> ChosenGameData(const std::filesystem::path& user_data_root) {
  std::ifstream file(user_data_root / kLauncherStateName, std::ios::binary);
  if (!file) {
    return std::nullopt;
  }
  const nlohmann::json state = nlohmann::json::parse(file, nullptr, false);
  if (!state.is_object()) {
    return std::nullopt;
  }
  const auto folder = state.find("gameDataDirectory");
  if (folder == state.end() || !folder->is_string() || folder->get_ref<const std::string&>().empty()) {
    return std::nullopt;
  }
  const std::string& utf8 = folder->get_ref<const std::string&>();
  return std::filesystem::path(std::u8string(utf8.begin(), utf8.end()));
}

}  // namespace

SetupStatus CheckSetup(const std::filesystem::path& game_data_root,
                       std::string_view manifest_version) {
  std::ifstream marker(game_data_root / kSetupMarkerName, std::ios::binary);
  std::string line;
  if (!marker || !std::getline(marker, line)) {
    return SetupStatus::kMissing;
  }
  if (line.ends_with('\r')) {
    line.pop_back();
  }
  constexpr std::string_view kKey = "manifest_version=";
  if (!line.starts_with(kKey) || std::string_view(line).substr(kKey.size()) != manifest_version) {
    return SetupStatus::kWrongManifest;
  }
  return SetupStatus::kDone;
}

Startup ResolveStartup(const Folders& given, const std::filesystem::path& exe_folder,
                       std::string_view manifest_version) {
  if (!given.game_data_root.empty()) {
    return {given, false, false};
  }
  const std::filesystem::path root = exe_folder.parent_path();
  Startup startup{given, true, false};
  if (startup.folders.user_data_root.empty()) {
    startup.folders.user_data_root = root / "userdata";
  }
  if (startup.folders.cache_root.empty()) {
    startup.folders.cache_root = root / "cache";
  }

  // The launcher remembers the chosen game data folder in the portable
  // userdata folder, whatever --user_data_root says.
  startup.folders.game_data_root = ChosenGameData(root / "userdata").value_or(root / "gamedata");
  startup.needs_setup = CheckSetup(startup.folders.game_data_root, manifest_version) !=
                        SetupStatus::kDone;
  // Title Update 2 sits next to the game data folder, wherever setup put it (the launcher's
  // TitleUpdateSetup.UpdateDataDirectory).
  if (startup.folders.update_data_root.empty()) {
    // parent_path() of ".../gamedata/" is ".../gamedata": drop a trailing separator first.
    const std::filesystem::path game_data = startup.folders.game_data_root.has_filename()
                                                ? startup.folders.game_data_root
                                                : startup.folders.game_data_root.parent_path();
    startup.folders.update_data_root = game_data.parent_path() / "updatedata";
  }
  startup.logs_folder = root / "logs";
  return startup;
}

std::filesystem::path SessionLogPath(const std::filesystem::path& logs_folder,
                                     std::string_view stamp) {
  const std::string name(stamp);
  std::filesystem::path folder = logs_folder / name;
  std::error_code error;
  for (int n = 2; std::filesystem::exists(folder, error); ++n) {
    folder = logs_folder / (name + "-" + std::to_string(n));
  }
  return folder / "runtime.log";
}

std::vector<std::pair<std::string_view, std::string_view>> PlumbingToSet(
    const Startup& startup, const std::function<std::string(std::string_view)>& value_of) {
  // Keep in step with launcher/src/Launcher.Core/LaunchArguments.cs.
  static constexpr std::pair<std::string_view, std::string_view> kPlumbing[] = {
      {"gpu_plugin", "xenos"},
      {"render_target_path_vulkan", "fsi"},
  };
  std::vector<std::pair<std::string_view, std::string_view>> flags;
  if (!startup.portable) {
    return flags;
  }
  for (const auto& flag : kPlumbing) {
    if (value_of(flag.first).empty()) {
      flags.push_back(flag);
    }
  }
  return flags;
}

std::string LocalStamp(std::time_t time) {
  std::tm local{};
#if defined(_WIN32)
  localtime_s(&local, &time);
#else
  localtime_r(&time, &local);
#endif
  char stamp[32];
  return std::string(stamp, std::strftime(stamp, sizeof(stamp), "%Y%m%d-%H%M%S", &local));
}

void ShowRunLauncherMessage() {
#if defined(_WIN32)
  const std::wstring text(kRunLauncherMessage.begin(), kRunLauncherMessage.end());
  ::MessageBoxW(nullptr, text.c_str(), L"WWE All Stars",
                MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND);
#else
  rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Help, kRunLauncherMessage);
#endif
}

void HideWindow(void* native_window) {
#if defined(_WIN32)
  if (native_window) {
    ::ShowWindow(static_cast<HWND>(native_window), SW_HIDE);
  }
#else
  (void)native_window;
#endif
}

void RequestWindowClose(void* native_window) {
#if defined(_WIN32)
  if (native_window) {
    ::PostMessageW(static_cast<HWND>(native_window), WM_CLOSE, 0, 0);
  }
#else
  (void)native_window;
#endif
}

std::filesystem::path TitleIconPath(const std::filesystem::path& exe_folder) {
  return exe_folder.parent_path() / "userdata" / "icon.ico";
}

void ApplyTitleIcon(void* native_window, const std::filesystem::path& icon_path) {
#if defined(_WIN32)
  if (!native_window) {
    return;
  }
  std::ifstream file(icon_path, std::ios::binary);
  std::string ico((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
  // ICONDIR (6 bytes), then 16-byte entries: size at +8, file offset at +12. Take the largest.
  if (ico.size() < 6 + 16) {
    return;
  }
  const auto u16 = [&](size_t at) { return uint32_t(uint8_t(ico[at])) | uint32_t(uint8_t(ico[at + 1])) << 8; };
  const auto u32 = [&](size_t at) { return u16(at) | u16(at + 2) << 16; };
  const uint32_t count = u16(4);
  uint32_t best_offset = 0, best_size = 0, best_width = 0;
  for (uint32_t i = 0; i < count && 6 + 16 * (i + 1) <= ico.size(); ++i) {
    const size_t entry = 6 + 16 * i;
    const uint32_t width = uint8_t(ico[entry]) ? uint8_t(ico[entry]) : 256;
    const uint32_t size = u32(entry + 8), offset = u32(entry + 12);
    if (offset + uint64_t(size) <= ico.size() && width >= best_width) {
      best_offset = offset, best_size = size, best_width = width;
    }
  }
  if (!best_size) {
    return;
  }
  auto* image = reinterpret_cast<PBYTE>(ico.data() + best_offset);
  const auto make = [&](int metric_x, int metric_y) {
    return ::CreateIconFromResourceEx(image, best_size, TRUE, 0x00030000, ::GetSystemMetrics(metric_x),
                                      ::GetSystemMetrics(metric_y), LR_DEFAULTCOLOR);
  };
  // The icons live as long as the window, which lives as long as the process.
  const HWND window = static_cast<HWND>(native_window);
  if (HICON big_icon = make(SM_CXICON, SM_CYICON)) {
    ::SendMessageW(window, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(big_icon));
  }
  if (HICON small_icon = make(SM_CXSMICON, SM_CYSMICON)) {
    ::SendMessageW(window, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(small_icon));
  }
#else
  (void)native_window;
  (void)icon_path;
#endif
}

}  // namespace wwe::startup
