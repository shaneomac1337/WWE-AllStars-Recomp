using System.ComponentModel;
using System.Diagnostics;

namespace WweAllStars.Launcher.Core;

/// <summary>One process start: the exe, its folder and its arguments, unquoted.</summary>
public sealed record ProcessStart(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments);

/// <summary>A game process the launcher watches.</summary>
public interface IRunningGame
{
    /// <summary>Completes with the process exit code when the game exits.</summary>
    Task<int> ExitCode { get; }
}

/// <summary>Starts and finds game processes; tests replace it with a fake.</summary>
public interface IProcessStarter
{
    IRunningGame Start(ProcessStart start);

    /// <summary>A copy of <paramref name="executable"/> that is already running (started by an earlier launcher, say), or null.</summary>
    IRunningGame? FindRunning(string executable);
}

/// <summary>
/// Starts the game as an independent process: no redirected streams and no job object, so the
/// game keeps running when the launcher closes.
/// </summary>
public sealed class DetachedProcessStarter : IProcessStarter
{
    public IRunningGame Start(ProcessStart start)
    {
        var info = new ProcessStartInfo(start.Executable)
        {
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info)
            ?? throw new LauncherException($"The game did not start: {start.Executable}");
        return new WatchedProcess(process);
    }

    public IRunningGame? FindRunning(string executable)
    {
        var wanted = Path.GetFullPath(executable);
        IRunningGame? found = null;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (found is null && path is not null &&
                    string.Equals(Path.GetFullPath(path), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    // Hold a handle now: for a process this launcher did not start, Windows
                    // forgets the exit code once the last handle to it closes.
                    _ = process.SafeHandle;
                    found = new WatchedProcess(process);
                    continue;
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                // It exited meanwhile, or belongs to another user: not ours to watch.
            }
            process.Dispose();
        }
        return found;
    }

    /// <summary>Watches only: the launcher never kills the game.</summary>
    private sealed class WatchedProcess(Process process) : IRunningGame
    {
        public Task<int> ExitCode { get; } = WaitAsync(process);

        private static async Task<int> WaitAsync(Process process)
        {
            using (process)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
        }
    }
}

/// <summary>A failure the launcher reports to the player in plain words.</summary>
public sealed class LauncherException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What one Play started; <see cref="Exited"/> completes when the game exits.</summary>
public sealed record GameSession(string? LogPath, Task<GameExitReport> Exited);

/// <summary>
/// Play: starts the game with the layout's folders and a new session log, then watches it.
/// One game at a time, so two copies never write the same saves.
/// </summary>
public sealed class GameLauncher(PortableLayout layout, IProcessStarter starter, Func<DateTimeOffset> clock)
{
    private readonly Lock _lock = new();
    private IRunningGame? _running;

    public bool IsRunning
    {
        get { lock (_lock) return _running is not null; }
    }

    /// <summary>The status line's running state.</summary>
    public string Status => IsRunning ? "Game running" : "Ready";

    /// <summary>The session folder's copy of the mods' load report, beside <c>runtime.log</c>, which the game writes.</summary>
    public const string ModReportFileName = "mods.log";

    /// <param name="mods">
    /// The mod library's prepare-for-Play result: the game starts on its update folder, and its
    /// load report goes in the session folder. Null starts on TU2's folder.
    /// </param>
    public GameSession Play(PreparedMods? mods = null)
    {
        IRunningGame game;
        string logPath;
        lock (_lock)
        {
            if (_running is not null) throw new LauncherException("The game is already running.");
            if (!File.Exists(layout.GameExecutable))
                throw new LauncherException($"The game is missing: {layout.GameExecutable} was not found.");
            logPath = layout.NewSessionLogPath(clock());
            var sessionFolder = Path.GetDirectoryName(logPath)!;
            Directory.CreateDirectory(sessionFolder);
            try
            {
                if (mods is not null) File.WriteAllLines(Path.Combine(sessionFolder, ModReportFileName), mods.Report.Lines());
                game = starter.Start(new ProcessStart(layout.GameExecutable, layout.GameDirectory,
                    LaunchArguments.Build(layout, logPath, mods?.UpdateDataRoot)));
            }
            catch (Exception error) when (error is not LauncherException)
            {
                throw new LauncherException($"The game did not start: {error.Message}", error);
            }
            _running = game;
        }
        return new GameSession(logPath, Watch(game, logPath));
    }

    /// <summary>
    /// Watches a copy of the game that is already running (started before this launcher opened),
    /// so Play stays refused until it exits. Its log is taken to be the latest session's. Null if
    /// none runs.
    /// </summary>
    public GameSession? AttachToRunning()
    {
        IRunningGame? game;
        lock (_lock)
        {
            if (_running is not null) return null;
            game = starter.FindRunning(layout.GameExecutable);
            if (game is null) return null;
            _running = game;
        }
        var logPath = layout.LatestSessionLogPath();
        return new GameSession(logPath, Watch(game, logPath));
    }

    private async Task<GameExitReport> Watch(IRunningGame game, string? logPath)
    {
        try
        {
            return GameExitReport.FromExit(await game.ExitCode.ConfigureAwait(false), logPath);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_running, game)) _running = null;
            }
        }
    }
}
