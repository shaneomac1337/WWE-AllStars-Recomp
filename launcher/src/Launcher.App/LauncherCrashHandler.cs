using System.Reflection;
using Avalonia.Threading;
using WweAllStars.Launcher.Core;
using WweAllStars.Launcher.Platform;

namespace WweAllStars.Launcher;

/// <summary>
/// Catches the window launcher's unhandled exceptions (UI thread, other threads, unobserved
/// tasks) and saves a <see cref="LauncherCrashReport"/>: one for the crash that ends the run,
/// and one for the first unobserved task, which does not end it, so it never uses up the
/// crash's report. A frozen launcher reports nothing; its <see cref="LauncherLog"/> shows the
/// step instead.
/// </summary>
internal static class LauncherCrashHandler
{
    private static int _reportedCrash;
    private static int _reportedTask;

    /// <summary>The GPU names, once the settings page asked the game for them.</summary>
    internal static string? KnownGpu { get; set; }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Report(error, ref _reportedCrash);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, ref _reportedTask);
            e.SetObserved();
        };
    }

    /// <summary>After Avalonia started: exceptions on the UI thread. They still end the launcher.</summary>
    public static void InstallOnUiThread() =>
        Dispatcher.UIThread.UnhandledException += (_, e) => Report(e.Exception, ref _reportedCrash);

    /// <summary>The <c>--debug-crash</c> switches.</summary>
    public static void ThrowIfAsked(LauncherDebugCrash crash)
    {
        switch (crash)
        {
            case LauncherDebugCrash.UiThread:
                Dispatcher.UIThread.Post(() => throw new InvalidOperationException("--debug-crash: launcher UI thread"),
                    DispatcherPriority.Background);
                break;
            case LauncherDebugCrash.BackgroundTask:
                _ = Task.Run(() => throw new InvalidOperationException("--debug-crash-task: launcher background task"));
                // An unobserved task reports when the finalizer sees it.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                });
                break;
        }
    }

    private static void Report(Exception error, ref int reported)
    {
        if (Interlocked.Exchange(ref reported, 1) != 0)
        {
            App.Log.Write($"Unhandled launcher exception (already reported): {error}");
            return;
        }
        try
        {
            var layout = App.Options.Layout;
            var dump = MiniDump.TryWrite(layout.LogsDirectory);
            LauncherCrashReport.Write(layout, App.Log, error, Facts(), DateTimeOffset.Now, dump);
        }
        catch (Exception failure)
        {
            App.Log.Write($"crash report: failed: {failure.Message}");
        }
    }

    private static LauncherCrashFacts Facts()
    {
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
        return new LauncherCrashFacts(ReleaseVersion.Describe(version), Environment.OSVersion.VersionString, KnownGpu);
    }
}
