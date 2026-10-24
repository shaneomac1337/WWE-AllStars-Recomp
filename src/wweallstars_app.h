#pragma once

#include <atomic>
#include <cstdlib>
#include <span>

#include <rex/rex_app.h>
#include <rex/system.h>
#include <rex/system/host_gpu_loss.h>
#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/input/input_system.h>
#include <rex/logging.h>
#include <rex/perf/counter.h>
#include <rex/graphics/graphics_system.h>
#include <rex/ui/keybinds.h>
#include <rex/ui/presenter.h>
#include <rex/ui/window.h>

#include "crash_report.h"
#include "d3d_census.h"
#include "frame_capture.h"
#include "frame_clock.h"
#include "game_tick_log.h"
#include "image_check.h"
#include "native/native_renderer.h"
#include "resolve_watch.h"
#include "script_input.h"
#include "settings/config_file.h"
#include "settings/exit_row.h"
#include "settings/gpu_list.h"
#include "settings/schema.h"
#include "settings/settings_page.h"
#include "startup_paths.h"
#include "vibration/force_feedback.h"
#include "vibration/mode.h"
#include "xma_stats.h"

#if defined(WWE_PGO_GENERATE)
extern "C" int __llvm_profile_dump(void);  // clang_rt.profile
#endif

class WweAllStarsApp : public rex::ReXApp {
 public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp> Create(
      rex::ui::WindowedAppContext& ctx) {
    wwe::crash::Install();
    // The launcher's headless export, before any window or the runtime.
    wwe::settings::PrintSchemaAndExitIfAsked();
    wwe::settings::PrintGpusAndExitIfAsked();
    return std::unique_ptr<WweAllStarsApp>(new WweAllStarsApp(ctx, "wweallstars",
        PPCImageConfig));
  }

  void OnPreSetup(rex::RuntimeConfig& config) override {
    // The prepare-pipelines mode needs Native and a window it can hide.
    if (wwe::native::PrepareRequested()) {
      rex::cvar::SetFlagByName("wwe_native_renderer", "true");
      rex::cvar::SetFlagByName("fullscreen", "false");
    }
    wwe::InstallScriptInput(config, wwe::PresentCount);
    wwe::native::ApplyPresenterSettings();
  }

  // Started without the launcher (Steam Deck Game Mode, a shortcut), the game
  // finds its folders in the portable layout; before setup it tells the player
  // to run the launcher once instead of failing on game_data_root.
  bool SetupEnvironment() override {
    startup_ = wwe::startup::ResolveStartup(
        {.game_data_root = rex::cvar::GetFlagByName("game_data_root"),
         .user_data_root = rex::cvar::GetFlagByName("user_data_root"),
         .cache_root = rex::cvar::GetFlagByName("cache_root"),
         .update_data_root = rex::cvar::GetFlagByName("update_data_root")},
        rex::filesystem::GetExecutableFolder(), WWE_DISC_MANIFEST_VERSION);
    if (startup_.needs_setup) {
      wwe::startup::ShowRunLauncherMessage();
      return false;
    }
    // A direct start loads the GPU backend the launcher would pass, with the
    // same priority as the launcher's command line.
    for (const auto& [name, value] : wwe::startup::PlumbingToSet(
             startup_, [](std::string_view flag) { return rex::cvar::GetFlagByName(flag); })) {
      rex::cvar::SetFlagFromCommandLine(name, value);
    }
    // A direct start logs beside the launcher's own sessions (<root>/logs), not
    // in the SDK's <exe folder>/logs, so the launcher's Open logs finds it.
    if (!startup_.logs_folder.empty() && rex::cvar::GetFlagByName("log_file").empty()) {
      const std::u8string log = wwe::startup::SessionLogPath(
          startup_.logs_folder, wwe::startup::LocalStamp(std::time(nullptr))).u8string();
      rex::cvar::SetFlagFromCommandLine("log_file", std::string(log.begin(), log.end()));
    }
    return rex::ReXApp::SetupEnvironment();
  }

  void OnConfigurePaths(rex::PathConfig& paths) override {
    if (startup_.portable) {
      paths.game_data_root = startup_.folders.game_data_root;
      paths.user_data_root = startup_.folders.user_data_root;
      paths.cache_root = startup_.folders.cache_root;
      paths.update_data_root = startup_.folders.update_data_root;
    }
    config_path_ = paths.config_path;
    // Before the SDK loads it: Supersampling becomes a render resolution (graphics remaster).
    wwe::settings::MigrateSettingsFile(config_path_);
    user_data_folder_ = paths.user_data_root;
    wwe::crash::SetSettingsFile(wwe::settings::SettingsPath(user_data_folder_));
  }

  void OnConfigureFonts(ImFontAtlas* atlas) override { wwe::settings::AddFonts(atlas); }

  // The PC SETTINGS page's own file, over the main config (command-line flags
  // still win), before anything reads the settings.
  void OnPostInitLogging() override {
    wwe::settings::LoadSettingsFile(config_path_, user_data_folder_);
  }

  // The recompiled code is TU2 code (ADR-0022): stop before it runs on any other
  // image, such as the disc exe without default.xexp or with another patch.
  void OnPostLoadXexImage() override {
    namespace check = wwe::image_check;
    const std::span<const uint8_t> text(runtime()->memory()->TranslateVirtual(check::kTextStart),
                                        check::kTextSize);
    const uint64_t hash = check::TextHash(text);
    if (hash == check::kTextHash) {
      REXLOG_INFO("Image check: Title Update 2 image (.text {:016X})", hash);
      return;
    }
    REXLOG_ERROR("Image check failed: .text {:016X}, this build needs {:016X} (Title Update 2). {}",
                 hash, check::kTextHash, check::kMismatchMessage);
    rex::FlushLogging();
    if (!rex::cvar::Query<bool>("headless")) {
      rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Error, check::kMismatchMessage);
    }
    std::_Exit(EXIT_FAILURE);
  }

  void OnPostSetup() override {
    // The SDK builds Tracy with manual lifetime, so nothing profiles until it is started.
    if (rex::cvar::Query<bool>("wwe_profile")) {
      rex::perf::Profiler::Startup();
      REXLOG_INFO("Tracy profiler started: {}", rex::perf::Profiler::is_enabled());
    }
    // v0.10.0 defines perf_log_csv and writes counters at guest swaps, but
    // does not open the requested file. Initialize it before guest threads run.
    const auto path = rex::cvar::GetFlagByName("perf_log_csv");
    if (!path.empty()) {
      rex::perf::SetCsvLogPath(path);
    }
    wwe::settings::LogGpu(runtime());
    {
      const auto [gpu, driver] = wwe::settings::RuntimeGpuAndDriver(runtime());
      wwe::crash::SetGpu(gpu, driver);
      wwe::crash::SetGuestMemory(runtime()->memory()->virtual_membase(),
                                 runtime()->memory()->physical_membase());
      if (auto* game_window = window()) {
        wwe::crash::SetWindow(game_window->GetNativeWindowHandle());
      }
      // A device loss the SDK sees is a GPU failure under Native.
      rex::system::host_gpu_loss::SetHandler(&wwe::native::OnHostGpuLoss);
    }
    wwe::settings::ApplySuggestedPresetOnFirstStart(wwe::settings::RuntimeSuggestedPreset(runtime()));
    wwe::frame_clock::Configure();
    wwe::game_tick::Configure(runtime());
    wwe::frame_capture::Configure();
    wwe::native::Configure(runtime());
    ConfigureLiveVsync();
    // The launcher's setup step: build pipelines, report and exit before the
    // game module launches.
    if (wwe::native::PrepareRequested()) {
      if (auto* game_window = window()) {
        wwe::startup::HideWindow(game_window->GetNativeWindowHandle());
      }
      wwe::native::PreparePipelinesAndExit();
    }
    wwe::ConfigureShaderTopUp(runtime()->cache_root());
    wwe::resolve_watch::Configure(runtime());
    // The rumble mixer reads the pause menu and GWorld at each pad request.
    wwe::vibration::Configure(
        static_cast<rex::input::InputSystem*>(runtime()->input_system()),
        &wwe::vibration::Clock::now, [base = runtime()->memory()->virtual_membase()] {
          return wwe::vibration::ReadGameState([base](uint32_t address) {
            return wwe::vibration::guest::Load32(base, address);
          });
        });
    // The PC SETTINGS page (F4, or both sticks clicked). Presentation (and the ImGui drawer) is set up
    // before the runtime, so this waits for both here.
    wwe::settings::Configure(imgui_drawer(),
                             static_cast<rex::input::InputSystem*>(runtime()->input_system()),
                             user_data_folder_, [this](std::function<void()> function) {
                               app_context().CallInUIThread(std::move(function));
                             },
                             {wwe::settings::RuntimeGpuNames(runtime()),
                              runtime()->game_data_root(),
                              wwe::settings::RuntimeSuggestedPreset(runtime())});
    // EXIT TO DESKTOP in the main menu: the same close as the window's button.
    wwe::settings::SetExitToDesktop([this] {
      app_context().CallInUIThread([this] {
        if (auto* game_window = window()) {
          wwe::startup::RequestWindowClose(game_window->GetNativeWindowHandle());
        }
      });
    });
  }

  // Closing the window hard-exits the process without OnShutdown, so the
  // final counts are logged here; OnShutdown covers the guest exiting.
  bool OnWindowCloseRequested() override {
    wwe::xma::LogStats("final");
    wwe::native::OnExit();
#if defined(WWE_PGO_GENERATE)
    // The hard exit skips the profile runtime's atexit write. Dumping marks the profile
    // written, so a later normal exit does not merge the counters a second time.
    if (__llvm_profile_dump() == 0) {
      REXLOG_INFO("PGO profile written");
    } else {
      REXLOG_WARN("PGO profile could not be written");
    }
#endif
    return true;
  }

  void OnShutdown() override {
    wwe::xma::LogStats("final");
    wwe::native::OnExit();
    wwe::native::Shutdown();
  }

  // Holding the capture key saves one capture: its key repeats are dropped
  // before the binds run (the SDK's bind dispatch does not see repeats).
  void OnKeyDown(rex::ui::KeyEvent& e) override {
    if (wwe::frame_capture::IsCaptureRepeat(e.virtual_key(), e.prev_state())) {
      e.set_handled(true);
      return;
    }
    rex::ReXApp::OnKeyDown(e);
  }

  // Native fits its output to the window (graphics-remaster ticket 01); the
  // render thread reads the size the UI thread hands over here.
  void OnWindowPixelSizeChanged(uint32_t pixel_width, uint32_t pixel_height) override {
    rex::ReXApp::OnWindowPixelSizeChanged(pixel_width, pixel_height);
    wwe::native::SetWindowSize(pixel_width, pixel_height);
  }

  void OnPreLaunchModule() override {
    // Hide the cursor over the game once the mouse has been still for a second.
    if (auto* game_window = window()) {
      game_window->SetCursorAutoHideDelayMs(1000);
      game_window->SetCursorVisibility(rex::ui::Window::CursorVisibility::kAutoHidden);
      wwe::startup::ApplyTitleIcon(game_window->GetNativeWindowHandle(),
                                   wwe::startup::TitleIconPath(rex::filesystem::GetExecutableFolder()));
    }
  }

 private:
  // A VSync change (the PC SETTINGS page, the F6 overlay) rebuilds the
  // swapchain, which reads the cvar when it is created. Both pages apply changes
  // from inside a UI drawer, where the presenter must not reconnect, so the
  // rebuild is deferred (CallInUIThread would run it at once on the UI thread);
  // toggles made before it runs share one rebuild.
  void ConfigureLiveVsync() {
    rex::cvar::RegisterChangeCallback("present_vsync", [this](std::string_view, std::string_view) {
      if (vsync_rebuild_posted_.exchange(true)) {
        return;
      }
      app_context().CallInUIThreadDeferred([this] {
        vsync_rebuild_posted_ = false;
        auto* graphics = runtime() ? runtime()->graphics_system() : nullptr;
        if (rex::ui::Presenter* presenter = graphics ? graphics->presenter() : nullptr) {
          REXLOG_INFO("present_vsync = {}: rebuilding the swapchain",
                      rex::cvar::GetFlagByName("present_vsync"));
          presenter->OnSurfaceResizeFromUIThread();
        }
      });
    });
  }

  std::atomic<bool> vsync_rebuild_posted_{false};
  wwe::startup::Startup startup_;
  std::filesystem::path config_path_;
  // Holds the PC SETTINGS file (wwe::settings::SettingsPath).
  std::filesystem::path user_data_folder_;
};
