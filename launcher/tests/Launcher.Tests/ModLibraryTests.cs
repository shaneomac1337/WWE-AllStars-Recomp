using System.Buffers.Binary;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class ModLibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-mods-").FullName;
    private readonly PortableLayout _layout;
    private readonly FakeLinker _linker = new();
    private readonly FakeRecycleBin _recycleBin = new();

    private static readonly KnownGameFiles Known = new(["CHR_101_A.xxx", "Front_End.xxx", "UI_SHELL.xxx", "Engine.xxx"]);

    public ModLibraryTests()
    {
        _layout = new PortableLayout(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library() => new(_layout, Known, _linker, _recycleBin);

    private string LoadOrderFile => Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName);

    private string Mod(string folder, string? manifest = null, params (string Name, byte[] Bytes)[] packages)
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ModLibrary.ManifestFileName), manifest ?? Manifest(folder));
        foreach (var (name, bytes) in packages)
        {
            Directory.CreateDirectory(Path.Combine(path, "packages"));
            File.WriteAllBytes(Path.Combine(path, "packages", name), TestPackage.ForFile(bytes, name));
        }
        return path;
    }

    private static string Manifest(string name, string game = "TU2", string version = "1.0") =>
        $"""
        name = "{name}"
        author = "Someone"
        version = "{version}"
        description = "A test mod."
        game = "{game}"
        kind = "cosmetic"
        """;

    [Fact]
    public void A_mod_folder_the_launcher_has_not_seen_is_listed_off_and_new()
    {
        Mod("Hogan Red", Manifest("Hogan Red"));

        var mods = Library().List();

        var mod = Assert.Single(mods);
        Assert.Equal("Hogan Red", mod.Id);
        Assert.Equal("Hogan Red", mod.Manifest!.Name);
        Assert.Equal("Someone", mod.Manifest.Author);
        Assert.Equal("1.0", mod.Manifest.Version);
        Assert.Equal("A test mod.", mod.Manifest.Description);
        Assert.False(mod.Enabled);
        Assert.True(mod.IsNew);
        Assert.Empty(mod.Problems);
    }

    [Fact]
    public void A_new_mod_is_written_to_the_load_order_file_off_and_new()
    {
        Mod("Hogan Red");

        Library().List();

        Assert.Contains("off | Hogan Red | new", File.ReadAllLines(LoadOrderFile));
    }

    [Fact]
    public void An_entry_whose_folder_is_gone_is_dropped()
    {
        Mod("Kept");
        Directory.CreateDirectory(_layout.ModsDirectory);
        File.WriteAllLines(LoadOrderFile, ["on | Gone", "off | Kept"]);

        var mods = Library().List();

        Assert.Equal(["Kept"], mods.Select(mod => mod.Id));
        Assert.DoesNotContain(File.ReadAllLines(LoadOrderFile), line => line.Contains("Gone"));
    }

    [Fact]
    public void Hand_edits_to_the_load_order_file_are_read_back()
    {
        Mod("Arena");
        Mod("Attire");
        Mod("Menus");
        Library().List();
        File.WriteAllLines(LoadOrderFile, ["# my order", "on | Menus", "", "off | Attire", "ON|Arena"]);

        var mods = Library().List();

        Assert.Equal(["Menus", "Attire", "Arena"], mods.Select(mod => mod.Id));
        Assert.Equal([true, false, true], mods.Select(mod => mod.Enabled));
        Assert.All(mods, mod => Assert.False(mod.IsNew));
    }

    [Fact]
    public void Turning_a_mod_on_is_saved_and_clears_its_new_mark()
    {
        Mod("Hogan Red");
        var library = Library();
        library.List();

        library.SetEnabled("Hogan Red", true);

        var mod = Assert.Single(Library().List());
        Assert.True(mod.Enabled);
        Assert.False(mod.IsNew);
        Assert.Contains("on | Hogan Red", File.ReadAllLines(LoadOrderFile));
    }

    [Fact]
    public void Dot_folders_in_mods_are_not_mods()
    {
        Directory.CreateDirectory(Path.Combine(_layout.ModsDirectory, ".staging-1234"));

        Assert.Empty(Library().List());
    }

    private static readonly byte[] PlainPackage = new TestPackage().Bytes();
    private static readonly byte[] WholeFilePackage = new TestPackage { Compressed = TestPackage.Compression.WholeFile }.Bytes();

    private string ProblemOf(string folder)
    {
        var mod = Assert.Single(Library().List(), mod => mod.Id == folder);
        Assert.False(mod.Enabled);
        return Assert.Single(mod.Problems);
    }

    [Fact]
    public void A_valid_package_mod_has_no_problem_and_lists_its_packages()
    {
        Mod("Good", null, ("CHR_101_A.xxx", PlainPackage), ("Front_End.xxx", WholeFilePackage));

        var mod = Assert.Single(Library().List());

        Assert.Empty(mod.Problems);
        Assert.Equal(["CHR_101_A.xxx", "Front_End.xxx"], mod.Packages.Select(package => package.Name));
    }

    [Fact]
    public void A_folder_without_a_manifest_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_layout.ModsDirectory, "Loose"));

        Assert.Equal("It has no mod.toml.", ProblemOf("Loose"));
    }

    [Fact]
    public void A_manifest_without_a_name_is_refused()
    {
        Mod("Nameless", "game = \"TU2\"");

        Assert.Equal("Its mod.toml has no name.", ProblemOf("Nameless"));
    }

    [Fact]
    public void A_mod_for_another_game_version_is_refused()
    {
        Mod("Old", Manifest("Old", game: "TU1"));

        Assert.Equal("It is made for TU1; this launcher runs Title Update 2 (game = \"TU2\").", ProblemOf("Old"));
    }

    [Fact]
    public void A_manifest_with_an_unknown_kind_is_refused()
    {
        Mod("Odd", "name = \"Odd\"\ngame = \"TU2\"\nkind = \"visual\"");

        Assert.Equal("Its kind is \"visual\"; it must be \"cosmetic\" or \"gameplay\".", ProblemOf("Odd"));
    }

    [Fact]
    public void A_package_with_an_unsafe_name_is_refused()
    {
        Mod("Unsafe", null, ("CHR 101;A.xxx", PlainPackage));

        Assert.Equal("packages/CHR 101;A.xxx has a file name the game can't use.", ProblemOf("Unsafe"));
    }

    [Fact]
    public void A_folder_inside_packages_is_refused()
    {
        var mod = Mod("Nested");
        Directory.CreateDirectory(Path.Combine(mod, "packages", "CookedXenon"));

        Assert.Equal("packages/CookedXenon is a folder; packages go straight in packages/ by their short name.",
            ProblemOf("Nested"));
    }

    [Fact]
    public void A_file_that_is_not_a_package_is_refused()
    {
        Mod("Text", null, ("CHR_101_A.xxx", "hello world"u8.ToArray()));

        Assert.Equal("packages/CHR_101_A.xxx is not a game package.", ProblemOf("Text"));
    }

    [Fact]
    public void A_non_package_extension_in_packages_is_refused()
    {
        Mod("Readme", null, ("readme.txt", PlainPackage));

        Assert.Equal("packages/readme.txt is not a package (.xxx).", ProblemOf("Readme"));
    }

    [Fact]
    public void A_package_name_the_game_does_not_have_is_refused()
    {
        Mod("Extra", null, ("CHR_999_Z.xxx", PlainPackage));

        Assert.Equal("packages/CHR_999_Z.xxx replaces no package the game has; a mod can only replace packages.",
            ProblemOf("Extra"));
    }

    [Fact]
    public void A_broken_mod_turned_on_by_hand_stays_off_and_cannot_be_turned_on()
    {
        Mod("Extra", null, ("CHR_999_Z.xxx", PlainPackage));
        Directory.CreateDirectory(_layout.ModsDirectory);
        File.WriteAllLines(LoadOrderFile, ["on | Extra"]);
        var library = Library();

        Assert.False(Assert.Single(library.List()).Enabled);
        Assert.Throws<LauncherException>(() => library.SetEnabled("Extra", true));
    }

    private static readonly string[] Tu2TocLines =
    [
        "184629 0 0 .\\Coalesced.ini 0 ",
        "1409024 6464092 0 .\\Engine.xxx 0 ",
        "458752 0 0 .\\UI_SHELL.xxx 0 ",
    ];

    /// <summary>A stand-in for Title Update 2's folder: its TOC format, CRLF and trailing spaces.</summary>
    private string Tu2()
    {
        var folder = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "PatchTOC.txt"), string.Join("\r\n", Tu2TocLines) + "\r\n");
        File.WriteAllText(Path.Combine(folder, "Coalesced.ini"), "tu2 config");
        File.WriteAllText(Path.Combine(folder, "Engine.xxx"), "tu2 engine");
        File.WriteAllText(Path.Combine(folder, "UI_SHELL.xxx"), "tu2 shell");
        File.WriteAllText(Path.Combine(folder, "default.xexp"), "tu2 patch");
        return folder;
    }

    private ModLibrary LibraryWithOn(params string[] ids)
    {
        var library = Library();
        library.List();
        foreach (var id in ids) library.SetEnabled(id, true);
        return library;
    }

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(folder, path),
            path => Convert.ToHexString(File.ReadAllBytes(path)) + "@" + File.GetLastWriteTimeUtc(path).Ticks);

    private static string[] TocOf(string folder) => File.ReadAllText(Path.Combine(folder, "PatchTOC.txt"))
        .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void With_no_mod_in_use_Play_gets_the_TU2_folder_itself()
    {
        var tu2 = Tu2();
        Mod("Off", null, ("CHR_101_A.xxx", PlainPackage));

        var prepared = Library().PrepareForPlay(tu2, online: false);

        Assert.Equal(tu2, prepared.UpdateDataRoot);
        Assert.Empty(prepared.Report.Used);
    }

    [Fact]
    public void An_update_folder_that_cant_be_deleted_for_a_rebuild_stops_Play_and_names_it()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));
        var library = LibraryWithOn("Hogan");
        var built = library.PrepareForPlay(tu2, online: false).UpdateDataRoot;
        Mod("Shell", null, ("UI_SHELL.xxx", PlainPackage));
        library.SetEnabled("Shell", true);

        LauncherException error;
        using (File.Open(Path.Combine(built, "Engine.xxx"), FileMode.Open, FileAccess.Read, FileShare.None))
            error = Assert.Throws<LauncherException>(() => library.PrepareForPlay(tu2, online: false));

        Assert.Contains(built, error.Message);
        Assert.Contains("could not be deleted", error.Message);
        Assert.Contains("choose Play again", error.Message);
    }

    [Fact]
    public void A_mod_in_use_gets_an_update_folder_under_the_cache_with_TU2s_files_and_its_package()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));

        var prepared = LibraryWithOn("Hogan").PrepareForPlay(tu2, online: false);

        Assert.StartsWith(_layout.CacheDirectory + Path.DirectorySeparatorChar, prepared.UpdateDataRoot);
        Assert.Equal(
            ["CHR_101_A.xxx", "Coalesced.ini", "default.xexp", "Engine.xxx", "PatchTOC.txt", "UI_SHELL.xxx"],
            Directory.EnumerateFiles(prepared.UpdateDataRoot).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(TestPackage.ForFile(PlainPackage, "CHR_101_A.xxx"), File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, "CHR_101_A.xxx")));
        Assert.Equal("tu2 engine", File.ReadAllText(Path.Combine(prepared.UpdateDataRoot, "Engine.xxx")));
    }

    [Fact]
    public void A_plain_mod_package_gets_a_PatchTOC_line_with_its_size_and_no_uncompressed_size()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));

        var prepared = LibraryWithOn("Hogan").PrepareForPlay(tu2, online: false);

        Assert.Equal([.. Tu2TocLines, $"{TestPackage.ForFile(PlainPackage, "CHR_101_A.xxx").Length} 0 0 .\\CHR_101_A.xxx 0 "], TocOf(prepared.UpdateDataRoot));
    }

    [Fact]
    public void A_whole_file_compressed_package_gets_the_size_it_expands_to()
    {
        var tu2 = Tu2();
        Mod("Menus", null, ("Front_End.xxx", WholeFilePackage));
        // TestPackage writes one chunk; its header's uncompressed size is at offset 12, little-endian.
        var menus = TestPackage.ForFile(WholeFilePackage, "Front_End.xxx");
        var expanded = BinaryPrimitives.ReadInt32LittleEndian(menus.AsSpan(12));

        var prepared = LibraryWithOn("Menus").PrepareForPlay(tu2, online: false);

        Assert.Equal($"{menus.Length} {expanded} 0 .\\Front_End.xxx 0 ", TocOf(prepared.UpdateDataRoot)[^1]);
        Assert.Equal(new TestPackage().Bytes("Front_End.xxx").Length, expanded);
    }

    [Fact]
    public void A_mod_package_with_a_TU2_name_replaces_TU2s_line_and_file()
    {
        var tu2 = Tu2();
        Mod("Shell", null, ("ui_shell.xxx", PlainPackage));

        var prepared = LibraryWithOn("Shell").PrepareForPlay(tu2, online: false);

        Assert.Equal([Tu2TocLines[0], Tu2TocLines[1], $"{TestPackage.ForFile(PlainPackage, "ui_shell.xxx").Length} 0 0 .\\ui_shell.xxx 0 "],
            TocOf(prepared.UpdateDataRoot));
        var shell = Assert.Single(Directory.EnumerateFiles(prepared.UpdateDataRoot, "ui_shell.xxx"));
        Assert.Equal(TestPackage.ForFile(PlainPackage, "ui_shell.xxx"), File.ReadAllBytes(shell));
    }

    [Fact]
    public void When_two_mods_replace_the_same_package_the_later_one_in_the_load_order_wins()
    {
        var tu2 = Tu2();
        Mod("First", null, ("CHR_101_A.xxx", PlainPackage));
        Mod("Second", null, ("CHR_101_A.xxx", WholeFilePackage));
        Library().List();
        File.WriteAllLines(LoadOrderFile, ["on | First", "on | Second"]);

        var prepared = Library().PrepareForPlay(tu2, online: false);

        Assert.Equal(TestPackage.ForFile(WholeFilePackage, "CHR_101_A.xxx"), File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, "CHR_101_A.xxx")));
        Assert.Single(TocOf(prepared.UpdateDataRoot), line => line.EndsWith(@".\CHR_101_A.xxx 0 ", StringComparison.Ordinal));
        Assert.Equal(["First", "Second"], prepared.Report.Used.Select(mod => mod.Id));
        Assert.Empty(prepared.Report.Used[0].Files);
        Assert.Equal(["CHR_101_A.xxx"], prepared.Report.Used[1].Files);
    }

    [Fact]
    public void The_TU2_folder_is_never_written()
    {
        var tu2 = Tu2();
        var before = Snapshot(tu2);
        Mod("Shell", null, ("UI_SHELL.xxx", PlainPackage), ("CHR_101_A.xxx", WholeFilePackage));
        var library = LibraryWithOn("Shell");

        library.PrepareForPlay(tu2, online: false);
        library.SetEnabled("Shell", false);
        library.PrepareForPlay(tu2, online: false);

        Assert.Equal(before, Snapshot(tu2));
    }

    [Fact]
    public void Unchanged_files_are_hard_linked_from_TU2_and_the_mods()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));

        var prepared = LibraryWithOn("Hogan").PrepareForPlay(tu2, online: false);

        Assert.Equal(
            ["CHR_101_A.xxx", "Coalesced.ini", "default.xexp", "Engine.xxx", "UI_SHELL.xxx"],
            _linker.Links.Select(link => Path.GetFileName(link.Link)).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(_linker.Links, link => Assert.StartsWith(prepared.UpdateDataRoot, link.Link));
    }

    [Fact]
    public void Where_hard_links_fail_the_files_are_copied()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));
        _linker.Works = false;

        var prepared = LibraryWithOn("Hogan").PrepareForPlay(tu2, online: false);

        Assert.Equal(TestPackage.ForFile(PlainPackage, "CHR_101_A.xxx"), File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, "CHR_101_A.xxx")));
        Assert.Equal("tu2 patch", File.ReadAllText(Path.Combine(prepared.UpdateDataRoot, "default.xexp")));
        Assert.Empty(_linker.Links);
    }

    [Fact]
    public void Play_with_nothing_changed_reuses_the_update_folder()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));
        var library = LibraryWithOn("Hogan");
        var first = library.PrepareForPlay(tu2, online: false);
        var toc = File.GetLastWriteTimeUtc(Path.Combine(first.UpdateDataRoot, "PatchTOC.txt"));
        var links = _linker.Links.Count;

        var second = library.PrepareForPlay(tu2, online: false);

        Assert.Equal(first.UpdateDataRoot, second.UpdateDataRoot);
        Assert.Equal(links, _linker.Links.Count);
        Assert.Equal(toc, File.GetLastWriteTimeUtc(Path.Combine(second.UpdateDataRoot, "PatchTOC.txt")));
        Assert.Equal(first.Report.Lines(), second.Report.Lines());
    }

    [Fact]
    public void A_changed_mod_file_rebuilds_the_update_folder()
    {
        var tu2 = Tu2();
        var mod = Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));
        var library = LibraryWithOn("Hogan");
        library.PrepareForPlay(tu2, online: false);
        File.WriteAllBytes(Path.Combine(mod, "packages", "CHR_101_A.xxx"), TestPackage.ForFile(WholeFilePackage, "CHR_101_A.xxx"));

        var prepared = library.PrepareForPlay(tu2, online: false);

        Assert.Equal(TestPackage.ForFile(WholeFilePackage, "CHR_101_A.xxx"), File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, "CHR_101_A.xxx")));
        Assert.StartsWith($"{TestPackage.ForFile(WholeFilePackage, "CHR_101_A.xxx").Length} ", TocOf(prepared.UpdateDataRoot)[^1]);
    }

    [Fact]
    public void A_mod_turned_off_leaves_none_of_its_files_in_the_update_folder()
    {
        var tu2 = Tu2();
        Mod("Hogan", null, ("CHR_101_A.xxx", PlainPackage));
        Mod("Menus", null, ("Front_End.xxx", PlainPackage));
        var library = LibraryWithOn("Hogan", "Menus");
        library.PrepareForPlay(tu2, online: false);
        library.SetEnabled("Hogan", false);

        var prepared = library.PrepareForPlay(tu2, online: false);

        Assert.False(File.Exists(Path.Combine(prepared.UpdateDataRoot, "CHR_101_A.xxx")));
        Assert.DoesNotContain(TocOf(prepared.UpdateDataRoot), line => line.Contains("CHR_101_A"));
        Assert.True(File.Exists(Path.Combine(prepared.UpdateDataRoot, "Front_End.xxx")));
    }

    [Fact]
    public void The_load_report_names_the_mods_used_in_order_and_the_files_they_supply()
    {
        var tu2 = Tu2();
        Mod("Hogan", Manifest("Hogan Red", version: "1.2"), ("CHR_101_A.xxx", PlainPackage));

        var report = LibraryWithOn("Hogan").PrepareForPlay(tu2, online: false).Report;

        var used = Assert.Single(report.Used);
        Assert.Equal(new UsedMod("Hogan", "Hogan Red", "1.2", ["CHR_101_A.xxx"]), used, UsedModComparer);
        Assert.Equal(["mods: 1 in use", "mod 1: Hogan Red 1.2 (Hogan): CHR_101_A.xxx"], report.Lines());
    }

    [Fact]
    public void The_known_packages_are_the_discs_and_TU2s_by_short_name()
    {
        var known = KnownGameFiles.From(DiscManifest.Supported, TitleUpdateManifest.Supported);

        Assert.True(known.IsPackage("CHR_101_A.xxx"));
        Assert.True(known.IsPackage("front_end.xxx"));
        Assert.True(known.IsPackage("UI_SHELL.xxx"));
        Assert.False(known.IsPackage("CHR_999_Z.xxx"));
        Assert.False(known.IsPackage("Coalesced.ini"));
        Assert.False(known.IsPackage("Xbox360TOC.txt"));
    }

    [Fact]
    public void Remove_moves_the_mod_to_the_recycle_bin_and_drops_its_entry()
    {
        var folder = Mod("Hogan");
        Mod("Menus");
        var library = LibraryWithOn("Hogan");

        library.Remove("Hogan");

        Assert.Equal([folder], _recycleBin.Recycled);
        Assert.Equal(["Menus"], library.List().Select(mod => mod.Id));
        Assert.DoesNotContain(File.ReadAllLines(LoadOrderFile), line => line.Contains("Hogan"));
    }

    private static readonly EqualityComparer<UsedMod> UsedModComparer = EqualityComparer<UsedMod>.Create(
        (a, b) => a!.Id == b!.Id && a.Name == b.Name && a.Version == b.Version && a.Files.SequenceEqual(b.Files),
        mod => mod.Id.GetHashCode(StringComparison.Ordinal));
}
