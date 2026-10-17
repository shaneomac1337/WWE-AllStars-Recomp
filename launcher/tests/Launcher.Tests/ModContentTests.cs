using System.Buffers.Binary;
using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// Content mods: the combined content built at Play from the official packs and the content
/// mods in use (spec, "Content mods (new wrestlers): one combined content, built at Play").
/// </summary>
public sealed class ModContentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-mod-content-").FullName;
    private readonly PortableLayout _layout;
    private readonly FakeLinker _linker = new();

    private static readonly KnownGameFiles Known = new(["CHR_101_A.xxx", "Engine.xxx"],
        ["Xe-Engine.ini", "Xe-Game.ini", "int/CharacterNames.int", "spa/CharacterNames.spa"]);
    private static readonly byte[] PlainPackage = new TestPackage().Bytes();
    private static readonly byte[] WholeFilePackage = new TestPackage { Compressed = TestPackage.Compression.WholeFile }.Bytes();

    public ModContentTests()
    {
        _layout = new PortableLayout(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library() => new(_layout, Known, _linker, new FakeRecycleBin());

    private string TitleRoot => Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866");
    private string ContentRoot => Path.Combine(TitleRoot, "00000002");
    private string Combined => Path.Combine(ContentRoot, ModLibrary.CombinedContentName);
    private string CombinedHeader => Path.Combine(TitleRoot, "Headers", "00000002", ModLibrary.CombinedContentName + ".header");

    private string Tu2()
    {
        var folder = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "PatchTOC.txt"), "10 0 0 .\\Engine.xxx 0 \r\n");
        File.WriteAllText(Path.Combine(folder, "Engine.xxx"), "tu2 engine");
        return folder;
    }

    private const string Pack1Ini =
        "[GameDLCManager]\r\nPhaseID=1\r\n+InstalledPhases=1\r\nAllAvailable=1\r\n\r\n" +
        "[AvailableCharacters]\r\nCharacterData_110=1\r\nCharacterData_204=2\r\n";

    /// <summary>Pack 1 as the DLC page installs it: <c>Phase1\</c>, its own (stale) TOC, two inis, a header.</summary>
    private string Pack1(string ini = Pack1Ini)
    {
        var folder = Path.Combine(ContentRoot, DlcLibrary.Pack1FileName, "Phase1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Xe-Game.ini"), ini);
        File.WriteAllText(Path.Combine(folder, "AI__Xe-Game.ini"), "[AI.Variables.Character.MillionDollarMan]\r\nX=1\r\n");
        File.WriteAllText(Path.Combine(folder, "DLCTOC.txt"), "1 0 0 .\\AnimFSM_Gone.xxx 0\r\n");
        File.WriteAllBytes(Path.Combine(folder, "CombinedGlobalCharacters.xxx"), WholeFilePackage);
        File.WriteAllBytes(Path.Combine(folder, "CHR_110_A.xxx"), PlainPackage);
        File.WriteAllBytes(Path.Combine(folder, "110_JumboTron.bik"), [1, 2, 3, 4, 5]);
        var headers = Path.Combine(TitleRoot, "Headers", "00000002");
        Directory.CreateDirectory(headers);
        File.WriteAllBytes(Path.Combine(headers, DlcLibrary.Pack1FileName + ".header"), [9, 9, 9]);
        return Path.Combine(ContentRoot, DlcLibrary.Pack1FileName);
    }

    private string ContentMod(string id, params (string Path, byte[] Bytes)[] files)
    {
        var folder = Path.Combine(_layout.ModsDirectory, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ModLibrary.ManifestFileName),
            $"name = \"{id}\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"gameplay\"\n");
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(folder, "content", path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, TestPackage.ForFile(bytes, Path.GetFileName(path)));
        }
        return folder;
    }

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    private ModLibrary LibraryWithOn(params string[] ids)
    {
        var library = Library();
        library.List();
        File.WriteAllLines(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName), ids.Select(id => "on | " + id));
        return library;
    }

    [Fact]
    public void A_content_mod_in_use_gets_a_combined_content_with_a_full_licence_header_and_the_next_phase()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("Xe-Game.ini", Text("[AvailableCharacters]\r\nCharacterData_120=1\r\n")));

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        var header = File.ReadAllBytes(CombinedHeader);
        Assert.Equal(0x148 + 4, header.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(ModLibrary.CombinedContentName, Encoding.ASCII.GetString(header, 0x108, 42).TrimEnd('\0'));
        Assert.Equal(0x54510866u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x140)));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x148)));
        Assert.Equal(
            "[GameDLCManager]\r\nPhaseID=2\r\n\r\n[AvailableCharacters]\r\n" +
            "CharacterData_110=1\r\nCharacterData_204=2\r\nCharacterData_120=1\r\n",
            File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")));
    }

    private string[] TocLines() => File.ReadAllText(Path.Combine(Combined, "DLCTOC.txt")).Split("\r\n")[..^1];

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(folder, path),
            path => Convert.ToHexString(File.ReadAllBytes(path)) + "@" + File.GetLastWriteTimeUtc(path).Ticks);

    [Fact]
    public void The_combined_content_holds_the_packs_files_flat_and_a_TOC_generated_from_them()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("Wrestler/CHR_120_A.xxx", PlainPackage));
        var expanded = BinaryPrimitives.ReadInt32LittleEndian(WholeFilePackage.AsSpan(12));

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Equal(
            ["110_JumboTron.bik", "AI__Xe-Game.ini", "CHR_110_A.xxx", "CHR_120_A.xxx", "CombinedGlobalCharacters.xxx", "DLCTOC.txt", "Xe-Game.ini"],
            Directory.EnumerateFileSystemEntries(Combined).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        // Pack 1's own TOC has stale lines; the generated one lists the files there are, and no ini.
        Assert.Equal(
        [
            @"5 0 0 .\110_JumboTron.bik 0",
            $@"{PlainPackage.Length} 0 0 .\CHR_110_A.xxx 0",
            $@"{TestPackage.ForFile(PlainPackage, "CHR_120_A.xxx").Length} 0 0 .\CHR_120_A.xxx 0",
            $@"{WholeFilePackage.Length} {expanded} 0 .\CombinedGlobalCharacters.xxx 0",
        ], TocLines());
        Assert.Equal(WholeFilePackage, File.ReadAllBytes(Path.Combine(Combined, "CombinedGlobalCharacters.xxx")));
    }

    [Fact]
    public void The_packs_files_are_hard_linked_and_copied_where_links_fail()
    {
        var tu2 = Tu2();
        var pack = Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Contains(_linker.Links, link => link.Source == Path.Combine(pack, "Phase1", "CHR_110_A.xxx"));
        Assert.Equal(PlainPackage, File.ReadAllBytes(Path.Combine(Combined, "CHR_110_A.xxx")));

        _linker.Works = false;
        File.WriteAllBytes(Path.Combine(pack, "Phase1", "CHR_110_A.xxx"), WholeFilePackage);
        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Equal(WholeFilePackage, File.ReadAllBytes(Path.Combine(Combined, "CHR_110_A.xxx")));
    }

    [Fact]
    public void With_no_official_pack_the_phase_is_1_and_with_a_higher_pack_one_above_it()
    {
        var tu2 = Tu2();
        ContentMod("Wrestler", ("Xe-Game.ini", Text("[AvailableCharacters]\r\nCharacterData_120=1\r\n")));
        var library = LibraryWithOn("Wrestler");

        var alone = library.PrepareForPlay(tu2, online: false);

        Assert.StartsWith("[GameDLCManager]\r\nPhaseID=1\r\n", File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")));
        Assert.Equal(1, alone.Report.Content!.Phase);
        Assert.Empty(alone.Report.Content.Packs);

        Pack1(Pack1Ini.Replace("PhaseID=1", "PhaseID=3"));
        var withPack = library.PrepareForPlay(tu2, online: false);

        Assert.StartsWith("[GameDLCManager]\r\nPhaseID=4\r\n", File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")));
        Assert.Equal([DlcLibrary.Pack1FileName], withPack.Report.Content!.Packs.Select(pack => pack.FileName));
    }

    [Fact]
    public void The_characters_are_the_union_of_every_packs_and_mods_lines()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("First", ("Xe-Game.ini", Text("[AvailableCharacters]\r\nCharacterData_120=1\r\nCharacterData_110=1\r\n")));
        ContentMod("Second", ("ini/Xe-Game.ini", Text("[AvailableCharacters]\r\nCharacterData_125=1\r\n\r\n[Some.Section]\r\nKey=Value\r\n")));

        LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false);

        Assert.Equal(
            "[GameDLCManager]\r\nPhaseID=2\r\n\r\n[AvailableCharacters]\r\n" +
            "CharacterData_110=1\r\nCharacterData_204=2\r\nCharacterData_120=1\r\nCharacterData_125=1\r\n",
            File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")));
        // A mod's other sections still merge into Xe-Game.ini, through a fragment of its own.
        Assert.Equal("[Some.Section]\r\nKey=Value\r\n", File.ReadAllText(Path.Combine(Combined, "M2__Xe-Game.ini")));
        Assert.False(File.Exists(Path.Combine(Combined, "M1__Xe-Game.ini")));
    }

    [Fact]
    public void Every_mods_ini_fragment_merges_beside_the_packs_under_a_name_of_its_own()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("First", ("AI__Xe-Game.ini", Text("[AI.Variables.Character.First]\r\nX=1\r\n")));
        ContentMod("Second", ("AI__Xe-Game.ini", Text("[AI.Variables.Character.Second]\r\nX=2\r\n")), ("Xe-Engine.ini", Text("[Engine]\r\n")));

        LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false);

        Assert.Equal("[AI.Variables.Character.MillionDollarMan]\r\nX=1\r\n", File.ReadAllText(Path.Combine(Combined, "AI__Xe-Game.ini")));
        Assert.Equal("[AI.Variables.Character.First]\r\nX=1\r\n", File.ReadAllText(Path.Combine(Combined, "M1_AI__Xe-Game.ini")));
        Assert.Equal("[AI.Variables.Character.Second]\r\nX=2\r\n", File.ReadAllText(Path.Combine(Combined, "M2_AI__Xe-Game.ini")));
        Assert.Equal("[Engine]\r\n", File.ReadAllText(Path.Combine(Combined, "M2__Xe-Engine.ini")));
    }

    [Fact]
    public void When_two_content_mods_ship_the_same_file_the_later_one_wins()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("First", ("CHR_120_A.xxx", PlainPackage), ("CHR_110_A.xxx", PlainPackage));
        ContentMod("Second", ("deep/CHR_120_A.xxx", WholeFilePackage));

        var report = LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false).Report;

        Assert.Equal(TestPackage.ForFile(WholeFilePackage, "CHR_120_A.xxx"), File.ReadAllBytes(Path.Combine(Combined, "CHR_120_A.xxx")));
        Assert.Single(TocLines(), line => line.EndsWith(@".\CHR_120_A.xxx 0", StringComparison.Ordinal));
        Assert.Equal(["content/CHR_110_A.xxx"], report.Used[0].Files);
        Assert.Equal(["content/deep/CHR_120_A.xxx"], report.Used[1].Files);
    }

    [Fact]
    public void The_load_report_gives_the_phase_the_packs_and_each_mods_files()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage), ("Xe-Game.ini", Text("[AvailableCharacters]\r\nCharacterData_120=1\r\n")));

        var report = LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false).Report;

        Assert.Equal(
        [
            "mods: 1 in use",
            "mod 1: Wrestler 1.0 (Wrestler): content/CHR_120_A.xxx, content/Xe-Game.ini",
            $"content: phase 2, official packs WWE ALL STARS PACK 1 ({DlcLibrary.Pack1FileName})",
        ], report.Lines());
    }

    [Fact]
    public void A_content_mod_alone_starts_the_game_on_the_TU2_folder()
    {
        var tu2 = Tu2();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));

        var prepared = LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Equal(tu2, prepared.UpdateDataRoot);
        Assert.True(Directory.Exists(Combined));
    }

    [Fact]
    public void The_combined_content_is_removed_when_no_content_mod_is_in_use()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));
        var library = LibraryWithOn("Wrestler");
        library.PrepareForPlay(tu2, online: false);
        library.SetEnabled("Wrestler", false);

        var prepared = library.PrepareForPlay(tu2, online: false);

        Assert.False(Directory.Exists(Combined));
        Assert.False(File.Exists(CombinedHeader));
        Assert.Null(prepared.Report.Content);
    }

    [Fact]
    public void With_online_play_on_a_content_mod_is_left_out_and_the_combined_content_removed()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));
        var library = LibraryWithOn("Wrestler");
        library.PrepareForPlay(tu2, online: false);

        var report = library.PrepareForPlay(tu2, online: true).Report;

        Assert.False(Directory.Exists(Combined));
        Assert.False(File.Exists(CombinedHeader));
        Assert.Empty(report.Used);
        Assert.Contains("off for online play: Wrestler 1.0 (Wrestler): a content mod (new wrestlers), offline only.", report.Lines());
    }

    [Fact]
    public void A_content_mod_declared_cosmetic_is_a_gameplay_mod()
    {
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));
        var manifest = Path.Combine(_layout.ModsDirectory, "Wrestler", ModLibrary.ManifestFileName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("gameplay", "cosmetic"));

        var mod = Assert.Single(Library().List());

        Assert.Equal(ModKind.Gameplay, mod.Kind);
        Assert.Contains("adds content (new wrestlers)", mod.ForcedGameplayReason);
        Assert.True(mod.IsContentMod);
    }

    [Fact]
    public void After_the_game_exits_the_combined_content_is_removed()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));
        var library = LibraryWithOn("Wrestler");
        library.PrepareForPlay(tu2, online: false);

        library.RemoveCombinedContent();

        Assert.False(Directory.Exists(Combined));
        Assert.False(File.Exists(CombinedHeader));
        // And the next Play builds it again.
        library.PrepareForPlay(tu2, online: false);
        Assert.True(File.Exists(Path.Combine(Combined, "CHR_120_A.xxx")));
    }

    [Fact]
    public void A_combined_content_stamp_that_cant_be_deleted_stops_the_removal_and_names_it()
    {
        var tu2 = Tu2();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));
        var library = LibraryWithOn("Wrestler");
        library.PrepareForPlay(tu2, online: false);
        var stamp = Path.Combine(_layout.CacheDirectory, "mod-content.stamp");

        LauncherException error;
        using (File.Open(stamp, FileMode.Open, FileAccess.Read, FileShare.None))
            error = Assert.Throws<LauncherException>(library.RemoveCombinedContent);

        Assert.Contains(stamp, error.Message);
        Assert.Contains("could not be deleted", error.Message);
    }

    [Fact]
    public void The_players_pack_folders_and_headers_are_never_written()
    {
        var tu2 = Tu2();
        var pack = Pack1();
        var header = Path.Combine(TitleRoot, "Headers", "00000002", DlcLibrary.Pack1FileName + ".header");
        var before = Snapshot(pack);
        var headerBefore = File.ReadAllBytes(header);
        ContentMod("Wrestler", ("CHR_110_A.xxx", WholeFilePackage), ("Xe-Game.ini", Text("[AvailableCharacters]\r\nCharacterData_120=1\r\n")));
        var library = LibraryWithOn("Wrestler");

        library.PrepareForPlay(tu2, online: false);
        library.RemoveCombinedContent();

        Assert.Equal(before, Snapshot(pack));
        Assert.Equal(headerBefore, File.ReadAllBytes(header));
    }

    [Fact]
    public void The_DLC_page_does_not_list_the_combined_content_as_a_pack()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Equal([DlcLibrary.Pack1FileName], new DlcLibrary(_layout.UserDataDirectory).Installed().Select(pack => pack.FileName));
    }

    [Fact]
    public void Play_with_nothing_changed_keeps_the_combined_content()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("CHR_120_A.xxx", PlainPackage));
        var library = LibraryWithOn("Wrestler");
        library.PrepareForPlay(tu2, online: false);
        var toc = File.GetLastWriteTimeUtc(Path.Combine(Combined, "DLCTOC.txt"));
        var links = _linker.Links.Count;

        library.PrepareForPlay(tu2, online: false);

        Assert.Equal(links, _linker.Links.Count);
        Assert.Equal(toc, File.GetLastWriteTimeUtc(Path.Combine(Combined, "DLCTOC.txt")));
    }

    [Fact]
    public void A_changed_mod_file_or_order_rebuilds_the_combined_content()
    {
        var tu2 = Tu2();
        Pack1();
        var first = ContentMod("First", ("CHR_120_A.xxx", PlainPackage));
        ContentMod("Second", ("CHR_120_A.xxx", WholeFilePackage));
        LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false);

        LibraryWithOn("Second", "First").PrepareForPlay(tu2, online: false);
        Assert.Equal(TestPackage.ForFile(PlainPackage, "CHR_120_A.xxx"), File.ReadAllBytes(Path.Combine(Combined, "CHR_120_A.xxx")));

        File.WriteAllBytes(Path.Combine(first, "content", "CHR_120_A.xxx"), [.. TestPackage.ForFile(PlainPackage, "CHR_120_A.xxx"), 0, 0]);
        LibraryWithOn("Second", "First").PrepareForPlay(tu2, online: false);
        Assert.Equal(TestPackage.ForFile(PlainPackage, "CHR_120_A.xxx").Length + 2, new FileInfo(Path.Combine(Combined, "CHR_120_A.xxx")).Length);
    }

    [Theory]
    [InlineData("DLCTOC.txt", "1 0 0 .\\X.xxx 0\r\n", "content/DLCTOC.txt: a mod doesn't ship DLCTOC.txt; the launcher writes it.")]
    [InlineData("Xe-Game.ini", "[GameDLCManager]\r\nPhaseID=5\r\n", "content/Xe-Game.ini sets [GameDLCManager] PhaseID; the launcher sets the phase.")]
    [InlineData("AI__Xe-Game.ini", "[GameDLCManager]\r\nPhaseID=5\r\n", "content/AI__Xe-Game.ini sets [GameDLCManager] PhaseID; the launcher sets the phase.")]
    [InlineData("CHR 120.xxx", "", "content/CHR 120.xxx has a file name the game can't use.")]
    [InlineData("CHR_120_A.xxx", "not a package", "content/CHR_120_A.xxx is not a game package.")]
    public void A_content_file_the_launcher_must_write_or_the_game_cant_use_keeps_the_mod_off(string name, string text, string problem)
    {
        ContentMod("Wrestler", (name, Text(text)));
        var library = LibraryWithOn("Wrestler");

        var mod = Assert.Single(library.List());

        Assert.False(mod.Enabled);
        Assert.Equal(problem, Assert.Single(mod.Problems));
    }

    [Fact]
    public void Two_content_files_of_one_name_in_different_folders_are_a_problem()
    {
        ContentMod("Wrestler", ("a/CHR_120_A.xxx", PlainPackage), ("b/CHR_120_A.xxx", PlainPackage));

        var mod = Assert.Single(Library().List());

        Assert.Equal("content/b/CHR_120_A.xxx and content/a/CHR_120_A.xxx have the same name; the content goes in one folder.",
            Assert.Single(mod.Problems));
    }

    [Fact]
    public void A_named_Xe_Game_fragments_characters_go_into_the_generated_ini_and_its_other_sections_stay_in_it()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("Roster__Xe-Game.ini", Text(
            "[AvailableCharacters]\r\nCharacterData_120=1\r\n\r\n[AI.Variables.Character.New]\r\nX=3\r\n")));

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.EndsWith("CharacterData_204=2\r\nCharacterData_120=1\r\n", File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")));
        Assert.Single(Directory.EnumerateFiles(Combined, "*.ini"), path => File.ReadAllText(path).Contains("PhaseID", StringComparison.Ordinal));
        Assert.Equal("[AI.Variables.Character.New]\r\nX=3\r\n", File.ReadAllText(Path.Combine(Combined, "M1_Roster__Xe-Game.ini")));
    }

    [Fact]
    public void Engine_ini_and_language_fragments_are_renamed_to_one_split_before_their_target()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("First", ("Names__CharacterNames.int", Text("[Names]\r\nA=1\r\n")), ("CharacterNames.spa", Text("[Names]\r\nA=2\r\n")));
        ContentMod("Second", ("Names__CharacterNames.int", Text("[Names]\r\nB=1\r\n")),
            ("Anims__Xe-Engine.ini", Text("[DLCFrontEndPackages]\r\n+package=X\r\n")));

        LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false);

        Assert.Equal("[Names]\r\nA=1\r\n", File.ReadAllText(Path.Combine(Combined, "M1_Names__CharacterNames.int")));
        Assert.Equal("[Names]\r\nA=2\r\n", File.ReadAllText(Path.Combine(Combined, "M1__CharacterNames.spa")));
        Assert.Equal("[Names]\r\nB=1\r\n", File.ReadAllText(Path.Combine(Combined, "M2_Names__CharacterNames.int")));
        Assert.Equal("[DLCFrontEndPackages]\r\n+package=X\r\n", File.ReadAllText(Path.Combine(Combined, "M2_Anims__Xe-Engine.ini")));
    }

    [Fact]
    public void Two_fragments_of_one_mod_that_would_get_the_same_name_both_merge()
    {
        var tu2 = Tu2();
        Pack1();
        ContentMod("Wrestler", ("Xe-Engine.ini", Text("[A]\r\n")), ("__Xe-Engine.ini", Text("[B]\r\n")));

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        // Not "M12__": that is mod 12's name.
        var fragments = Directory.EnumerateFiles(Combined, "M1*Xe-Engine.ini").Select(path => Path.GetFileName(path)).Order().ToList();
        Assert.Equal(["M1__Xe-Engine.ini", "M1x2__Xe-Engine.ini"], fragments);
        Assert.Equal(["[A]\r\n", "[B]\r\n"], fragments.Select(name => File.ReadAllText(Path.Combine(Combined, name))).Order());
    }

    [Theory]
    [InlineData("AI__Xe-Gameplay.ini", "content/AI__Xe-Gameplay.ini merges into Xe-Gameplay.ini, which the game doesn't have.")]
    [InlineData("A__B__Xe-Game.ini", "content/A__B__Xe-Game.ini merges into B__Xe-Game.ini, which the game doesn't have.")]
    [InlineData("Names__Screens.int", "content/Names__Screens.int merges into Screens.int, which the game doesn't have.")]
    [InlineData("Names__CharacterNames.deu", "content/Names__CharacterNames.deu merges into CharacterNames.deu, which the game doesn't have.")]
    public void A_fragment_whose_target_the_game_doesnt_have_keeps_the_mod_off(string name, string problem)
    {
        ContentMod("Wrestler", (name, Text("[Section]\r\nKey=1\r\n")));

        var mod = Assert.Single(LibraryWithOn("Wrestler").List());

        Assert.False(mod.Enabled);
        Assert.Equal(problem, Assert.Single(mod.Problems));
    }
}