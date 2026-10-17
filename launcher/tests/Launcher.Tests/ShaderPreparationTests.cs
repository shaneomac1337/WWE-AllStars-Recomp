using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class ShaderPreparationTests : IDisposable
{
    private const ulong ConverterId = 0x1122334455667788;

    private readonly string _root = Directory.CreateTempSubdirectory("wwe prepare ").FullName;
    private readonly FakeConverter _converter = new();

    private static readonly byte[] PixelContainer = TestPackage.Container(vertex: false, 1, 2, 3, 4);
    private static readonly byte[] VertexContainer = TestPackage.Container(vertex: true, 9, 8, 7, 6, 5);
    private const string Pixel = "ps_0ebfdcb75639341a";
    private const string Vertex = "vs_cf73b915d1bc6715";

    public ShaderPreparationTests()
    {
        new TestPackage().Add(PixelContainer).Add(VertexContainer)
            .Write(Path.Combine(GameData, "Game", "CookedXenon", "Engine.xxx"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string GameData => Path.Combine(_root, "gamedata");
    private string Cache => Path.Combine(_root, "cache");
    private string PackPath => Path.Combine(Cache, ShaderPackFile.FileName);
    private string TopUp => Path.Combine(Cache, ShaderPreparation.TopUpFolderName);

    private ShaderPreparation Preparation() => new(_converter, ConverterId);

    private void Run(IProgress<ShaderProgress>? progress = null, CancellationToken? cancel = null) =>
        Preparation().Run(GameData, Cache, progress, cancel ?? TestContext.Current.CancellationToken);

    /// <summary>Only the pack and the top-up folder stay in the cache folder.</summary>
    private IEnumerable<string> CacheEntries() =>
        Directory.Exists(Cache) ? Directory.EnumerateFileSystemEntries(Cache).Select(Path.GetFileName).Order()! : [];

    [Fact]
    public void Success_converts_the_disc_shaders_and_writes_a_pack_the_game_accepts()
    {
        var reports = new List<ShaderProgress>();

        Run(new SyncProgress<ShaderProgress>(reports.Add));

        Assert.Equal([Pixel + ".bin", Vertex + ".bin"], _converter.Inputs);
        Assert.True(ShaderPackFile.Check(PackPath, ConverterId).Ready);
        Assert.Equal([ShaderPackFile.FileName], CacheEntries());
        Assert.Equal(new ShaderProgress(ShaderStep.Done, 1), reports[^1]);
        Assert.Contains(reports, report => report.Step == ShaderStep.Converting && report.Fraction is > 0 and < 1);
    }

    [Fact]
    public void Containers_the_game_left_in_the_top_up_folder_are_converted_too()
    {
        var extra = TestPackage.Container(vertex: false, 7, 7, 7, 7);
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), extra);
        File.WriteAllText(Path.Combine(TopUp, "readme.txt"), "not a container");

        Run();

        Assert.Equal([Pixel + ".bin", "ps_462f8e854c09056a.bin", Vertex + ".bin"], _converter.Inputs);
        Assert.False(Preparation().StatusOf(Cache).NeedsPreparing);
    }

    [Fact]
    public void A_mods_package_shaders_are_added_to_the_pack_beside_the_discs()
    {
        var modPackages = Path.Combine(_root, "mods", "Hogan Red", "packages");
        new TestPackage().Add(PixelContainer).Add(TestPackage.Container(vertex: false, 7, 7, 7, 7))
            .Write(Path.Combine(modPackages, "CHR_101_A.xxx"));

        Preparation().Run(GameData, Cache, null, TestContext.Current.CancellationToken, [modPackages]);

        Assert.Equal([Pixel + ".bin", "ps_462f8e854c09056a.bin", Vertex + ".bin"], _converter.Inputs);
        Assert.True(ShaderPackFile.Check(PackPath, ConverterId).Ready);
    }

    [Fact]
    public void A_converter_failure_says_the_game_runs_on_emulated_and_writes_no_pack()
    {
        _converter.Failure = "XenosRecomp could not convert 2 shaders.";

        var error = Assert.Throws<ShaderPreparationException>(() => Run());

        Assert.Contains("XenosRecomp could not convert 2 shaders.", error.Message);
        Assert.Contains("Emulated", error.Message);
        Assert.Empty(CacheEntries());
    }

    [Fact]
    public void A_damaged_package_fails_the_step_and_names_the_file()
    {
        File.WriteAllBytes(Path.Combine(GameData, "Game", "CookedXenon", "Engine.xxx"), [0x9E, 0x2A, 0x83, 0xC1, 0, 0]);

        var error = Assert.Throws<ShaderPreparationException>(() => Run());

        Assert.Contains("Engine.xxx", error.Message);
        Assert.Contains("Emulated", error.Message);
    }

    [Fact]
    public void Game_data_without_shaders_fails_the_step()
    {
        File.Delete(Path.Combine(GameData, "Game", "CookedXenon", "Engine.xxx"));

        var error = Assert.Throws<ShaderPreparationException>(() => Run());

        Assert.Contains("Emulated", error.Message);
        Assert.Empty(CacheEntries());
    }

    [Fact]
    public void Cancel_keeps_the_previous_pack_and_cleans_up()
    {
        Directory.CreateDirectory(Cache);
        File.WriteAllBytes(PackPath, [1, 2, 3]);
        using var cancel = new CancellationTokenSource();
        _converter.OnConvert = cancel.Cancel;

        Assert.ThrowsAny<OperationCanceledException>(() => Run(cancel: cancel.Token));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(PackPath));
        Assert.Equal([ShaderPackFile.FileName], CacheEntries());
    }

    [Fact]
    public void A_missing_pack_needs_preparing()
    {
        var status = Preparation().StatusOf(Cache);

        Assert.True(status.NeedsPreparing);
        Assert.Equal(ShaderPackProblem.Missing, status.Pack.Problem);
    }

    [Fact]
    public void A_pack_from_another_converter_is_stale_and_offered_for_rebuild()
    {
        Run();
        var later = new ShaderPreparation(_converter, ConverterId + 1);

        var status = later.StatusOf(Cache);

        Assert.True(status.NeedsPreparing);
        Assert.Equal(ShaderPackProblem.Stale, status.Pack.Problem);
        Assert.Contains("Emulated", status.Message);
    }

    [Fact]
    public void A_fresh_pack_needs_nothing()
    {
        Run();

        Assert.False(Preparation().StatusOf(Cache).NeedsPreparing);
    }

    [Fact]
    public void A_container_the_game_found_after_the_pack_was_built_is_offered()
    {
        Run();
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), TestPackage.Container(vertex: false, 7, 7, 7, 7));

        var status = Preparation().StatusOf(Cache);

        Assert.True(status.Pack.Ready);
        Assert.True(status.NeedsPreparing);
        Assert.Equal(1, status.WaitingShaders);
    }

    [Fact]
    public void Shaders_the_game_found_for_a_usable_pack_are_added_after_the_session_without_asking()
    {
        Run();
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), TestPackage.Container(vertex: false, 7, 7, 7, 7));

        Assert.True(Preparation().StatusOf(Cache).AddsAfterSession);
    }

    [Fact]
    public void A_ready_pack_with_nothing_waiting_is_not_rebuilt_after_the_session()
    {
        Run();

        Assert.False(Preparation().StatusOf(Cache).AddsAfterSession);
    }

    /// <summary>A one-shader pack as the game writes beside a top-up it converted live (ticket 22).</summary>
    internal static byte[] OneShaderPack(ulong converterId)
    {
        var pack = new byte[32 + 24];
        "WWSP"u8.CopyTo(pack);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(4), ShaderPackFile.FormatVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(pack.AsSpan(8), converterId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pack.AsSpan(16), 1);
        return pack;
    }

    [Fact]
    public void A_shader_the_game_converted_live_is_not_waiting()
    {
        Run();
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), TestPackage.Container(vertex: false, 7, 7, 7, 7));
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a" + ShaderPreparation.ConvertedExtension), OneShaderPack(ConverterId));

        var status = Preparation().StatusOf(Cache);

        Assert.Equal(0, status.WaitingShaders);
        Assert.False(status.NeedsPreparing);
        Assert.False(status.AddsAfterSession);
    }

    [Fact]
    public void A_shader_converted_live_with_another_xenosrecomp_is_still_waiting()
    {
        Run();
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), TestPackage.Container(vertex: false, 7, 7, 7, 7));
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a" + ShaderPreparation.ConvertedExtension), OneShaderPack(0x99));

        Assert.Equal(1, Preparation().StatusOf(Cache).WaitingShaders);
    }

    [Fact]
    public void A_missing_pack_is_offered_rather_than_built_after_the_session()
    {
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), TestPackage.Container(vertex: false, 7, 7, 7, 7));

        var status = Preparation().StatusOf(Cache);

        Assert.True(status.NeedsPreparing);
        Assert.False(status.AddsAfterSession);
    }

    /// <summary>
    /// Writes XenosRecomp's output shape for whatever containers it is given: one entry per
    /// container in shader_cache.cpp and an HLSL file named after each.
    /// </summary>
    private sealed class FakeConverter : IShaderConverter
    {
        public string? Failure { get; set; }
        public Action? OnConvert { get; set; }
        public List<string> Inputs { get; } = [];

        public void Convert(string inputDirectory, string outputDirectory, IProgress<double>? progress, CancellationToken cancel)
        {
            Inputs.AddRange(Directory.EnumerateFiles(inputDirectory).Select(Path.GetFileName).Order()!);
            OnConvert?.Invoke();
            cancel.ThrowIfCancellationRequested();
            progress?.Report(0.5);
            if (Failure is not null) throw new ShaderConverterException(Failure);
            var cache = new StringBuilder("ShaderCacheEntry g_shaderCacheEntries[] = {\n");
            Directory.CreateDirectory(Path.Combine(outputDirectory, "hlsl"));
            for (var i = 0; i < Inputs.Count; i++)
            {
                cache.Append($"\t{{ 0x{i + 1:X}, 0, 0, {i}, 1, 0 }},\n");
                File.WriteAllText(Path.Combine(outputDirectory, "hlsl", Path.ChangeExtension(Inputs[i], ".hlsl")), "float4 main();\n");
            }
            cache.Append("};\n")
                .Append("const uint8_t g_compressedSpirvCache[] = {1,2,3,};\n")
                .Append("const size_t g_spirvCacheCompressedSize = 3;\n")
                .Append($"const size_t g_spirvCacheDecompressedSize = {Inputs.Count};\n");
            File.WriteAllText(Path.Combine(outputDirectory, "shader_cache.cpp"), cache.ToString());
            progress?.Report(1);
        }
    }
}
