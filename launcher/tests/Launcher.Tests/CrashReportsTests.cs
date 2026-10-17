using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class CrashReportsTests : IDisposable
{
    private readonly string _logs = Path.Combine(Directory.CreateTempSubdirectory("wwe-crash-").FullName, "logs");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_logs)!, recursive: true);

    private string Report(string stamp)
    {
        Directory.CreateDirectory(_logs);
        var path = Path.Combine(_logs, $"crash-{stamp}.zip");
        File.WriteAllText(path, "zip");
        return path;
    }

    [Fact]
    public void No_logs_folder_means_no_unseen_reports()
    {
        Assert.Empty(new CrashReports(_logs).TakeUnseen());
    }

    [Fact]
    public void Reports_not_yet_seen_are_listed_newest_first()
    {
        var older = Report("20261015-100000");
        var newer = Report("20261016-090000");
        Directory.CreateDirectory(Path.Combine(_logs, "20261016-085959"));

        Assert.Equal([newer, older], new CrashReports(_logs).TakeUnseen());
    }

    [Fact]
    public void Reports_of_the_same_second_are_listed_by_their_number_newest_first()
    {
        var first = Report("20261016-090000");
        var second = Report("20261016-090000-2");
        var tenth = Report("20261016-090000-10");
        var later = Report("20261016-090001");

        Assert.Equal([later, tenth, second, first], new CrashReports(_logs).TakeUnseen());
    }

    [Fact]
    public void A_report_marked_seen_is_not_listed()
    {
        var shown = Report("20261015-100000");
        var other = Report("20261016-090000");
        var reports = new CrashReports(_logs);

        reports.MarkSeen(shown);

        Assert.Equal([other], reports.TakeUnseen());
    }

    [Fact]
    public void Listed_reports_count_as_seen_at_the_next_start()
    {
        Report("20261015-100000");
        new CrashReports(_logs).TakeUnseen();
        var later = Report("20261016-090000");

        Assert.Equal([later], new CrashReports(_logs).TakeUnseen());
        Assert.Empty(new CrashReports(_logs).TakeUnseen());
    }

    [Fact]
    public void Only_the_last_10_reports_are_kept()
    {
        var all = Enumerable.Range(1, 12).Select(day => Report($"202610{day:00}-120000")).ToList();

        var listed = new CrashReports(_logs).TakeUnseen();

        Assert.Equal(10, listed.Count);
        Assert.False(File.Exists(all[0]));
        Assert.False(File.Exists(all[1]));
        Assert.All(all.Skip(2), path => Assert.True(File.Exists(path)));
        Assert.Equal(all.Skip(2).Reverse(), listed);
    }
}
