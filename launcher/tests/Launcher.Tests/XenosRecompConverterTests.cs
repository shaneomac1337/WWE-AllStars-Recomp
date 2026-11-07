using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class XenosRecompConverterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe xenos ").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", ".."));

    [Fact]
    public void The_converter_id_is_the_one_the_game_build_expects()
    {
        // CMakeLists.txt: the first 16 hex digits of the patch's SHA-256.
        var patch = File.ReadAllBytes(Path.Combine(RepoRoot(), "patches", "xenosrecomp-wweallstars.patch"));
        var fromPatch = Convert.ToUInt64(Convert.ToHexString(SHA256.HashData(patch))[..16], 16);

        Assert.Equal(ShaderConverter.Id, fromPatch);
    }

    [Fact]
    public void A_missing_converter_is_a_plain_failure_of_the_shader_step()
    {
        new TestPackage().Add(TestPackage.Container(vertex: false, 1, 2, 3, 4))
            .Write(Path.Combine(_root, "gamedata", "Game", "CookedXenon", "Engine.xxx"));
        var converter = new XenosRecompConverter(Path.Combine(_root, "game", PortableLayout.GameExecutableName), Path.Combine(_root, "xenosrecomp"));

        var error = Assert.Throws<ShaderPreparationException>(() =>
            new ShaderPreparation(converter, ShaderConverter.Id).Run(Path.Combine(_root, "gamedata"), Path.Combine(_root, "cache"),
                null, TestContext.Current.CancellationToken));

        Assert.Contains("shader converter is missing", error.Message);
        Assert.Contains("Emulated", error.Message);
    }

    [Fact]
    public void Unzipping_over_an_old_release_loses_its_standalone_converter()
    {
        var layout = new PortableLayout(_root);
        Directory.CreateDirectory(layout.ShaderConverterDirectory);
        foreach (var name in new[] { "XenosRecomp.exe", "fmt.dll", XenosRecompConverter.IncludeName })
            File.WriteAllText(Path.Combine(layout.ShaderConverterDirectory, name), "x");

        layout.RemoveOldShaderConverter();
        layout.RemoveOldShaderConverter();

        Assert.Equal([XenosRecompConverter.IncludeName],
            Directory.GetFiles(layout.ShaderConverterDirectory).Select(path => Path.GetFileName(path)!));
    }
}
