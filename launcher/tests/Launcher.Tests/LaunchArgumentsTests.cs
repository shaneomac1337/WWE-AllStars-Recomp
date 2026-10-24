using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public class LaunchArgumentsTests
{
    // Every player setting the in-game page lists (src/settings/registry.h), plus the two the
    // spec adds. A flag for any of them would show as "pinned" in game.
    private static readonly string[] PlayerSettings =
    [
        "fullscreen", "present_vsync", "vblank_follows_display", "wwe_native_renderer",
        "wwe_native_cas", "wwe_native_resolution_scale",
        "wwe_native_anisotropy", "wwe_native_color_mode", "wwe_60fps_entrances", "wwe_120fps_matches", "audio_mute", "wwe_vibration_mode", "wwe_vibration_strength",
        "vulkan_device", "user_language",
    ];

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "WWE All Stars");

    [Fact]
    public void Portable_layout_gives_exactly_the_plumbing_flags_and_paths()
    {
        var layout = new PortableLayout(Root);
        var log = Path.Combine(Root, "logs", "20261004-120000", "runtime.log");

        var arguments = LaunchArguments.Build(layout, log);

        Assert.Equal(
            [
                "--game_data_root=" + Path.Combine(Root, "gamedata"),
                "--update_data_root=" + Path.Combine(Root, "updatedata"),
                "--user_data_root=" + Path.Combine(Root, "userdata"),
                "--cache_root=" + Path.Combine(Root, "cache"),
                "--gpu_plugin=xenos",
                "--render_target_path_vulkan=fsi",
                "--log_file=" + log,
                "--log_level=info",
                "--log_flush_interval=1",
            ],
            arguments);
    }

    [Fact]
    public void No_argument_names_a_player_setting()
    {
        var arguments = LaunchArguments.Build(new PortableLayout(Root), Path.Combine(Root, "x.log"));

        var names = arguments.Select(argument => argument[2..argument.IndexOf('=')]);
        Assert.Empty(names.Intersect(PlayerSettings));
    }

    [Fact]
    public void A_chosen_game_data_folder_replaces_the_portable_one()
    {
        var gameData = Path.Combine(Path.GetTempPath(), "elsewhere", "assets");
        var layout = new PortableLayout(Root, gameDataDirectory: gameData);

        var arguments = LaunchArguments.Build(layout, Path.Combine(Root, "x.log"));

        Assert.Contains("--game_data_root=" + gameData, arguments);
        // Title Update 2 sits next to the chosen game data folder.
        Assert.Contains("--update_data_root=" + Path.Combine(Path.GetTempPath(), "elsewhere", "updatedata"), arguments);
        Assert.Contains("--user_data_root=" + Path.Combine(Root, "userdata"), arguments);
    }

    [Fact]
    public void The_prepare_pipelines_mode_mounts_title_update_2_too()
    {
        // The game stops on a different image than the TU2 one it was built from (ticket tu2-dlc 03).
        var arguments = LaunchArguments.PreparePipelines(new PortableLayout(Root));
        Assert.Contains("--update_data_root=" + Path.Combine(Root, "updatedata"), arguments);
    }
}
