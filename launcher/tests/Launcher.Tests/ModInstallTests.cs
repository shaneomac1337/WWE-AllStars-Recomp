using System.Buffers.Binary;
using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>Installing downloaded mods (ticket 07): inspect what an input holds, then install, update or wrap.</summary>
public sealed class ModInstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-modinstall-").FullName;
    private readonly PortableLayout _layout;
    private readonly string _downloads;
    private readonly CancellationToken _cancel = TestContext.Current.CancellationToken;

    private static readonly KnownGameFiles Known = new(
        ["CHR_101_A.xxx", "Front_End.xxx", "UI_SHELL.xxx"],
        ["WWEGame.ini", "int/WWEGame.int"]);

    private static readonly byte[] Package = new TestPackage().Bytes();
    private static readonly byte[] OtherPackage = new TestPackage { Compressed = TestPackage.Compression.WholeFile }.Bytes();

    public ModInstallTests()
    {
        _layout = new PortableLayout(Path.Combine(_root, "launcher"));
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library(long maxUnpackedBytes = PackageIntake.MaxUnpackedBytes) =>
        new(_layout, Known, new FakeLinker(), new FakeRecycleBin()) { MaxUnpackedBytes = maxUnpackedBytes };

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Manifest(string name, string version = "1.0", string game = "TU2") => Text(
        $"""
        name = "{name}"
        author = "Someone"
        version = "{version}"
        game = "{game}"
        kind = "cosmetic"
        """);

    private string Download(string name) => Path.Combine(_downloads, name);

    private string Zip(string name, params (string Name, byte[] Data)[] files)
    {
        TestArchives.Zip(Download(name), Named(files));
        return Download(name);
    }

    private string Folder(string name, params (string Name, byte[] Data)[] files)
    {
        var folder = Directory.CreateDirectory(Download(name)).FullName;
        foreach (var (file, data) in Named(files))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
            File.WriteAllBytes(Path.Combine(folder, file), data);
        }
        return folder;
    }

    /// <summary>The files with each test package made under its own file name.</summary>
    private static (string Name, byte[] Data)[] Named(params (string Name, byte[] Data)[] files) =>
        [.. files.Select(file => (file.Name, TestPackage.ForFile(file.Data, Path.GetFileName(file.Name))))];

    private string[] ModsFolder() =>
        Directory.Exists(_layout.ModsDirectory)
            ? [.. Directory.EnumerateFileSystemEntries(_layout.ModsDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_layout.ModsDirectory, path)).Order()]
            : [];

    private ModInfo InstallOne(ModLibrary library, string input)
    {
        using var intake = library.Inspect([input], _cancel);
        Assert.Empty(intake.Refusals);
        var id = library.Install(Assert.Single(intake.Mods), _cancel);
        return Assert.Single(library.List(), mod => mod.Id == id);
    }

    private ModRefusal RefusalOf(ModLibrary library, string input)
    {
        using var intake = library.Inspect([input], _cancel);
        Assert.Empty(intake.Mods);
        Assert.Empty(intake.Wraps);
        return Assert.Single(intake.Refusals);
    }

    [Fact]
    public void A_zip_holding_one_mod_installs_it_off_and_new()
    {
        var zip = Zip("hogan.zip", ("Hogan Red/mod.toml", Manifest("Hogan Red")), ("Hogan Red/packages/CHR_101_A.xxx", Package));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);
        var candidate = Assert.Single(intake.Mods);
        Assert.False(candidate.IsUpdate);
        Assert.Empty(intake.Wraps);
        Assert.Empty(intake.Refusals);
        library.Install(candidate, _cancel);

        var mod = Assert.Single(library.List());
        Assert.Equal("Hogan Red", mod.Id);
        Assert.False(mod.Enabled);
        Assert.True(mod.IsNew);
        Assert.Empty(mod.Problems);
        Assert.Equal(["CHR_101_A.xxx"], mod.Packages.Select(package => package.Name));
    }

    [Fact]
    public void Every_mod_in_a_7z_bundle_installs()
    {
        var bundle = Download("bundle.7z");
        TestArchives.SevenZip(bundle, Named(
            ("Pack/Arena/mod.toml", Manifest("Arena")), ("Pack/Arena/packages/Front_End.xxx", Package),
            ("Pack/Attire/mod.toml", Manifest("Attire")), ("Pack/Attire/packages/CHR_101_A.xxx", Package),
            ("Pack/readme.txt", Text("two mods"))));
        var library = Library();

        using var intake = library.Inspect([bundle], _cancel);
        Assert.Equal(["Arena", "Attire"], intake.Mods.Select(mod => mod.Id));
        Assert.Empty(intake.Refusals);
        foreach (var candidate in intake.Mods) library.Install(candidate, _cancel);

        Assert.Equal(["Arena", "Attire"], library.List().Select(mod => mod.Id));
        Assert.All(library.List(), mod => Assert.Empty(mod.Problems));
    }

    [Fact]
    public void A_mod_in_a_rar_installs()
    {
        var rar = Download("menus.rar");
        TestArchives.Rar(rar, Named(("mod.toml", Manifest("Menus")), ("packages/UI_SHELL.xxx", Package)));

        var mod = InstallOne(Library(), rar);

        Assert.Equal("Menus", mod.Id);
        Assert.Empty(mod.Problems);
    }

    [Fact]
    public void A_folder_installs_as_a_copy_and_is_left_as_it_was()
    {
        var folder = Folder("Hogan Red", ("mod.toml", Manifest("Hogan Red")), ("packages/CHR_101_A.xxx", Package));

        var mod = InstallOne(Library(), folder);

        Assert.Equal("Hogan Red", mod.Id);
        Assert.Equal(TestPackage.ForFile(Package, "CHR_101_A.xxx"), File.ReadAllBytes(Path.Combine(mod.Folder, "packages", "CHR_101_A.xxx")));
        Assert.True(File.Exists(Path.Combine(folder, "packages", "CHR_101_A.xxx")));
    }

    [Fact]
    public void The_folder_is_named_from_the_manifest_not_the_archive()
    {
        var zip = Zip("hogan-red-v1.1-FINAL.zip",
            ("hogan_red_v11/mod.toml", Manifest("Hulk Hogan: Red & Yellow?")), ("hogan_red_v11/packages/CHR_101_A.xxx", Package));

        var mod = InstallOne(Library(), zip);

        Assert.Equal("Hulk Hogan_ Red & Yellow_", mod.Id);
        Assert.Equal(["Hulk Hogan_ Red & Yellow_"], Directory.GetDirectories(_layout.ModsDirectory).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("Hogan Red", "Hogan Red")]
    [InlineData("  Hogan   Red  ", "Hogan Red")]
    [InlineData("a/b\\c|d", "a_b_c_d")]
    [InlineData("...hidden.", "hidden")]
    [InlineData("CON", "CON_")]
    [InlineData("com1", "com1_")]
    [InlineData("???", null)]
    [InlineData("..", null)]
    public void A_file_safe_folder_name_is_made_from_the_mod_name(string name, string? folder)
    {
        Assert.Equal(folder, ModLibrary.FolderNameFor(name));
    }

    [Fact]
    public void An_update_asks_with_both_versions_and_keeps_the_mods_state_and_place()
    {
        var library = Library();
        InstallOne(library, Zip("arena.zip", ("mod.toml", Manifest("Arena")), ("packages/Front_End.xxx", Package)));
        InstallOne(library, Zip("hogan.zip", ("mod.toml", Manifest("Hogan Red")),
            ("packages/CHR_101_A.xxx", Package), ("packages/UI_SHELL.xxx", Package)));
        library.SetEnabled("Hogan Red", true);
        File.WriteAllLines(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName), ["on | Hogan Red", "off | Arena"]);

        using var intake = library.Inspect([Zip("hogan-1.1.zip", ("mod.toml", Manifest("Hogan Red", "1.1")),
            ("packages/CHR_101_A.xxx", OtherPackage))], _cancel);
        var update = Assert.Single(intake.Mods);
        Assert.True(update.IsUpdate);
        Assert.Equal("1.0", update.Installed!.Manifest!.Version);
        Assert.Equal("1.1", update.Manifest.Version);
        library.Install(update, _cancel);

        var mods = library.List();
        Assert.Equal(["Hogan Red", "Arena"], mods.Select(mod => mod.Id));
        Assert.True(mods[0].Enabled);
        Assert.False(mods[0].IsNew);
        Assert.Equal("1.1", mods[0].Manifest!.Version);
        Assert.Equal(["CHR_101_A.xxx"], mods[0].Packages.Select(package => package.Name));
        Assert.Equal(TestPackage.ForFile(OtherPackage, "CHR_101_A.xxx"), File.ReadAllBytes(mods[0].Packages[0].Path));
        Assert.DoesNotContain(ModsFolder(), entry => entry.StartsWith(".staging-", StringComparison.Ordinal) || entry.StartsWith(".old-", StringComparison.Ordinal));
    }

    [Fact]
    public void Loose_game_files_are_offered_for_wrapping_with_the_archive_name()
    {
        var zip = Zip("Hogan Loose Files.zip",
            ("CookedXenon/CHR_101_A.xxx", Package), ("WWEGame.ini", Text("[Game]\r\nSpeed=2\r\n")), ("readme.txt", Text("by me")));
        var library = Library();

        using var intake = library.Inspect([zip], _cancel);

        Assert.Empty(intake.Mods);
        Assert.Empty(intake.Refusals);
        var wrap = Assert.Single(intake.Wraps);
        Assert.Equal("Hogan Loose Files", wrap.SuggestedName);
        Assert.Equal(["packages/CHR_101_A.xxx", "config/WWEGame.ini"], wrap.Files);
        Assert.All(ModsFolder(), entry => Assert.StartsWith(".intake-", entry, StringComparison.Ordinal));
    }

    [Fact]
    public void Wrapping_writes_a_gameplay_manifest_and_lays_the_files_out()
    {
        var zip = Zip("loose.zip", ("CHR_101_A.xxx", Package), ("WWEGame.ini", Text("[Game]\r\nSpeed=2\r\n")),
            ("WWEGame.int", Text("[Menu]\r\nTitle=Hi\r\n")), ("readme.txt", Text("by me")));
        var library = Library();

        using (var intake = library.Inspect([zip], _cancel))
            library.Wrap(Assert.Single(intake.Wraps), "Hogan \"Loose\"", cosmetic: false, _cancel);

        var mod = Assert.Single(library.List());
        Assert.Equal("Hogan _Loose_", mod.Id);
        Assert.Equal("Hogan \"Loose\"", mod.Manifest!.Name);
        Assert.Equal(ModKind.Gameplay, mod.Manifest.Kind);
        Assert.Equal("TU2", mod.Manifest.Game);
        Assert.False(mod.Enabled);
        Assert.True(mod.IsNew);
        Assert.Empty(mod.Problems);
        Assert.Equal(
            ["Hogan _Loose_", @"Hogan _Loose_\config",@"Hogan _Loose_\config\int", @"Hogan _Loose_\config\int\WWEGame.int",
             @"Hogan _Loose_\config\WWEGame.ini", @"Hogan _Loose_\mod.toml", @"Hogan _Loose_\packages",
             @"Hogan _Loose_\packages\CHR_101_A.xxx", "load-order.txt"],
            ModsFolder().Select(entry => entry.Replace('/', '\\')));
    }

    [Fact]
    public void Wrapping_with_the_looks_only_box_makes_a_cosmetic_mod()
    {
        var file = Path.Combine(Folder("drop", ("CHR_101_A.xxx", Package)), "CHR_101_A.xxx");
        var library = Library();

        using (var intake = library.Inspect([file], _cancel))
        {
            var wrap = Assert.Single(intake.Wraps);
            Assert.Equal("CHR_101_A", wrap.SuggestedName);
            library.Wrap(wrap, "Red Boots", cosmetic: true, _cancel);
        }

        Assert.Equal(ModKind.Cosmetic, Assert.Single(library.List()).Manifest!.Kind);
    }

    [Fact]
    public void Wrapping_under_an_installed_name_updates_that_mod()
    {
        var library = Library();
        InstallOne(library, Zip("boots.zip", ("mod.toml", Manifest("Red Boots")), ("packages/CHR_101_A.xxx", Package)));
        library.SetEnabled("Red Boots", true);

        using (var intake = library.Inspect([Zip("boots2.zip", ("CHR_101_A.xxx", OtherPackage))], _cancel))
        {
            Assert.NotNull(library.Installed("Red Boots"));
            library.Wrap(Assert.Single(intake.Wraps), "Red Boots", cosmetic: true, _cancel);
        }

        var mod = Assert.Single(library.List());
        Assert.True(mod.Enabled);
        Assert.Equal(TestPackage.ForFile(OtherPackage, "CHR_101_A.xxx"), File.ReadAllBytes(mod.Packages[0].Path));
    }

    [Fact]
    public void A_missing_input_is_refused()
    {
        Assert.Equal("Not found.", RefusalOf(Library(), Download("gone.zip")).Reason);
    }

    [Fact]
    public void An_unreadable_archive_is_refused()
    {
        File.WriteAllBytes(Download("broken.zip"), Text("not a zip at all"));

        Assert.StartsWith("The archive can't be read", RefusalOf(Library(), Download("broken.zip")).Reason);
    }

    [Fact]
    public void An_archive_past_the_size_guard_is_refused()
    {
        var zip = Zip("big.zip", ("mod.toml", Manifest("Big")), ("packages/CHR_101_A.xxx", Package));

        Assert.Equal("The archive unpacks to more than 8 GB, far more than any mod.",
            RefusalOf(Library(maxUnpackedBytes: 100), zip).Reason);
    }

    [Fact]
    public void Files_that_are_not_game_files_are_refused()
    {
        var zip = Zip("tool.zip", ("CHR_101_A.xxx", Package), ("bin/Tool.exe", Text("MZ")));

        Assert.Equal("This is not a mod: it has no mod.toml, and bin/Tool.exe is not a game package or config file.",
            RefusalOf(Library(), zip).Reason);
    }

    [Fact]
    public void An_archive_with_nothing_usable_is_refused()
    {
        var zip = Zip("readme.zip", ("readme.txt", Text("hello")));

        Assert.Equal("This is not a mod: it has no mod.toml and no game files.", RefusalOf(Library(), zip).Reason);
    }

    [Fact]
    public void Loose_files_holding_the_same_package_twice_are_refused()
    {
        var zip = Zip("twice.zip", ("a/CHR_101_A.xxx", Package), ("b/CHR_101_A.xxx", OtherPackage));

        Assert.Equal("CHR_101_A.xxx is in here more than once, so it isn't clear which one to use.", RefusalOf(Library(), zip).Reason);
    }

    [Fact]
    public void A_mod_that_fails_its_checks_is_refused_with_the_reason()
    {
        var zip = Zip("old.zip", ("mod.toml", Manifest("Old Mod", game: "TU1")), ("packages/CHR_101_A.xxx", Package));

        Assert.Equal("Old Mod: It is made for TU1; this launcher runs Title Update 2 (game = \"TU2\").", RefusalOf(Library(), zip).Reason);
    }

    [Fact]
    public void A_mod_with_a_package_the_game_does_not_have_is_refused()
    {
        var zip = Zip("new.zip", ("mod.toml", Manifest("New Arena")), ("packages/Brand_New.xxx", Package));

        Assert.StartsWith("New Arena: packages/Brand_New.xxx replaces no package", RefusalOf(Library(), zip).Reason);
    }

    [Fact]
    public void A_mod_whose_name_makes_no_folder_name_is_refused()
    {
        var zip = Zip("odd.zip", ("mod.toml", Manifest("???")), ("packages/CHR_101_A.xxx", Package));

        Assert.Equal("???: its name can't be used as a folder name.", RefusalOf(Library(), zip).Reason);
    }

    [Fact]
    public void The_same_mod_twice_in_one_drop_installs_once()
    {
        var first = Zip("a.zip", ("mod.toml", Manifest("Arena")), ("packages/Front_End.xxx", Package));
        var second = Zip("b.zip", ("mod.toml", Manifest("Arena", "2.0")), ("packages/Front_End.xxx", Package));

        using var intake = Library().Inspect([first, second], _cancel);

        Assert.Equal(first, Assert.Single(intake.Mods).Source);
        Assert.Equal("Arena is in here twice; only the first one is installed.", Assert.Single(intake.Refusals).Reason);
    }

    [Fact]
    public void Refusals_leave_nothing_in_mods()
    {
        var zip = Zip("tool.zip", ("bin/Tool.exe", Text("MZ")));
        var library = Library();

        RefusalOf(library, zip);

        Assert.Empty(ModsFolder());
    }

    [Fact]
    public void A_cancelled_update_leaves_mods_as_it_was()
    {
        var library = Library();
        InstallOne(library, Zip("hogan.zip", ("mod.toml", Manifest("Hogan Red")), ("packages/CHR_101_A.xxx", Package)));
        library.SetEnabled("Hogan Red", true);
        var before = ModsFolder();
        var loadOrder = File.ReadAllText(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName));
        using var intake = library.Inspect([Zip("hogan-1.1.zip", ("mod.toml", Manifest("Hogan Red", "1.1")),
            ("packages/CHR_101_A.xxx", OtherPackage))], _cancel);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => library.Install(Assert.Single(intake.Mods), cancelled.Token));

        Assert.Equal(before, ModsFolder().Where(entry => !entry.StartsWith(".intake-", StringComparison.Ordinal)));
        Assert.Equal(loadOrder, File.ReadAllText(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName)));
        Assert.Equal(TestPackage.ForFile(Package, "CHR_101_A.xxx"), File.ReadAllBytes(Path.Combine(_layout.ModsDirectory, "Hogan Red", "packages", "CHR_101_A.xxx")));
    }

    [Fact]
    public void An_install_that_fails_its_checks_leaves_mods_as_it_was()
    {
        var library = Library();
        var folder = Folder("Arena", ("mod.toml", Manifest("Arena")), ("packages/Front_End.xxx", Package));
        using var intake = library.Inspect([folder], _cancel);
        File.WriteAllBytes(Path.Combine(folder, "packages", "Front_End.xxx"), Text("damaged since"));

        var error = Assert.Throws<LauncherException>(() => library.Install(Assert.Single(intake.Mods), _cancel));

        Assert.Equal("Arena wasn't installed: packages/Front_End.xxx is not a game package.", error.Message);
        Assert.All(ModsFolder(), entry => Assert.StartsWith(".intake-", entry, StringComparison.Ordinal));
    }

    [Fact]
    public void Disposing_the_intake_removes_the_unpacked_files()
    {
        var zip = Zip("hogan.zip", ("mod.toml", Manifest("Hogan Red")), ("packages/CHR_101_A.xxx", Package));
        var library = Library();

        library.Inspect([zip], _cancel).Dispose();

        Assert.Empty(ModsFolder());
    }

    [Fact]
    public void The_inner_inis_TU2_ships_are_read_from_its_bundles()
    {
        var update = Directory.CreateDirectory(Path.Combine(_root, "updatedata")).FullName;
        File.WriteAllBytes(Path.Combine(update, "Coalesced.ini"),
            Bundle(@"..\WWEGame\Config\XeWWEGame.ini", @"..\Engine\Config\XeEngine.ini"));
        File.WriteAllBytes(Path.Combine(update, "Coalesced.INT"), Bundle(@"..\Engine\Localization\INT\Core.int"));

        var files = ModWrapping.ConfigFilesIn(update);

        Assert.Equal(["XeWWEGame.ini", "XeEngine.ini", "int/Core.int"], files);
    }

    [Fact]
    public void A_wide_inner_ini_name_is_read_as_UTF_16LE_like_the_bundle_writer_reads_it()
    {
        var update = Directory.CreateDirectory(Path.Combine(_root, "updatedata")).FullName;
        File.WriteAllBytes(Path.Combine(update, "Coalesced.ini"), Bundle(wide: true, @"..\WWEGame\Config\XeWWEGame.ini"));

        var files = ModWrapping.ConfigFilesIn(update);

        Assert.Equal(["XeWWEGame.ini"], files);
    }

    [Fact]
    public void A_damaged_bundle_still_gives_the_names_read_before_the_damage()
    {
        var update = Directory.CreateDirectory(Path.Combine(_root, "updatedata")).FullName;
        var bundle = Bundle(@"..\WWEGame\Config\XeWWEGame.ini", @"..\Engine\Config\XeEngine.ini");
        File.WriteAllBytes(Path.Combine(update, "Coalesced.ini"), bundle[..^6]);

        var files = ModWrapping.ConfigFilesIn(update);

        Assert.Equal(["XeWWEGame.ini", "XeEngine.ini"], files);
    }

    /// <summary>A Coalesced bundle: a big-endian count, then name/body pairs of UE3 strings (ANSI, NUL-ended).</summary>
    private static byte[] Bundle(params string[] names) => Bundle(wide: false, names);

    /// <summary>A Coalesced bundle whose names are UTF-16LE (negative length) when <paramref name="wide"/>.</summary>
    private static byte[] Bundle(bool wide, params string[] names)
    {
        using var output = new MemoryStream();
        void Int(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            output.Write(bytes);
        }
        void String(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value + "\0");
            Int(bytes.Length);
            output.Write(bytes);
        }
        void Wide(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            Int(-(bytes.Length / 2));
            output.Write(bytes);
        }
        Int(names.Length * 2);
        foreach (var name in names)
        {
            if (wide) Wide(name);
            else String(name);
            String("[Section]\r\nKey=Value\r\n");
        }
        return output.ToArray();
    }
}
