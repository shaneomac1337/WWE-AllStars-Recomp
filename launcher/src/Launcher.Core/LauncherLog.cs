namespace WweAllStars.Launcher.Core;

/// <summary>
/// The launcher's own log, <c>logs/launcher.log</c> (GitHub issue #1: a launcher that froze on
/// a picked disc image wrote nothing). Each line is opened, written and closed at once, so the
/// last line of a frozen run shows the step it stopped at. A new start moves the last run to
/// <c>launcher.previous.log</c>: the player restarts to find the log, and the frozen run survives.
/// Logging never throws.
/// </summary>
public sealed class LauncherLog
{
    public const string FileName = "launcher.log";
    public const string PreviousFileName = "launcher.previous.log";

    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();

    private LauncherLog(string? path, Func<DateTimeOffset> clock)
    {
        _path = path;
        _clock = clock;
    }

    /// <summary>A log that writes nothing (the designer previewer, tools).</summary>
    public static LauncherLog Off { get; } = new(null, () => DateTimeOffset.MinValue);

    /// <summary>The file this log writes, or null when it writes nothing.</summary>
    public string? FilePath => _path;

    public static string PathIn(PortableLayout layout) => Path.Combine(layout.LogsDirectory, FileName);

    public static LauncherLog Start(PortableLayout layout, Func<DateTimeOffset> clock)
    {
        var path = PathIn(layout);
        try
        {
            Directory.CreateDirectory(layout.LogsDirectory);
            if (File.Exists(path)) File.Move(path, Path.Combine(layout.LogsDirectory, PreviousFileName), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new LauncherLog(null, clock);
        }
        return new LauncherLog(path, clock);
    }

    public void Write(string message)
    {
        if (_path is null) return;
        var line = $"{_clock():yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
