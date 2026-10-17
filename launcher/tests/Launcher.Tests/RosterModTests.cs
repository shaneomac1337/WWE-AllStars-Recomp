using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// A content mod's roster-entry file (<c>content/roster.toml</c>): its slot rules and problems, and
/// the roster package Play writes into the combined content (spec, "Roster entries, written by the launcher").
/// </summary>
public sealed class RosterModTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-roster-mod-").FullName;
    private readonly PortableLayout _layout;

    private const string Cgc = "CombinedGlobalCharacters.xxx";

    public RosterModTests()
    {
        _layout = new PortableLayout(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library(KnownGameFiles? known = null) =>
        new(_layout, known ?? new KnownGameFiles(["Engine.xxx"]), new FakeLinker(), new FakeRecycleBin());

    private string Combined => Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866", "00000002", ModLibrary.CombinedContentName);

    private string Tu2()
    {
        var folder = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "PatchTOC.txt"), "");
        return folder;
    }

    /// <summary>The disc's roster, where the game data is: <c>Game/CookedXenon/</c>.</summary>
    private void DiscRoster(byte[] package)
    {
        var folder = Path.Combine(_layout.GameDataDirectory, "Game", "CookedXenon");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, Cgc), package);
    }

    private void Pack1(byte[] roster)
    {
        var folder = Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866", "00000002", DlcLibrary.Pack1FileName, "Phase1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Xe-Game.ini"), "[GameDLCManager]\r\nPhaseID=1\r\n\r\n[AvailableCharacters]\r\nCharacterData_110=1\r\n");
        File.WriteAllBytes(Path.Combine(folder, Cgc), roster);
        var headers = Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866", "Headers", "00000002");
        Directory.CreateDirectory(headers);
        File.WriteAllBytes(Path.Combine(headers, DlcLibrary.Pack1FileName + ".header"), [9]);
    }

    private void ContentMod(string id, string roster, params (string Path, byte[] Bytes)[] files)
    {
        var folder = Path.Combine(_layout.ModsDirectory, id);
        Directory.CreateDirectory(Path.Combine(folder, "content"));
        File.WriteAllText(Path.Combine(folder, ModLibrary.ManifestFileName), $"name = \"{id}\"\nversion = \"1.0\"\ngame = \"TU2\"\n");
        File.WriteAllText(Path.Combine(folder, "content", RosterFile.FileName), roster);
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(folder, "content", path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    private ModLibrary LibraryWithOn(params string[] ids) => LibraryWithOn(null, ids);

    private ModLibrary LibraryWithOn(KnownGameFiles? known, params string[] ids)
    {
        var library = Library(known);
        library.List();
        File.WriteAllLines(Path.Combine(_layout.ModsDirectory, ModLibrary.LoadOrderFileName), ids.Select(id => "on | " + id));
        return library;
    }

    private const string Entry120 =
        "# A new wrestler in slot 120.\r\n[120]\r\ntemplate = 110\r\nCharacterName = \"Spike Twelve\" # shown name\r\n" +
        "CodeName = \"SpikeTwelve\"\r\nCharacterCode = \"CHR120\"\r\nAnimSetAsset = \"AnimSet_Spike.AnimSet_Spike_AnimSet\"\r\nnHealth = 80\r\n";

    private IReadOnlyDictionary<string, string> Written(int id) => RosterWriter.ReadEntry(File.ReadAllBytes(Path.Combine(Combined, Cgc)), id);

    [Fact]
    public void Play_writes_the_entry_into_the_highest_packs_roster_and_turns_the_slot_on()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes(templateName: "Disc Template"));
        Pack1(TestRosterPackage.Bytes(templateName: "Pack Template"));
        ContentMod("Wrestler", Entry120);

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        var entry = Written(120);
        Assert.Equal("Spike Twelve", entry["CharacterName"]);
        Assert.Equal("CHR120", entry["CharacterCode"]);
        Assert.Equal("AnimSet_Spike.AnimSet_Spike_AnimSet", entry["AnimSetAsset"]);
        Assert.Equal("80", entry["nHealth"]);
        Assert.Equal("Pack Template", Written(110)["CharacterName"]);
        Assert.Equal(
            "[GameDLCManager]\r\nPhaseID=2\r\n\r\n[AvailableCharacters]\r\nCharacterData_110=1\r\nCharacterData_120=1\r\n",
            File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")));
    }

    [Fact]
    public void With_no_official_pack_the_disc_roster_is_the_base()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes(templateName: "Disc Template"));
        ContentMod("Wrestler", Entry120);

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Equal("Disc Template", Written(110)["CharacterName"]);
        Assert.Equal("Spike Twelve", Written(120)["CharacterName"]);
    }

    [Fact]
    public void The_written_roster_is_uncompressed_in_the_TOC_and_the_roster_file_stays_out_of_the_content()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        ContentMod("Wrestler", Entry120);

        LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false);

        var size = new FileInfo(Path.Combine(Combined, Cgc)).Length;
        Assert.Contains($@"{size} 0 0 .\{Cgc} 0", File.ReadAllText(Path.Combine(Combined, "DLCTOC.txt")).Split("\r\n"));
        Assert.False(File.Exists(Path.Combine(Combined, RosterFile.FileName)));
    }

    [Fact]
    public void The_load_report_names_each_slot_and_the_mod_that_filled_it()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        ContentMod("Wrestler", Entry120);

        var report = LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false).Report;

        Assert.Equal(
        [
            "mods: 1 in use",
            "mod 1: Wrestler 1.0 (Wrestler): content/roster.toml",
            "content: phase 1, no official pack",
            "roster: CharacterData_120 filled by Wrestler 1.0 (Wrestler)",
        ], report.Lines());
    }

    [Fact]
    public void When_two_mods_fill_the_same_slot_the_later_one_wins()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        ContentMod("First", Entry120);
        ContentMod("Second", "[120]\r\nCharacterName = \"Second Wrestler\"\r\nCodeName = \"SecondWrestler\"\r\n");

        var report = LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false).Report;

        Assert.Equal("Second Wrestler", Written(120)["CharacterName"]);
        Assert.Contains("roster: CharacterData_120 filled by Second 1.0 (Second)", report.Lines());
        Assert.DoesNotContain(report.Lines(), line => line.Contains("by First", StringComparison.Ordinal));
    }

    [Fact]
    public void A_roster_entry_the_base_cant_take_stops_Play_with_the_reason()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        ContentMod("Wrestler", "[120]\r\ntemplate = 121\r\nCharacterName = \"X\"\r\nCodeName = \"X\"\r\n");

        var error = Assert.Throws<LauncherException>(() => LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false));

        Assert.Contains("CharacterData_121", error.Message);
    }

    [Fact]
    public void With_no_roster_to_write_into_Play_says_so()
    {
        var tu2 = Tu2();
        ContentMod("Wrestler", Entry120);

        var error = Assert.Throws<LauncherException>(() => LibraryWithOn("Wrestler").PrepareForPlay(tu2, online: false));

        Assert.Contains(Cgc, error.Message);
    }

    [Fact]
    public void A_CodeName_the_disc_roster_already_has_keeps_the_mod_off()
    {
        DiscRoster(TestRosterPackage.Bytes());
        ContentMod("Wrestler", "[120]\r\nCharacterName = \"X\"\r\nCodeName = \"teddibiase\"\r\n");

        var mod = Assert.Single(LibraryWithOn("Wrestler").List());

        Assert.False(mod.Enabled);
        Assert.Equal(
            "content/roster.toml: [120] has CodeName teddibiase, which Million Dollar Man (roster entry 110) already has; " +
            "the game would show and play that wrestler. Give it a CodeName of its own.",
            Assert.Single(mod.Problems));
    }

    [Fact]
    public void The_CodeNames_checked_are_the_highest_packs_roster_when_one_is_installed()
    {
        DiscRoster(TestRosterPackage.Bytes(templateCode: "DiscOnly"));
        Pack1(TestRosterPackage.Bytes(templateName: "Pack Template", templateCode: "PackOnly"));
        ContentMod("Taken", "[120]\r\nCharacterName = \"X\"\r\nCodeName = \"PackOnly\"\r\n");
        ContentMod("Free", "[120]\r\nCharacterName = \"X\"\r\nCodeName = \"DiscOnly\"\r\n");

        var mods = LibraryWithOn("Taken", "Free").List();

        Assert.Contains("Pack Template (roster entry 110)", Assert.Single(mods[0].Problems), StringComparison.Ordinal);
        Assert.Empty(mods[1].Problems);
    }

    [Fact]
    public void Play_gives_each_entry_its_name_in_every_language_the_game_ships()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        ContentMod("Wrestler", Entry120);
        var known = new KnownGameFiles(["Engine.xxx"], ["int/CharacterNames.int", "fra/CharacterNames.fra", "deu/CharacterNames.deu"], ["int", "fra"]);

        LibraryWithOn(known, "Wrestler").PrepareForPlay(tu2, online: false);

        const string Names = "[CharacterNames]\r\nSpikeTwelve_Name=SPIKE TWELVE\r\nSpikeTwelve_Short=SPIKE TWELVE\r\n";
        Assert.Equal(Names, File.ReadAllText(Path.Combine(Combined, "M1_Roster__CharacterNames.int")));
        Assert.Equal(Names, File.ReadAllText(Path.Combine(Combined, "M1_Roster__CharacterNames.fra")));
        Assert.False(File.Exists(Path.Combine(Combined, "M1_Roster__CharacterNames.deu")));
    }

    [Fact]
    public void A_name_the_mod_gives_itself_is_not_generated_for_that_language()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        var own = "[CharacterNames]\r\nSpikeTwelve_Name = THE SPIKE\r\nSpikeTwelve_Short=SPIKE\r\n";
        ContentMod("Wrestler", Entry120, ("My__CharacterNames.int", Encoding.ASCII.GetBytes(own)));
        var known = new KnownGameFiles(["Engine.xxx"], ["int/CharacterNames.int", "fra/CharacterNames.fra"], ["int", "fra"]);

        LibraryWithOn(known, "Wrestler").PrepareForPlay(tu2, online: false);

        Assert.Equal(own, File.ReadAllText(Path.Combine(Combined, "M1_My__CharacterNames.int")));
        Assert.False(File.Exists(Path.Combine(Combined, "M1_Roster__CharacterNames.int")));
        Assert.True(File.Exists(Path.Combine(Combined, "M1_Roster__CharacterNames.fra")));
    }

    [Fact]
    public void When_two_mods_give_one_CodeName_the_later_ones_entry_is_written_and_the_earlier_ones_left_out()
    {
        var tu2 = Tu2();
        DiscRoster(TestRosterPackage.Bytes());
        // The test roster has no CharacterData_125, so writing First's entry would stop Play.
        ContentMod("First", "[125]\r\nCharacterName = \"First\"\r\nCodeName = \"Spike\"\r\n");
        ContentMod("Second", "[120]\r\nCharacterName = \"Second\"\r\nCodeName = \"Spike\"\r\n");

        var report = LibraryWithOn("First", "Second").PrepareForPlay(tu2, online: false).Report;

        Assert.Equal("Second", Written(120)["CharacterName"]);
        Assert.Contains("content conflict: CodeName Spike: First, Second give it; Second wins", report.Lines());
        Assert.DoesNotContain(report.Lines(), line => line.Contains("CharacterData_125", StringComparison.Ordinal));
        Assert.DoesNotContain("CharacterData_125", File.ReadAllText(Path.Combine(Combined, "Xe-Game.ini")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[121]\r\nCharacterName = \"X\"\r\n",
        "content/roster.toml: [121] is not a free roster slot; a roster entry fills one of 120, 125, 127, 128, 129, 130, 215, 217, 228, 229, 230.")]
    [InlineData("[200]\r\nCharacterName = \"X\"\r\n",
        "content/roster.toml: [200] is not a free roster slot; a roster entry fills one of 120, 125, 127, 128, 129, 130, 215, 217, 228, 229, 230.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nCodeName = \"X\"\r\n[120]\r\nCharacterName = \"Y\"\r\n", "content/roster.toml: [120] is given twice.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\n",
        "content/roster.toml: [120] has no CodeName, so it would keep template 110's and show and play as that wrestler. Give it a CodeName of its own (letters and digits).")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nCodeName = \"Spike 12\"\r\n",
        "content/roster.toml: [120] CodeName = \"Spike 12\" is not a quoted CodeName of letters and digits.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nCodeName = \"Spike\"\r\n[125]\r\nCharacterName = \"Y\"\r\nCodeName = \"spike\"\r\n",
        "content/roster.toml: [125] has CodeName spike, as [120] does; each entry needs a CodeName of its own.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nHairColour = \"Red\"\r\n",
        "content/roster.toml: [120] sets HairColour, which is not a property a roster entry can set.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nnHealth = lots\r\n", "content/roster.toml: [120] nHealth = lots is not a whole number of 0 or more.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nClassType = 300\r\n", "content/roster.toml: [120] ClassType = 300 is not a whole number from 0 to 255.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nScale = -1\r\n", "content/roster.toml: [120] Scale = -1 is not a number above 0, such as 1.02.")]
    [InlineData("[120]\r\nCharacterName = X\r\n", "content/roster.toml: [120] CharacterName = X is not a quoted name.")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nAnimSetAsset = \"NoDot\"\r\n",
        "content/roster.toml: [120] AnimSetAsset = \"NoDot\" is not a quoted asset name such as \"Package.Object\".")]
    [InlineData("[120]\r\ntemplate = 125\r\nCharacterName = \"X\"\r\n",
        "content/roster.toml: [120] template = 125 is not a complete roster entry to copy (101–130 or 201–230, not a placeholder).")]
    [InlineData("[120]\r\nCharacterName = \"X\"\r\nCharacterName = \"Y\"\r\n", "content/roster.toml: [120] sets CharacterName twice.")]
    [InlineData("[120]\r\nnHealth = 50\r\n", "content/roster.toml: [120] has no CharacterName.")]
    [InlineData("CharacterName = \"X\"\r\n", "content/roster.toml: line 1 comes before any [id].")]
    [InlineData("[120]\r\nthis is not a line\r\n", "content/roster.toml: line 2 is not [id] or key = value.")]
    [InlineData("# nothing yet\r\n", "content/roster.toml: it has no roster entry ([id] sections).")]
    public void A_roster_file_with_a_bad_slot_property_or_value_keeps_the_mod_off(string roster, string problem)
    {
        ContentMod("Wrestler", roster);
        var library = LibraryWithOn("Wrestler");

        var mod = Assert.Single(library.List());

        Assert.False(mod.Enabled);
        Assert.Equal(problem, Assert.Single(mod.Problems));
    }

    [Fact]
    public void Every_placeholder_slot_and_every_documented_property_is_accepted()
    {
        var text = new StringBuilder();
        foreach (var id in RosterFile.Placeholders)
            text.Append($"[{id}]\r\ntemplate = 121\r\nCharacterName = \"Name {id}\"\r\nCodeName = \"Code{id}\"\r\n" +
                $"CharacterCode = \"CHR{id}\"\r\nIntroCinemaLevelName = \"Intro_CHR{id}\"\r\nClassType = 2\r\nnHealth = 90\r\nnStun = 1\r\n" +
                "nPower = 2\r\nnStamina = 3\r\nnGrappling = 4\r\nnAgility = 5\r\nScale = 1.02\r\nbroadPhaseRadius = 37\r\n" +
                "SkeletalMeshAsset = \"A.B\"\r\nPhysicsMeshAsset = \"A.B\"\r\nMeshTreeAsset = \"A.B.C\"\r\nBruiseResources = \"A.B\"\r\n" +
                "AnimSetAsset = \"A.B\"\r\nAnimSetFinisherAsset = \"A.B\"\r\nAnimFSMAsset = \"A.B\"\r\nAIFSMAsset = \"A.B\"\r\nMomentumData = \"A.B\"\r\n" +
                "AudioAsset = \"A.B\"\r\nAudioCommentaryAsset = \"A.B\"\r\nAudioVictoryMusicAsset = \"A.B\"\r\nAudioCrowdChantAsset = \"A.B\"\r\n" +
                "AudioGruntAsset = \"A.B\"\r\nCostumesArray.0.Model = \"A.B.C\"\r\nCharacterPortraits.3.PortraitTexture = \"HUDPortraitsGEN.GEN_100\"\r\n");
        ContentMod("Wrestler", text.ToString());

        var mod = Assert.Single(LibraryWithOn("Wrestler").List());

        Assert.Empty(mod.Problems);
        Assert.True(mod.Enabled);
    }
}
