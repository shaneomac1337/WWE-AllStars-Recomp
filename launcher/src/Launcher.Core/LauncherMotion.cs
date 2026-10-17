namespace WweAllStars.Launcher.Core;

/// <summary>
/// Whether the launcher's slow motion (the key-art pan and zoom, the turning bursts) may run.
/// Avalonia redraws the whole window about 60 times a second while any animation runs, even
/// when the window is minimized or covered, so the motion runs only while the player looks at
/// the launcher: focused, not minimized, and no game running (launcher ticket 28). With reduced
/// effects it never runs, and the window drops its Mica material too (GitHub issue #1).
/// </summary>
public static class LauncherMotion
{
    public static bool Allowed(bool focused, bool minimized, bool gameRunning, bool reduceEffects) =>
        focused && !minimized && !gameRunning && !reduceEffects;

    /// <summary>The saved preference or <c>--reduce-effects</c>, which a launcher that freezes can be started with.</summary>
    public static bool EffectsReduced(bool preference, bool commandLine) => preference || commandLine;
}
