namespace WweAllStars.Launcher.Core;

/// <summary>
/// The game's command line. It carries folder paths and hidden plumbing only, never a player
/// setting: a setting given on the command line shows as "pinned" in game
/// (src/settings/registry.h), so player settings go through the settings file instead.
/// </summary>
public static class LaunchArguments
{
    // FSI keeps EDRAM contents across frames the guest does not redraw (scripts/smoke.ps1).
    private const string RenderTargetPath = "--render_target_path_vulkan=fsi";

    /// <param name="updateDataRoot">The update folder the mod library prepared; TU2's own folder when null.</param>
    public static IReadOnlyList<string> Build(PortableLayout layout, string sessionLogPath, string? updateDataRoot = null) =>
    [
        "--game_data_root=" + layout.GameDataDirectory,
        "--update_data_root=" + (updateDataRoot ?? layout.UpdateDataDirectory),
        "--user_data_root=" + layout.UserDataDirectory,
        "--cache_root=" + layout.CacheDirectory,
        "--gpu_plugin=xenos",
        RenderTargetPath,
        "--log_file=" + sessionLogPath,
        "--log_level=info",
        "--log_flush_interval=1",
    ];

    /// <summary>The prepare log: one file, outside the session folders, so it never counts as the latest session.</summary>
    public static string PrepareLogPath(PortableLayout layout) => Path.Combine(layout.LogsDirectory, "prepare-pipelines.log");

    /// <summary>
    /// The game's headless prepare-pipelines mode (ticket 06): the same folders and GPU plugin
    /// as Play, so it builds for the GPU and settings the game will use. The mode forces
    /// Native and a hidden window; the windowed flag is spelled out all the same.
    /// </summary>
    /// <param name="updateDataRoot">As <see cref="Build"/>: the folder Play uses; TU2's own folder when null.</param>
    public static IReadOnlyList<string> PreparePipelines(PortableLayout layout, string? updateDataRoot = null) =>
    [
        "--wwe_prepare_pipelines=true",
        "--game_data_root=" + layout.GameDataDirectory,
        "--update_data_root=" + (updateDataRoot ?? layout.UpdateDataDirectory),
        "--user_data_root=" + layout.UserDataDirectory,
        "--cache_root=" + layout.CacheDirectory,
        "--gpu_plugin=xenos",
        // As Play: the render target path shapes the pipelines, so the prepare must match it.
        RenderTargetPath,
        "--fullscreen=false",
        "--log_file=" + PrepareLogPath(layout),
        "--log_level=info",
    ];
}
