// Where the game's folders are when it starts, and whether the launcher has
// set the game up (launcher spec, "Game exe without setup").
//
// Folder paths given on the command line win: the launcher and the developer
// scripts pass them, and the SDK checks them itself. Without a game data
// folder on the command line, the game uses the portable layout around its
// own folder (<root>/game/wweallstars.exe): <root>/gamedata (or the folder
// chosen at setup, remembered in <root>/userdata/launcher-state.json),
// updatedata (Title Update 2) next to that game data folder, <root>/userdata
// and <root>/cache. That game data folder counts only once the launcher's
// setup marker names the disc manifest this build expects.

#pragma once

#include <ctime>
#include <filesystem>
#include <functional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace wwe::startup {

// The launcher's setup marker in the game data folder: one line,
// "manifest_version=<id>" (launcher/src/Launcher.Core/GameSetup.cs).
inline constexpr std::string_view kSetupMarkerName = ".wweallstars-setup";
// The launcher's own state file in the user data folder (LauncherState.cs).
inline constexpr std::string_view kLauncherStateName = "launcher-state.json";

enum class SetupStatus { kDone, kMissing, kWrongManifest };

// Whether the game data folder holds a setup marker naming manifest_version.
SetupStatus CheckSetup(const std::filesystem::path& game_data_root,
                       std::string_view manifest_version);

// Folder paths; an empty one keeps the SDK's default.
struct Folders {
  std::filesystem::path game_data_root;
  std::filesystem::path user_data_root;
  std::filesystem::path cache_root;
  // Title Update 2's files, mounted as update:\ (updatedata next to the game data folder).
  std::filesystem::path update_data_root;
};

struct Startup {
  Folders folders;
  // True when the folders come from the portable layout, not the command line.
  bool portable = false;
  // The game data folder has not been set up by the launcher: tell the player
  // to run it once instead of starting.
  bool needs_setup = false;
  // Where a portable start writes its session log (<root>/logs); empty when the
  // command line decides, as the launcher and the developer scripts do.
  std::filesystem::path logs_folder;
};

// The folders to start with, from the paths given on the command line (empty
// when not given) and the folder holding the game exe.
Startup ResolveStartup(const Folders& given, const std::filesystem::path& exe_folder,
                       std::string_view manifest_version);

// A session's log in logs_folder, named the way the launcher names its own
// (PortableLayout.NewSessionLogPath): <stamp>/runtime.log, or <stamp>-2,
// <stamp>-3... when that folder exists already. stamp is the local start time
// as yyyyMMdd-HHmmss. Creates nothing.
std::filesystem::path SessionLogPath(const std::filesystem::path& logs_folder,
                                     std::string_view stamp);

// The plumbing flags a portable start sets itself, as name/value pairs: the
// ones the launcher passes (LaunchArguments.cs) that the release has no config
// for, so a direct start loads the GPU backend. A flag value_of already
// reports set (the command line) is kept; a non-portable start sets none.
std::vector<std::pair<std::string_view, std::string_view>> PlumbingToSet(
    const Startup& startup, const std::function<std::string(std::string_view)>& value_of);

// time as local yyyyMMdd-HHmmss, the stamp SessionLogPath takes.
std::string LocalStamp(std::time_t time);

// What a player who starts the game before setup is told.
inline constexpr std::string_view kRunLauncherMessage =
    "Run WWEAllStarsLauncher once to set up your game files.";

// Shows kRunLauncherMessage in a native message box and waits for it to close.
void ShowRunLauncherMessage();

// Hides the game window (the prepare-pipelines mode draws nothing); the
// window's native handle (an HWND on Windows), ignored elsewhere.
void HideWindow(void* native_window);

// Asks the game window to close, as its close button does (WM_CLOSE, so the
// same close request and shutdown run); the native handle, ignored elsewhere.
void RequestWindowClose(void* native_window);

// The title tile the launcher cut from the player's own disc for its shortcuts and
// window: <root>/userdata/icon.ico beside the game's folder. Never shipped.
std::filesystem::path TitleIconPath(const std::filesystem::path& exe_folder);

// Sets that icon on the game window and its taskbar button, as Xenia shows the
// game's title icon; the exe keeps the project's own. Does nothing without the file.
void ApplyTitleIcon(void* native_window, const std::filesystem::path& icon_path);

}  // namespace wwe::startup
