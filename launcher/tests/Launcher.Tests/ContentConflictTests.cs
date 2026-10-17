using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// Ticket 17: content-mod conflicts on the Mods page and in the load report (spec, "Conflicts"
/// under content mods): the same roster slot, a whole roster package, the same file.
/// </summary>
public sealed class ContentConflictTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-content-conflicts-").FullName;
    private readonly PortableLayout _layout;

    private const string Cgc = "CombinedGlobalCharacters.xxx";
    private const string SharedDlc = "Shared_Characters_DLC.xxx";
    private const string Pack1Name = "WWE ALL STARS PACK 1";

    public ContentConflictTests()
    {
        _layout = new PortableLayout(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library() => new(_layout, new KnownGameFiles(["Engine.xxx"]), new FakeLinker(), new FakeRecycleBin());

    private static string Slot(int id, string name, string? codeName = null) =>
        $"[{id}]\r\ntemplate = 110\r\nCharacterName = \"{name}\"\r\nCodeName = \"{codeName ?? name.Replace(" ", "", StringComparison.Ordinal)}\"\r\n" +
        $"CharacterCode = \"CHR{id}\"\r\n";

    /// <summary>A content mod: its <c>roster.toml</c> (none when null) and other <c>content/</c> files.</summary>
    private void ContentMod(string id, string? roster, params (string Path, byte[] Bytes)[] files)
    {
        var folder = Path.Combine(_layout.ModsDirectory, id);
        Directory.CreateDirectory(Path.Combine(folder, "content"));
        File.WriteAllText(Path.Combine(folder, ModLibrary.ManifestFileName), $"name = \"{id} mod\"\nversion = \"1.0\"\ngame = \"TU2\"\n");
        if (roster is not null) File.WriteAllText(Path.Combine(folder, "content", RosterFile.FileName), roster);
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(folder, "content", path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, TestPackage.ForFile(bytes, Path.GetFileName(path)));
        }
    }

    /// <summary>Pack 1 installed by the DLC page, with its roster packages and one wrestler's package.</summary>
    private void Pack1()
    {
        var folder = Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866", "00000002", DlcLibrary.Pack1FileName, "Phase1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Xe-Game.ini"), "[GameDLCManager]\r\nPhaseID=1\r\n\r\n[AvailableCharacters]\r\nCharacterData_110=1\r\n");
        File.WriteAllBytes(Path.Combine(folder, Cgc), TestRosterPackage.Bytes());
        File.WriteAllBytes(Path.Combine(folder, SharedDlc), Package);
        File.WriteAllBytes(Path.Combine(folder, "CHR_DLC_01.xxx"), Package);
        var headers = Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866", "Headers", "00000002");
        Directory.CreateDirectory(headers);
        File.WriteAllBytes(Path.Combine(headers, DlcLibrary.Pack1FileName + ".header"), [9]);
    }

    private static readonly byte[] Package = new TestPackage().Bytes();

    private ModLibrary LibraryWithOn(params string[] ids)
    {
        var library = Library();
        library.List();
        File.WriteAllLines(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName), ids.Select(id => "on | " + id));
        return library;
    }

    [Fact]
    public void Two_content_mods_filling_the_same_slot_conflict_and_the_later_one_wins()
    {
        ContentMod("A", Slot(120, "First") + Slot(125, "Only A"));
        ContentMod("B", Slot(120, "Second"));

        var conflict = Assert.Single(LibraryWithOn("A", "B").Conflicts());

        Assert.Equal(ModConflictKind.RosterSlot, conflict.Kind);
        Assert.Equal("CharacterData_120", conflict.File);
        Assert.Equal(["A", "B"], conflict.Mods);
        Assert.Equal("B", conflict.Winner);
    }

    [Fact]
    public void Two_content_mods_giving_one_CodeName_in_different_slots_conflict_and_the_later_one_wins()
    {
        ContentMod("A", Slot(125, "First", codeName: "Spike"));
        ContentMod("B", Slot(120, "Second", codeName: "SPIKE"));
        ContentMod("C", Slot(128, "Third"));

        var conflict = Assert.Single(LibraryWithOn("A", "B", "C").Conflicts());

        Assert.Equal(ModConflictKind.RosterCodeName, conflict.Kind);
        Assert.Equal("Spike", conflict.File);
        Assert.Equal(["A", "B"], conflict.Mods);
        Assert.Equal("B", conflict.Winner);
    }

    [Fact]
    public void A_mod_shipping_a_whole_roster_package_conflicts_with_the_installed_pack()
    {
        Pack1();
        ContentMod("Roster", null, ($"packages/{Cgc}", TestRosterPackage.Bytes()));

        var conflict = Assert.Single(LibraryWithOn("Roster").Conflicts());

        Assert.Equal(ModConflictKind.RosterPackage, conflict.Kind);
        Assert.Equal(Cgc, conflict.File);
        Assert.Equal(["Roster"], conflict.Mods);
        Assert.Equal([Pack1Name], conflict.Packs);
        Assert.Equal("Roster", conflict.Winner);
    }

    [Fact]
    public void A_whole_roster_package_conflicts_with_every_other_content_mod_and_the_last_shipper_wins()
    {
        ContentMod("Portraits", null, (SharedDlc, Package));
        ContentMod("Wrestler", Slot(120, "New"));
        ContentMod("Other", null, ("CHR_NEW_01.xxx", Package));

        var conflict = Assert.Single(LibraryWithOn("Portraits", "Wrestler", "Other").Conflicts());

        Assert.Equal(ModConflictKind.RosterPackage, conflict.Kind);
        Assert.Equal(SharedDlc, conflict.File);
        Assert.Equal(["Portraits", "Wrestler", "Other"], conflict.Mods);
        Assert.Empty(conflict.Packs);
        Assert.Equal("Portraits", conflict.Winner);
    }

    [Fact]
    public void A_lone_whole_roster_package_with_no_pack_installed_replaces_only_the_discs_and_is_no_conflict()
    {
        ContentMod("Roster", null, (Cgc, TestRosterPackage.Bytes()));

        Assert.Empty(LibraryWithOn("Roster").Conflicts());
    }

    [Fact]
    public void Two_content_mods_shipping_the_same_file_conflict_and_the_later_one_wins()
    {
        ContentMod("A", null, ("one/CHR_NEW_01.xxx", Package), ("A__Xe-Game.ini", "[X]\r\nY=1\r\n"u8.ToArray()));
        ContentMod("B", null, ("CHR_NEW_01.xxx", Package), ("A__Xe-Game.ini", "[X]\r\nY=2\r\n"u8.ToArray()));
        ContentMod("C", null, ("CHR_NEW_02.xxx", Package));

        var conflict = Assert.Single(LibraryWithOn("B", "C", "A").Conflicts());

        Assert.Equal(ModConflictKind.ContentFile, conflict.Kind);
        Assert.Equal("CHR_NEW_01.xxx", conflict.File);
        Assert.Equal(["B", "A"], conflict.Mods);
        Assert.Equal("A", conflict.Winner);
    }

    [Fact]
    public void A_mod_file_with_an_official_pack_files_name_conflicts_with_the_pack_and_the_mod_wins()
    {
        Pack1();
        ContentMod("Fix", null, ("CHR_DLC_01.xxx", Package));

        var conflict = Assert.Single(LibraryWithOn("Fix").Conflicts());

        Assert.Equal(ModConflictKind.ContentFile, conflict.Kind);
        Assert.Equal("CHR_DLC_01.xxx", conflict.File);
        Assert.Equal(["Fix"], conflict.Mods);
        Assert.Equal([Pack1Name], conflict.Packs);
        Assert.Equal("Fix", conflict.Winner);
    }

    [Fact]
    public void A_content_mod_that_is_off_clashes_with_nothing()
    {
        ContentMod("A", Slot(120, "First"));
        ContentMod("B", Slot(120, "Second"));
        var library = Library();
        library.List();
        File.WriteAllLines(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName), ["on | A", "off | B"]);

        Assert.Empty(library.Conflicts());
    }

    [Fact]
    public void The_load_report_names_the_content_conflicts()
    {
        var tu2 = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(tu2);
        File.WriteAllText(Path.Combine(tu2, "PatchTOC.txt"), "");
        var disc = Path.Combine(_layout.GameDataDirectory, "Game", "CookedXenon");
        Directory.CreateDirectory(disc);
        File.WriteAllBytes(Path.Combine(disc, Cgc), TestRosterPackage.Bytes());
        Pack1();
        ContentMod("A", Slot(120, "First"), ("CHR_DLC_01.xxx", Package));
        ContentMod("B", Slot(120, "Second"));

        var report = LibraryWithOn("A", "B").PrepareForPlay(tu2, online: false).Report;

        Assert.Equal(2, report.ContentConflicts.Count);
        Assert.Contains("content conflict: roster slot CharacterData_120: A, B fill it; B wins", report.Lines());
        Assert.Contains($"content conflict: CHR_DLC_01.xxx is shipped by {Pack1Name}, A; A wins", report.Lines());
    }
}
