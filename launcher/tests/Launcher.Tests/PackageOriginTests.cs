using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// A package copied under another package's name hangs the game (ticket 20): the mod library
/// finds the name a package was made under and keeps a renamed copy off with the reason.
/// </summary>
public sealed class PackageOriginTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-package-origin-").FullName;
    private readonly PortableLayout _layout;
    private readonly CancellationToken _cancel = TestContext.Current.CancellationToken;

    private static readonly KnownGameFiles Known = new(["CHR_101_A.xxx", "CHR_101_B.xxx", "UI_SHELL.xxx"]);

    public PackageOriginTests()
    {
        _layout = new PortableLayout(Path.Combine(_root, "launcher"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library() => new(_layout, Known, new FakeLinker(), new FakeRecycleBin());

    private static RenamedPackage? Check(byte[] package, string fileName)
    {
        using var stream = new MemoryStream(package);
        return PackageOrigin.Check(stream, fileName, Known.IsPackage);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Chunks")]
    [InlineData("WholeFile")]
    public void A_package_under_its_own_name_passes(string compression)
    {
        var package = new TestPackage { Compressed = Enum.Parse<TestPackage.Compression>(compression), MadeAs = "CHR_101_B" }.Bytes();

        Assert.Null(Check(package, "CHR_101_B.xxx"));
        Assert.Null(Check(package, "chr_101_b.xxx"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Chunks")]
    [InlineData("WholeFile")]
    public void A_package_under_another_packages_name_says_what_it_was_made_as(string compression)
    {
        var package = new TestPackage { Compressed = Enum.Parse<TestPackage.Compression>(compression), MadeAs = "CHR_101_B" }.Bytes();

        Assert.Equal(new RenamedPackage("CHR_101_B.xxx"), Check(package, "CHR_101_A.xxx"));
    }

    [Fact]
    public void A_package_that_names_neither_its_file_nor_a_game_package_is_renamed_from_an_unknown_name()
    {
        var package = new TestPackage { MadeAs = "My_Own_Wrestler" }.Bytes();

        Assert.Equal(new RenamedPackage(null), Check(package, "CHR_101_A.xxx"));
    }

    [Fact]
    public void A_file_that_is_no_package_is_left_to_the_other_checks()
    {
        Assert.Null(Check("hello world"u8.ToArray(), "CHR_101_A.xxx"));
    }

    [Fact]
    public void A_package_cut_short_in_its_tables_is_damaged()
    {
        var package = new TestPackage { MadeAs = "CHR_101_B" }.Bytes();

        Assert.Throws<InvalidDataException>(() => Check(package[..120], "CHR_101_B.xxx"));
    }

    private void InstalledMod(string folder, string area, string fileName, byte[] package)
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        Directory.CreateDirectory(Path.Combine(path, area));
        File.WriteAllText(Path.Combine(path, ModLibrary.ManifestFileName),
            $"name = \"{folder}\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"cosmetic\"\n");
        File.WriteAllBytes(Path.Combine(path, area, fileName), package);
        Directory.CreateDirectory(_layout.ModsDirectory);
        File.WriteAllText(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName), $"on | {folder}\r\n");
    }

    [Fact]
    public void A_renamed_replacement_package_keeps_its_mod_off_with_the_reason()
    {
        InstalledMod("Attire Swap", "packages", "CHR_101_A.xxx",
            new TestPackage { Compressed = TestPackage.Compression.WholeFile, MadeAs = "CHR_101_B" }.Bytes());

        var mod = Assert.Single(Library().List());

        Assert.False(mod.Enabled);
        Assert.Equal("packages/CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it.",
            Assert.Single(mod.Problems));
        Assert.Throws<LauncherException>(() => Library().SetEnabled("Attire Swap", true));
    }

    [Fact]
    public void A_renamed_content_package_keeps_its_mod_off_with_the_reason()
    {
        InstalledMod("New Wrestler", "content", "CHR_301_A.xxx", new TestPackage { MadeAs = "CHR_101_A" }.Bytes());

        var mod = Assert.Single(Library().List());

        Assert.False(mod.Enabled);
        Assert.Equal("content/CHR_301_A.xxx was made as CHR_101_A.xxx: renaming a package does not replace it.",
            Assert.Single(mod.Problems));
    }

    [Fact]
    public void A_mod_with_a_renamed_package_is_refused_at_install()
    {
        var zip = Path.Combine(_root, "swap.zip");
        TestArchives.Zip(zip,
            ("mod.toml", "name = \"Attire Swap\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"cosmetic\"\n"u8.ToArray()),
            ("packages/CHR_101_A.xxx", new TestPackage { MadeAs = "CHR_101_B" }.Bytes()));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);

        Assert.Empty(intake.Mods);
        Assert.Equal("Attire Swap: packages/CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it.",
            Assert.Single(intake.Refusals).Reason);
        Assert.False(Directory.Exists(Path.Combine(_layout.ModsDirectory, "Attire Swap")));
    }

    [Fact]
    public void Loose_renamed_packages_are_refused_for_wrapping()
    {
        var zip = Path.Combine(_root, "loose.zip");
        TestArchives.Zip(zip,
            ("CHR_101_A.xxx", new TestPackage { Compressed = TestPackage.Compression.WholeFile, MadeAs = "CHR_101_B" }.Bytes()),
            ("UI_SHELL.xxx", new TestPackage().Bytes("UI_SHELL.xxx")));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);

        Assert.Empty(intake.Wraps);
        Assert.Equal("CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it.",
            Assert.Single(intake.Refusals).Reason);
    }

    [Fact]
    public void Loose_packages_under_their_own_names_are_offered_for_wrapping()
    {
        var zip = Path.Combine(_root, "loose.zip");
        TestArchives.Zip(zip, ("CHR_101_A.xxx", new TestPackage { Compressed = TestPackage.Compression.WholeFile }.Bytes("CHR_101_A.xxx")));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);

        Assert.Empty(intake.Refusals);
        Assert.Equal(["packages/CHR_101_A.xxx"], Assert.Single(intake.Wraps).Files);
    }

    private static readonly string Disc = FindDisc();

    [Fact]
    public void Every_disc_package_passes_under_its_own_name()
    {
        Assert.SkipUnless(Directory.Exists(Disc), "the disc's CookedXenon is not in assets/");
        var files = Directory.EnumerateFiles(Disc, "*.xxx", SearchOption.AllDirectories).ToList();
        var known = new KnownGameFiles(files.Select(Path.GetFileName).OfType<string>());

        var renamed = new List<string>();
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            if (PackageOrigin.Check(stream, Path.GetFileName(file), known.IsPackage) is not null)
                renamed.Add(Path.GetRelativePath(Disc, file));
        }

        Assert.Equal(5414, files.Count);
        Assert.Empty(renamed);
    }

    [Theory]
    [InlineData("CHR_101_B.xxx", "CHR_101_A.xxx")] // the owner check's attire swap (modding research §2.3)
    [InlineData("CHR_101_B_C.xxx", "CHR_101_A_C.xxx")]
    [InlineData("Arena_002.xxx", "Arena_001.xxx")]
    [InlineData("UI_ONLINE.xxx", "UI_SHELL.xxx")]
    [InlineData("AnimSet_Edge.xxx", "AnimSet_Andre.xxx")]
    public void A_disc_package_under_another_name_fails(string package, string renamedTo)
    {
        var path = Path.Combine(Disc, package);
        Assert.SkipUnless(File.Exists(path), "the disc's CookedXenon is not in assets/");
        var known = new KnownGameFiles(Directory.EnumerateFiles(Disc, "*.xxx", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OfType<string>());

        using var stream = File.OpenRead(path);
        var renamed = PackageOrigin.Check(stream, renamedTo, known.IsPackage);

        Assert.NotNull(renamed);
        if (renamed.MadeAs is not null) Assert.Equal(package, renamed.MadeAs, ignoreCase: true);
    }

    /// <summary>The extracted disc's packages (gitignored), found above the tests, a worktree's checkout included.</summary>
    private static string FindDisc()
    {
        for (var folder = AppContext.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
            if (Directory.Exists(Path.Combine(folder, "assets", "Game", "CookedXenon"))) return Path.Combine(folder, "assets", "Game", "CookedXenon");
        return Path.Combine(AppContext.BaseDirectory, "assets", "Game", "CookedXenon");
    }
}
