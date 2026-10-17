using System.Globalization;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// The portable folder layout (spec, "Folder layout"): the launcher's folder holds
/// <c>game</c> (the game exe and its DLLs), <c>gamedata</c> (the extracted disc),
/// <c>updatedata</c> (Title Update 2, always next to <c>gamedata</c>),
/// <c>userdata</c>, <c>mods</c>, <c>cache</c> and <c>logs</c>. The game data folder and the game exe
/// can be put elsewhere; the other folders stay beside the launcher.
/// </summary>
public sealed class PortableLayout
{
    public PortableLayout(string root, string? gameDataDirectory = null, string? gameExecutable = null)
    {
        Root = Path.GetFullPath(root);
        GameDataDirectory = Path.GetFullPath(gameDataDirectory ?? Path.Combine(Root, "gamedata"));
        GameExecutable = Path.GetFullPath(gameExecutable ?? Path.Combine(Root, "game", GameExecutableName));
    }

    public const string GameExecutableName = "wweallstars.exe";

    public string Root { get; }
    public string GameExecutable { get; }
    public string GameDirectory => Path.GetDirectoryName(GameExecutable)!;
    public string GameDataDirectory { get; }
    /// <summary>Title Update 2's files, next to the game data folder; the game mounts them as <c>update:</c>.</summary>
    public string UpdateDataDirectory => TitleUpdateSetup.UpdateDataDirectory(GameDataDirectory);
    public string UserDataDirectory => Path.Combine(Root, "userdata");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string LogsDirectory => Path.Combine(Root, "logs");
    /// <summary>The player's mods and their load order (modding spec, "Location").</summary>
    public string ModsDirectory => Path.Combine(Root, "mods");

    /// <summary>XenosRecomp with its DLLs and <c>shader_common.h</c>, shipped in the release zip.</summary>
    public string ShaderConverterDirectory => Path.Combine(Root, "xenosrecomp");

    /// <summary>
    /// The PC SETTINGS file the game reads at startup and its in-game page writes: in the user
    /// data folder (<c>src/settings/config_file.h</c>), so replacing the game folder keeps it.
    /// </summary>
    public string SettingsFile => Path.Combine(UserDataDirectory, "wweallstars.settings.toml");

    /// <summary>
    /// The log of a session started at <paramref name="now"/>: <c>logs/yyyyMMdd-HHmmss/runtime.log</c>,
    /// with <c>-2</c>, <c>-3</c>... when that folder exists already. Creates nothing.
    /// </summary>
    public string NewSessionLogPath(DateTimeOffset now)
    {
        var name = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var folder = Path.Combine(LogsDirectory, name);
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(LogsDirectory, $"{name}-{n}");
        return Path.Combine(folder, SessionLogName);
    }

    /// <summary>The newest session's log (the order <see cref="NewSessionLogPath"/> names them in), or null.</summary>
    public string? LatestSessionLogPath()
    {
        if (!Directory.Exists(LogsDirectory)) return null;
        string? latest = null;
        (string Stamp, int Number) newest = default;
        foreach (var folder in Directory.EnumerateDirectories(LogsDirectory))
        {
            if (!TrySessionOrder(Path.GetFileName(folder), out var order)) continue;
            var log = Path.Combine(folder, SessionLogName);
            if (!File.Exists(log)) continue;
            if (latest is null || string.CompareOrdinal(order.Stamp, newest.Stamp) > 0 ||
                (order.Stamp == newest.Stamp && order.Number > newest.Number))
            {
                latest = log;
                newest = order;
            }
        }
        return latest;
    }

    /// <summary>What "Open logs" opens: the latest session's folder, or <c>logs</c> (created) before the first session.</summary>
    public string LogsFolderToOpen()
    {
        var latest = LatestSessionLogPath();
        if (latest is not null) return Path.GetDirectoryName(latest)!;
        Directory.CreateDirectory(LogsDirectory);
        return LogsDirectory;
    }

    private const string SessionLogName = "runtime.log";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    // "yyyyMMdd-HHmmss" is session 1 of that second; "yyyyMMdd-HHmmss-n" is session n.
    private static bool TrySessionOrder(string name, out (string Stamp, int Number) order)
    {
        order = default;
        if (name.Length < StampFormat.Length) return false;
        var stamp = name[..StampFormat.Length];
        if (!DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        var number = 1;
        if (name.Length > stamp.Length &&
            (name[stamp.Length] != '-' || !int.TryParse(name.AsSpan(stamp.Length + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out number)))
            return false;
        order = (stamp, number);
        return true;
    }
}
