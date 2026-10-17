using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>Cosmetic or gameplay, and the online rule (spec, "Cosmetic or gameplay", "Online rule").</summary>
public sealed class ModOnlineRuleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-mods-online-").FullName;
    private readonly PortableLayout _layout;

    // One package per gameplay-list family, plus look-alikes that are not on the list.
    private static readonly string[] GameplayPackages =
    [
        "Core.xxx", "Engine.xxx", "Game.xxx", "Game_Match.xxx", "GameCharacterData_101.xxx", "GameOnline.xxx",
        "AI_Wrestler.xxx", "Scripts_Match.xxx", "Aimed_Moves.xxx", "OnlineSubsystemLive.xxx",
        "CombinedGlobalCharacters.xxx", "CombinedStartup_int.xxx", "CHR_Physics.xxx", "Triggers_Grabs.xxx",
        "Trigs_Vols_Main.xxx", "AnimFSM_Andre.xxx", "AnimSet_Andre.xxx", "Env_Collision.xxx",
        "Arena_001.xxx", "Arena_002_M.xxx", "Arena_003_Ring.xxx", "Arena_004_Level.xxx", "Arena_Apron.xxx",
        "Arena_Global.xxx",
    ];

    private static readonly string[] CosmeticPackages =
    [
        "CHR_101_A.xxx", "Front_End.xxx", "GameFrontEnd.xxx", "CoreArena.xxx", "Arena_001_Audio.xxx",
        "Arena_002_LvL_Light.xxx", "Arena_003_Cin_Ring.xxx", "Arena_004_Entrance.xxx", "Arena_Crowd.xxx",
        "Arena_LensFlares.xxx", "Arena_00A.xxx", "Arena_0011.xxx",
    ];

    private static readonly KnownGameFiles Known = new([.. GameplayPackages, .. CosmeticPackages, "UI_SHELL.xxx"]);

    public ModOnlineRuleTests()
    {
        _layout = new PortableLayout(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library() => new(_layout, Known, new FakeLinker(), new FakeRecycleBin());

    private static readonly byte[] PlainPackage = new TestPackage().Bytes();

    private static string Manifest(string name, string? kind) =>
        $"name = \"{name}\"\nversion = \"1.0\"\ngame = \"TU2\"\n" + (kind is null ? "" : $"kind = \"{kind}\"\n");

    private string Mod(string folder, string? kind, params string[] packages)
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        Directory.CreateDirectory(Path.Combine(path, "packages"));
        File.WriteAllText(Path.Combine(path, ModLibrary.ManifestFileName), Manifest(folder, kind));
        foreach (var name in packages) File.WriteAllBytes(Path.Combine(path, "packages", name), TestPackage.ForFile(PlainPackage, name));
        return path;
    }

    private ModInfo Listed(string id) => Assert.Single(Library().List(), mod => mod.Id == id);

    public static TheoryData<string> GameplayFamilies => [.. GameplayPackages];

    [Theory]
    [MemberData(nameof(GameplayFamilies))]
    public void A_cosmetic_mod_replacing_a_package_on_the_gameplay_list_is_a_gameplay_mod(string package)
    {
        Mod("Looks", "cosmetic", "CHR_101_A.xxx", package);

        var mod = Listed("Looks");

        Assert.Empty(mod.Problems);
        Assert.Equal(ModKind.Gameplay, mod.Kind);
        Assert.Equal($"Declared cosmetic, but it replaces {package}, a script or gameplay-data package.", mod.ForcedGameplayReason);
    }

    public static TheoryData<string> CosmeticFamilies => [.. CosmeticPackages];

    [Theory]
    [MemberData(nameof(CosmeticFamilies))]
    public void A_cosmetic_mod_replacing_only_packages_off_the_list_is_cosmetic(string package)
    {
        Mod("Looks", "cosmetic", package);

        var mod = Listed("Looks");

        Assert.Equal(ModKind.Cosmetic, mod.Kind);
        Assert.Null(mod.ForcedGameplayReason);
    }

    [Fact]
    public void A_config_edit_makes_a_cosmetic_mod_a_gameplay_mod()
    {
        var folder = Mod("Text", "cosmetic", "Front_End.xxx");
        Directory.CreateDirectory(Path.Combine(folder, "config"));
        File.WriteAllText(Path.Combine(folder, "config", "WWEGame.ini"), "[Engine.GameInfo]\nMaxPlayers=8\n");

        var mod = Listed("Text");

        Assert.Equal(ModKind.Gameplay, mod.Kind);
        Assert.Equal("Declared cosmetic, but it changes config.", mod.ForcedGameplayReason);
    }

    [Fact]
    public void An_empty_config_folder_is_no_config_edit()
    {
        var folder = Mod("Looks", "cosmetic", "Front_End.xxx");
        Directory.CreateDirectory(Path.Combine(folder, "config"));

        Assert.Equal(ModKind.Cosmetic, Listed("Looks").Kind);
    }

    [Fact]
    public void A_config_edit_and_gameplay_packages_are_both_named()
    {
        var folder = Mod("Both", "cosmetic", "Engine.xxx", "Scripts_Match.xxx");
        Directory.CreateDirectory(Path.Combine(folder, "config", "INT"));
        File.WriteAllText(Path.Combine(folder, "config", "INT", "WWEGame.int"), "x");

        Assert.Equal("Declared cosmetic, but it changes config and it replaces Engine.xxx, Scripts_Match.xxx, " +
            "script or gameplay-data packages.", Listed("Both").ForcedGameplayReason);
    }

    [Fact]
    public void A_mod_that_does_not_state_its_kind_is_a_gameplay_mod()
    {
        Mod("Unlabelled", kind: null, "CHR_101_A.xxx");

        var mod = Listed("Unlabelled");

        Assert.Empty(mod.Problems);
        Assert.Equal(ModKind.Gameplay, mod.Kind);
        Assert.Null(mod.ForcedGameplayReason);
    }

    [Fact]
    public void A_mod_declared_gameplay_is_a_gameplay_mod_with_no_forced_reason()
    {
        Mod("Moves", "gameplay", "CHR_101_A.xxx");

        var mod = Listed("Moves");

        Assert.Equal(ModKind.Gameplay, mod.Kind);
        Assert.Null(mod.ForcedGameplayReason);
    }

    private string Tu2()
    {
        var folder = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "PatchTOC.txt"), "1409024 6464092 0 .\\Engine.xxx 0 \r\n");
        File.WriteAllText(Path.Combine(folder, "Engine.xxx"), "tu2 engine");
        File.WriteAllText(Path.Combine(folder, "default.xexp"), "tu2 patch");
        return folder;
    }

    /// <summary>A cosmetic mod, a mod forced to gameplay, and an unlabelled one, all on.</summary>
    private ModLibrary ThreeModsOn()
    {
        Mod("Attire", "cosmetic", "CHR_101_A.xxx");
        Mod("Faster", "cosmetic", "Engine.xxx");
        Mod("Menus", kind: null, "Front_End.xxx");
        var library = Library();
        library.List();
        foreach (var id in new[] { "Attire", "Faster", "Menus" }) library.SetEnabled(id, true);
        return library;
    }

    private static string[] FilesIn(string folder) =>
        [.. Directory.EnumerateFiles(folder).Select(path => Path.GetFileName(path)!).Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void With_online_play_on_gameplay_mods_are_left_out_and_cosmetic_ones_kept()
    {
        var tu2 = Tu2();

        var prepared = ThreeModsOn().PrepareForPlay(tu2, online: true);

        Assert.Equal(["Attire"], prepared.Report.Used.Select(mod => mod.Id));
        Assert.Equal(["Faster", "Menus"], prepared.Report.LeftOut.Select(mod => mod.Id));
        Assert.Equal(["CHR_101_A.xxx", "default.xexp", "Engine.xxx", "PatchTOC.txt"], FilesIn(prepared.UpdateDataRoot));
        Assert.Equal("tu2 engine", File.ReadAllText(Path.Combine(prepared.UpdateDataRoot, "Engine.xxx")));
        var toc = File.ReadAllText(Path.Combine(prepared.UpdateDataRoot, "PatchTOC.txt"));
        Assert.Contains(@".\Engine.xxx", toc);
        Assert.DoesNotContain("Front_End", toc);
        Assert.StartsWith("1409024 6464092 0 ", toc);
    }

    [Fact]
    public void With_online_play_off_every_enabled_mod_is_used()
    {
        var tu2 = Tu2();

        var prepared = ThreeModsOn().PrepareForPlay(tu2, online: false);

        Assert.Equal(["Attire", "Faster", "Menus"], prepared.Report.Used.Select(mod => mod.Id));
        Assert.Empty(prepared.Report.LeftOut);
        Assert.Equal(["CHR_101_A.xxx", "default.xexp", "Engine.xxx", "Front_End.xxx", "PatchTOC.txt"], FilesIn(prepared.UpdateDataRoot));
        Assert.Equal(TestPackage.ForFile(PlainPackage, "Engine.xxx"), File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, "Engine.xxx")));
    }

    [Fact]
    public void Turning_online_play_on_rebuilds_the_update_folder_without_the_gameplay_mods()
    {
        var tu2 = Tu2();
        var library = ThreeModsOn();
        library.PrepareForPlay(tu2, online: false);

        var prepared = library.PrepareForPlay(tu2, online: true);

        Assert.Equal(["CHR_101_A.xxx", "default.xexp", "Engine.xxx", "PatchTOC.txt"], FilesIn(prepared.UpdateDataRoot));
        Assert.Equal("tu2 engine", File.ReadAllText(Path.Combine(prepared.UpdateDataRoot, "Engine.xxx")));
    }

    [Fact]
    public void With_only_gameplay_mods_on_online_play_gets_the_TU2_folder_itself()
    {
        var tu2 = Tu2();
        Mod("Menus", kind: null, "Front_End.xxx");
        var library = Library();
        library.List();
        library.SetEnabled("Menus", true);

        var prepared = library.PrepareForPlay(tu2, online: true);

        Assert.Equal(tu2, prepared.UpdateDataRoot);
        Assert.Empty(prepared.Report.Used);
        Assert.Equal(["Menus"], prepared.Report.LeftOut.Select(mod => mod.Id));
    }

    [Fact]
    public void The_load_report_lists_the_left_out_mods_and_why()
    {
        var report = ThreeModsOn().PrepareForPlay(Tu2(), online: true).Report;

        Assert.Equal(
        [
            "mods: 1 in use",
            "mod 1: Attire 1.0 (Attire): CHR_101_A.xxx",
            "mods: 2 off for online play",
            "off for online play: Faster 1.0 (Faster): Declared cosmetic, but it replaces Engine.xxx, a script or gameplay-data package.",
            "off for online play: Menus 1.0 (Menus): a gameplay mod.",
        ], report.Lines());
    }

    [Fact]
    public void The_page_marks_enabled_gameplay_mods_off_for_online_play()
    {
        ThreeModsOn();
        Mod("Spare", kind: null, "Front_End.xxx");

        var mods = Library().List();

        Assert.Equal(["Faster", "Menus"], mods.Where(mod => mod.IsOffForOnline(online: true)).Select(mod => mod.Id));
        Assert.DoesNotContain(mods, mod => mod.IsOffForOnline(online: false));
    }

    [Fact]
    public void With_online_play_off_a_gameplay_mods_kind_line_says_it_is_left_out_online()
    {
        ThreeModsOn();

        Assert.Equal("Gameplay: left out of online play.", ModOnlineRule.KindLine(Listed("Menus"), online: false));
    }

    [Fact]
    public void With_online_play_on_the_kind_line_leaves_the_why_to_the_gold_line()
    {
        ThreeModsOn();

        Assert.Equal("Gameplay.", ModOnlineRule.KindLine(Listed("Menus"), online: true));
        Assert.StartsWith("Gameplay. Declared cosmetic, but", ModOnlineRule.KindLine(Listed("Faster"), online: true));
        Assert.Equal("Cosmetic: also loads in online play.", ModOnlineRule.KindLine(Listed("Attire"), online: true));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 gameplay mod off for online play: only cosmetic mods load online.")]
    [InlineData(3, "3 gameplay mods off for online play: only cosmetic mods load online.")]
    public void The_Play_area_says_how_many_mods_are_off_for_online_play(int count, string line)
    {
        Assert.Equal(line, ModOnlineRule.OffForOnlineLine(count));
    }
}
