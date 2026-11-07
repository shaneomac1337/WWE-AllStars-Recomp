using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class ReleaseCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-release-").FullName;
    private readonly TestDisc _disc = new();
    private static readonly byte[] UpdateFile = System.Text.Encoding.UTF8.GetBytes("a title update file");
    private static readonly TitleUpdateManifest Update = TitleUpdateManifest.Parse(
        $"manifest_version\ttest-tu\n{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(UpdateFile))}\t{UpdateFile.Length}\tdefault.xexp\n" +
        $"{new string('0', 64)}\t378\tPatchTOC.txt\n");

    public ReleaseCheckTests()
    {
        // The layout scripts/package.ps1 stages (spec, "Folder layout").
        foreach (var path in new[]
                 {
                     "WWEAllStarsLauncher.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll",
                     "README.txt", "licenses/WWEAllStarsRecomp-LICENSE.txt", "licenses/rexglue-LICENSE.txt",
                     "game/wweallstars.exe", "game/rexruntimerd.dll", "game/rexgpu-xenosrd.dll",
                     "game/vcruntime140.dll", "xenosrecomp/dxcompiler.dll",
                     "xenosrecomp/shader_common.h",
                 })
            Stage(path, System.Text.Encoding.UTF8.GetBytes("release file " + path));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Stage(string path, byte[] data)
    {
        var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }

    private IReadOnlyList<ReleaseProblem> Check() => ReleaseCheck.Run(_root, DiscManifest.Parse(_disc.ManifestText()), Update);

    [Fact]
    public void A_complete_release_with_no_game_files_passes()
    {
        Assert.Empty(Check());
    }

    [Fact]
    public void No_release_file_name_clashes_with_the_supported_disc()
    {
        Assert.Empty(ReleaseCheck.Run(_root, DiscManifest.Supported, TitleUpdateManifest.Supported));
    }

    [Fact]
    public void A_title_update_file_fails_even_when_renamed()
    {
        Stage("game/extra.dll", UpdateFile);

        var problem = Assert.Single(Check());
        Assert.Equal("game/extra.dll", problem.Path);
        Assert.Contains("default.xexp", problem.Reason);
    }

    [Fact]
    public void A_file_named_like_a_title_update_file_fails()
    {
        Stage("game/PatchTOC.txt", [1, 2, 3]);

        Assert.Contains("PatchTOC.txt", Assert.Single(Check()).Reason);
    }

    [Theory]
    [InlineData("LIVE")] // DLC
    [InlineData("CON ")] // a title update or a console-signed package
    [InlineData("PIRS")]
    public void An_xbox_package_fails_even_when_renamed(string magic)
    {
        var package = new TestStfs { Magic = magic };
        package.Files.Add(("a.bin", TestStfs.Content(100, 1)));
        Stage("game/extra.dll", package.Build());

        var problem = Assert.Single(Check());
        Assert.Equal("game/extra.dll", problem.Path);
        Assert.Contains("Xbox 360 package", problem.Reason);
    }

    [Fact]
    public void A_file_from_the_disc_fails_even_when_renamed()
    {
        Stage("game/extra.dll", _disc.Files["CookedXenon/Startup.xxx"]);

        var problem = Assert.Single(Check());
        Assert.Equal("game/extra.dll", problem.Path);
        Assert.Contains("CookedXenon/Startup.xxx", problem.Reason);
    }

    [Theory]
    [InlineData("gamedata/readme.txt")]
    [InlineData("gamedata.partial/readme.txt")]
    [InlineData("updatedata/readme.txt")]
    [InlineData("updatedata.partial/readme.txt")]
    [InlineData("userdata/readme.txt")]
    [InlineData("cache/readme.txt")]
    [InlineData("logs/20261004-120000/readme.txt")]
    [InlineData("game/assets/readme.txt")]
    [InlineData("game/metadata/icons/readme.txt")]
    public void A_file_in_a_player_data_folder_fails(string path)
    {
        Stage(path, [1, 2, 3]);

        Assert.Equal(path, Assert.Single(Check()).Path);
    }

    [Theory]
    [InlineData("wweallstars.shaderpack")] // the shader pack setup builds
    [InlineData("xenosrecomp/0123abcd.bin")] // a shader container
    [InlineData("xenosrecomp/0123abcd.spv")]
    [InlineData("nxeart")] // the disc's key-art package
    [InlineData("nxebg.jpg")] // key art read from it
    [InlineData("icon.ico")] // the shortcut's title tile
    [InlineData("game/title.png")]
    [InlineData("game/default.xex")]
    [InlineData("WWE All Stars.iso")]
    [InlineData("game/wweallstars.settings.toml")]
    public void A_shader_art_or_other_non_program_file_fails(string path)
    {
        Stage(path, [1, 2, 3]);

        Assert.Equal(path, Assert.Single(Check()).Path);
    }

    [Theory]
    [InlineData("WWEAllStarsLauncher.exe")]
    [InlineData("av_libglesv2.dll")] // Avalonia's native renderers sit beside the AOT exe (ADR-0018)
    [InlineData("README.txt")]
    [InlineData("licenses/WWEAllStarsRecomp-LICENSE.txt")]
    [InlineData("game/wweallstars.exe")]
    [InlineData("xenosrecomp/dxcompiler.dll")] // the game's shader converter loads it
    [InlineData("xenosrecomp/shader_common.h")]
    public void A_release_missing_a_file_the_player_needs_fails(string path)
    {
        File.Delete(Path.Combine(_root, path));

        var problem = Assert.Single(Check());
        Assert.Equal(path, problem.Path);
        Assert.Equal("missing", problem.Reason);
    }

    [Fact]
    public void A_file_named_like_a_disc_file_fails_even_with_other_content()
    {
        _disc.Files["Movies/Intro_Xenon.txt"] = TestDisc.Bytes(300, 9);
        Stage("game/intro_xenon.txt", [1, 2, 3]);

        var problem = Assert.Single(Check());
        Assert.Equal("game/intro_xenon.txt", problem.Path);
        Assert.Contains("Movies/Intro_Xenon.txt", problem.Reason);
    }
}
