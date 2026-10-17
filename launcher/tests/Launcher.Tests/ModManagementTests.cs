using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>Ticket 06: what the Mods page does with the library (order, conflicts, the "new" mark).</summary>
public sealed class ModManagementTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-mods-page-").FullName;
    private readonly PortableLayout _layout;

    private static readonly KnownGameFiles Known = new(["CHR_101_A.xxx", "Front_End.xxx", "UI_SHELL.xxx"]);
    private static readonly byte[] Package = new TestPackage().Bytes();

    public ModManagementTests()
    {
        _layout = new PortableLayout(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library() => new(_layout, Known, new FakeLinker(), new FakeRecycleBin());

    private string LoadOrderFile => Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName);

    private void Mod(string folder, params string[] packages)
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        Directory.CreateDirectory(Path.Combine(path, "packages"));
        File.WriteAllText(Path.Combine(path, ModLibrary.ManifestFileName),
            $"name = \"{folder} mod\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"cosmetic\"\n");
        foreach (var package in packages) File.WriteAllBytes(Path.Combine(path, "packages", package), TestPackage.ForFile(Package, package));
    }

    private static string[] Order(ModLibrary library) => [.. library.List().Select(mod => mod.Id)];

    [Fact]
    public void Moving_a_mod_down_swaps_it_with_the_next_and_is_saved()
    {
        Mod("A");
        Mod("B");
        Mod("C");
        var library = Library();
        library.List();

        library.MoveDown("A");

        Assert.Equal(["B", "A", "C"], Order(Library()));
        Assert.Equal(["off | B | new", "off | A | new", "off | C | new"],
            File.ReadAllLines(LoadOrderFile).Where(line => !line.StartsWith('#')));
    }

    [Fact]
    public void Moving_a_mod_up_swaps_it_with_the_one_before()
    {
        Mod("A");
        Mod("B");
        Mod("C");
        var library = Library();
        library.List();

        library.MoveUp("C");

        Assert.Equal(["A", "C", "B"], Order(Library()));
    }

    [Fact]
    public void Moving_the_first_mod_up_or_the_last_down_changes_nothing()
    {
        Mod("A");
        Mod("B");
        var library = Library();
        library.List();

        library.MoveUp("A");
        library.MoveDown("B");

        Assert.Equal(["A", "B"], Order(Library()));
    }

    [Fact]
    public void Moving_a_mod_that_is_not_there_is_refused()
    {
        Mod("A");

        Assert.Throws<LauncherException>(() => Library().MoveUp("Gone"));
    }

    private ModLibrary WithOn(params string[] ids)
    {
        var library = Library();
        library.List();
        foreach (var id in ids) library.SetEnabled(id, true);
        return library;
    }

    [Fact]
    public void A_file_two_mods_in_use_replace_is_a_conflict_the_later_mod_wins()
    {
        Mod("A", "CHR_101_A.xxx", "UI_SHELL.xxx");
        Mod("B", "CHR_101_A.xxx");
        Mod("C", "Front_End.xxx", "CHR_101_A.xxx");

        var conflict = Assert.Single(WithOn("A", "B", "C").Conflicts());

        Assert.Equal("CHR_101_A.xxx", conflict.File);
        Assert.Equal(["A", "B", "C"], conflict.Mods);
        Assert.Equal("C", conflict.Winner);
    }

    [Fact]
    public void Moving_the_winner_up_hands_the_conflict_to_the_other_mod()
    {
        Mod("A", "CHR_101_A.xxx");
        Mod("B", "CHR_101_A.xxx");
        var library = WithOn("A", "B");

        library.MoveUp("B");

        var conflict = Assert.Single(library.Conflicts());
        Assert.Equal(["B", "A"], conflict.Mods);
        Assert.Equal("A", conflict.Winner);
    }

    [Fact]
    public void A_mod_that_is_off_is_in_no_conflict()
    {
        Mod("A", "CHR_101_A.xxx");
        Mod("B", "CHR_101_A.xxx");

        Assert.Empty(WithOn("A").Conflicts());
    }

    [Fact]
    public void Conflicts_are_listed_by_file_name()
    {
        Mod("A", "UI_SHELL.xxx", "CHR_101_A.xxx");
        Mod("B", "ui_shell.xxx", "CHR_101_A.xxx");

        Assert.Equal(["CHR_101_A.xxx", "UI_SHELL.xxx"], WithOn("A", "B").Conflicts().Select(conflict => conflict.File));
    }

    [Fact]
    public void Leaving_the_page_clears_every_new_mark_and_keeps_order_and_toggles()
    {
        Mod("A");
        Mod("B");
        var library = Library();
        library.List();
        library.SetEnabled("B", true);
        library.MoveUp("B");
        Mod("C");

        library.MarkAllSeen();

        var mods = Library().List();
        Assert.Equal(["B", "A", "C"], mods.Select(mod => mod.Id));
        Assert.Equal([true, false, false], mods.Select(mod => mod.Enabled));
        Assert.All(mods, mod => Assert.False(mod.IsNew));
    }

    [Fact]
    public void Moving_a_mod_keeps_its_new_mark()
    {
        Mod("A");
        Mod("B");
        var library = Library();
        library.List();

        library.MoveDown("A");

        Assert.All(Library().List(), mod => Assert.True(mod.IsNew));
    }

    private string CrashManifest(LauncherCrashFacts facts)
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var path = LauncherCrashReport.Write(_layout, LauncherLog.Off, new InvalidOperationException("boom"), facts, now);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path!);
        using var reader = new StreamReader(zip.GetEntry("manifest.txt")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void A_launcher_crash_report_lists_the_enabled_mods_in_load_order()
    {
        Mod("A", "CHR_101_A.xxx");
        Mod("B");
        Mod("C", "UI_SHELL.xxx");
        var library = WithOn("C", "A");

        var manifest = CrashManifest(new LauncherCrashFacts("1.4.0", "Windows", null, library.EnabledMods()));

        Assert.Contains("mods: A mod 1.0 (A), C mod 1.0 (C)\n", manifest.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_launcher_crash_report_with_no_mod_on_says_none()
    {
        Mod("A");

        var manifest = CrashManifest(new LauncherCrashFacts("1.4.0", "Windows", null, Library().EnabledMods()));

        Assert.Contains("mods: none\n", manifest.ReplaceLineEndings("\n"));
    }
}
