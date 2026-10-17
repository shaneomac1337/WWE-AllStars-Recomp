using System.Security.Cryptography;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// Local-only checks over the owner's own TU2 and Pack 1 packages in the repo's <c>dlc/</c>
/// folder and the disc's <c>assets/nxeart</c> (gitignored; game data never enters Git),
/// skipped when the files are absent. The file lists and SHA-256s come from
/// <c>.scratch/tu2-dlc/prototype/</c> (hashes only, no content).
/// </summary>
public sealed class RealPackageTests : IDisposable
{
    private static readonly string Repo = FindRepo();
    private static readonly string Tu2 = Path.Combine(Repo, "dlc", "tu00000005_00000000");
    private static readonly string Pack1 = Path.Combine(Repo, "dlc", "F5B3E0579C2BD2939FD88B0E0A936E8095E284D754");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wwe-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Title_update_2_verifies_and_unpacks_to_the_known_files()
    {
        Assert.SkipUnless(File.Exists(Tu2), "the owner's TU2 package is not in dlc/");
        using var package = StfsPackage.Open(Tu2);
        Assert.Equal(StfsContentType.TitleUpdate, package.ContentType);
        Assert.Equal(0x54510866u, package.TitleId);
        Assert.Equal(0x60CEF505u, package.MediaId);
        Assert.Equal("WWE All Stars Title Update #2", package.DisplayName.Replace("  ", " "));
        package.Verify(null, CancellationToken.None);
        CheckUnpacked(package, "tu2-files.sha256");
    }

    [Fact]
    public void Pack_1_verifies_and_unpacks_to_the_known_files()
    {
        Assert.SkipUnless(File.Exists(Pack1), "the owner's Pack 1 package is not in dlc/");
        using var package = StfsPackage.Open(Pack1);
        Assert.Equal(StfsContentType.MarketplaceContent, package.ContentType);
        Assert.Equal("WWE ALL STARS PACK 1", package.DisplayName);
        package.Verify(null, CancellationToken.None);
        CheckUnpacked(package, "pack1-files.sha256");
    }

    [Fact]
    public void Title_update_2_installs_against_the_shipped_manifest_and_pack_1_is_refused()
    {
        Assert.SkipUnless(File.Exists(Tu2) && File.Exists(Pack1), "the owner's packages are not in dlc/");
        var setup = new TitleUpdateSetup(TitleUpdateManifest.Supported, new DriveDiskSpace(), TimeProvider.System);
        var update = Path.Combine(_folder, "updatedata");
        setup.Install(Tu2, update, SetupStart.Resume, null, CancellationToken.None);
        Assert.Equal(SetupStatus.Done, setup.StatusOf(update));
        Assert.True(setup.CheckInstalled(update, null, CancellationToken.None).NothingToRepair);
        var error = Assert.Throws<SetupException>(() => setup.Check(Pack1));
        Assert.Equal(SetupError.WrongPackage, error.Error);
    }

    [Fact]
    public void The_disc_dashboard_art_still_gives_key_art_and_a_title_tile()
    {
        var nxeart = Path.Combine(Repo, "assets", KeyArt.PackageFile);
        Assert.SkipUnless(File.Exists(nxeart), "the disc's nxeart is not in assets/");
        using var package = StfsPackage.Open(nxeart);
        package.Verify(null, CancellationToken.None);
        var background = KeyArt.BackgroundIn(Path.Combine(Repo, "assets"));
        Assert.NotNull(background);
        Assert.Equal([0xFF, 0xD8], background[..2]); // a JPEG
        Assert.Equal((byte)0x89, TitleTile.ReadPng(nxeart)[0]); // a PNG
    }

    [Fact]
    public void The_discs_roster_names_every_wrestler_by_a_CodeName_of_its_own()
    {
        var disc = new[] { Path.Combine(Repo, "assets"), Environment.GetEnvironmentVariable("WWE_ALLSTARS_ASSETS") ?? "" }
            .Select(folder => Path.Combine(folder, "Game", "CookedXenon", "CombinedGlobalCharacters.xxx"))
            .FirstOrDefault(File.Exists);
        Assert.SkipUnless(disc is not null, "the disc's CombinedGlobalCharacters.xxx is not in assets/");

        var names = RosterWriter.Names(File.ReadAllBytes(disc));

        var wrestlers = names.Where(name => !RosterFile.Placeholders.Contains(name.Id) && name.CodeName.Length > 0).ToList();
        Assert.Contains(names, name => name is { Id: 110, CodeName: "TedDibiase" });
        Assert.True(wrestlers.Count >= 40, $"{wrestlers.Count} wrestlers with a CodeName");
        Assert.Equal(wrestlers.Count, wrestlers.Select(name => name.CodeName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_roster_entry_written_into_the_discs_roster_reads_back_and_keeps_the_rest()
    {
        // WWE_ALLSTARS_ASSETS points a worktree, which has no assets/, at the main checkout's.
        var disc = new[] { Path.Combine(Repo, "assets"), Environment.GetEnvironmentVariable("WWE_ALLSTARS_ASSETS") ?? "" }
            .Select(folder => Path.Combine(folder, "Game", "CookedXenon", "CombinedGlobalCharacters.xxx"))
            .FirstOrDefault(File.Exists);
        Assert.SkipUnless(disc is not null, "the disc's CombinedGlobalCharacters.xxx is not in assets/");
        var original = File.ReadAllBytes(disc);
        var entry = new RosterEntry(120, 110, new Dictionary<string, string>
        {
            ["CharacterName"] = "Roster Test",
            ["CharacterCode"] = "CHR120",
            ["IntroCinemaLevelName"] = "Intro_CHR120",
            ["AnimSetAsset"] = "AnimSet_Roster_Test.AnimSet_Roster_Test_AnimSet",
            ["AudioAsset"] = "Audio_Set_CHR120.Audio_Set_CHR120",
        });

        var written = RosterWriter.Write(original, [entry]);

        var filled = RosterWriter.ReadEntry(written, 120);
        Assert.Equal("Roster Test", filled["CharacterName"]);
        Assert.Equal("TedDibiase", filled["CodeName"]);
        Assert.Equal("CHR120", filled["CharacterCode"]);
        Assert.Equal("AnimSet_Roster_Test.AnimSet_Roster_Test_AnimSet", filled["AnimSetAsset"]);
        Assert.Equal("Audio_Set_CHR120.Audio_Set_CHR120", filled["AudioAsset"]);
        Assert.Equal("Audio_MX_Victory_CHR110.Master.MX.VICTORY.Audio_MX_Victory_CHR110", filled["AudioVictoryMusicAsset"]);
        Assert.Equal("true", filled["LegendId.b20"]);
        Assert.Equal("Undefined", RosterWriter.ReadEntry(original, 120)["CharacterName"]);
        foreach (var id in new[] { 101, 110, 121, 204, 230 })
            Assert.Equal(RosterWriter.ReadEntry(original, id), RosterWriter.ReadEntry(written, id));
        // Every shader cache record end was moved with its body: the same containers read back.
        Directory.CreateDirectory(Path.Combine(_folder, "a"));
        Directory.CreateDirectory(Path.Combine(_folder, "b"));
        File.WriteAllBytes(Path.Combine(_folder, "a", "CombinedGlobalCharacters.xxx"), original);
        File.WriteAllBytes(Path.Combine(_folder, "b", "CombinedGlobalCharacters.xxx"), written);
        Assert.Equal(DiscShaders.Read(Path.Combine(_folder, "a"), null, CancellationToken.None).Select(c => c.Name),
            DiscShaders.Read(Path.Combine(_folder, "b"), null, CancellationToken.None).Select(c => c.Name));
    }

    private void CheckUnpacked(StfsPackage package, string listName)
    {
        package.Extract(_folder, null, CancellationToken.None);
        var expected = File.ReadAllLines(Path.Combine(Repo, ".scratch", "tu2-dlc", "prototype", listName))
            .Where(line => line.Length > 66)
            .ToDictionary(line => line[66..].TrimStart('.', '/').Replace('\\', '/'), line => line[..64],
                StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(_folder, path).Replace('\\', '/'),
            path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), actual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (path, hash) in expected) Assert.Equal(hash, actual[path]);
    }

    private static string FindRepo()
    {
        for (var folder = AppContext.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
            if (File.Exists(Path.Combine(folder, "wweallstars_manifest.toml"))) return folder;
        return AppContext.BaseDirectory;
    }
}
