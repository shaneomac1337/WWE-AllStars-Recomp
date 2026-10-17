using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// The crash reports the game saves in the logs folder (<c>logs/crash-yyyyMMdd-HHmmss.zip</c>,
/// src/crash_report_plan.h). The launcher lists the ones the player hasn't seen at its start;
/// a report counts as seen once it is listed, or linked in an exit message. Only the last
/// <see cref="Keep"/> are kept.
/// </summary>
public sealed class CrashReports(string logsDirectory)
{
    public const int Keep = 10;

    private string SeenFile => Path.Combine(logsDirectory, "crash-reports-seen.txt");

    /// <summary>
    /// Deletes all but the newest <see cref="Keep"/> reports, then returns the unseen ones,
    /// newest first, and marks them seen.
    /// </summary>
    public IReadOnlyList<string> TakeUnseen()
    {
        if (!Directory.Exists(logsDirectory)) return [];
        var reports = Directory.EnumerateFiles(logsDirectory, "crash-*.zip")
            .OrderByDescending(path => SortKey(Path.GetFileName(path))).ToList();
        foreach (var old in reports.Skip(Keep))
        {
            try { File.Delete(old); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var seen = ReadSeen();
        var unseen = reports.Take(Keep).Where(path => !seen.Contains(Path.GetFileName(path))).ToList();
        WriteSeen(reports.Take(Keep).Select(Path.GetFileName).OfType<string>());
        return unseen;
    }

    // The stamp sorts by time; a second report of the same second is "<stamp>-2", then "-3" and
    // on (src/crash_report.cpp, LauncherCrashReport), sorted by its number, not as text.
    private static readonly Regex NumberedName = new(@"^(?<base>crash-\d{8}-\d{6}(-launcher)?)-(?<number>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (string Base, int Number) SortKey(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        var match = NumberedName.Match(stem);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? (match.Groups["base"].Value, number)
            : (stem, 1);
    }

    /// <summary>Marks one report seen, e.g. the one an exit message linked.</summary>
    public void MarkSeen(string reportPath)
    {
        var seen = ReadSeen();
        seen.Add(Path.GetFileName(reportPath));
        WriteSeen(seen);
    }

    private HashSet<string> ReadSeen()
    {
        try
        {
            return File.Exists(SeenFile)
                ? File.ReadAllLines(SeenFile).Where(line => line.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteSeen(IEnumerable<string> names)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            File.WriteAllLines(SeenFile, names.Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
