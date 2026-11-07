using System.Buffers.Binary;
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

    private readonly FakeLinker _linker = new();

    private ModLibrary Library() => new(_layout, Known, _linker, new FakeRecycleBin());

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

        Assert.Equal("CHR_101_B.xxx", Check(package, "CHR_101_A.xxx")?.MadeAs);
    }

    [Fact]
    public void A_package_that_names_neither_its_file_nor_a_game_package_is_renamed_from_an_unknown_name()
    {
        var package = new TestPackage { MadeAs = "My_Own_Wrestler" }.Bytes();

        Assert.Equal(new RenamedPackage(null, "the name it was made under is unknown"), Check(package, "CHR_101_A.xxx"));
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
        Assert.Equal("packages/CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it, "
            + "and the launcher can't rename this one: packages compressed as a whole can't be renamed.", Assert.Single(mod.Problems));
        Assert.Throws<LauncherException>(() => Library().SetEnabled("Attire Swap", true));
    }

    [Fact]
    public void A_renamable_replacement_package_installs_with_a_note()
    {
        InstalledMod("Backlash Arena", "packages", "CHR_101_A.xxx", new TestPackage { MadeAs = "CHR_101_B" }.Bytes());

        var mod = Assert.Single(Library().List());

        Assert.Empty(mod.Problems);
        Assert.True(mod.Enabled);
        Assert.Equal(["CHR_101_A.xxx was made as CHR_101_B.xxx; the launcher renames it for this slot."], mod.Notes);
        Assert.Equal("CHR_101_B.xxx", Assert.Single(mod.Packages).MadeAs);
    }

    [Fact]
    public void A_renamed_replacement_package_that_cant_be_renamed_says_why()
    {
        InstalledMod("Attire Swap", "packages", "CHR_101_A.xxx",
            new TestPackage { MadeAs = "CHR_101_B", ExtraNames = ["CHR_101_B_FX"] }.Bytes());

        var mod = Assert.Single(Library().List());

        Assert.False(mod.Enabled);
        Assert.Equal("packages/CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it, "
            + "and the launcher can't rename this one: its name table also names CHR_101_B in CHR_101_B_FX.", Assert.Single(mod.Problems));
        Assert.Empty(mod.Notes);
    }

    [Fact]
    public void A_renamed_content_package_that_cant_be_renamed_keeps_its_mod_off_with_the_reason()
    {
        InstalledMod("New Wrestler", "content", "CHR_301_A.xxx", new TestPackage { MadeAs = "CHR_101_A", ExtraNames = ["CHR_101_A_FX"] }.Bytes());

        var mod = Assert.Single(Library().List());

        Assert.False(mod.Enabled);
        Assert.Equal("content/CHR_301_A.xxx was made as CHR_101_A.xxx: renaming a package does not replace it, "
            + "and the launcher can't rename this one: its name table also names CHR_1 in CHR_101_A_FX.", Assert.Single(mod.Problems));
    }

    [Fact]
    public void A_renamable_content_package_installs_with_a_note()
    {
        InstalledMod("New Wrestler", "content", "CHR_301_A.xxx", new TestPackage { MadeAs = "CHR_101_A" }.Bytes());

        var mod = Assert.Single(Library().List());

        Assert.Empty(mod.Problems);
        Assert.Equal(["CHR_301_A.xxx was made as CHR_101_A.xxx; the launcher renames it for this slot."], mod.Notes);
        Assert.Equal("CHR_101_A.xxx", Assert.Single(mod.Content).MadeAs);
    }

    [Fact]
    public void A_mod_with_a_renamable_package_installs_as_the_modder_sent_it()
    {
        var zip = Path.Combine(_root, "swap.zip");
        var package = new TestPackage { MadeAs = "CHR_101_B" }.Bytes();
        TestArchives.Zip(zip,
            ("mod.toml", "name = \"Attire Swap\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"cosmetic\"\n"u8.ToArray()),
            ("packages/CHR_101_A.xxx", package));
        var library = Library();

        using (var intake = library.Inspect([zip], _cancel))
        {
            Assert.Empty(intake.Refusals);
            library.Install(Assert.Single(intake.Mods), _cancel);
        }

        var mod = Assert.Single(library.List());
        Assert.Equal(["CHR_101_A.xxx was made as CHR_101_B.xxx; the launcher renames it for this slot."], mod.Notes);
        Assert.Equal(package, File.ReadAllBytes(Path.Combine(_layout.ModsDirectory, "Attire Swap", "packages", "CHR_101_A.xxx")));
    }

    [Fact]
    public void A_mod_with_a_package_that_cant_be_renamed_is_refused_at_install_with_the_reason()
    {
        var zip = Path.Combine(_root, "swap.zip");
        TestArchives.Zip(zip,
            ("mod.toml", "name = \"Attire Swap\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"cosmetic\"\n"u8.ToArray()),
            ("packages/CHR_101_A.xxx", new TestPackage { MadeAs = "CHR_101_B", ExtraNames = ["CHR_101_B_FX"] }.Bytes()));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);

        Assert.Empty(intake.Mods);
        Assert.Equal("Attire Swap: packages/CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it, "
            + "and the launcher can't rename this one: its name table also names CHR_101_B in CHR_101_B_FX.",
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
        Assert.Equal("CHR_101_A.xxx was made as CHR_101_B.xxx: renaming a package does not replace it, "
            + "and the launcher can't rename this one: packages compressed as a whole can't be renamed.",
            Assert.Single(intake.Refusals).Reason);
    }

    [Fact]
    public void Loose_renamable_packages_are_offered_for_wrapping()
    {
        var zip = Path.Combine(_root, "loose.zip");
        TestArchives.Zip(zip,
            ("CHR_101_A.xxx", new TestPackage { Compressed = TestPackage.Compression.Chunks, MadeAs = "CHR_101_B" }.Bytes()),
            ("UI_SHELL.xxx", new TestPackage().Bytes("UI_SHELL.xxx")));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);

        Assert.Empty(intake.Refusals);
        Assert.Equal(["packages/CHR_101_A.xxx", "packages/UI_SHELL.xxx"], Assert.Single(intake.Wraps).Files.Order(StringComparer.Ordinal));
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

    /// <summary>A stand-in for Title Update 2's folder with its TOC.</summary>
    private string Tu2()
    {
        var folder = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "PatchTOC.txt"), "458752 0 0 .\\UI_SHELL.xxx 0 \r\n");
        File.WriteAllText(Path.Combine(folder, "UI_SHELL.xxx"), "tu2 shell");
        return folder;
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Chunks")]
    public void Play_writes_a_renamable_package_renamed_for_its_slot_with_its_own_PatchTOC_sizes(string compression)
    {
        var sent = new TestPackage { Compressed = Enum.Parse<TestPackage.Compression>(compression), MadeAs = "CHR_101_B" }.Bytes();
        InstalledMod("Attire Swap", "packages", "CHR_101_A.xxx", sent);

        var prepared = Library().PrepareForPlay(Tu2(), online: false);

        var written = Path.Combine(prepared.UpdateDataRoot, "CHR_101_A.xxx");
        using (var stream = File.OpenRead(written)) Assert.Null(PackageOrigin.Check(stream, "CHR_101_A.xxx", Known.IsPackage));
        Assert.DoesNotContain(_linker.Links, link => link.Link == written);
        Assert.Equal(sent, File.ReadAllBytes(Path.Combine(_layout.ModsDirectory, "Attire Swap", "packages", "CHR_101_A.xxx")));
        var line = File.ReadAllText(Path.Combine(prepared.UpdateDataRoot, "PatchTOC.txt"))
            .Split("\r\n").Single(line => line.EndsWith(@".\CHR_101_A.xxx 0 ", StringComparison.Ordinal));
        Assert.Equal($"{new FileInfo(written).Length} 0 0 .\\CHR_101_A.xxx 0 ", line);
        Assert.Equal(["CHR_101_A.xxx"], Assert.Single(prepared.Report.Used).Files);
    }

    [Fact]
    public void A_package_that_fails_to_rename_at_Play_leaves_no_half_written_copy()
    {
        var sent = new TestPackage { Compressed = TestPackage.Compression.TwoChunks, MadeAs = "CHR_101_B" }.Bytes();
        // The second chunk's block one byte short: its header passes the install's check, the block does not expand.
        var block = PackageRenameTests.Chunks(sent)[1].CompressedOffset + 16;
        BinaryPrimitives.WriteInt32BigEndian(sent.AsSpan(block), BinaryPrimitives.ReadInt32BigEndian(sent.AsSpan(block)) - 1);
        InstalledMod("Attire Swap", "packages", "CHR_101_A.xxx", sent);
        var library = Library();
        Assert.True(Assert.Single(library.List()).Enabled);

        var error = Assert.Throws<LauncherException>(() => library.PrepareForPlay(Tu2(), online: false));

        Assert.Contains("CHR_101_A.xxx could not be renamed from CHR_101_B.xxx", error.Message);
        Assert.False(File.Exists(Path.Combine(library.OverlayDirectory + ".partial", "CHR_101_A.xxx")));
    }

    [Fact]
    public void The_renamed_copy_is_kept_until_the_update_folder_is_rebuilt()
    {
        InstalledMod("Attire Swap", "packages", "CHR_101_A.xxx",
            new TestPackage { Compressed = TestPackage.Compression.Chunks, MadeAs = "CHR_101_B" }.Bytes());
        var tu2 = Tu2();
        var library = Library();
        var written = Path.Combine(library.PrepareForPlay(tu2, online: false).UpdateDataRoot, "CHR_101_A.xxx");
        var first = File.GetLastWriteTimeUtc(written);
        File.SetLastWriteTimeUtc(written, first.AddHours(-1));

        library.PrepareForPlay(tu2, online: false);

        Assert.Equal(first.AddHours(-1), File.GetLastWriteTimeUtc(written));
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
