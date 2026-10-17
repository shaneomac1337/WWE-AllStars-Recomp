using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class LauncherLogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-launcher-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 23, 41, 5, 120, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PortableLayout Layout => new(_root);
    private string LogPath => Path.Combine(_root, "logs", "launcher.log");

    [Fact]
    public void Each_line_is_on_disk_the_moment_it_is_written()
    {
        var log = LauncherLog.Start(Layout, () => Now);

        log.Write("Opening the disc image picker");

        // Read while the log is still in use, as a frozen launcher would leave it.
        using var reader = new StreamReader(new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Equal("2026-10-16 23:41:05.120 Opening the disc image picker", reader.ReadToEnd().Split(Environment.NewLine)[^2]);
    }

    [Fact]
    public void A_new_start_keeps_the_previous_run_beside_it()
    {
        // The player whose launcher froze starts it again to find the log: the frozen run must survive.
        LauncherLog.Start(Layout, () => Now).Write("The picker returned nothing");

        LauncherLog.Start(Layout, () => Now).Write("second run");

        Assert.Contains("The picker returned nothing", File.ReadAllText(Path.Combine(_root, "logs", "launcher.previous.log")));
        Assert.DoesNotContain("The picker returned nothing", File.ReadAllText(LogPath));
        Assert.Contains("second run", File.ReadAllText(LogPath));
    }

    [Fact]
    public void A_log_that_cannot_be_written_never_stops_the_launcher()
    {
        // logs is a file, so no log can be made there.
        File.WriteAllText(Path.Combine(_root, "logs"), "");

        var log = LauncherLog.Start(Layout, () => Now);
        log.Write("still running");
    }
}
