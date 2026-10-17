using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher;

public partial class App : Application
{
    /// <summary>The parsed command line; set by Program before Avalonia starts.</summary>
    internal static LauncherOptions Options { get; set; } =
        LauncherOptions.Parse([], AppContext.BaseDirectory);

    /// <summary>The launcher's own log (<c>logs/launcher.log</c>), started by Program; off in the previewer.</summary>
    internal static LauncherLog Log { get; set; } = LauncherLog.Off;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LauncherCrashHandler.InstallOnUiThread();
            desktop.MainWindow = new MainWindow();
            LauncherCrashHandler.ThrowIfAsked(Options.DebugCrash);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
