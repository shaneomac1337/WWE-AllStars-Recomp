using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// When the launcher rebuilds pipelines (spec, "When the launcher rebuilds pipelines") and how
/// it runs the game's prepare mode. The game is a fake that prints ticket 06's output.
/// </summary>
public sealed class PipelinePreparationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe pipelines ").FullName;
    private readonly FakeRunner _runner = new();
    private readonly PortableLayout _layout;

    public PipelinePreparationTests()
    {
        _layout = new PortableLayout(_root);
        Directory.CreateDirectory(_layout.CacheDirectory);
        Directory.CreateDirectory(_layout.GameDirectory);
        File.WriteAllBytes(Pack, [1, 2, 3]);
        File.WriteAllText(KeyList, "wwe-pipeline-keys 1\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Pack => Path.Combine(_layout.CacheDirectory, ShaderPackFile.FileName);
    private string KeyList => Path.Combine(_layout.GameDirectory, PipelinePreparation.KeyListFileName);
    private string Store => Path.Combine(_layout.CacheDirectory, PipelinePreparation.StoreFolderName);
    private string TopUp => Path.Combine(_layout.CacheDirectory, ShaderPreparation.TopUpFolderName);

    private PipelinePreparation Preparation() => new(_layout, _runner);

    /// <summary>A finished prepare: the fake game writes the store, as the real one does.</summary>
    private Task<PipelineResult> Prepare(params string[] lines)
    {
        _runner.Lines = lines.Length > 0 ? lines : OkRun;
        return Preparation().RunAsync(null, TestContext.Current.CancellationToken);
    }

    private static readonly string[] OkRun =
    [
        "wwe-prepare-pipelines 1",
        "progress stage=modules done=2 total=2",
        "progress stage=pipelines done=3 total=3",
        "result status=ok modules=2 pipelines=3 failed=0 ms=9",
    ];

    private PipelineRebuild Check(bool packReady = true, bool repair = false) => Preparation().Check(packReady, repair);

    [Fact]
    public void A_prepare_that_never_ran_rebuilds_for_the_missing_store()
    {
        Assert.Equal(PipelineRebuild.NoStore, Check());
    }

    [Fact]
    public async Task After_a_prepare_nothing_changed_and_nothing_is_rebuilt()
    {
        await Prepare();

        Assert.Equal(PipelineRebuild.None, Check());
        Assert.False(PipelineRebuild.None.Rebuilds());
    }

    [Fact]
    public async Task A_new_pack_rebuilds()
    {
        await Prepare();
        File.WriteAllBytes(Pack, [1, 2, 4]);

        Assert.Equal(PipelineRebuild.PackChanged, Check());
    }

    [Fact]
    public async Task New_top_ups_rebuild()
    {
        await Prepare();
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), [7]);

        Assert.Equal(PipelineRebuild.TopUpsChanged, Check());
    }

    [Fact]
    public async Task Top_ups_the_game_converted_live_do_not_rebuild()
    {
        // Their pipelines were made in that session and are in the store (ticket 22).
        await Prepare();
        Directory.CreateDirectory(TopUp);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a.bin"), [7]);
        File.WriteAllBytes(Path.Combine(TopUp, "ps_462f8e854c09056a" + ShaderPreparation.ConvertedExtension), [8]);

        Assert.Equal(PipelineRebuild.None, Check());
    }

    [Fact]
    public async Task A_new_shipped_key_list_rebuilds()
    {
        await Prepare();
        File.AppendAllText(KeyList, "more keys\n");

        Assert.Equal(PipelineRebuild.KeyListChanged, Check());
    }

    [Fact]
    public async Task A_deleted_store_rebuilds()
    {
        await Prepare();
        File.Delete(Path.Combine(Store, "keys.bin"));

        Assert.Equal(PipelineRebuild.NoStore, Check());
    }

    [Fact]
    public async Task The_games_stale_marker_rebuilds()
    {
        await Prepare();
        File.WriteAllText(Path.Combine(Store, "pipelines-stale"), "wwe-pipelines-stale 1\nreason=another driver version\n");

        Assert.Equal(PipelineRebuild.Stale, Check());
        Assert.True(PipelineRebuild.Stale.Rebuilds());
    }

    [Fact]
    public async Task Repair_always_rebuilds()
    {
        await Prepare();

        Assert.Equal(PipelineRebuild.Repair, Check(repair: true));
        Assert.True(PipelineRebuild.Repair.Rebuilds());
    }

    [Fact]
    public void Without_a_usable_pack_nothing_is_rebuilt_and_the_pack_is_offered_instead()
    {
        var rule = Check(packReady: false, repair: true);

        Assert.Equal(PipelineRebuild.NoShaderPack, rule);
        Assert.False(rule.Rebuilds());
    }

    [Fact]
    public async Task A_failed_prepare_keeps_asking_for_a_rebuild()
    {
        await Prepare();
        File.WriteAllBytes(Pack, [9]);

        var result = await Prepare("wwe-prepare-pipelines 1", "result status=failed reason=vulkan_setup ms=3");

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(PipelineRebuild.PackChanged, Check());
    }

    [Fact]
    public async Task A_partial_prepare_counts_as_done()
    {
        var result = await Prepare("wwe-prepare-pipelines 1", "result status=partial modules=2 pipelines=2 failed=1 ms=3");

        Assert.Equal(PipelineOutcome.Partial, result.Outcome);
        Assert.Equal(PipelineRebuild.None, Check());
    }

    [Fact]
    public async Task The_run_reports_progress_as_lines_arrive_and_returns_the_result()
    {
        var progress = new List<PipelineProgress>();
        _runner.Lines = OkRun;

        var result = await Preparation().RunAsync(new SyncProgress<PipelineProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(PipelineOutcome.Ready, result.Outcome);
        Assert.Equal([new PipelineProgress(PipelineStage.Modules, 2, 2), new PipelineProgress(PipelineStage.Pipelines, 3, 3)], progress);
    }

    [Fact]
    public async Task With_mods_in_use_the_run_plans_from_the_update_folder_play_uses()
    {
        var overlay = Path.Combine(_layout.CacheDirectory, "mod-update");
        _runner.Lines = OkRun;

        await new PipelinePreparation(_layout, _runner, overlay).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Contains("--update_data_root=" + overlay, _runner.Started!.Arguments);
        Assert.DoesNotContain("--update_data_root=" + _layout.UpdateDataDirectory, _runner.Started.Arguments);
    }

    private string Overlay => Path.Combine(_layout.CacheDirectory, "mod-update");

    private string Tu2Folder()
    {
        Directory.CreateDirectory(_layout.UpdateDataDirectory);
        File.WriteAllBytes(Path.Combine(_layout.UpdateDataDirectory, "CHR_101_A.xxx"), [1]);
        return _layout.UpdateDataDirectory;
    }

    private string OverlayFolder(byte[] package)
    {
        Directory.CreateDirectory(Overlay);
        File.WriteAllBytes(Path.Combine(Overlay, "CHR_101_A.xxx"), package);
        return Overlay;
    }

    [Fact]
    public async Task Prepared_on_TU2s_folder_a_play_with_mods_rebuilds()
    {
        Tu2Folder();
        await Prepare();
        var overlay = OverlayFolder([2]);

        Assert.Equal(PipelineRebuild.UpdateFolderChanged, new PipelinePreparation(_layout, _runner, overlay).Check(true));
        Assert.True(PipelineRebuild.UpdateFolderChanged.Rebuilds());
    }

    [Fact]
    public async Task Prepared_on_the_mods_folder_other_mods_rebuild()
    {
        var overlay = OverlayFolder([2]);
        _runner.Lines = OkRun;
        await new PipelinePreparation(_layout, _runner, overlay).RunAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineRebuild.None, new PipelinePreparation(_layout, _runner, overlay).Check(true));

        OverlayFolder([3, 3]);

        Assert.Equal(PipelineRebuild.UpdateFolderChanged, new PipelinePreparation(_layout, _runner, overlay).Check(true));
    }

    [Fact]
    public async Task Prepared_on_the_mods_folder_a_play_without_mods_rebuilds()
    {
        Tu2Folder();
        _runner.Lines = OkRun;
        await new PipelinePreparation(_layout, _runner, OverlayFolder([2])).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(PipelineRebuild.UpdateFolderChanged, Check());
    }

    [Fact]
    public async Task A_record_from_before_the_update_folder_was_kept_counts_as_TU2s_folder()
    {
        Tu2Folder();
        await Prepare();
        var record = Path.Combine(Store, "launcher-inputs");
        File.WriteAllLines(record, File.ReadAllLines(record).Where(line => !line.StartsWith("update=", StringComparison.Ordinal)));

        Assert.Equal(PipelineRebuild.None, Check());
        Assert.Equal(PipelineRebuild.UpdateFolderChanged, new PipelinePreparation(_layout, _runner, OverlayFolder([2])).Check(true));
    }

    [Fact]
    public async Task The_run_starts_the_game_in_prepare_mode_with_the_layouts_folders()
    {
        await Prepare();

        var start = _runner.Started!;
        Assert.Equal(_layout.GameExecutable, start.Executable);
        Assert.Contains("--wwe_prepare_pipelines=true", start.Arguments);
        Assert.Contains("--gpu_plugin=xenos", start.Arguments);
        Assert.Contains("--cache_root=" + _layout.CacheDirectory, start.Arguments);
        Assert.Contains("--user_data_root=" + _layout.UserDataDirectory, start.Arguments);
        Assert.Contains("--game_data_root=" + _layout.GameDataDirectory, start.Arguments);
        Assert.Contains("--fullscreen=false", start.Arguments);
        // Built for the render target path Play uses, or the session's pipelines would differ.
        var play = LaunchArguments.Build(_layout, "x.log").Single(a => a.StartsWith("--render_target_path_vulkan=", StringComparison.Ordinal));
        Assert.Contains(play, start.Arguments);
    }

    [Fact]
    public async Task A_game_that_does_not_start_is_reported_not_thrown()
    {
        _runner.StartFailure = new LauncherException("The game exe is missing: x");

        var result = await Prepare();

        Assert.Equal(PipelineOutcome.Crashed, result.Outcome);
        Assert.Contains("The game exe is missing", result.Message);
    }

    /// <summary>Prints the given lines; on an ok or partial result it writes a store like the game.</summary>
    private sealed class FakeRunner : IPrepareRunner
    {
        public string[] Lines { get; set; } = [];
        public ProcessStart? Started { get; private set; }
        public LauncherException? StartFailure { get; set; }

        public Task<int> RunAsync(ProcessStart start, Action<string> onLine, CancellationToken cancel)
        {
            Started = start;
            if (StartFailure is not null) throw StartFailure;
            foreach (var line in Lines) onLine(line);
            var result = Lines.LastOrDefault(line => line.StartsWith("result ", StringComparison.Ordinal)) ?? "";
            var code = result.Contains("status=ok") ? 0 : result.Contains("status=partial") ? 3 : result.Contains("status=failed") ? 2 : 1;
            if (code is 0 or 3)
            {
                var cacheRoot = start.Arguments.Single(a => a.StartsWith("--cache_root=", StringComparison.Ordinal))["--cache_root=".Length..];
                var store = Path.Combine(cacheRoot, PipelinePreparation.StoreFolderName);
                Directory.CreateDirectory(store);
                File.WriteAllBytes(Path.Combine(store, "keys.bin"), [1]);
                File.WriteAllBytes(Path.Combine(store, "driver-cache.bin"), [2]);
                File.Delete(Path.Combine(store, "pipelines-stale"));
            }
            return Task.FromResult(code);
        }
    }
}
