using Avalonia;
using WweAllStars.Launcher.Core;
using WweAllStars.Launcher.Platform;

namespace WweAllStars.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LauncherOptions options;
        try
        {
            options = LauncherOptions.Parse(args, AppContext.BaseDirectory) with
            {
                FileLinker = FilePlatform.Linker,
                RecycleBin = FilePlatform.RecycleBin,
            };
        }
        catch (LauncherException error)
        {
            HostConsole.Current.Attach();
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        if (options.Headless)
        {
            HostConsole.Current.Attach();
            try
            {
                return HeadlessCommands.Run(options, Console.Out, () => DateTimeOffset.Now);
            }
            catch (LauncherException error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        App.Options = options;
        App.Log = LauncherLog.Start(options.Layout, () => DateTimeOffset.Now);
        App.Log.Write($"Launcher started: {Environment.OSVersion}, arguments: {string.Join(' ', args)}");
        LauncherCrashHandler.Install();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
