using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// A mod's shaders prepared before Play (spec, "Shaders"; ticket 09): prepare-for-Play flags a
/// change in the mods in use, and the mods' packages feed the shader pack.
/// </summary>
public sealed class ModShadersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-mod-shaders-").FullName;
    private readonly PortableLayout _layout;

    private static readonly KnownGameFiles Known = new(["CHR_101_A.xxx", "Engine.xxx"]);

    // Names from FNV-1a-64 of the bytes, as in DiscShadersTests.
    private static readonly byte[] PixelContainer = TestPackage.Container(vertex: false, 1, 2, 3, 4);
    private static readonly byte[] OtherPixelContainer = TestPackage.Container(vertex: false, 7, 7, 7, 7);
    private const string Pixel = "ps_0ebfdcb75639341a";
    private const string OtherPixel = "ps_462f8e854c09056a";

    public ModShadersTests()
    {
        _layout = new PortableLayout(_root);
        Directory.CreateDirectory(TitleUpdate);
        File.WriteAllText(Path.Combine(TitleUpdate, "PatchTOC.txt"), "1 0 0 .\\Engine.xxx 0 \r\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string TitleUpdate => Path.Combine(_root, "updatedata");

    private ModLibrary Library() => new(_layout, Known, new FakeLinker(), new FakeRecycleBin());

    private PreparedMods Prepare() => Library().PrepareForPlay(TitleUpdate, online: false);

    /// <summary>A mod, on, whose one package carries <paramref name="container"/> in its shader cache.</summary>
    private void Mod(string folder, byte[] container, string package = "CHR_101_A.xxx")
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(path).FullName, ModLibrary.ManifestFileName),
            $"name = \"{folder}\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"cosmetic\"\n");
        new TestPackage().Add(container).Write(Path.Combine(path, "packages", package));
        Library().SetEnabled(folder, true);
    }

    [Fact]
    public void Turning_on_a_mod_flags_its_shaders_for_preparing()
    {
        Mod("Hogan Red", PixelContainer);

        Assert.True(Prepare().ShadersChanged);
    }

    [Fact]
    public void No_mods_flag_nothing()
    {
        Assert.False(Prepare().ShadersChanged);
    }

    [Fact]
    public void Once_prepared_the_same_mods_flag_nothing_at_the_next_play()
    {
        Mod("Hogan Red", PixelContainer);
        Library().ShadersPrepared(Prepare().Shaders);

        Assert.False(Prepare().ShadersChanged);
    }

    [Fact]
    public void Turning_on_another_mod_flags_again()
    {
        Mod("Hogan Red", PixelContainer);
        Library().ShadersPrepared(Prepare().Shaders);

        Mod("Engine Tweak", OtherPixelContainer, "Engine.xxx");

        Assert.True(Prepare().ShadersChanged);
    }

    [Fact]
    public void Turning_a_mod_off_flags_nothing()
    {
        Mod("Hogan Red", PixelContainer);
        Mod("Engine Tweak", OtherPixelContainer, "Engine.xxx");
        Library().ShadersPrepared(Prepare().Shaders);

        Library().SetEnabled("Engine Tweak", false);

        Assert.False(Prepare().ShadersChanged);
    }

    [Fact]
    public void An_updated_package_flags_again()
    {
        Mod("Hogan Red", PixelContainer);
        Library().ShadersPrepared(Prepare().Shaders);

        new TestPackage().Add(PixelContainer).Add(OtherPixelContainer)
            .Write(Path.Combine(_layout.ModsDirectory, "Hogan Red", "packages", "CHR_101_A.xxx"));

        Assert.True(Prepare().ShadersChanged);
    }

    /// <summary>A content mod, on, whose one new package (under <c>content/deep/</c>) carries <paramref name="container"/>.</summary>
    private void ContentMod(string folder, byte[] container)
    {
        var path = Path.Combine(_layout.ModsDirectory, folder);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(path).FullName, ModLibrary.ManifestFileName),
            $"name = \"{folder}\"\nversion = \"1.0\"\ngame = \"TU2\"\nkind = \"gameplay\"\n");
        new TestPackage().Add(container).Write(Path.Combine(path, "content", "deep", "CHR_120_A.xxx"));
        File.WriteAllText(Path.Combine(path, "content", "readme.bik"), "not a package");
        Library().SetEnabled(folder, true);
    }

    [Fact]
    public void Turning_on_a_content_mod_flags_its_new_packages_shaders()
    {
        ContentMod("New Wrestler", PixelContainer);

        Assert.True(Prepare().ShadersChanged);
    }

    [Fact]
    public void The_scan_finds_the_shader_containers_in_a_content_mod_package()
    {
        ContentMod("New Wrestler", PixelContainer);

        var sources = Prepare().Shaders;
        var shaders = DiscShaders.Read(sources.Folders, null, TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine(_layout.ModsDirectory, "New Wrestler", "content")], sources.Folders);
        Assert.Single(sources.Packages);
        Assert.Equal([Pixel], shaders.Select(shader => shader.Name));
    }

    [Fact]
    public void Once_prepared_a_content_mod_flags_nothing_at_the_next_play()
    {
        ContentMod("New Wrestler", PixelContainer);
        Library().ShadersPrepared(Prepare().Shaders);

        Assert.False(Prepare().ShadersChanged);
    }

    [Fact]
    public void The_scan_finds_the_shader_containers_in_a_mod_package()
    {
        Mod("Hogan Red", PixelContainer);
        new TestPackage { HasShaderCache = false }.Add(OtherPixelContainer)
            .Write(Path.Combine(_layout.ModsDirectory, "Hogan Red", "packages", "Engine.xxx"));

        var sources = Prepare().Shaders;
        var shaders = DiscShaders.Read(sources.Folders, null, TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine(_layout.ModsDirectory, "Hogan Red", "packages")], sources.Folders);
        Assert.Equal([Pixel], shaders.Select(shader => shader.Name));
    }
}
