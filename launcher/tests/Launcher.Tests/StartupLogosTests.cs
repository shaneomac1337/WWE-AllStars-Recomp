using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>Skip startup logos (faster-starts ticket 01): the launcher's edit to <c>[VendorStartup]</c> at Play.</summary>
public sealed class StartupLogosTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-startup-logos-").FullName;
    private readonly PortableLayout _layout;

    public StartupLogosTests() => _layout = new PortableLayout(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string GameIni = @"..\Game\Config\Xenon\Cooked\Xe-Game.ini";
    private const string EngineIni = @"..\Game\Config\Xenon\Cooked\Xe-Engine.ini";
    private const string Disclaimer = "Movies=../Game/Movies/STUNT_DISCLAIMER_[LANG].bik";

    // As TU2's Xe-Game.ini has it.
    private const string Tu2Game =
        "[VendorStartup]\r\n" +
        Disclaimer + "\r\n" +
        "Movies=../Game/Movies/THQ.bik\r\n" +
        "Movies=../Game/Movies/WWE.bik\r\n" +
        "MovieLengths0=5000.0\r\n" +
        "MovieLengths1=9500.0\r\n" +
        "MovieLengths2=2500.0\r\n" +
        "\r\n" +
        "[Credits]\r\n" +
        "Movie=../Game/Movies/CREDITS_VIDEO.bik\r\n";

    private string Tu2()
    {
        var folder = _layout.UpdateDataDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Coalesced.ini"),
            CoalescedBundleTests.Bundle((EngineIni, "[URL]\r\nProtocol=unreal\r\n"), (GameIni, Tu2Game)));
        File.WriteAllText(Path.Combine(folder, "Engine.xxx"), "tu2 engine");
        File.WriteAllText(Path.Combine(folder, "PatchTOC.txt"), "999 0 0 .\\Coalesced.ini 0 \r\n1409024 6464092 0 .\\Engine.xxx 0 \r\n");
        return folder;
    }

    private void Mod(string folder, string xeGame)
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        Directory.CreateDirectory(Path.Combine(path, "config"));
        File.WriteAllText(Path.Combine(path, ModLibrary.ManifestFileName),
            $"name = \"{folder}\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"gameplay\"\n");
        File.WriteAllText(Path.Combine(path, "config", "Xe-Game.ini"), xeGame);
    }

    private PreparedMods Play(bool skipLogos, params string[] on)
    {
        var tu2 = Tu2();
        var library = new ModLibrary(_layout, new KnownGameFiles(["Engine.xxx"]), new FakeLinker(), new FakeRecycleBin());
        library.List();
        foreach (var id in on) library.SetEnabled(id, true);
        return library.PrepareForPlay(tu2, online: false, skipLogos);
    }

    private static byte[] Bundle(PreparedMods prepared) => File.ReadAllBytes(Path.Combine(prepared.UpdateDataRoot, "Coalesced.ini"));

    private static string GameBody(PreparedMods prepared) => CoalescedBundle.Parse(Bundle(prepared)).Find("Xe-Game.ini")!.Body;

    [Fact]
    public void With_it_on_the_startup_movies_keep_only_the_disclaimer()
    {
        var prepared = Play(skipLogos: true);

        var body = GameBody(prepared);
        Assert.DoesNotContain("THQ.bik", body);
        Assert.DoesNotContain("WWE.bik", body);
        Assert.Contains(Disclaimer, body);
        // The rest of the ini is as TU2 ships it.
        Assert.Equal(Tu2Game.Replace("Movies=../Game/Movies/THQ.bik\r\n", "").Replace("Movies=../Game/Movies/WWE.bik\r\n", ""), body);
        Assert.NotEqual(_layout.UpdateDataDirectory, prepared.UpdateDataRoot);
        Assert.Contains("config: launcher (Skip startup logos): Coalesced.ini Xe-Game.ini: 2 edits", prepared.Report.Lines());
    }

    [Fact]
    public void With_it_off_and_no_mod_the_game_starts_on_Title_Update_2_itself()
    {
        var prepared = Play(skipLogos: false);

        Assert.Equal(_layout.UpdateDataDirectory, prepared.UpdateDataRoot);
        Assert.Empty(prepared.Report.ConfigEdits);
    }

    [Fact]
    public void With_it_off_the_bundle_is_byte_for_byte_the_mods_only_merge()
    {
        Mod("Tuning", "[Credits]\r\nMovie=../Game/Movies/OTHER.bik\r\n");
        var modsOnly = Bundle(Play(skipLogos: false, "Tuning"));
        var expected = CoalescedBundle.Parse(File.ReadAllBytes(Path.Combine(_layout.UpdateDataDirectory, "Coalesced.ini")));
        expected.Find("Xe-Game.ini")!.Body = Tu2Game.Replace("CREDITS_VIDEO", "OTHER");

        Assert.Equal(expected.ToBytes(), modsOnly);
    }

    [Fact]
    public void A_mod_editing_the_startup_movies_combines_with_it()
    {
        Mod("Intro", "[VendorStartup]\r\n+Movies=../Game/Movies/MOD_INTRO.bik\r\n");

        var both = Play(skipLogos: true, "Intro");
        var body = GameBody(both);
        Assert.Contains("Movies=../Game/Movies/MOD_INTRO.bik", body);
        Assert.Contains(Disclaimer, body);
        Assert.DoesNotContain("THQ.bik", body);
        Assert.DoesNotContain("WWE.bik", body);
        // The mod's edit comes first, then the launcher's, so the load report shows both.
        Assert.Equal(["Intro", "launcher (Skip startup logos)"], both.Report.ConfigEdits.Select(edit => edit.ModName));

        // Turning it off again gives the mod's movie list back with the logos, from a rebuilt folder.
        var modOnly = GameBody(Play(skipLogos: false, "Intro"));
        Assert.Contains("THQ.bik", modOnly);
        Assert.Contains("MOD_INTRO.bik", modOnly);
    }
}
