using System.Security.Cryptography;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class GameSetupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-setup-").FullName;
    private readonly TestDisc _disc = new();
    private readonly FakeDiskSpace _space = new();
    private readonly FakeTime _time = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Iso => Path.Combine(_root, "WWE All Stars.iso");
    private string GameData => Path.Combine(_root, "gamedata");

    private GameSetup Setup()
    {
        if (!File.Exists(Iso)) _disc.WriteImage(Iso);
        return new GameSetup(DiscManifest.Parse(_disc.ManifestText()), _space, _time);
    }

    private void Run(GameSetup setup, SetupStart start = SetupStart.Resume,
        IProgress<SetupProgress>? progress = null, CancellationToken? cancel = null) =>
        setup.Run(Iso, GameData, start, progress, cancel ?? TestContext.Current.CancellationToken);

    private SetupException SetupFails(GameSetup setup, SetupStart start = SetupStart.Resume,
        IProgress<SetupProgress>? progress = null, CancellationToken? cancel = null) =>
        Assert.Throws<SetupException>(() => Run(setup, start, progress, cancel));

    [Fact]
    public void A_file_that_is_not_an_xbox_image_is_refused_before_anything_is_written()
    {
        File.WriteAllBytes(Iso, TestDisc.Bytes(200_000, 9));

        var setup = Setup();
        var error = SetupFails(setup);

        Assert.Equal(SetupError.NotAnXboxImage, error.Error);
        Assert.Contains("not an Xbox 360 disc image", error.Message);
        Assert.Equal(SetupStatus.NeedsSetup, setup.StatusOf(GameData));
        Assert.False(Directory.Exists(GameSetup.PartialDirectory(GameData)));
    }

    [Fact]
    public void A_disc_whose_default_xex_differs_is_refused_as_the_wrong_version()
    {
        _disc.Files["default.xex"] = "XEX2 another region"u8.ToArray();
        _disc.ManifestOverrides["default.xex"] = "XEX2 supported one!"u8.ToArray();

        var error = SetupFails(Setup());

        Assert.Equal(SetupError.WrongDisc, error.Error);
        Assert.Contains("not the supported version", error.Message);
        Assert.False(Directory.Exists(GameSetup.PartialDirectory(GameData)));
    }

    [Fact]
    public void A_disc_with_a_different_file_list_is_refused_as_the_wrong_version()
    {
        _disc.Files["CookedXenon/Extra_FRA.xxx"] = TestDisc.Bytes(10, 5);
        _disc.WriteImage(Iso);
        _disc.Files.Remove("CookedXenon/Extra_FRA.xxx");

        var error = SetupFails(Setup());

        Assert.Equal(SetupError.WrongDisc, error.Error);
    }

    [Fact]
    public void Success_extracts_every_file_renames_the_folder_and_writes_the_marker()
    {
        var setup = Setup();

        Run(setup);

        foreach (var (path, data) in _disc.Files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(GameData, path)));
        Assert.False(Directory.Exists(GameSetup.PartialDirectory(GameData)));
        Assert.Equal("manifest_version=" + TestDisc.ManifestVersion + "\n",
            File.ReadAllText(Path.Combine(GameData, SetupMarker.FileName)));
        Assert.Equal(SetupStatus.Done, setup.StatusOf(GameData));
    }

    [Fact]
    public void The_disc_image_is_unchanged_byte_for_byte()
    {
        var setup = Setup();
        var before = SHA256.HashData(File.ReadAllBytes(Iso));
        var written = File.GetLastWriteTimeUtc(Iso);

        Run(setup);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Iso)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Iso));
    }

    [Fact]
    public void A_file_whose_hash_differs_is_reported_as_damaged_and_no_game_folder_appears()
    {
        _disc.ManifestOverrides["CookedXenon/Maps/Ring.xxx"] = TestDisc.Bytes(2048, 77);

        var error = SetupFails(Setup());

        Assert.Equal(SetupError.DamagedFile, error.Error);
        Assert.Contains("CookedXenon/Maps/Ring.xxx", error.Message);
        Assert.False(Directory.Exists(GameData));
        Assert.DoesNotContain(PartialFiles(), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.DoesNotContain("CookedXenon/Maps/Ring.xxx", PartialFiles());
    }

    [Fact]
    public void Not_enough_free_space_is_refused_before_extracting()
    {
        var setup = Setup();
        _space.Free = _disc.TotalBytes + GameSetup.SpareBytes - 1;

        var error = SetupFails(setup);

        Assert.Equal(SetupError.DiskFull, error.Error);
        Assert.Contains("Not enough disk space", error.Message);
        Assert.Equal([_root], _space.Asked);
        Assert.False(Directory.Exists(GameSetup.PartialDirectory(GameData)));
    }

    [Fact]
    public void A_game_data_folder_that_holds_other_files_is_refused()
    {
        Directory.CreateDirectory(GameData);
        File.WriteAllText(Path.Combine(GameData, "notes.txt"), "mine");

        var error = SetupFails(Setup());

        Assert.Equal(SetupError.GameDataFolderInUse, error.Error);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(GameData, "notes.txt")));
    }

    [Fact]
    public void Cancel_mid_extract_leaves_a_partial_folder_and_no_game_folder()
    {
        var setup = Setup();
        using var cancel = new CancellationTokenSource();
        var progress = new Reports(report =>
        {
            if (report.CurrentFile == "CookedXenon/Startup.xxx") cancel.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => Run(setup, progress: progress, cancel: cancel.Token));

        Assert.False(Directory.Exists(GameData));
        Assert.Equal(SetupStatus.Partial, setup.StatusOf(GameData));
        Assert.Contains("CookedXenon/Maps/Ring.xxx", PartialFiles());
        Assert.DoesNotContain("nxeart", PartialFiles());
        Assert.DoesNotContain(PartialFiles(), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Resume_keeps_verified_files_and_replaces_broken_and_stray_ones()
    {
        var setup = Setup();
        var partial = GameSetup.PartialDirectory(GameData);
        Directory.CreateDirectory(Path.Combine(partial, "CookedXenon"));
        var good = Path.Combine(partial, "Coalesced_INT.bin");
        File.WriteAllBytes(good, _disc.Files["Coalesced_INT.bin"]);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(good, old);
        File.WriteAllBytes(Path.Combine(partial, "CookedXenon", "Startup.xxx"), TestDisc.Bytes(3 * 2048 + 17, 99));
        File.WriteAllBytes(Path.Combine(partial, "nxeart.tmp"), [1, 2, 3]);

        Run(setup, SetupStart.Resume);

        Assert.Equal(old, File.GetLastWriteTimeUtc(Path.Combine(GameData, "Coalesced_INT.bin")));
        foreach (var (path, data) in _disc.Files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(GameData, path)));
        Assert.False(File.Exists(Path.Combine(GameData, "nxeart.tmp")));
        Assert.Equal(SetupStatus.Done, setup.StatusOf(GameData));
    }

    [Fact]
    public void Restart_throws_the_partial_folder_away()
    {
        var setup = Setup();
        var partial = GameSetup.PartialDirectory(GameData);
        Directory.CreateDirectory(partial);
        var good = Path.Combine(partial, "Coalesced_INT.bin");
        File.WriteAllBytes(good, _disc.Files["Coalesced_INT.bin"]);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(good, old);
        File.WriteAllText(Path.Combine(partial, "stray.txt"), "x");

        Run(setup, SetupStart.Restart);

        Assert.NotEqual(old, File.GetLastWriteTimeUtc(Path.Combine(GameData, "Coalesced_INT.bin")));
        Assert.False(File.Exists(Path.Combine(GameData, "stray.txt")));
        Assert.Equal(SetupStatus.Done, setup.StatusOf(GameData));
    }

    [Fact]
    public void The_game_folder_appears_only_once_complete()
    {
        var setup = Setup();
        var seenGameData = false;
        var progress = new Reports(report => seenGameData |= report.Step != SetupStep.Done && Directory.Exists(GameData));

        Run(setup, progress: progress);

        Assert.False(seenGameData);
        Assert.True(File.Exists(Path.Combine(GameData, SetupMarker.FileName)));
    }

    [Fact]
    public void Progress_reports_percent_current_file_and_time_left()
    {
        var setup = Setup();
        var progress = new Reports(_ => { });

        Run(setup, progress: progress);

        var extracting = progress.All.Where(report => report.Step == SetupStep.Extracting).ToList();
        Assert.NotEmpty(extracting);
        Assert.Contains(extracting, report => report.CurrentFile == "CookedXenon/Startup.xxx");
        Assert.All(extracting, report => Assert.Equal(_disc.TotalBytes, report.BytesTotal));
        Assert.Contains(extracting, report => report.TimeLeft is { } left && left > TimeSpan.Zero);
        Assert.Equal(extracting.Select(report => report.BytesDone).Order(), extracting.Select(report => report.BytesDone));
        var last = progress.All[^1];
        Assert.Equal(SetupStep.Done, last.Step);
        Assert.Equal(1.0, last.Fraction);
    }

    [Fact]
    public void Running_setup_on_a_finished_folder_changes_nothing()
    {
        var setup = Setup();
        Run(setup);
        var marker = Path.Combine(GameData, SetupMarker.FileName);
        var written = File.GetLastWriteTimeUtc(marker);

        Run(setup);

        Assert.Equal(written, File.GetLastWriteTimeUtc(marker));
        Assert.Equal(SetupStatus.Done, setup.StatusOf(GameData));
    }

    [Fact]
    public void A_marker_from_another_manifest_version_does_not_count_as_set_up()
    {
        Directory.CreateDirectory(GameData);
        SetupMarker.Write(GameData, "some-other-disc");

        Assert.Equal(SetupStatus.NeedsSetup, Setup().StatusOf(GameData));
    }

    [Fact]
    public void Verify_checks_the_disc_without_writing_anything()
    {
        var setup = Setup();

        setup.Verify(Iso);

        Assert.Equal([Iso], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Verify_names_each_step_before_it_starts_so_a_hang_shows_where()
    {
        var steps = new List<string>();

        Setup().Verify(Iso, steps.Add);

        Assert.Equal(["Opening the disc image", "Reading the disc's file list", "Hashing default.xex", "The disc is the supported one"],
            steps.Select(step => step.Split(':')[0]));
    }

    private IReadOnlyList<string> PartialFiles()
    {
        var partial = GameSetup.PartialDirectory(GameData);
        return Directory.Exists(partial)
            ? Directory.GetFiles(partial, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(partial, path).Replace('\\', '/')).ToList()
            : [];
    }
}
