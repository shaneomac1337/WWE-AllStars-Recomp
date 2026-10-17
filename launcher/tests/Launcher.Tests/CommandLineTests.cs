using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe launcher ").FullName;
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 7, 11, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Run(params string[] args)
    {
        var output = new StringWriter();
        var exitCode = HeadlessCommands.Run(LauncherOptions.Parse(args, _root), output, () => Now);
        Assert.Equal(0, exitCode);
        return output.ToString();
    }

    [Fact]
    public void No_arguments_open_the_window_on_the_launcher_folder()
    {
        var options = LauncherOptions.Parse([], _root);

        Assert.False(options.Headless);
        Assert.Equal(Path.Combine(_root, "gamedata"), options.Layout.GameDataDirectory);
        Assert.Equal(Path.Combine(_root, "game", "wweallstars.exe"), options.Layout.GameExecutable);
    }

    [Fact]
    public void Print_arguments_prints_the_quoted_command_line_without_creating_a_log_folder()
    {
        var text = Run("--print-arguments");

        var exe = Path.Combine(_root, "game", "wweallstars.exe");
        var log = Path.Combine(_root, "logs", "20261004-090711", "runtime.log");
        Assert.Equal(
            $"\"{exe}\" \"--game_data_root={Path.Combine(_root, "gamedata")}\"" +
            $" \"--update_data_root={Path.Combine(_root, "updatedata")}\"" +
            $" \"--user_data_root={Path.Combine(_root, "userdata")}\"" +
            $" \"--cache_root={Path.Combine(_root, "cache")}\"" +
            " --gpu_plugin=xenos --render_target_path_vulkan=fsi" +
            $" \"--log_file={log}\" --log_level=info --log_flush_interval=1" + Environment.NewLine,
            text);
        Assert.False(Directory.Exists(Path.Combine(_root, "logs")));
    }

    [Fact]
    public void Game_data_and_game_exe_can_point_at_a_developer_checkout()
    {
        var assets = Path.Combine(_root, "repo", "assets");
        var exe = Path.Combine(_root, "repo", "out", "wweallstars.exe");

        var options = LauncherOptions.Parse(["--game-data", assets, "--game", exe], _root);

        Assert.Equal(assets, options.Layout.GameDataDirectory);
        Assert.Equal(exe, options.Layout.GameExecutable);
        Assert.Equal(Path.Combine(_root, "userdata"), options.Layout.UserDataDirectory);
    }

    [Fact]
    public void Root_moves_every_portable_folder()
    {
        var other = Path.Combine(_root, "portable");

        var options = LauncherOptions.Parse(["--root", other], _root);

        Assert.Equal(Path.Combine(other, "gamedata"), options.Layout.GameDataDirectory);
        Assert.Equal(Path.Combine(other, "logs"), options.Layout.LogsDirectory);
    }

    [Fact]
    public void Reduce_effects_opens_the_window_with_its_effects_off()
    {
        Assert.False(LauncherOptions.Parse([], _root).ReduceEffects);

        var options = LauncherOptions.Parse(["--reduce-effects"], _root);

        Assert.True(options.ReduceEffects);
        Assert.False(options.Headless);
    }

    [Theory]
    [InlineData("--debug-crash", LauncherDebugCrash.UiThread)]
    [InlineData("--debug-crash-task", LauncherDebugCrash.BackgroundTask)]
    public void A_debug_switch_makes_the_window_launcher_throw(string option, LauncherDebugCrash crash)
    {
        Assert.Equal(LauncherDebugCrash.None, LauncherOptions.Parse([], _root).DebugCrash);

        var options = LauncherOptions.Parse([option], _root);

        Assert.Equal(crash, options.DebugCrash);
        Assert.False(options.Headless);
    }

    [Theory]
    [InlineData("--frobnicate")]
    [InlineData("--game-data")]
    public void An_unknown_or_incomplete_option_is_a_plain_error(string argument)
    {
        var error = Assert.Throws<LauncherException>(() => LauncherOptions.Parse([argument], _root));

        Assert.Contains(argument, error.Message);
    }

    // A stand-in Title Update 2 package and its manifest (the real ones are game data / embedded).
    private static readonly (string Path, byte[] Data)[] Tu2Files =
    [
        ("default.xexp", TestStfs.Content(4000, 31)),
        ("Coalesced.ini", TestStfs.Content(2 * 0x1000 + 9, 32)),
    ];

    private string TuPackage => Path.Combine(_root, "dumps", "tu00000005_00000000");
    private string TuManifest => Path.Combine(_root, "tu-manifest.tsv");

    /// <summary>--title-update and --tu-manifest for the stand-in TU2 (written on first use).</summary>
    private string[] Tu(bool withPackage = true)
    {
        if (!File.Exists(TuPackage))
        {
            var package = new TestStfs { ContentType = 0x000B0000, MediaId = TitleUpdateSetup.MediaId, Version = 5, BaseVersion = 5 };
            package.Files.AddRange(Tu2Files);
            package.WriteTo(TuPackage);
            File.WriteAllText(TuManifest, "manifest_version\ttest-tu2\n" + string.Join('\n', Tu2Files.Select(f =>
                $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Data))}\t{f.Data.Length}\t{f.Path}")) + "\n");
        }
        return withPackage ? ["--title-update", TuPackage, "--tu-manifest", TuManifest] : ["--tu-manifest", TuManifest];
    }

    private (string Iso, string Manifest) WriteDisc(TestDisc disc)
    {
        var iso = Path.Combine(_root, "my disc.iso");
        var manifest = Path.Combine(_root, "manifest.tsv");
        disc.WriteImage(iso);
        File.WriteAllText(manifest, disc.ManifestText());
        return (iso, manifest);
    }

    [Fact]
    public void Verify_reports_a_supported_disc_and_writes_nothing()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());

        var text = Run("--verify", iso, "--manifest", manifest);

        Assert.Contains("supported", text);
        Assert.False(Directory.Exists(Path.Combine(_root, "gamedata")));
        Assert.False(Directory.Exists(Path.Combine(_root, "userdata")));
    }

    [Fact]
    public void Verify_of_another_disc_is_a_plain_error()
    {
        var disc = new TestDisc();
        disc.ManifestOverrides["default.xex"] = "XEX2 other"u8.ToArray();
        var (iso, manifest) = WriteDisc(disc);

        var error = Assert.Throws<LauncherException>(() =>
            HeadlessCommands.Run(LauncherOptions.Parse(["--verify", iso, "--manifest", manifest], _root), new StringWriter(), () => Now));

        Assert.Contains("not the supported version", error.Message);
    }

    [Fact]
    public void Extract_sets_up_the_game_data_folder_and_reports_progress()
    {
        var disc = new TestDisc();
        var (iso, manifest) = WriteDisc(disc);

        var text = Run(["--extract", iso, "--manifest", manifest, .. Tu()]);

        Assert.Contains("100%", text);
        Assert.True(File.Exists(Path.Combine(_root, "gamedata", "CookedXenon", "Startup.xxx")));
        Assert.True(File.Exists(Path.Combine(_root, "gamedata", SetupMarker.FileName)));
    }

    [Fact]
    public void The_launcher_remembers_a_chosen_game_data_folder_after_setup()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        var chosen = Path.Combine(_root, "big drive", "WWE game data");

        Run(["--extract", iso, "--manifest", manifest, "--game-data", chosen, .. Tu()]);
        var reopened = LauncherOptions.Parse([], _root);

        Assert.Equal(chosen, reopened.Layout.GameDataDirectory);
        Assert.Equal(SetupStatus.Done, new GameSetup(DiscManifest.Load(manifest), new DriveDiskSpace(), TimeProvider.System)
            .StatusOf(reopened.Layout.GameDataDirectory));
    }

    [Fact]
    public void A_set_up_folder_still_finds_its_game_data_after_it_is_moved()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        var install = Path.Combine(_root, "install");
        var moved = Path.Combine(_root, "moved install");
        Assert.Equal(0, HeadlessCommands.Run(
            LauncherOptions.Parse(["--extract", iso, "--manifest", manifest, .. Tu()], install), new StringWriter(), () => Now));

        Directory.Move(install, moved);
        var reopened = LauncherOptions.Parse([], moved);

        Assert.Equal(Path.Combine(moved, "gamedata"), reopened.Layout.GameDataDirectory);
    }

    [Fact]
    public void A_remembered_folder_that_is_gone_gives_way_to_the_game_data_beside_the_launcher()
    {
        // What a release before relative paths saved, copied to another PC.
        var userData = new PortableLayout(_root).UserDataDirectory;
        new LauncherState { GameDataDirectory = Path.Combine(_root, "old pc", "gamedata") }.Save(userData);
        Directory.CreateDirectory(Path.Combine(_root, "gamedata"));

        var options = LauncherOptions.Parse([], _root);

        Assert.Equal(Path.Combine(_root, "gamedata"), options.Layout.GameDataDirectory);
    }

    [Fact]
    public void A_remembered_folder_that_is_gone_is_kept_when_there_is_no_game_data_beside_the_launcher()
    {
        // An unplugged drive: setup must not quietly move the game data elsewhere.
        var userData = new PortableLayout(_root).UserDataDirectory;
        var unplugged = Path.Combine(_root, "unplugged drive", "gamedata");
        new LauncherState { GameDataDirectory = unplugged }.Save(userData);

        var options = LauncherOptions.Parse([], _root);

        Assert.Equal(unplugged, options.Layout.GameDataDirectory);
    }

    [Fact]
    public void Repair_lists_the_bad_files_and_re_extracts_them()
    {
        var disc = new TestDisc();
        var (iso, manifest) = WriteDisc(disc);
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        File.Delete(Path.Combine(_root, "gamedata", "nxeart"));

        var text = Run(["--repair", iso, "--manifest", manifest, .. Tu(withPackage: false)]);

        Assert.Contains("missing: nxeart", text);
        Assert.Contains("Repaired 1 file", text);
        Assert.Equal(disc.Files["nxeart"], File.ReadAllBytes(Path.Combine(_root, "gamedata", "nxeart")));
    }

    [Fact]
    public void Repair_of_a_clean_folder_reports_nothing_to_repair()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);

        var text = Run(["--repair", iso, "--manifest", manifest, .. Tu(withPackage: false)]);

        Assert.Contains("Nothing to repair", text);
    }

    [Fact]
    public void Repair_works_on_a_folder_that_lost_its_marker_and_marks_it_set_up()
    {
        var disc = new TestDisc();
        var (iso, manifest) = WriteDisc(disc);
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        File.Delete(Path.Combine(_root, "gamedata", SetupMarker.FileName));
        File.Delete(Path.Combine(_root, "gamedata", "nxeart"));

        var text = Run(["--repair", iso, "--manifest", manifest, .. Tu(withPackage: false)]);

        Assert.Contains("missing: nxeart", text);
        Assert.Contains("Repaired 1 file", text);
        Assert.Equal(SetupStatus.Done, new GameSetup(DiscManifest.Load(manifest), new DriveDiskSpace(), TimeProvider.System)
            .StatusOf(Path.Combine(_root, "gamedata")));
    }

    [Fact]
    public void Repair_of_a_clean_folder_that_lost_its_marker_marks_it_set_up()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        File.Delete(Path.Combine(_root, "gamedata", SetupMarker.FileName));

        var text = Run(["--repair", iso, "--manifest", manifest, .. Tu(withPackage: false)]);

        Assert.Contains("Nothing to repair", text);
        Assert.Equal(SetupStatus.Done, new GameSetup(DiscManifest.Load(manifest), new DriveDiskSpace(), TimeProvider.System)
            .StatusOf(Path.Combine(_root, "gamedata")));
    }

    [Fact]
    public void Repair_before_setup_is_a_plain_error()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());

        var error = Assert.Throws<LauncherException>(() =>
            HeadlessCommands.Run(LauncherOptions.Parse(["--repair", iso, "--manifest", manifest], _root), new StringWriter(), () => Now));

        Assert.Contains("--extract", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "gamedata")));
    }

    [Fact]
    public void Verify_checks_the_title_update_too()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        var text = Run(["--verify", iso, "--manifest", manifest, .. Tu()]);
        Assert.Contains("Title Update 2", text);
        Assert.False(Directory.Exists(Path.Combine(_root, "updatedata")));
    }

    [Fact]
    public void Extract_unpacks_title_update_2_next_to_the_game_data()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        var text = Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        Assert.Contains("Title Update 2 ready", text);
        Assert.Equal(Tu2Files[0].Data, File.ReadAllBytes(Path.Combine(_root, "updatedata", "default.xexp")));
    }

    [Fact]
    public void Extract_without_title_update_2_sets_up_the_disc_then_explains_what_is_missing()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        var error = Assert.Throws<LauncherException>(() => HeadlessCommands.Run(
            LauncherOptions.Parse(["--extract", iso, "--manifest", manifest, .. Tu(withPackage: false)], _root),
            new StringWriter(), () => Now));
        Assert.Contains("Title Update 2", error.Message);
        Assert.Contains("--title-update", error.Message);
        Assert.True(File.Exists(Path.Combine(_root, "gamedata", SetupMarker.FileName)));
    }

    [Fact]
    public void Title_update_alone_upgrades_a_0_9_0_install_without_touching_the_disc_files()
    {
        // A 0.9.0 install: game data set up, saves in userdata, no updatedata.
        var (iso, manifest) = WriteDisc(new TestDisc());
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        Directory.Delete(Path.Combine(_root, "updatedata"), recursive: true);
        var save = Path.Combine(_root, "userdata", "B13EBABEBABEBABE", "54510866", "00000001", "STORY.SAV");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        File.WriteAllBytes(save, [1, 2, 3]);
        var startup = Path.Combine(_root, "gamedata", "CookedXenon", "Startup.xxx");
        var stamp = File.GetLastWriteTimeUtc(startup);

        var text = Run(["--manifest", manifest, .. Tu()]);

        Assert.Contains("Title Update 2 ready", text);
        Assert.True(File.Exists(Path.Combine(_root, "updatedata", "default.xexp")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(startup));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(save));
        Assert.Contains("--update_data_root=", string.Join(' ', LaunchArguments.Build(LauncherOptions.Parse([], _root).Layout, "x.log")));
    }

    [Fact]
    public void Repair_re_verifies_the_update_folder_and_needs_the_title_update_only_when_a_file_is_bad()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        Assert.Contains("Nothing to repair", Run(["--repair", iso, "--manifest", manifest, .. Tu(withPackage: false)]));

        File.WriteAllBytes(Path.Combine(_root, "updatedata", "Coalesced.ini"), [9]);
        var error = Assert.Throws<LauncherException>(() => HeadlessCommands.Run(
            LauncherOptions.Parse(["--repair", iso, "--manifest", manifest, .. Tu(withPackage: false)], _root),
            new StringWriter(), () => Now));
        Assert.Contains("--title-update", error.Message);

        var text = Run(["--repair", iso, "--manifest", manifest, .. Tu()]);
        Assert.Contains("damaged: Coalesced.ini", text);
        Assert.Equal(Tu2Files[1].Data, File.ReadAllBytes(Path.Combine(_root, "updatedata", "Coalesced.ini")));
    }

    [Fact]
    public void Add_dlc_installs_a_pack_and_lists_it()
    {
        var pack = new TestStfs { DisplayName = "WWE ALL STARS PACK 1", Files = { ("Phase1/DLCTOC.txt", TestStfs.Content(50, 51)) } }
            .WriteTo(Path.Combine(_root, "dumps", DlcLibrary.Pack1FileName));

        var text = Run("--add-dlc", pack);

        Assert.Contains("WWE ALL STARS PACK 1", text);
        Assert.Contains("Million Dollar Man", text);
        Assert.Single(new DlcLibrary(Path.Combine(_root, "userdata")).Installed());
    }

    [Fact]
    public void Setup_takes_the_optional_dlc_step_when_given_and_skips_it_otherwise()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);
        Assert.Empty(new DlcLibrary(Path.Combine(_root, "userdata")).Installed());

        var pack = new TestStfs { DisplayName = "WWE ALL STARS PACK 1", Files = { ("Phase1/DLCTOC.txt", TestStfs.Content(50, 52)) } }
            .WriteTo(Path.Combine(_root, "dumps", DlcLibrary.Pack1FileName));
        var text = Run(["--extract", iso, "--manifest", manifest, .. Tu(), "--add-dlc", pack]);

        Assert.Contains("Title Update 2 ready", text);
        Assert.Contains("DLC installed: WWE ALL STARS PACK 1", text);
        Assert.Single(new DlcLibrary(Path.Combine(_root, "userdata")).Installed());
    }

    [Fact]
    public void Add_dlc_with_a_title_update_is_a_plain_error()
    {
        var error = Assert.Throws<LauncherException>(() => HeadlessCommands.Run(
            LauncherOptions.Parse(["--add-dlc", Tu()[1]], _root), new StringWriter(), () => Now));
        Assert.Contains("title update", error.Message);
    }

    [Fact]
    public void Setup_keeps_the_remembered_window_placement()
    {
        var (iso, manifest) = WriteDisc(new TestDisc());
        var userData = new PortableLayout(_root).UserDataDirectory;
        var window = new WindowPlacement(200, 100, 1100, 640, Maximized: false);
        LauncherState.RememberWindow(userData, window);

        Run(["--extract", iso, "--manifest", manifest, .. Tu()]);

        Assert.Equal(window, LauncherState.Load(userData).Window);
    }

    [Fact]
    public void The_shader_converter_ships_beside_the_launcher_unless_one_is_named()
    {
        var shipped = LauncherOptions.Parse(["--prepare-shaders"], _root);
        var named = LauncherOptions.Parse(["--prepare-shaders", "--xenosrecomp", Path.Combine(_root, "dev", "XenosRecomp.exe")], _root);

        Assert.True(shipped.Headless);
        Assert.Equal(Path.Combine(_root, "xenosrecomp", "XenosRecomp.exe"), shipped.ShaderConverterExecutable);
        Assert.Equal(Path.Combine(_root, "dev", "XenosRecomp.exe"), named.ShaderConverterExecutable);
    }

    [Fact]
    public void Extract_then_prepare_shaders_reports_a_shader_failure_plainly_and_keeps_the_game_data()
    {
        // The test disc's packages hold no shaders.
        var (iso, manifest) = WriteDisc(new TestDisc());

        var error = Assert.Throws<LauncherException>(() =>
            HeadlessCommands.Run(LauncherOptions.Parse(["--extract", iso, "--manifest", manifest, "--prepare-shaders", .. Tu()], _root),
                new StringWriter(), () => Now));

        Assert.Contains("Emulated", error.Message);
        Assert.True(File.Exists(Path.Combine(_root, "gamedata", SetupMarker.FileName)));
    }

    [Fact]
    public void Prepare_pipelines_runs_headless_and_reports_a_missing_game_plainly()
    {
        var options = LauncherOptions.Parse(["--prepare-pipelines"], _root);

        var error = Assert.Throws<LauncherException>(() => HeadlessCommands.Run(options, new StringWriter(), () => Now));

        Assert.True(options.Headless);
        Assert.Contains("game exe is missing", error.Message);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    public void Quoting_follows_the_windows_argument_rules(string argument, string quoted)
    {
        Assert.Equal(quoted, HeadlessCommands.Quote(argument));
    }
}
