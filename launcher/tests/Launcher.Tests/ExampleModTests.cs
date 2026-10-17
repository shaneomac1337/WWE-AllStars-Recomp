using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// The example mod in <c>docs/example-mod/</c> (modding ticket 22) installs through the mod library
/// as it is, with no problem. The real-data case also plays it against TU2's bundles and the disc's
/// roster, found in the nearest <c>updatedata/</c> and <c>assets/</c> above the tests, and skips
/// without them.
/// </summary>
public sealed class ExampleModTests : IDisposable
{
    private static readonly string Example = Path.Combine(FindRepo(), "docs", "example-mod");
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-example-mod-").FullName;
    private readonly PortableLayout _layout;

    public ExampleModTests() => _layout = new PortableLayout(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModLibrary Library(KnownGameFiles known) => new(_layout, known, new FakeLinker(), new FakeRecycleBin());

    private static ModInfo InstallExample(ModLibrary library)
    {
        using var intake = library.Inspect([Example]);
        Assert.Empty(intake.Refusals);
        Assert.Empty(intake.Wraps);
        var candidate = Assert.Single(intake.Mods);
        Assert.False(candidate.IsUpdate);
        var id = library.Install(candidate);
        return Assert.Single(library.List(), mod => mod.Id == id);
    }

    [Fact]
    public void The_example_mod_installs_with_no_problems()
    {
        var known = new KnownGameFiles(["Engine.xxx"], ["Xe-Game.ini", "int/CharacterNames.int"]);

        var mod = InstallExample(Library(known));

        Assert.Empty(mod.Problems);
        Assert.Equal("Example Wrestler", mod.Id);
        Assert.Equal("TU2", mod.Manifest!.Game);
        Assert.Equal(ModKind.Gameplay, mod.Kind);
        Assert.Null(mod.ForcedGameplayReason);
        Assert.Equal(["config/Xe-Game.ini"], mod.Config.Select(file => file.Shown));
        Assert.True(mod.IsContentMod);
        Assert.Equal(["content/Example__CharacterNames.int", "content/roster.toml"], mod.Content.Select(file => file.Shown).Order(StringComparer.Ordinal));
        Assert.Empty(mod.Packages);
    }

    [Fact]
    public void The_example_mod_plays_against_TU2_and_the_discs_roster()
    {
        var tu2 = Nearest("updatedata");
        var roster = Nearest(Path.Combine("assets", "Game", "CookedXenon", "CombinedGlobalCharacters.xxx"))
            ?? (Environment.GetEnvironmentVariable("WWE_ALLSTARS_ASSETS") is { Length: > 0 } assets
                ? Path.Combine(assets, "Game", "CookedXenon", "CombinedGlobalCharacters.xxx") : null);
        Assert.SkipUnless(tu2 != null && File.Exists(Path.Combine(tu2, ModLibrary.ConfigBundle)) && roster != null && File.Exists(roster),
            "TU2's update folder (updatedata/) or the disc's roster (assets/) is not beside the repo");
        var update = Directory.CreateDirectory(_layout.UpdateDataDirectory).FullName;
        foreach (var file in Directory.EnumerateFiles(tu2!)) File.Copy(file, Path.Combine(update, Path.GetFileName(file)));
        var cooked = Directory.CreateDirectory(Path.Combine(_layout.GameDataDirectory, "Game", "CookedXenon")).FullName;
        File.Copy(roster!, Path.Combine(cooked, "CombinedGlobalCharacters.xxx"));
        var library = Library(new KnownGameFiles(["Engine.xxx"], ModWrapping.ConfigFilesIn(update)));

        var mod = InstallExample(library);
        Assert.Empty(mod.Problems);
        library.SetEnabled(mod.Id, true);
        var prepared = library.PrepareForPlay(update, online: false);

        var edit = Assert.Single(prepared.Report.ConfigEdits);
        Assert.Equal(new ConfigEdit("Example Wrestler", ModLibrary.ConfigBundle, "Xe-Game.ini", "1 edit"), edit);
        var merged = CoalescedBundle.Parse(File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, ModLibrary.ConfigBundle)));
        Assert.DoesNotContain("Movies=../Game/Movies/THQ.bik", merged.Find("Xe-Game.ini")!.Body, StringComparison.Ordinal);
        Assert.Equal(120, Assert.Single(prepared.Report.Content!.Roster).Id);
        Assert.Empty(prepared.Report.ContentConflicts);
        var written = File.ReadAllBytes(Path.Combine(_layout.UserDataDirectory, "0000000000000000", "54510866", "00000002",
            ModLibrary.CombinedContentName, "CombinedGlobalCharacters.xxx"));
        var entry = RosterWriter.ReadEntry(written, 120);
        Assert.Equal("Example Hogan", entry["CharacterName"]);
        Assert.Equal("ExampleHogan", entry["CodeName"]);

        // No official pack is installed here: the wrestler's packages all come from the disc
        // (modding ticket 23). The grunt sets are named by every disc wrestler and are no disc
        // file; the game plays the disc roster without them.
        var disc = Directory.EnumerateFiles(Path.GetDirectoryName(roster)!, "*.xxx")
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = entry.Where(property => property.Key.EndsWith("Asset", StringComparison.Ordinal)
                || property.Key.EndsWith(".Model", StringComparison.Ordinal) || property.Key.EndsWith(".PortraitTexture", StringComparison.Ordinal)
                || property.Key is "BruiseResources" or "MomentumData")
            .Select(property => property.Value.Split('.')[0])
            .Append(entry["IntroCinemaLevelName"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CHR_101_A", packages);
        var notOnDisc = packages.Where(package => !disc.Contains(package) && !package.StartsWith("Audio_SFX_Grunts_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([], notOnDisc.Order(StringComparer.Ordinal));
    }

    /// <summary>The nearest path of this name above the tests, so a worktree finds the main checkout's.</summary>
    private static string? Nearest(string relative)
    {
        for (var folder = AppContext.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
        {
            var path = Path.Combine(folder, relative);
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        return null;
    }

    private static string FindRepo()
    {
        for (var folder = AppContext.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
            if (File.Exists(Path.Combine(folder, "wweallstars_manifest.toml"))) return folder;
        return AppContext.BaseDirectory;
    }
}
