// The port's own player-facing settings, defined in one place so the in-game
// settings page and the tests see them without linking the renderer. Each
// declares when a change takes effect (in-game settings ticket 01).

#include <rex/cvar.h>

// Native is the default; false runs the Emulated renderer (ADR-0020).
REXCVAR_DEFINE_BOOL(wwe_native_renderer, true, "WWEAllStars",
                    "Draw with the native Vulkan renderer instead of the Emulated renderer "
                    "(Xenos GPU emulation)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
// Read when Native starts: the mode picks texture views, a shader spec bit
// and the pipelines the prebuild plan builds (native/texture_formats.h).
REXCVAR_DEFINE_INT32(wwe_native_color_mode, 0, "WWEAllStars",
                     "Native's colour curve for Xenos gamma: 0 = Original (the Xbox 360's "
                     "piecewise-linear curve, as Xenia), 1 = Natural (sRGB; an Enhancement)")
    .range(0, 1)
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
// Handed to the presenter at startup and, through a change callback, on every
// change after Native starts (native_renderer.cpp, ApplyLiveSharpening).
REXCVAR_DEFINE_DOUBLE(wwe_native_cas, 0.5, "WWEAllStars",
                      "Enhancement: CAS sharpening strength in [0, 1] applied to Native's "
                      "output through the presenter; 0 = off")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);
REXCVAR_DEFINE_INT32(wwe_native_resolution_scale, 2, "WWEAllStars",
                     "Native renderer: render at this multiple of the game's 720p (1-4); a "
                     "frame larger than the window is filtered down to it, a smaller one is "
                     "scaled up by the presenter")
    .range(1, 4)
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
// Read at each frame's start; part of the sampler cache key, so a change gets
// new samplers from the next frame (native/sampler_choice.h).
REXCVAR_DEFINE_INT32(wwe_native_anisotropy, 3, "WWEAllStars",
                     "Native renderer: anisotropic filtering for linear, mipmapped textures, as "
                     "anisotropic_override (-1 = the game's setting, 0 = off, 1..5 = 1x..16x)")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);
// Asked at every engine tick's step choice (game_tick_log.cpp), so a change applies at once.
REXCVAR_DEFINE_BOOL(wwe_60fps_entrances, true, "WWEAllStars",
                    "Run entrances and the arena intro at 60 fps instead of the original 30 "
                    "(an Enhancement)");
// Asked at every engine tick's step choice (game_tick_log.cpp), so a change applies at once.
REXCVAR_DEFINE_BOOL(wwe_120fps_matches, false, "WWEAllStars",
                    "Run offline matches at 120 fps instead of 60; menus and entrances keep "
                    "their rates, and online matches stay at 60 (an Enhancement)");
// Asked at each unlockables refresh (unlock_everything.cpp); the game only ever sets
// its unlock-all flag, so turning this off takes a restart.
REXCVAR_DEFINE_BOOL(wwe_unlock_everything, false, "WWEAllStars",
                    "Unlock every wrestler, arena, attire, Create a Superstar move, entrance "
                    "and Fantasy Warfare match; progress earned while it is on is still saved")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
// Asked when a Kismet delay or delayed link starts (faster_starts.cpp); read at the next start.
REXCVAR_DEFINE_BOOL(wwe_short_loading_screens, true, "WWEAllStars",
                    "Cut three fixed waits of the pre-match load (2.0 s before the intro movie, "
                    "5.0 s and 3.0 s before the bell) to one tick (ignored in online matches)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
// Read by the launcher, not the game: with it on, Play's Coalesced merge drops the THQ
// and WWE movies from [VendorStartup] (faster-starts ticket 01).
REXCVAR_DEFINE_BOOL(wwe_skip_startup_logos, false, "WWEAllStars",
                    "Skip the THQ and WWE logo movies at boot (the launcher removes them from "
                    "the startup movie list; the disclaimer stays)")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);
// Asked on every pad request and every hit, so a change applies at once.
REXCVAR_DEFINE_INT32(wwe_vibration_mode, 2, "WWEAllStars",
                     "Pad vibration: 0 = Off, 1 = Original (the Xbox 360's aerial hit pulse "
                     "only), 2 = Enhanced (a pulse on every hit plus big moments; an Enhancement)")
    .range(0, 2);
// Asked on every pad request, so a change applies at once.
REXCVAR_DEFINE_INT32(wwe_vibration_strength, 100, "WWEAllStars",
                     "Pad vibration strength in percent (0-100), applied to every rumble in "
                     "every mode with a perceptual curve (strength squared)")
    .range(0, 100);
