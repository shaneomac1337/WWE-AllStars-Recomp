// Renders the launcher's screens to PNG with Avalonia's headless platform: no window opens and
// the game is never started.
//
//   dotnet run --project tools/Launcher.Screenshots -- <output folder> [<nxeart>]
//
// Writes welcome, upgrade (0.9.0 install without Title Update 2), home, settings, keys (normal,
// a key another input has, a reserved key), dlc and about at
// 100 % and 200 % scaling. With an nxeart (the
// developer's own, e.g. assets/nxeart) the home screen shows its key art; without one it shows
// the typographic fallback. The output holds game art then, so keep it in ignored out/.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WweAllStars.Launcher;
using WweAllStars.Launcher.Core;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: Launcher.Screenshots <output folder> [<nxeart>]");
    return 2;
}
var output = Path.GetFullPath(args[0]);
var nxeart = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
Directory.CreateDirectory(output);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var scratch = Directory.CreateTempSubdirectory("wwe-launcher-screens-").FullName;
try
{
    // Welcome: a portable folder with nothing set up yet.
    Capture(NewRoot("welcome", setUp: false), "welcome", _ => { });

    // Upgrade: game data set up by 0.9.0, no Title Update 2 yet.
    Capture(NewRoot("upgrade", setUp: true, titleUpdate: false), "upgrade", _ => { });

    // Home and its panels: a set-up game data folder holding only the marker and the art.
    var home = NewRoot("home", setUp: true);
    Capture(home, "home", _ => { });
    Capture(home, "settings", window => Click(window, "SettingsButton"));
    // Settings scrolled to the Keyboard row and its Keys button.
    Capture(home, "settings-keyboard", window =>
    {
        Click(window, "SettingsButton");
        Dispatcher.UIThread.RunJobs();
        Named(window, "Change the keyboard's keys").BringIntoView();
        Dispatcher.UIThread.RunJobs();
    });
    Capture(home, "keys", OpenKeys);
    // A starts waiting for a key; J is X's, so the page asks whether to move it.
    Capture(home, "keys-move", window =>
    {
        OpenKeys(window);
        ClickNamed(window, "A key");
        PressKey(window, Key.J, PhysicalKey.J);
    });
    Capture(home, "keys-reserved", window =>
    {
        OpenKeys(window);
        ClickNamed(window, "B key");
        PressKey(window, Key.F4, PhysicalKey.F4);
    });
    Capture(home, "about", window => Click(window, "AboutButton"));
    // The DLC page with a stand-in Pack 1 (an empty folder named like it: no game data).
    var pack = Path.Combine(new DlcLibrary(new PortableLayout(home).UserDataDirectory).ContentDirectory, DlcLibrary.Pack1FileName, "Phase1");
    Directory.CreateDirectory(pack);
    // Sized like the real Pack 1 (207,433,728 bytes) without writing its bytes.
    using (var standIn = File.Create(Path.Combine(pack, "DLCTOC.txt"))) standIn.SetLength(207_433_728);
    Capture(home, "dlc", window => Click(window, "DlcButton"));
    return 0;
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

string NewRoot(string name, bool setUp, bool titleUpdate = true)
{
    var layout = new PortableLayout(Path.Combine(scratch, name));
    // No update check: screenshots never touch the network.
    LauncherPreferences.Load(layout).With(checkForUpdates: false).Save(layout);
    if (setUp)
    {
        Directory.CreateDirectory(layout.GameDataDirectory);
        SetupMarker.Write(layout.GameDataDirectory, DiscManifest.Supported.Version);
        if (nxeart is not null) File.Copy(nxeart, Path.Combine(layout.GameDataDirectory, KeyArt.PackageFile));
        WriteStandInShaderPack(layout.CacheDirectory);
        if (titleUpdate)
        {
            Directory.CreateDirectory(layout.UpdateDataDirectory);
            File.WriteAllText(Path.Combine(layout.UpdateDataDirectory, TitleUpdateSetup.MarkerFileName),
                "manifest_version=" + TitleUpdateManifest.Supported.Version + "\n");
        }
    }
    return layout.Root;
}

void Capture(string root, string name, Action<MainWindow> prepare)
{
    App.Options = LauncherOptions.Parse([], root);
    var window = new MainWindow();
    window.Show();
    // The key art is decoded on a pool thread; wait for it (or for the fallback) to settle.
    var keyArt = window.FindControl<Control>("KeyArtLayer")!;
    for (var i = 0; i < 100 && nxeart is not null && name != "welcome" && !keyArt.IsVisible; i++)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(20);
    }
    prepare(window);
    // A render target draws the focus outline at the wrong place; screenshots show none.
    window.FocusManager?.ClearFocus();
    Dispatcher.UIThread.RunJobs();
    foreach (var scale in new[] { 1, 2 })
    {
        var size = new PixelSize((int)(window.Width * scale), (int)(window.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        var path = Path.Combine(output, $"{name}-{scale * 100}.png");
        bitmap.Save(path);
        Console.WriteLine(path);
    }
    window.Close();
    Dispatcher.UIThread.RunJobs();
}

// A shader pack the launcher takes as prepared: a valid header for this converter and one
// empty entry, no shader code. The home screen then shows a finished install.
static void WriteStandInShaderPack(string cacheDirectory)
{
    const int header = 32, entry = 24;
    var pack = new byte[header + entry];
    "WWSP"u8.CopyTo(pack);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(4), ShaderPackFile.FormatVersion);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(pack.AsSpan(8), ShaderConverter.Id);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pack.AsSpan(16), 1);
    Directory.CreateDirectory(cacheDirectory);
    File.WriteAllBytes(Path.Combine(cacheDirectory, ShaderPackFile.FileName), pack);
}

static void Click(Window window, string button) =>
    window.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

static Button Named(Window window, string name) =>
    window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

static void ClickNamed(Window window, string name) => Named(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

static void OpenKeys(MainWindow window)
{
    Click(window, "SettingsButton");
    Dispatcher.UIThread.RunJobs();
    ClickNamed(window, "Change the keyboard's keys");
    Dispatcher.UIThread.RunJobs();
}

static void PressKey(Window window, Key key, PhysicalKey physical) =>
    window.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physical, Source = window,
    });
