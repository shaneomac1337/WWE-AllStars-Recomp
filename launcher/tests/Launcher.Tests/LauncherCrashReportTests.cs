using System.IO.Compression;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class LauncherCrashReportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-launcher-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 23, 59, 7, 0, TimeSpan.Zero);
    private static readonly LauncherCrashFacts Facts = new("1.3.0-beta", "Microsoft Windows NT 10.0.26200.0", "Intel(R) UHD Graphics 620");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PortableLayout Layout => new(_root);
    private string LogsDirectory => Path.Combine(_root, "logs");

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("debug crash"); }
        catch (Exception error) { return error; }
    }

    private static string Entry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_report_holds_the_launcher_log_and_a_manifest_with_the_exception()
    {
        var log = LauncherLog.Start(Layout, () => Now);
        log.Write("Opening the disc image picker");

        var path = LauncherCrashReport.Write(Layout, log, Thrown(), Facts, Now);

        Assert.Equal(Path.Combine(LogsDirectory, "crash-20261016-235907-launcher.zip"), path);
        using var zip = ZipFile.OpenRead(path!);
        Assert.Contains("Opening the disc image picker", Entry(zip, "launcher.log"));
        var manifest = Entry(zip, "manifest.txt");
        Assert.Contains("reason: launcher exception", manifest);
        Assert.Contains("version: 1.3.0-beta", manifest);
        Assert.Contains("os: Microsoft Windows NT 10.0.26200.0", manifest);
        Assert.Contains("gpu: Intel(R) UHD Graphics 620", manifest);
        Assert.Contains("System.InvalidOperationException: debug crash", manifest);
        Assert.Contains(nameof(Thrown), manifest);
    }

    [Fact]
    public void The_next_start_lists_the_launcher_report_with_the_game_reports()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(LogsDirectory).FullName, "crash-20261016-120000.zip"), "");
        var path = LauncherCrashReport.Write(Layout, LauncherLog.Start(Layout, () => Now), Thrown(), Facts, Now);

        Assert.Equal(path, new CrashReports(LogsDirectory).TakeUnseen()[0]);
    }

    [Fact]
    public void A_second_crash_in_the_same_second_keeps_both_reports()
    {
        var log = LauncherLog.Start(Layout, () => Now);

        var first = LauncherCrashReport.Write(Layout, log, Thrown(), Facts, Now);
        var second = LauncherCrashReport.Write(Layout, log, Thrown(), Facts, Now);

        Assert.Equal(Path.Combine(LogsDirectory, "crash-20261016-235907-launcher.zip"), first);
        Assert.Equal(Path.Combine(LogsDirectory, "crash-20261016-235907-launcher-2.zip"), second);
        using (ZipFile.OpenRead(first!)) { }
        using (ZipFile.OpenRead(second!)) { }
    }

    [Fact]
    public void A_report_that_cannot_be_written_never_loses_the_launcher_log()
    {
        var log = LauncherLog.Start(Layout, () => Now);
        log.Write("before the crash");
        // A dump another handle holds exclusively: the report cannot be written.
        var dump = Path.Combine(LogsDirectory, "launcher-1.dmp.tmp");
        string? path;
        using (new FileStream(dump, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            path = LauncherCrashReport.Write(Layout, log, Thrown(), Facts, Now, dump);
        }

        Assert.Null(path);
        Assert.Empty(Directory.EnumerateFiles(LogsDirectory, "crash-*.zip"));
        var text = File.ReadAllText(Path.Combine(LogsDirectory, "launcher.log"));
        Assert.Contains("before the crash", text);
        Assert.Contains("System.InvalidOperationException: debug crash", text);
    }
}
