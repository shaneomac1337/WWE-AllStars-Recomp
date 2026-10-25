using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WweAllStars.Launcher.Core;
using WweAllStars.Launcher.Platform;

namespace WweAllStars.Launcher;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new();

    private readonly GameSetup _setup = new(DiscManifest.Supported, new DriveDiskSpace(), TimeProvider.System);
    private readonly TitleUpdateSetup _titleUpdate = new(App.Options.TitleUpdateManifest, new DriveDiskSpace(), TimeProvider.System);
    // The chosen Title Update 2: the intake (its temp folder holds an archive's contents) and the package in it.
    private PackageIntakeResult? _titleUpdateIntake;
    private string? _titleUpdatePackage;
    // Counts title update choices: a check that finishes after a newer choice is dropped.
    private int _titleUpdatePick;
    // DLC chosen on the setup screen, installed after Title Update 2.
    private PackageIntakeResult? _setupDlcIntake;
    // Counts setup DLC choices, as _titleUpdatePick does.
    private int _setupDlcPick;
    // Everything chosen on the setup screen's DLC zone: each pick or drop adds to it.
    private readonly List<string> _setupDlcInputs = [];
    private PortableLayout _layout = App.Options.Layout;
    private GameLauncher _game = NewGameLauncher(App.Options.Layout);
    private readonly UpdateChecker _updates =
        UpdateChecker.ForProject(Http, typeof(App).Assembly.GetName().Version ?? new Version(0, 0));
    private LauncherPreferences _preferences = LauncherPreferences.Load(App.Options.Layout);
    private AvailableUpdate? _update;
    private string? _imagePath;
    private CancellationTokenSource? _setupCancel;
    // Repair and Prepare shaders; they decide, with the game, whether Play is on.
    private readonly HomeJobs _jobs = new();

    // One row of the Settings panel: its setting, its choice box (or, for a text row, its text
    // box), its help and its note line.
    private sealed record SettingRow(SchemaSetting Setting, ComboBox? Choices, TextBox? Text, TextBlock Help,
                                     TextBlock Note)
    {
        public Control Input => Choices ?? (Control)Text!;
    }

    private readonly List<SettingRow> _rows = [];
    private const string GpuKey = "vulkan_device";
    private const string KeyboardKey = "mnk_mode";
    private SettingsFile? _settings;
    private bool _filling;
    // Online play was chosen without a valid saved name: it turns on once the name is saved.
    private bool _onlineAfterName;
    private readonly GameServerCheck _serverCheck = new(Http, GameServerCheck.DefaultTimeout);
    // The running server check, cancelled by a newer one, and the settings it checks.
    private CancellationTokenSource? _serverCheckCancel;
    private (string Online, string Server)? _serverCheckedFor;
    private string? _keyArtFor;
    private Button? _panelOpener;
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private IReadOnlyList<Gpu>? _gpus;  // null until the game has listed them
    private Task<IReadOnlyList<Gpu>>? _gpuQuery;
    private readonly ShaderPreparation _shaders = App.Options.NewShaderPreparation();
    private static readonly IPrepareRunner PrepareRunner = new StreamingPrepareRunner(HeadlessCommands.PipelineTimeout);
    // A prepare before Play failed: the next Play starts the game without trying again first.
    private bool _playWithoutPipelines;
    // Adding the mods' shaders before Play failed: the next Play starts the game with the pack as it is.
    private bool _playWithoutModShaders;
    // The window state before Play minimized it, given back when the game exits.
    private WindowState? _stateBeforeGame;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        ApplyEffects();
        VersionText.Text = "Version " + ProductVersion();
        NoticesText.Text = ThirdPartyNotices();
        TitleBar.PointerPressed += OnTitleBarPressed;
        SettingsButton.Click += (_, _) => ShowSettings();
        ResetSettingsButton.Click += OnResetSettings;
        AboutButton.Click += (_, _) => ShowPanel(AboutView, AboutButton);
        ModsButton.Click += (_, _) => ShowMods();
        CloseModsButton.Click += (_, _) => ClosePanel();
        OpenModsFolderButton.Click += OnOpenModsFolder;
        WireModInstall();
        WireKeys();
        DlcButton.Click += (_, _) => ShowDlc();
        CloseDlcButton.Click += (_, _) => ClosePanel();
        CancelDlcButton.Click += (_, _) => _jobs.Cancel(HomeJob.AddDlc);
        AddDlcButton.Click += async (_, _) => { if (await PickDlcFiles() is { Count: > 0 } paths) AddDlc(paths); };
        AddDlcFolderButton.Click += async (_, _) => { if (await PickDlcFolder() is { } path) AddDlc([path]); };
        DlcView.AddHandler(DragDrop.DragOverEvent, OnDragOverFileOrFolder);
        DlcView.AddHandler(DragDrop.DropEvent, (_, e) => { if (DroppedItems(e) is { Count: > 0 } paths) AddDlc(paths); });
        SetupDlcButton.Click += async (_, _) => { if (await PickDlcFiles() is { Count: > 0 } paths) ChooseSetupDlc(paths); };
        SetupDlcZone.AddHandler(DragDrop.DragOverEvent, OnDragOverFileOrFolder);
        SetupDlcZone.AddHandler(DragDrop.DropEvent, (_, e) => { if (DroppedItems(e) is { Count: > 0 } paths) ChooseSetupDlc(paths); });
        ExitButton.Click += (_, _) => Close();
        CloseSettingsButton.Click += (_, _) => ClosePanel();
        CloseAboutButton.Click += (_, _) => ClosePanel();
        PanelScrim.PointerPressed += (_, _) => ClosePanel();
        KeyDown += OnKeyDown;
        PositionChanged += (_, _) => RememberNormalBounds();
        Resized += (_, _) => RememberNormalBounds();
        Closing += (_, _) => SavePlacement();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        // Closing the launcher only closes the launcher: the game is its own process.
        CloseButton.Click += (_, _) => Close();
        Closing += (_, _) =>
        {
            _setupCancel?.Cancel();
            _titleUpdateIntake?.Dispose();
            _setupDlcPick++;
            _setupDlcIntake?.Dispose();
            _serverCheckCancel?.Cancel();
            _jobs.CancelAll();
        };
        PlayButton.Click += OnPlay;
        PrepareShadersButton.Click += OnPrepareShaders;
        SwitchToEmulatedButton.Click += OnSwitchToEmulated;
        UpdateLink.Click += OnOpenRelease;
        OnlineRetryButton.Click += (_, _) => CheckGameServer(force: true);
        OpenLogsButton.Click += OnOpenLogs;
        CrashReportButton.Click += OnOpenCrashReport;
        CreateShortcutsButton.IsVisible = ShortcutPlatform.Current is not null;
        CreateShortcutsButton.Click += OnCreateShortcuts;
        RepairButton.Click += OnRepair;
        CancelRepairButton.Click += (_, _) => _jobs.Cancel(HomeJob.Repair);
        CheckForUpdates.IsChecked = _preferences.CheckForUpdates;
        // No repository to ask yet (ADR-0014): the setting would do nothing.
        CheckForUpdates.IsVisible = UpdateNote.IsVisible = _updates.IsAvailable;
        CheckForUpdates.IsCheckedChanged += OnCheckForUpdatesChanged;
        ReduceEffects.IsChecked = _preferences.ReduceEffects;
        ReduceEffects.IsCheckedChanged += OnReduceEffectsChanged;
        ChooseImageButton.Click += OnChooseImage;
        ChooseTitleUpdateButton.Click += OnChooseTitleUpdate;
        ChooseTitleUpdateFolderButton.Click += OnChooseTitleUpdateFolder;
        TitleUpdateZone.AddHandler(DragDrop.DragOverEvent, OnDragOverFileOrFolder);
        TitleUpdateZone.AddHandler(DragDrop.DropEvent, OnDropTitleUpdate);
        TitleUpdateHelpText.Text = TitleUpdateSetup.WhatTitleUpdate2Is;
        ChooseFolderButton.Click += OnChooseFolder;
        StartSetupButton.Click += (_, _) => RunSetup(SetupStart.Resume);
        RestartSetupButton.Click += (_, _) => RunSetup(SetupStart.Restart);
        CancelSetupButton.Click += (_, _) => _setupCancel?.Cancel();
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        Activated += (_, _) => UpdateMotion();
        Deactivated += (_, _) => UpdateMotion();
        Opened += (_, _) =>
        {
            // A game started before this launcher opened still blocks Play.
            var running = _game.AttachToRunning();
            if (running is not null) Watch(running);
            // Left by a launcher closed while the game ran; the exe started on its own must not see it.
            else RemoveCombinedContent();
            ShowCurrentScreen();
            ShowUpdateWhenFound();
            ShowUnseenCrashReports();
        };
    }

    private static GameLauncher NewGameLauncher(PortableLayout layout) =>
        new(layout, new DetachedProcessStarter(), () => DateTimeOffset.Now);

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    /// <summary>
    /// An install whose disc files are set up but which lacks Title Update 2 (one finished by 0.9.0):
    /// setup shows only the "Add Title Update 2" step.
    /// </summary>
    private bool AddingTitleUpdateOnly =>
        _setup.StatusOf(_layout.GameDataDirectory) == SetupStatus.Done &&
        _titleUpdate.StatusOf(_layout.UpdateDataDirectory) != SetupStatus.Done;

    /// <summary>
    /// The window and taskbar icon: the title tile from the player's own disc, the same
    /// <c>userdata/icon.ico</c> the shortcuts use, made from <c>gamedata/nxeart</c> if missing. Never
    /// shipped: the exe keeps the project's star (art/icon), shown until setup is done.
    /// </summary>
    private void ShowTitleIcon()
    {
        try
        {
            var icon = Path.Combine(_layout.UserDataDirectory, TitleTile.IconFileName);
            if (!File.Exists(icon)) icon = TitleTile.WriteIcon(_layout);
            Icon = new WindowIcon(icon);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // No tile on this disc's art, or the folder is read-only: the star stays.
        }
    }

    /// <summary>Home once the game data and Title Update 2 are set up; otherwise the welcome and setup screen.</summary>
    private void ShowCurrentScreen()
    {
        var status = _setup.StatusOf(_layout.GameDataDirectory);
        var upgrade = AddingTitleUpdateOnly;
        var done = status == SetupStatus.Done && !upgrade;
        HomeView.IsVisible = done;
        SetupView.IsVisible = !done;
        if (done)
        {
            ShowKeyArt();
            ShowTitleIcon();
        }
        else
        {
            // Back on the welcome screen: the next home screen reads its folder's art again.
            _keyArtFor = null;
            KeyArtLayer.IsVisible = HomeTitle.IsVisible = false;
        }
        Backdrop.IsVisible = !KeyArtLayer.IsVisible;
        UpdateMotion();
        PlayButton.IsDefault = done;
        StartSetupButton.IsDefault = !done;
        if (done)
        {
            PlayButton.Focus();
            ShowShaderOffer();
            CheckGameServer(force: true);
            UpdateModsOnlineLine();
            return;
        }
        GameDataText.Text = _layout.GameDataDirectory;
        DropZone.IsVisible = GameFolderRow.IsVisible = !upgrade;
        SetupKicker.Text = upgrade ? "ADD TITLE UPDATE 2" : "FIRST-TIME SETUP";
        SetupIntroText.Text = upgrade
            ? "Your game files are set up. This version of the port is built on Title Update 2, so it needs that " +
              "update before it can start. Your saves stay where they are, and the disc image is not needed again."
            : "To play, you need a disc image (.iso) of your own WWE All Stars Xbox 360 disc and its Title Update 2. " +
              "Setup checks both, copies and verifies the game files, and never changes your disc image or update " +
              "file. Nothing of the game is included with the port.";
        var partial = status == SetupStatus.Partial ||
                      (upgrade && _titleUpdate.StatusOf(_layout.UpdateDataDirectory) == SetupStatus.Partial);
        StartSetupButton.Content = upgrade ? "ADD UPDATE" : partial ? "RESUME SETUP" : "SET UP";
        RestartSetupButton.IsVisible = partial;
        SetupStatusText.Text = partial
            ? (upgrade ? "Adding Title Update 2 did not finish. Choose the update again, then resume or start over."
                       : "A previous setup did not finish. Choose your disc image and the update again, then resume it or start over.")
            : "";
        (upgrade ? ChooseTitleUpdateButton : ChooseImageButton).Focus();
    }

    /// <summary>
    /// The key art from the player's own disc, decoded off the UI thread; with no readable art the
    /// home screen keeps the typographic backdrop and shows the title instead.
    /// </summary>
    private async void ShowKeyArt()
    {
        var gameData = _layout.GameDataDirectory;
        if (_keyArtFor == gameData) return;
        _keyArtFor = gameData;
        HomeTitle.IsVisible = true;
        var art = await Task.Run(() =>
        {
            var jpeg = KeyArt.BackgroundIn(gameData);
            if (jpeg is null) return null;
            try
            {
                using var stream = new MemoryStream(jpeg);
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                // A JPEG the decoder refuses is no worse than no key art at all.
                return null;
            }
        });
        if (_keyArtFor != gameData || art is null) return;
        (KeyArtImage.Source as IDisposable)?.Dispose();
        KeyArtImage.Source = art;
        KeyArtLayer.IsVisible = true;
        HomeTitle.IsVisible = false;
        Backdrop.IsVisible = false;
        UpdateMotion();
    }

    private void ShowPanel(Control panel, Button opener)
    {
        ClosePanel();
        _panelOpener = opener;
        PanelScrim.IsVisible = true;
        panel.IsVisible = true;
        // Enter must not start the game from behind a panel.
        PlayButton.IsDefault = false;
        Control first = panel == DlcView ? AddDlcButton
            : panel == ModsView ? ModsScroll
            : panel == KeysView ? _keyRows[0].Button
            : panel != SettingsView ? NoticesScroll
            : _rows.Count > 0 ? _rows[0].Input : CheckForUpdates;
        first.Focus(NavigationMethod.Tab);
    }

    private void ClosePanel(bool cancel = false)
    {
        if (_panelOpener is null) return;
        FinishOnlineAfterName(cancel);
        if (ModsView.IsVisible) MarkModsSeen();
        _capturing = null;
        SettingsView.IsVisible = KeysView.IsVisible = AboutView.IsVisible = DlcView.IsVisible = ModsView.IsVisible = PanelScrim.IsVisible = false;
        PlayButton.IsDefault = HomeView.IsVisible;
        _panelOpener.Focus(NavigationMethod.Tab);
        _panelOpener = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _panelOpener is null) return;
        ClosePanel(cancel: true);
        e.Handled = true;
    }

    /// <summary>Opens where the player left the window, unless that place is no longer on any screen.</summary>
    private void RestorePlacement()
    {
        _normalSize = new Size(Width, Height);
        var saved = LauncherState.Load(_layout.UserDataDirectory).Window;
        if (saved is null || Screens is null) return;
        var screens = Screens.All.Select(screen =>
            new ScreenArea(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height));
        if (!saved.IsReachableOn(screens)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(saved.X, saved.Y);
        Width = Math.Max(MinWidth, saved.Width);
        Height = Math.Max(MinHeight, saved.Height);
        // A window that opens maximised goes back to the saved place, not the screen's corner.
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    // A maximised window remembers the size and place it goes back to.
    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _normalPosition = Position;
        _normalSize = ClientSize;
    }

    private void SavePlacement()
    {
        if (WindowState == WindowState.Minimized) return;
        RememberNormalBounds();
        try
        {
            LauncherState.RememberWindow(_layout.UserDataDirectory, new WindowPlacement(
                _normalPosition.X, _normalPosition.Y, _normalSize.Width, _normalSize.Height,
                WindowState == WindowState.Maximized));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not remembering the window is no reason to stop the launcher closing.
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty) ApplyMaterial();
        if (change.Property == WindowStateProperty) UpdateMotion();
    }

    /// <summary>
    /// Runs the key-art pan and the turning bursts only while the player looks at the launcher
    /// (<see cref="LauncherMotion"/>). Hidden elements never carry a running animation.
    /// </summary>
    private void UpdateMotion()
    {
        var motion = LauncherMotion.Allowed(IsActive, WindowState == WindowState.Minimized, _game.IsRunning, EffectsReduced);
        KeyArtImage.Classes.Set("kenburns", motion && KeyArtLayer.IsVisible);
        BigBackdropBurst.Classes.Set("spin", motion && Backdrop.IsVisible);
        SmallBackdropBurst.Classes.Set("spin", motion && Backdrop.IsVisible);
        PlayBurst.Classes.Set("spin", motion && HomeView.IsVisible);
    }

    /// <summary>
    /// Windows 11 gives the window its Mica (or Acrylic) material; the base then lets it through.
    /// Anywhere else the base stays solid, so the screens look the same without it.
    /// </summary>
    private bool EffectsReduced => LauncherMotion.EffectsReduced(_preferences.ReduceEffects, App.Options.ReduceEffects);

    /// <summary>Reduced effects ask for no material at all: no Mica, no Acrylic (GitHub issue #1).</summary>
    private void ApplyEffects()
    {
        TransparencyLevelHint = EffectsReduced
            ? [WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None];
        ApplyMaterial();
        UpdateMotion();
    }

    private void ApplyMaterial()
    {
        var material = ActualTransparencyLevel == WindowTransparencyLevel.Mica ||
                       ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        Resources["BaseBrush"] = new SolidColorBrush(material ? Color.Parse("#B30B1020") : Color.Parse("#0B1020"));
        Resources["PanelBrush"] = new SolidColorBrush(material ? Color.Parse("#D90B1020") : Color.Parse("#F00B1020"));
    }

    private static string ProductVersion()
    {
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
        // With the short commit the SDK appends: online matches only list the same build.
        return ReleaseVersion.Describe(version);
    }

    private static string ThirdPartyNotices()
    {
        using var reader = new StreamReader(AssetLoader.Open(new Uri("avares://WWEAllStarsLauncher/Assets/THIRD-PARTY-NOTICES.txt")));
        return reader.ReadToEnd();
    }

    private async void OnChooseImage(object? sender, RoutedEventArgs e)
    {
        if (await PickImage() is { } path) ChooseImage(path);
    }

    private async Task<string?> PickImage()
    {
        var files = await PickFiles(new FilePickerOpenOptions
        {
            Title = "Choose your WWE All Stars disc image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Xbox 360 disc image") { Patterns = ["*.iso"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.FirstOrDefault();
    }

    /// <summary>
    /// The file picker, logged before it opens and when it returns (GitHub issue #1: the launcher
    /// froze on a picked disc image and the log must show where).
    /// </summary>
    private async Task<IReadOnlyList<string>> PickFiles(FilePickerOpenOptions options)
    {
        App.Log.Write($"Opening the file picker: {options.Title}");
        var files = await StorageProvider.OpenFilePickerAsync(options);
        App.Log.Write($"The file picker returned {files.Count} item(s)");
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) App.Log.Write("Picked: " + string.Join(", ", paths));
        return paths;
    }

    private async Task<string?> PickFolder(FolderPickerOpenOptions options)
    {
        App.Log.Write($"Opening the folder picker: {options.Title}");
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        App.Log.Write($"The folder picker returned {folders.Count} item(s)");
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null) App.Log.Write("Picked: " + path);
        return path;
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder(new FolderPickerOpenOptions
            {
                Title = "Choose where the game files go (about 6.5 GB)",
                AllowMultiple = false,
            }) is not { } parent) return;
        // The game files get their own folder inside the one the player picks.
        SetGameDataDirectory(Path.Combine(parent, "gamedata"));
    }

    private void SetGameDataDirectory(string gameData)
    {
        _layout = new PortableLayout(_layout.Root, gameData, _layout.GameExecutable);
        App.Options = App.Options with { Layout = _layout };
        _game = NewGameLauncher(_layout);
        ShowCurrentScreen();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedImage(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DroppedImage(e) is { } path) ChooseImage(path);
    }

    private void OnDragOverFileOrFolder(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedItem(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    private void OnDropTitleUpdate(object? sender, DragEventArgs e)
    {
        if (DroppedItem(e) is { } path) ChooseTitleUpdate(path);
    }

    // A dropped file or folder (a dumped Content tree counts).
    private static string? DroppedItem(DragEventArgs e) => DroppedItems(e).FirstOrDefault();

    private static IReadOnlyList<string> DroppedItems(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    private async void OnChooseTitleUpdate(object? sender, RoutedEventArgs e)
    {
        if (await PickTitleUpdate() is { } path) ChooseTitleUpdate(path);
    }

    private async void OnChooseTitleUpdateFolder(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder(new FolderPickerOpenOptions
            {
                Title = "Choose a folder with Title Update 2 (a USB drive or a dumped Content folder)",
                AllowMultiple = false,
            }) is { } path) ChooseTitleUpdate(path);
    }

    private async Task<string?> PickTitleUpdate()
    {
        var files = await PickFiles(new FilePickerOpenOptions
        {
            Title = "Choose Title Update 2 (tu00000005_00000000, a TU_ file, or a zip, 7z or rar)",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All],
        });
        return files.FirstOrDefault();
    }

    /// <summary>Finds and checks Title Update 2 in a file, folder or archive; says what was found otherwise.</summary>
    private async void ChooseTitleUpdate(string input)
    {
        if (_setupCancel is not null) return;
        var pick = ++_titleUpdatePick;
        TitleUpdatePathText.Text = input;
        SetupStatusText.Text = "Checking the title update…";
        ForgetTitleUpdate();
        string message;
        try
        {
            var intake = await Task.Run(() => PackageIntake.Scan([input], Path.GetTempPath(), CancellationToken.None));
            if (pick != _titleUpdatePick)
            {
                intake.Dispose(); // a later choice won
                return;
            }
            _titleUpdateIntake = intake;
            var package = TitleUpdateSetup.Choose(intake);
            // Header, media ID and default.xexp only: seconds. Every block is checked during setup.
            await Task.Run(() => _titleUpdate.Check(package));
            if (pick != _titleUpdatePick) return;
            _titleUpdatePackage = package;
            message = "This is Title Update 2. Choose " + StartSetupButton.Content + " to continue.";
        }
        catch (SetupException error)
        {
            message = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            message = "The title update cannot be read: " + error.Message;
        }
        if (pick == _titleUpdatePick && _setupCancel is null) SetupStatusText.Text = message;
    }

    // The packages a mod may replace: the disc's and TU2's, from the manifests setup checks against.
    private KnownGameFiles? _knownGameFiles;

    private ModLibrary Mods => new(_layout,
        _knownGameFiles ??= KnownGameFiles.From(App.Options.Manifest, App.Options.TitleUpdateManifest,
            ModWrapping.ConfigFilesIn(_layout.UpdateDataDirectory)),
        FilePlatform.Linker, FilePlatform.RecycleBin);

    private void ShowMods()
    {
        ModsStatusText.Text = "";
        RefreshModsList();
        ShowPanel(ModsView, ModsButton);
    }

    /// <summary>
    /// One row per mod in load order: its place, name, version, author, problems and "new" mark
    /// as text, its on/off toggle, Up, Down and Remove; then the conflicts. After a change the
    /// control the player used keeps the focus, so the page works by keyboard.
    /// </summary>
    private void RefreshModsList(string? focusId = null, string? focusControl = null)
    {
        ModsList.Children.Clear();
        ModConflictsList.Children.Clear();
        ModConflictsHeading.IsVisible = false;
        IReadOnlyList<ModInfo> mods;
        IReadOnlyList<ModConflict> conflicts;
        var online = OnlinePlayOn();
        try
        {
            var library = Mods;
            mods = library.List();
            conflicts = library.Conflicts();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ModsStatusText.Text = $"Could not read the mods folder: {error.Message}";
            return;
        }
        if (mods.Count == 0)
        {
            ModsList.Children.Add(new TextBlock
            {
                Classes = { "body" }, TextWrapping = TextWrapping.Wrap,
                Text = $"No mods yet. The mods folder is {_layout.ModsDirectory}.",
            });
            return;
        }
        var names = mods.ToDictionary(mod => mod.Id, ModName, StringComparer.OrdinalIgnoreCase);
        var controls = new Dictionary<(string Id, string Control), Control>();
        for (var i = 0; i < mods.Count; i++)
        {
            var mod = mods[i];
            var name = names[mod.Id];
            var toggle = new CheckBox
            {
                Content = mod.Enabled ? "On" : "Off", IsChecked = mod.Enabled, IsEnabled = mod.Problems.Count == 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(toggle, "Use " + name);
            toggle.IsCheckedChanged += (_, _) => SetModEnabled(mod.Id, toggle.IsChecked == true);
            var up = ModButton("Up", "Move " + name + " up", i > 0, () => MoveMod(mod.Id, up: true));
            var down = ModButton("Down", "Move " + name + " down", i < mods.Count - 1, () => MoveMod(mod.Id, up: false));
            var remove = ModButton("Remove", "Remove " + name, true, () => RemoveMod(mod.Id, name));
            controls[(mod.Id, "toggle")] = toggle;
            controls[(mod.Id, "up")] = up;
            controls[(mod.Id, "down")] = down;

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                Text = $"{i + 1}. {name}" + (mod.IsNew ? "  (new)" : ""),
            });
            var details = string.Join(" · ", new[] { mod.Manifest?.Version, mod.Manifest?.Author }.Where(part => !string.IsNullOrEmpty(part)));
            if (details.Length > 0) text.Children.Add(new TextBlock { Classes = { "small" }, Text = details });
            // The description and problems take the row's full width, under the buttons.
            var more = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
            if (mod.Manifest is { Description.Length: > 0 } described)
                more.Children.Add(new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Text = described.Description });
            foreach (var problem in mod.Problems)
                more.Children.Add(new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Text = "Off: " + problem });
            if (mod.Manifest is not null && mod.Problems.Count == 0)
                more.Children.Add(new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Text = ModOnlineRule.KindLine(mod, online) });
            if (mod.IsOffForOnline(online))
                more.Children.Add(new TextBlock
                {
                    Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Foreground = this.FindResource("GoldBrush") as IBrush,
                    Text = mod.IsContentMod
                        ? "Off for online play: content mods (new wrestlers) are offline only."
                        : mod.DlcPackages.Count > 0
                        ? "Off for online play: it replaces a package an installed DLC pack ships, which loads offline only."
                        : "Off for online play: only cosmetic mods load while online play is on.",
                });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(toggle);
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            buttons.Children.Add(remove);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 10 };
            row.Children.Add(text);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            Grid.SetRow(more, 1);
            Grid.SetColumnSpan(more, 2);
            row.Children.Add(more);
            ModsList.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#330B1020")), Padding = new Thickness(12, 8), Child = row });
        }
        ModConflictsHeading.IsVisible = conflicts.Count > 0;
        foreach (var conflict in conflicts)
            ModConflictsList.Children.Add(new TextBlock
            {
                Classes = { "small" }, TextWrapping = TextWrapping.Wrap,
                Text = ConflictText(conflict, names),
            });

        if (focusId is null || focusControl is null) return;
        // A move to the top or bottom disables the button just used; the other one takes the focus.
        var fallback = focusControl == "up" ? "down" : "up";
        foreach (var key in new[] { focusControl, fallback, "toggle" })
            if (controls.TryGetValue((focusId, key), out var control) && control.IsEnabled)
            {
                Dispatcher.UIThread.Post(() => control.Focus(NavigationMethod.Tab));
                return;
            }
    }

    private static string ModName(ModInfo mod) => mod.Manifest is { Name.Length: > 0 } manifest ? manifest.Name : mod.Id;

    private static Button ModButton(string content, string automationName, bool enabled, Action click)
    {
        var button = new Button { Classes = { "secondary" }, Content = content, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>After a change: the running game keeps what it started with (spec, user story 20).</summary>
    private void ModChanged(string what)
    {
        App.Log.Write(what);
        ModsStatusText.Text = _game.IsRunning ? "The change applies the next time you choose Play." : "";
    }

    /// <summary>One line of the CONFLICTS list: what clashes, who supplies it, and who wins (tickets 06, 17).</summary>
    private static string ConflictText(ModConflict conflict, IReadOnlyDictionary<string, string> names)
    {
        var winner = names[conflict.Winner];
        string Join(IEnumerable<string> parts) => string.Join(", ", parts);
        return conflict.Kind switch
        {
            ModConflictKind.RosterSlot =>
                $"Roster slot {conflict.File["CharacterData_".Length..]}: {Join(conflict.Mods.Select(id => names[id]))} fill it. " +
                $"{winner} wins, as the lowest in the list.",
            ModConflictKind.RosterCodeName =>
                $"CodeName {conflict.File}: {Join(conflict.Mods.Select(id => names[id]))} give it to a wrestler, and the game would show one of them. " +
                $"{winner} wins, as the lowest in the list; the other wrestler is left out.",
            ModConflictKind.RosterPackage =>
                $"{conflict.File}: {winner} ships a whole one, which replaces it for " +
                $"{Join([.. conflict.Packs, .. conflict.Mods.Where(id => id != conflict.Winner).Select(id => names[id])])}. " +
                "The official packs' wrestlers stay only if it carries them" +
                (conflict.File.Equals("CombinedGlobalCharacters.xxx", StringComparison.OrdinalIgnoreCase)
                    ? "; roster.toml entries are still written into it." : "."),
            ModConflictKind.ContentFile =>
                $"{conflict.File}: {Join([.. conflict.Packs, .. conflict.Mods.Select(id => names[id])])} have it. {winner} wins, " +
                (conflict.Mods.Count > 1 ? "as the lowest in the list." : "as a mod's file replaces an official pack's."),
            _ => $"{conflict.File}: {Join(conflict.Mods.Select(id => names[id]))} replace it. {winner} wins, as the lowest in the list.",
        };
    }

    /// <summary>The player's <c>wwe_online</c> setting; off when the settings file can't be read.</summary>
    private bool OnlinePlayOn() => SettingsFile.OnlinePlayOn(_layout.SettingsFile);

    /// <summary>The Play area's "N gameplay mods off for online play" line (spec, "Online rule").</summary>
    private void UpdateModsOnlineLine()
    {
        var count = 0;
        if (OnlinePlayOn())
        {
            try
            {
                count = Mods.List().Count(mod => mod.IsOffForOnline(online: true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The Mods page reports an unreadable mods folder.
            }
        }
        ModsOnlineText.Text = ModOnlineRule.OffForOnlineLine(count);
        ModsOnlineText.IsVisible = count > 0;
    }

    private void SetModEnabled(string id, bool enabled)
    {
        try
        {
            Mods.SetEnabled(id, enabled);
            ModChanged($"Mod {id}: {(enabled ? "on" : "off")}");
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
        {
            ModsStatusText.Text = error.Message;
        }
        RefreshModsList(id, "toggle");
        UpdateModsOnlineLine();
    }

    private void MoveMod(string id, bool up)
    {
        try
        {
            if (up) Mods.MoveUp(id);
            else Mods.MoveDown(id);
            ModChanged($"Mod {id}: moved {(up ? "up" : "down")}");
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
        {
            ModsStatusText.Text = error.Message;
        }
        RefreshModsList(id, up ? "up" : "down");
    }

    /// <summary>To the Recycle Bin, so a mistaken removal can be undone there (spec, user story 17).</summary>
    private void RemoveMod(string id, string name)
    {
        try
        {
            Mods.Remove(id);
            ModChanged($"Mod {id}: moved to the Recycle Bin");
            if (ModsStatusText.Text is not { Length: > 0 })
                ModsStatusText.Text = $"{name} is in the Recycle Bin; restore it from there if you need it back.";
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
        {
            ModsStatusText.Text = error.Message;
        }
        RefreshModsList();
        UpdateModsOnlineLine();
        ModsScroll.Focus(NavigationMethod.Tab);
    }

    // Leaving the page clears the "new" marks: the player has seen them.
    private void MarkModsSeen()
    {
        try
        {
            Mods.MarkAllSeen();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            App.Log.Write($"Mods: the new marks could not be cleared: {error.Message}");
        }
    }

    private async void OnOpenModsFolder(object? sender, RoutedEventArgs e)
    {
        var folder = _layout.ModsDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
                ModsStatusText.Text = $"Could not open the mods folder: {folder}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ModsStatusText.Text = $"Could not open the mods folder: {error.Message}";
        }
    }

    /// <summary>Deletes the content mods' combined content (spec, "Removed when not wanted"); a failure is only logged.</summary>
    private void RemoveCombinedContent()
    {
        try
        {
            Mods.RemoveCombinedContent();
        }
        catch (LauncherException error)
        {
            App.Log.Write(error.Message);
        }
    }

    /// <summary>The update folder for this Play (ADR-0026), with its load report in the launcher log.</summary>
    private async Task<PreparedMods> PrepareMods()
    {
        var online = OnlinePlayOn();
        var skipLogos = SettingsFile.IsOn(_layout.SettingsFile, SettingsFile.SkipStartupLogosKey);
        var library = Mods;
        var updateFolder = _layout.UpdateDataDirectory;
        var prepared = await Task.Run(() => library.PrepareForPlay(updateFolder, online, skipLogos));
        foreach (var line in prepared.Report.Lines()) App.Log.Write(line);
        ModsOnlineText.Text = ModOnlineRule.OffForOnlineLine(prepared.Report.LeftOut.Count);
        ModsOnlineText.IsVisible = prepared.Report.LeftOut.Count > 0;
        return prepared;
    }

    private void ShowDlc()
    {
        RefreshDlcList();
        ShowPanel(DlcView, DlcButton);
    }

    private DlcLibrary Dlc => new(_layout.UserDataDirectory);

    /// <summary>One row per installed pack: name, wrestlers when known, size, Remove.</summary>
    private void RefreshDlcList()
    {
        DlcList.Children.Clear();
        var packs = Dlc.Installed();
        if (packs.Count == 0)
        {
            DlcList.Children.Add(new TextBlock { Classes = { "body" }, Text = "No DLC installed." });
            return;
        }
        foreach (var pack in packs)
        {
            var remove = new Button { Classes = { "secondary" }, Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, "Remove " + pack.DisplayName);
            remove.Click += (_, _) => RemoveDlc(pack);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Text = pack.DisplayName });
            if (pack.Wrestlers.Count > 0)
                text.Children.Add(new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Text = string.Join(", ", pack.Wrestlers) });
            text.Children.Add(new TextBlock { Classes = { "small" }, Text = $"{pack.Bytes / 1e6:0} MB" });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(text);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            DlcList.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#330B1020")), Padding = new Thickness(12, 8), Child = row });
        }
    }

    private async Task<IReadOnlyList<string>> PickDlcFiles()
    {
        return await PickFiles(new FilePickerOpenOptions
        {
            Title = "Choose DLC packages (or zip, 7z or rar files with packs inside); several at once work",
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.All],
        });
    }

    private async Task<string?> PickDlcFolder()
    {
        return await PickFolder(new FolderPickerOpenOptions
        {
            Title = "Choose a folder with DLC packs (a USB drive or a dumped Content folder)",
            AllowMultiple = false,
        });
    }

    /// <summary>Checks and installs every DLC pack in a file, folder or archive. Not while the game runs.</summary>
    private async void AddDlc(IReadOnlyList<string> inputs)
    {
        if (_jobs.IsRunning(HomeJob.AddDlc)) return;
        if (_game.IsRunning)
        {
            DlcStatusText.Text = "Close the game before adding DLC.";
            return;
        }
        // A job like Repair: Play stays off while packs are swapped into the content folder.
        _jobs.TryStart(HomeJob.AddDlc, out var cancel);
        SetDlcRunning(true);
        DlcStatusText.Text = "Looking for DLC…";
        var library = Dlc;
        try
        {
            using var intake = await Task.Run(() => PackageIntake.Scan(inputs, Path.GetTempPath(), cancel), cancel);
            var packages = DlcLibrary.Choose(intake);
            var added = new List<string>();
            foreach (var package in packages)
            {
                var name = Path.GetFileName(package);
                var progress = new Progress<DlcProgress>(p =>
                {
                    DlcProgressBar.Value = p.Fraction;
                    DlcStatusText.Text = $"{(p.Unpacking ? "Unpacking" : "Checking")} {name}… {p.Fraction:0%}";
                });
                var pack = await Task.Run(() => library.Install(package, progress, cancel), cancel);
                added.Add(pack.DisplayName);
            }
            DlcStatusText.Text = $"Added {string.Join(", ", added)}.";
        }
        catch (OperationCanceledException)
        {
            DlcStatusText.Text = "Adding DLC was cancelled. Packs added before are kept.";
        }
        catch (SetupException error)
        {
            DlcStatusText.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DlcStatusText.Text = "The DLC could not be read or written: " + error.Message;
        }
        finally
        {
            _jobs.End(HomeJob.AddDlc);
            SetDlcRunning(false);
            RefreshDlcList();
        }
    }

    private void RemoveDlc(DlcPack pack)
    {
        if (_jobs.IsRunning(HomeJob.AddDlc)) return;
        if (_game.IsRunning)
        {
            DlcStatusText.Text = "Close the game before removing DLC.";
            return;
        }
        try
        {
            Dlc.Remove(pack.FileName);
            DlcStatusText.Text = $"Removed {pack.DisplayName}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DlcStatusText.Text = $"{pack.DisplayName} could not be removed: {error.Message}";
        }
        RefreshDlcList();
    }

    private void SetDlcRunning(bool running)
    {
        AddDlcButton.IsEnabled = AddDlcFolderButton.IsEnabled = !running;
        CancelDlcButton.IsVisible = running;
        DragDrop.SetAllowDrop(DlcView, !running);
        DlcProgressBar.IsVisible = running;
        DlcProgressBar.Value = 0;
        UpdatePlayButton();
    }

    /// <summary>
    /// The setup screen's optional DLC: found now, installed after Title Update 2. Each pick or drop
    /// adds to what was chosen before.
    /// </summary>
    private async void ChooseSetupDlc(IReadOnlyList<string> added)
    {
        if (_setupCancel is not null) return;
        foreach (var path in added)
            if (!_setupDlcInputs.Contains(path, StringComparer.OrdinalIgnoreCase)) _setupDlcInputs.Add(path);
        var inputs = _setupDlcInputs.ToList();
        var pick = ++_setupDlcPick;
        _setupDlcIntake?.Dispose();
        _setupDlcIntake = null;
        SetupDlcText.Text = "Looking for DLC…";
        try
        {
            var intake = await Task.Run(() => PackageIntake.Scan(inputs, Path.GetTempPath(), CancellationToken.None));
            if (pick != _setupDlcPick)
            {
                intake.Dispose(); // a later choice won, or the window closed
                return;
            }
            try
            {
                var packs = DlcLibrary.Choose(intake);
                _setupDlcIntake = intake;
                SetupDlcText.Text = $"{(packs.Count == 1 ? "1 pack" : $"{packs.Count} packs")} to add: " +
                    string.Join(", ", intake.Items.Where(i => i.Kind == PackageKind.Dlc).Select(i => i.Header!.DisplayName).Distinct());
            }
            catch
            {
                intake.Dispose();
                throw;
            }
        }
        catch (SetupException error)
        {
            // What was just added holds no usable pack: keep the earlier choices.
            _setupDlcInputs.RemoveAll(path => added.Contains(path, StringComparer.OrdinalIgnoreCase));
            SetupDlcText.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _setupDlcInputs.RemoveAll(path => added.Contains(path, StringComparer.OrdinalIgnoreCase));
            if (pick == _setupDlcPick) SetupDlcText.Text = "The DLC cannot be read: " + error.Message;
        }
    }

    // Installs the setup screen's DLC; a problem with it never fails setup. Returns what to tell the player.
    private async Task<string?> InstallSetupDlc(CancellationToken cancel)
    {
        if (_setupDlcIntake is not { } intake) return null;
        try
        {
            var library = Dlc;
            foreach (var package in DlcLibrary.Choose(intake))
            {
                var name = Path.GetFileName(package);
                var progress = new Progress<DlcProgress>(p =>
                {
                    SetupProgressBar.Value = p.Fraction;
                    SetupStatusText.Text = $"Adding DLC: {(p.Unpacking ? "unpacking" : "checking")} {name}…";
                });
                await Task.Run(() => library.Install(package, progress, cancel), cancel);
            }
            return null;
        }
        catch (Exception error) when (error is SetupException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "DLC was not added: " + error.Message + " Add it later from the DLC page.";
        }
        finally
        {
            intake.Dispose();
            _setupDlcIntake = null;
            _setupDlcInputs.Clear();
            SetupDlcText.Text = "No DLC chosen";
        }
    }

    /// <summary>Drops the chosen title update and the temp folder an archive was unpacked to.</summary>
    private void ForgetTitleUpdate()
    {
        _titleUpdatePackage = null;
        _titleUpdateIntake?.Dispose();
        _titleUpdateIntake = null;
    }

    private static string? DroppedImage(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Select(file => file.TryGetLocalPath()).FirstOrDefault(path => path is not null);

    private async void ChooseImage(string path)
    {
        if (_setupCancel is not null)
        {
            App.Log.Write("Disc image ignored: setup is running");
            return;
        }
        App.Log.Write("Disc image chosen: " + path);
        _imagePath = path;
        ImagePathText.Text = path;
        SetupStatusText.Text = "Checking the disc image…";
        string message;
        try
        {
            // A wrong file is refused within seconds: only the file list and default.xex are read.
            await Task.Run(() => _setup.Verify(path, App.Log.Write));
            message = "This is the supported disc. Choose " + StartSetupButton.Content + " to copy the game files.";
        }
        catch (SetupException error)
        {
            message = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            message = "The disc image cannot be read: " + error.Message;
        }
        App.Log.Write("Disc image check finished: " + message);
        // A later choice or a setup that started meanwhile owns the status line now.
        if (_imagePath == path && _setupCancel is null) SetupStatusText.Text = message;
        App.Log.Write("Disc image check shown");
    }

    private async void RunSetup(SetupStart start)
    {
        if (_setupCancel is not null) return;
        var upgrade = AddingTitleUpdateOnly;
        if (_imagePath is null && !upgrade)
        {
            SetupStatusText.Text = "Choose your disc image first.";
            return;
        }
        if (_titleUpdatePackage is null)
        {
            SetupStatusText.Text = _titleUpdateIntake is null
                ? "Choose Title Update 2 too. " + TitleUpdateSetup.WhatTitleUpdate2Is
                : "The chosen file is not Title Update 2. Choose the update again.";
            return;
        }
        using var cancel = _setupCancel = new CancellationTokenSource();
        SetSetupRunning(true);
        SetupStatusText.Text = upgrade ? "Checking Title Update 2…" : "Checking the disc image…";
        var image = _imagePath;
        var titleUpdate = _titleUpdatePackage;
        var gameData = _layout.GameDataDirectory;
        var updateData = _layout.UpdateDataDirectory;
        var progress = new Progress<SetupProgress>(ShowProgress);
        try
        {
            if (!upgrade)
            {
                // Remember the chosen folder now, so an interrupted setup is found again next time.
                LauncherState.RememberGameData(_layout.UserDataDirectory, gameData);
                await Task.Run(() => _setup.Run(image!, gameData, start, progress, cancel.Token), cancel.Token);
            }
            await Task.Run(() => _titleUpdate.Install(titleUpdate, updateData, start, progress, cancel.Token), cancel.Token);
            // Unpacked and verified: the package (or an archive's temp copy) is no longer needed.
            _titleUpdatePick++;
            ForgetTitleUpdate();
            var dlcProblem = await InstallSetupDlc(cancel.Token);
            if (!upgrade) await ApplySuggestedPreset();
            // The game files are in place; the shader step's own failure or cancel only means Emulated.
            SetupStatusText.Text = "Preparing shaders for the Native renderer…";
            var shaders = await RunShaderStep(new Progress<ShaderProgress>(ShowSetupShaderProgress), cancel.Token);
            // Then the pipelines, for this GPU and driver. It takes seconds and is not cancelled
            // part way; without it the game still runs, and the next Play tries again.
            string? pipelines = null;
            if (shaders is null)
            {
                CancelSetupButton.IsVisible = false;
                SetupProgressBar.Value = 0;
                SetupStatusText.Text = "Preparing pipelines for your graphics card…";
                SetupDetailText.Text = "";
                var result = await Pipelines().RunAsync(new Progress<PipelineProgress>(ShowSetupPipelineProgress), CancellationToken.None);
                if (result.Outcome != PipelineOutcome.Ready) pipelines = result.Message;
            }
            ShowCurrentScreen();
            var finished = (upgrade ? "Title Update 2 added." : "Setup finished.") + (dlcProblem is null ? "" : " " + dlcProblem);
            StatusText.Text = shaders ?? (pipelines is null
                ? $"{finished} Your game is ready: choose Play."
                : $"{finished} {pipelines}");
        }
        catch (OperationCanceledException)
        {
            SetupStatusText.Text = "Setup cancelled. You can resume it later or start over.";
            ShowCurrentScreen();
        }
        catch (SetupException error)
        {
            SetupStatusText.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetupStatusText.Text = "Setup could not write the game files: " + error.Message;
        }
        finally
        {
            _setupCancel = null;
            SetSetupRunning(false);
        }
    }

    private void SetSetupRunning(bool running)
    {
        ChooseImageButton.IsEnabled = !running;
        ChooseTitleUpdateButton.IsEnabled = ChooseTitleUpdateFolderButton.IsEnabled = !running;
        DragDrop.SetAllowDrop(TitleUpdateZone, !running);
        ChooseFolderButton.IsEnabled = !running;
        StartSetupButton.IsEnabled = !running;
        RestartSetupButton.IsEnabled = !running;
        DragDrop.SetAllowDrop(DropZone, !running);
        CancelSetupButton.IsVisible = running;
        SetupProgressBar.IsVisible = running;
        if (running) SetupProgressBar.Value = 0;
        else SetupDetailText.Text = "";
    }

    private void ShowProgress(SetupProgress progress)
    {
        if (_setupCancel is null) return;
        SetupProgressBar.Value = progress.Fraction;
        SetupStatusText.Text = progress.Step switch
        {
            SetupStep.CheckingImage => "Checking the disc image…",
            SetupStep.CheckingExistingFiles => "Checking the files from the last setup…",
            SetupStep.Extracting => "Copying and verifying game files…",
            SetupStep.VerifyingGameFiles => "Checking the game files already in this folder…",
            SetupStep.VerifyingTitleUpdate => "Checking Title Update 2…",
            SetupStep.UnpackingTitleUpdate => "Unpacking and verifying Title Update 2…",
            _ => "Finishing…",
        };
        var left = progress.TimeLeft is { } time
            ? $" · {(time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss"))} left"
            : "";
        SetupDetailText.Text = $"{progress.Fraction:0%} · {progress.CurrentFile}{left}";
    }

    /// <summary>
    /// Repair: re-hash every game file, list the missing or damaged ones, ask for the disc image
    /// and re-extract only those. Not while the game runs, since it holds the files open.
    /// </summary>
    private async void OnRepair(object? sender, RoutedEventArgs e)
    {
        if (_jobs.IsRunning(HomeJob.Repair)) return;
        if (_game.IsRunning)
        {
            StatusText.Text = "Close the game before repairing its files.";
            return;
        }
        _jobs.TryStart(HomeJob.Repair, out var cancel);
        SetRepairRunning(true);
        var gameData = _layout.GameDataDirectory;
        var updateData = _layout.UpdateDataDirectory;
        var progress = new Progress<SetupProgress>(ShowRepairProgress);
        string? done = null;  // set when the files verify: then the pipelines are rebuilt too
        try
        {
            var check = await Task.Run(() => _setup.CheckInstalled(gameData, progress, cancel), cancel);
            var discDone = check.NothingToRepair;
            if (!discDone)
            {
                StatusText.Text = $"{check.Summary()} Choose your disc image to repair them.";
                if (await PickImage() is { } image)
                {
                    await Task.Run(() => _setup.Repair(image, gameData, check, progress, cancel), cancel);
                    discDone = true;
                }
                else StatusText.Text = $"{check.Summary()} Repair needs your disc image; choose Repair again when you have it.";
            }
            if (discDone)
            {
                // Title Update 2's files too; the update itself is asked for only when one is bad.
                var update = await Task.Run(() => _titleUpdate.CheckInstalled(updateData, progress, cancel), cancel);
                if (update.NothingToRepair)
                {
                    // A folder that lost its marker but whose files verify is set up again.
                    _titleUpdate.MarkVerified(updateData, update);
                    done = check.NothingToRepair ? "Every game file and Title Update 2 file verifies. Nothing to repair." : "Repaired. Your game is ready: choose Play.";
                }
                else
                {
                    StatusText.Text = $"Title Update 2: {update.Summary()} Choose the update to repair them.";
                    if (await PickTitleUpdate() is { } input)
                    {
                        using var intake = await Task.Run(() => PackageIntake.Scan([input], Path.GetTempPath(), cancel), cancel);
                        var package = TitleUpdateSetup.Choose(intake);
                        await Task.Run(() => _titleUpdate.Repair(package, updateData, update, progress, cancel), cancel);
                        done = "Repaired. Your game is ready: choose Play.";
                    }
                    else StatusText.Text = $"Title Update 2: {update.Summary()} Repair needs the update; choose Repair again when you have it.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Repair cancelled. Files repaired so far are kept; choose Repair to finish.";
        }
        catch (SetupException error)
        {
            StatusText.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Repair could not check or write the game files: " + error.Message;
        }
        finally
        {
            _jobs.End(HomeJob.Repair);
            SetRepairRunning(false);
        }
        if (done is null) return;
        StatusText.Text = done;
        // Repair always rebuilds the pipelines; without a usable pack, Prepare shaders comes first.
        var rebuild = Pipelines().Check(_shaders.StatusOf(_layout.CacheDirectory).Pack.Ready, repair: true);
        if (!rebuild.Rebuilds() || _game.IsRunning || _jobs.IsRunning(HomeJob.PrepareShaders)) return;
        if (await RunPipelineJob(rebuild) is { } result)
            StatusText.Text = result.Usable ? $"{done} {result.Message}" : result.Message;
    }

    private void SetRepairRunning(bool running)
    {
        UpdatePlayButton();
        RepairButton.IsEnabled = !running && !_jobs.IsRunning(HomeJob.PreparePipelines);
        CancelRepairButton.IsVisible = running;
        RepairProgressBar.IsVisible = running;
        RepairProgressBar.Value = 0;
    }

    private void ShowRepairProgress(SetupProgress progress)
    {
        if (!_jobs.IsRunning(HomeJob.Repair)) return;
        RepairProgressBar.Value = progress.Fraction;
        var step = progress.Step switch
        {
            SetupStep.VerifyingGameFiles => "Checking game files",
            SetupStep.VerifyingTitleUpdateFiles => "Checking Title Update 2",
            SetupStep.VerifyingTitleUpdate or SetupStep.UnpackingTitleUpdate => "Repairing Title Update 2",
            _ => "Repairing game files",
        };
        StatusText.Text = $"{step}… {progress.Fraction:0%}";
    }

    /// <param name="updateDataRoot">The update folder Play uses, so the prepare plans the mods' materials too (ticket 09).</param>
    private PipelinePreparation Pipelines(string? updateDataRoot = null) => new(_layout, PrepareRunner, updateDataRoot);

    private void ShowSetupPipelineProgress(PipelineProgress progress)
    {
        if (_setupCancel is null) return;
        SetupProgressBar.Value = progress.Fraction;
        SetupDetailText.Text = PipelineDetail(progress);
    }

    private static string PipelineDetail(PipelineProgress progress) =>
        $"{progress.Fraction:0%} · {HeadlessCommands.PipelineStageName(progress.Stage)} ({progress.Done} of {progress.Total})";

    /// <summary>What the status line says while the launcher rebuilds without being asked.</summary>
    private static string RebuildReason(PipelineRebuild rebuild) => rebuild switch
    {
        PipelineRebuild.Stale => "Your graphics card or driver changed, so the game's pipelines are built again",
        PipelineRebuild.PackChanged or PipelineRebuild.TopUpsChanged => "The Native shaders changed, so the game's pipelines are built again",
        PipelineRebuild.KeyListChanged => "This version of the game plans other pipelines, so they are built again",
        PipelineRebuild.UpdateFolderChanged => "Other mods are in use, so the game's pipelines are built again",
        PipelineRebuild.Repair => "Rebuilding the game's pipelines",
        _ => "Preparing the game's pipelines for your graphics card",
    };

    /// <summary>
    /// The home screen's pipeline job: runs the prepare with its own progress bar while Play is
    /// off. Returns the result, or null when a prepare already runs.
    /// </summary>
    private async Task<PipelineResult?> RunPipelineJob(PipelineRebuild why, string? updateDataRoot = null)
    {
        if (!_jobs.TryStart(HomeJob.PreparePipelines, out _)) return null;
        PlayButton.IsEnabled = false;
        RepairButton.IsEnabled = PrepareShadersButton.IsEnabled = false;
        PipelineProgressBar.Value = 0;
        PipelineProgressBar.IsVisible = true;
        var reason = RebuildReason(why);
        StatusText.Text = reason + "…";
        try
        {
            return await Pipelines(updateDataRoot).RunAsync(new Progress<PipelineProgress>(value =>
            {
                if (!_jobs.IsRunning(HomeJob.PreparePipelines)) return;
                PipelineProgressBar.Value = value.Fraction;
                StatusText.Text = $"{reason}… {PipelineDetail(value)}";
            }), CancellationToken.None);
        }
        finally
        {
            _jobs.End(HomeJob.PreparePipelines);
            PipelineProgressBar.IsVisible = false;
            RepairButton.IsEnabled = !_jobs.IsRunning(HomeJob.Repair);
            PrepareShadersButton.IsEnabled = true;
            UpdatePlayButton();
        }
    }

    private void ShowSetupShaderProgress(ShaderProgress progress)
    {
        if (_setupCancel is null) return;
        SetupProgressBar.Value = progress.Fraction;
        SetupStatusText.Text = "Preparing shaders for the Native renderer…";
        SetupDetailText.Text = $"{progress.Fraction:0%} · {HeadlessCommands.ShaderStepName(progress.Step)}";
    }

    /// <summary>
    /// Runs the shader step over the disc and the mods turned on (ticket 09); returns null on
    /// success, else what to tell the player.
    /// </summary>
    private async Task<string?> RunShaderStep(IProgress<ShaderProgress> progress, CancellationToken cancel)
    {
        var gameData = _layout.GameDataDirectory;
        var cache = _layout.CacheDirectory;
        var library = Mods;
        try
        {
            await Task.Run(() =>
            {
                var mods = library.ShaderSources();
                _shaders.Run(gameData, cache, progress, cancel, mods.Folders);
                library.ShadersPrepared(mods);
            }, cancel);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"The mods could not be read for shaders: {error.Message}";
        }
        catch (OperationCanceledException)
        {
            return "Preparing shaders was cancelled. The game runs on Emulated until you choose Prepare shaders.";
        }
        catch (ShaderPreparationException error)
        {
            return error.Message;
        }
    }

    /// <summary>Offers Prepare shaders when the pack is missing, stale or lacks shaders the game found.</summary>
    private void ShowShaderOffer(bool gameAskedForIt = false)
    {
        if (_jobs.IsRunning(HomeJob.PrepareShaders)) return;
        var status = _shaders.StatusOf(_layout.CacheDirectory);
        PrepareShadersButton.IsVisible = status.NeedsPreparing || gameAskedForIt;
        if (status.NeedsPreparing) StatusText.Text = status.Message;
    }

    private void OnPrepareShaders(object? sender, RoutedEventArgs e) => PrepareShaders();

    private async void PrepareShaders(bool afterSession = false) => await PrepareShadersJob(afterSession);

    /// <summary>The home screen's shader job, with its progress bar; true when the pack was written.</summary>
    private async Task<bool> PrepareShadersJob(bool afterSession = false, bool forMods = false)
    {
        if (afterSession && _jobs.IsRunning(HomeJob.PrepareShaders)) return false;
        if (!_jobs.TryStart(HomeJob.PrepareShaders, out var cancel))
        {
            _jobs.Cancel(HomeJob.PrepareShaders);
            return false;
        }
        UpdatePlayButton();
        PrepareShadersButton.Content = "CANCEL SHADERS";
        ShaderProgressBar.Value = 0;
        ShaderProgressBar.IsVisible = true;
        var progress = new Progress<ShaderProgress>(value =>
        {
            ShaderProgressBar.Value = value.Fraction;
            StatusText.Text = $"Preparing shaders: {HeadlessCommands.ShaderStepName(value.Step).ToLowerInvariant()} ({value.Fraction:0%})";
        });
        var message = await RunShaderStep(progress, cancel);
        _jobs.End(HomeJob.PrepareShaders);
        ShaderProgressBar.IsVisible = false;
        PrepareShadersButton.Content = "PREPARE SHADERS";
        UpdatePlayButton();
        ShowShaderOffer();
        StatusText.Text = message ?? (forMods ? "Added the mods' shaders to Native."
            : afterSession ? "Added the shaders the game found; they show from the next start."
            : "Shaders ready: the game can run on Native.");
        return message is null;
    }

    // Switches and plays in one step (gpu-robustness story 3).
    private async void OnSwitchToEmulated(object? sender, RoutedEventArgs e)
    {
        try
        {
            new SettingsFile(_layout.SettingsFile, SettingsSchema.Committed()).SwitchToEmulated();
            SwitchToEmulatedButton.IsVisible = false;
            StatusText.Text = "The game starts on Emulated. Choose Native again in Settings.";
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save the renderer setting: {error.Message}";
            return;
        }
        // An exception leaving an async void handler would close the launcher.
        try
        {
            await Play();
        }
        catch (Exception error)
        {
            StatusText.Text = $"Could not start the game: {error.Message}";
            UpdatePlayButton();
        }
    }

    /// <summary>Play is on unless the game runs or a job that must finish first does.</summary>
    private void UpdatePlayButton() => PlayButton.IsEnabled = _jobs.CanPlay(_game.IsRunning);

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        // An exception leaving an async void handler would close the launcher.
        try
        {
            await Play();
        }
        catch (Exception error)
        {
            StatusText.Text = $"Could not start the game: {error.Message}";
            UpdatePlayButton();
        }
    }

    private async Task Play()
    {
        if (!_jobs.CanPlay(_game.IsRunning)) return;
        PreparedMods mods;
        try
        {
            mods = await PrepareMods();
        }
        catch (LauncherException error)
        {
            StatusText.Text = error.Message;
            return;
        }
        // The mods in use bring packages the pack was not built with: add their shaders first
        // (spec, "Shaders"; ticket 09). The new pack makes the check below prepare pipelines.
        // Without a usable pack Prepare shaders is offered as before, and covers the mods.
        if (mods.ShadersChanged && !_playWithoutModShaders && _shaders.StatusOf(_layout.CacheDirectory).Pack.Ready)
        {
            if (!await PrepareShadersJob(forMods: true))
            {
                // Said once; the top-up still catches the shaders in game.
                _playWithoutModShaders = true;
                StatusText.Text += " Choose Play to start anyway.";
                return;
            }
        }
        _playWithoutModShaders = false;
        // Rebuild without a click when the pack, the top-ups or the key list changed, the store
        // is missing or the game marked it stale (spec, "When the launcher rebuilds pipelines").
        var rebuild = Pipelines(mods.UpdateDataRoot).Check(_shaders.StatusOf(_layout.CacheDirectory).Pack.Ready);
        if (rebuild.Rebuilds() && !_playWithoutPipelines)
        {
            if (await RunPipelineJob(rebuild, mods.UpdateDataRoot) is not { } result) return;
            if (!result.Usable)
            {
                // Said once, then the next Play starts the game as it is.
                _playWithoutPipelines = true;
                StatusText.Text = result.Message + " Choose Play to start anyway.";
                return;
            }
        }
        _playWithoutPipelines = false;
        try
        {
            Watch(_game.Play(mods));
            SwitchToEmulatedButton.IsVisible = false;
        }
        catch (LauncherException error)
        {
            StatusText.Text = error.Message;
        }
    }

    /// <summary>
    /// One check per launch, on a pool thread: the window opens and Play works while it runs,
    /// and a failure or a timeout just leaves the link hidden.
    /// </summary>
    private async void ShowUpdateWhenFound()
    {
        var enabled = _preferences.CheckForUpdates;
        _update = await Task.Run(() => _updates.CheckAsync(enabled));
        if (_update is null) return;
        UpdateLink.Content = $"Update available: {_update.Tag}  ·  open the release page";
        UpdateLink.IsVisible = true;
    }

    private async void OnOpenRelease(object? sender, RoutedEventArgs e)
    {
        if (_update is not null) await this.Launcher.LaunchUriAsync(_update.ReleasePage);
    }

    private void OnReduceEffectsChanged(object? sender, RoutedEventArgs e)
    {
        try
        {
            _preferences = _preferences.With(reduceEffects: ReduceEffects.IsChecked == true);
            _preferences.Save(App.Options.Layout);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save the effects setting: {error.Message}";
        }
        App.Log.Write($"Reduce effects: {(_preferences.ReduceEffects ? "on" : "off")}");
        ApplyEffects();
    }

    private void OnCheckForUpdatesChanged(object? sender, RoutedEventArgs e)
    {
        try
        {
            _preferences = _preferences.With(checkForUpdates: CheckForUpdates.IsChecked == true);
            _preferences.Save(App.Options.Layout);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save the update setting: {error.Message}";
        }
    }

    private void Watch(GameSession session)
    {
        PlayButton.IsEnabled = false;
        StatusText.Text = _game.Status;
        // Out of the way while the game runs: minimized and still (launcher ticket 28).
        if (WindowState != WindowState.Minimized) _stateBeforeGame = WindowState;
        WindowState = WindowState.Minimized;
        UpdateMotion();
        session.Exited.ContinueWith(exited => Dispatcher.UIThread.Post(() =>
        {
            // Back in front with the exit report, as the player left it.
            if (_stateBeforeGame is { } before && WindowState == WindowState.Minimized) WindowState = before;
            _stateBeforeGame = null;
            UpdateMotion();
            // Before Play is back on: the exe started on its own never sees the content mods.
            RemoveCombinedContent();
            // The game has just exited; a repair or the shader step still running keeps Play off.
            PlayButton.IsEnabled = _jobs.CanPlay(gameRunning: false);
            if (exited.IsCompletedSuccessfully) ShowShaderOffer(gameAskedForIt: exited.Result.OfferShaderRebuild);
            SwitchToEmulatedButton.IsVisible = exited.IsCompletedSuccessfully && exited.Result.OfferEmulated;
            _crashReport = exited.IsCompletedSuccessfully ? exited.Result.CrashReportPath : null;
            CrashReportButton.IsVisible = _crashReport is not null;
            if (_crashReport is not null) new CrashReports(_layout.LogsDirectory).MarkSeen(_crashReport);
            StatusText.Text = exited.IsCompletedSuccessfully
                ? exited.Result.Message
                : "The launcher lost track of the game. Open logs to see what happened.";
            if (exited.IsCompletedSuccessfully && _shaders.StatusOf(_layout.CacheDirectory).AddsAfterSession)
                PrepareShaders(afterSession: true);
        }), TaskScheduler.Default);
    }

    // What OPEN CRASH REPORT shows: one report (selected in its folder) or the logs folder.
    private string? _crashReport;

    /// <summary>Crash reports saved since the launcher last looked, e.g. by a game started without it.</summary>
    private void ShowUnseenCrashReports()
    {
        var unseen = new CrashReports(_layout.LogsDirectory).TakeUnseen();
        if (unseen.Count == 0) return;
        _crashReport = unseen.Count == 1 ? unseen[0] : _layout.LogsDirectory;
        CrashReportButton.Content = unseen.Count == 1 ? "OPEN CRASH REPORT" : "OPEN CRASH REPORTS";
        CrashReportButton.IsVisible = true;
        StatusText.Text = unseen.Count == 1
            ? "The game saved a crash report since the launcher last ran. Attach it to your GitHub issue."
            : $"The game saved {unseen.Count} crash reports since the launcher last ran. Attach them to your GitHub issue.";
    }

    private async void OnOpenCrashReport(object? sender, RoutedEventArgs e)
    {
        if (_crashReport is not { } target) return;
        try
        {
            if (File.Exists(target) && OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\"")?.Dispose();
            else if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(
                         File.Exists(target) ? Path.GetDirectoryName(target)! : target)))
                StatusText.Text = $"Could not open the crash report: {target}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                         or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open the crash report: {error.Message}";
        }
    }

    private async void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = App.Options.Layout.LogsFolderToOpen();
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
                StatusText.Text = $"Could not open the logs folder: {folder}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not open the logs folder: {error.Message}";
        }
    }

    private void OnCreateShortcuts(object? sender, RoutedEventArgs e)
    {
        if (ShortcutPlatform.Current is not { } platform || Environment.ProcessPath is not { } launcher) return;
        try
        {
            var created = GameShortcuts.Create(_layout, launcher, platform.Folders, platform);
            StatusText.Text = created.UsesTitleTile
                ? "Shortcuts added to the desktop and the Start menu, with the title tile from your disc."
                : "Shortcuts added to the desktop and the Start menu. Your disc's title tile could not be read, so they show the launcher's icon.";
        }
        // The shell reports a failed save as an HRESULT, which .NET maps to one of these.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException
                                         or ArgumentException)
        {
            StatusText.Text = $"Could not create the shortcuts: {error.Message}";
        }
    }

    /// <summary>Opens the Settings panel with the game's startup settings read fresh from its file.</summary>
    private void ShowSettings()
    {
        try
        {
            RefreshSettings();
            if (_gpus is null) _ = LoadGpusAsync();
            SettingsStatus.Text = "";
        }
        catch (LauncherException error)
        {
            StatusText.Text = error.Message;
            return;
        }
        ShowPanel(SettingsView, SettingsButton);
    }

    // The game exe's GPUs, asked for once (headless, it exits at once).
    private Task<IReadOnlyList<Gpu>> GpusAsync() => _gpuQuery ??= GpuList.QueryAsync(_layout,
        (start, cancel) => HeadlessGame.RunAsync(start, TimeSpan.FromSeconds(15), cancel), CancellationToken.None);

    // Asks the game exe for its GPUs, then offers them.
    private async Task LoadGpusAsync()
    {
        var gpus = await GpusAsync();
        if (_gpus is not null) return;
        _gpus = gpus;
        if (gpus.Count > 0) LauncherCrashHandler.KnownGpu = string.Join("; ", gpus.Select(gpu => gpu.Name));
        if (!SettingsView.IsVisible || gpus.Count == 0) return;
        try
        {
            // Keyboard focus stays on the row it was on.
            var focused = _rows.FindIndex(r => r.Input.IsFocused);
            RefreshSettings();
            if (focused >= 0 && focused < _rows.Count) _rows[focused].Input.Focus();
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
        }
    }

    // Built on each open: the languages follow the game data folder, the GPUs the game's list.
    private void RefreshSettings()
    {
        _settings = new SettingsFile(_layout.SettingsFile,
            SettingsSchema.Committed().WithRuntimeChoices(_layout.GameDataDirectory, _gpus ?? []));
        _onlineAfterName = false;
        BuildSettingRows();
    }

    // The rows come from the game's exported list, grouped under its tabs.
    private void BuildSettingRows()
    {
        _rows.Clear();
        SettingsList.Children.Clear();
        string? tab = null;
        var subheading = "";
        foreach (var setting in _settings!.Schema.Startup)
        {
            if (setting.Tab != tab)
            {
                tab = setting.Tab;
                var heading = new TextBlock
                {
                    Text = tab,
                    Margin = new(0, SettingsList.Children.Count == 0 ? 10 : 18, 0, 4),
                };
                heading.Classes.Add("kicker");
                SettingsList.Children.Add(heading);
                subheading = "";
            }
            // A run of rows sharing a sub-heading (Faster starts) gets it once, above its first row.
            if (setting.Heading != subheading)
            {
                subheading = setting.Heading;
                if (subheading.Length > 0)
                    SettingsList.Children.Add(new TextBlock
                    {
                        Text = subheading, FontSize = 15, FontWeight = FontWeight.SemiBold,
                        Foreground = this.FindResource("GoldBrush") as IBrush, Margin = new(0, 12, 0, 0),
                    });
            }
            var choices = setting.IsText ? null : new ComboBox
            {
                ItemsSource = setting.Choices.Select(c => c.Label).ToList(),
            };
            var textBox = setting.IsText ? new TextBox { MaxLength = setting.MaxLength, Watermark = "Not set" } : null;
            Control input = choices ?? (Control)textBox!;
            input.Width = 200;
            input.HorizontalAlignment = HorizontalAlignment.Right;
            input.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(input, setting.Label);
            var note = new TextBlock
            {
                FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#FFB45A")),
                TextWrapping = TextWrapping.Wrap,
            };
            var help = new TextBlock { Text = setting.Help, TextWrapping = TextWrapping.Wrap };
            help.Classes.Add("small");
            var row = new SettingRow(setting, choices, textBox, help, note);
            if (choices is not null) choices.SelectionChanged += (_, _) => OnChoice(row);
            if (textBox is not null)
            {
                textBox.TextChanged += (_, _) => OnText(row);
                textBox.LostFocus += (_, _) => FinishOnlineAfterName();
                textBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) FinishOnlineAfterName();
                };
            }
            _rows.Add(row);

            var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 6) };
            var text = new StackPanel { Spacing = 2, Margin = new(0, 0, 20, 0) };
            text.Children.Add(new TextBlock
            {
                Text = setting.Label, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
            });
            text.Children.Add(help);
            text.Children.Add(note);
            grid.Children.Add(text);
            // A text row with a default (the Game server) puts it back with one click.
            Control cell = input;
            // The Keyboard row opens the Keys page.
            if (setting.Key == KeyboardKey)
            {
                var keys = new Button { Content = "Keys", VerticalAlignment = VerticalAlignment.Center };
                keys.Classes.Add("secondary");
                AutomationProperties.SetName(keys, "Change the keyboard's keys");
                keys.Click += (_, _) => ShowKeys(keys);
                input.Width = 120;
                cell = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { input, keys },
                };
            }
            if (textBox is not null && setting.Default.Length > 0)
            {
                var useDefault = new Button { Content = "Default", VerticalAlignment = VerticalAlignment.Center };
                useDefault.Classes.Add("secondary");
                AutomationProperties.SetName(useDefault, $"{setting.Label}: use the default");
                ToolTip.SetTip(useDefault, setting.Default);
                useDefault.Click += (_, _) => OnUseDefault(row);
                cell = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { input, useDefault },
                };
            }
            Grid.SetColumn(cell, 1);
            grid.Children.Add(cell);
            SettingsList.Children.Add(grid);
        }
        ShowValues();
    }

    // Selects each row's saved value and greys the Native-only rows while Emulated is chosen.
    private void ShowValues()
    {
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = _settings!.Read();
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
            return;
        }
        _filling = true;
        try
        {
            foreach (var row in _rows)
            {
                var value = values[row.Setting.Key];
                // Typing stays where it is: a text box is only filled when the file differs.
                if (row.Text is not null && row.Text.Text != value) row.Text.Text = value;
                if (row.Choices is not null)
                    row.Choices.SelectedIndex = row.Setting.Choices.ToList().FindIndex(c => c.Value == value);
                // The Graphics preset names the suggestion for the GPU chosen now.
                row.Help.Text = SettingsFile.RowHelp(row.Setting,
                    GpuList.SuggestionFor(_gpus ?? [], values.GetValueOrDefault(GpuKey, GpuList.Automatic)));
                var unavailable = SettingsFile.UnavailableNote(row.Setting, values);
                row.Input.IsEnabled = unavailable is null;
                ShowNote(row, unavailable ?? OnlineNote(row, values));
            }
        }
        finally
        {
            _filling = false;
        }
        CheckGameServer();
        UpdateModsOnlineLine();
    }

    private static void ShowNote(SettingRow row, string? note)
    {
        row.Note.Text = note ?? "";
        row.Note.IsVisible = !string.IsNullOrEmpty(note);
    }

    // What the online rows say beyond their help: Online play waiting for a name, or a name
    // that online play needs (a hand-edited file).
    private string? OnlineNote(SettingRow row, IReadOnlyDictionary<string, string> values) => row.Setting.Key switch
    {
        SettingsFile.OnlineKey when _onlineAfterName => "Type a player name below to turn online play on.",
        SettingsFile.PlayerNameKey when SettingsFile.NeedsPlayerName(values) =>
            "Online play is on: choose a player name, or the game signs in with your profile's name.",
        SettingsFile.PlayerNameKey when _onlineAfterName => SettingsFile.TextError(row.Setting, row.Text?.Text ?? ""),
        _ => null,
    };

    private SettingRow? Row(string key) => _rows.FirstOrDefault(r => r.Setting.Key == key);

    // Saved the moment it changes, so closing the launcher never loses a change.
    private void OnChoice(SettingRow row)
    {
        if (_filling || row.Choices is not { SelectedIndex: >= 0 } choices) return;
        var value = row.Setting.Choices[choices.SelectedIndex].Value;
        // Online play asks for the name first and stays off until one is saved (launcher-online spec).
        if (row.Setting.Key == SettingsFile.OnlineKey && value == "true" &&
            Row(SettingsFile.PlayerNameKey) is { Text: { } name } && !_settings!.CanTurnOnlineOn())
        {
            _onlineAfterName = true;
            SettingsStatus.Text = "Choose a player name first.";
            ShowValues();
            name.Focus();
            return;
        }
        _onlineAfterName = false;
        try
        {
            _settings!.Set(row.Setting.Key, value);
            SettingsStatus.Text = "Saved.";
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
        }
        ShowValues();
    }

    // Checked as the player types; saved once the rule accepts it.
    private void OnText(SettingRow row)
    {
        if (_filling || row.Text is null) return;
        var value = row.Text.Text ?? "";
        // Avalonia raises TextChanged after ShowValues filled the box, and typing can lead back
        // to the saved value: nothing to save, only the notes to show again.
        try
        {
            if (value == _settings!.Read()[row.Setting.Key])
            {
                ShowValues();
                return;
            }
        }
        catch (LauncherException readError)
        {
            SettingsStatus.Text = readError.Message;
            return;
        }
        if (SettingsFile.TextError(row.Setting, value) is { } error)
        {
            ShowNote(row, error);
            return;
        }
        try
        {
            _settings!.Set(row.Setting.Key, value);
            SettingsStatus.Text = _onlineAfterName && row.Setting.Key == SettingsFile.PlayerNameKey
                ? "Saved. Press Enter to turn online play on."
                : "Saved.";
        }
        catch (LauncherException saveError)
        {
            SettingsStatus.Text = saveError.Message;
        }
        ShowValues();
    }

    private void OnUseDefault(SettingRow row)
    {
        try
        {
            _settings!.SetDefault(row.Setting.Key);
            SettingsStatus.Text = "Saved.";
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
        }
        ShowValues();
        ShowNote(row, null);
    }

    // Enter in the name box, leaving it, or closing the panel: online play, chosen before the
    // name, turns on now if a valid name is saved, and stays off otherwise. Escape cancels.
    private void FinishOnlineAfterName(bool cancel = false)
    {
        if (!_onlineAfterName || _settings is null) return;
        _onlineAfterName = false;
        try
        {
            if (cancel || !_settings.CanTurnOnlineOn())
            {
                SettingsStatus.Text = "Online play stays off until you choose a player name.";
            }
            else
            {
                _settings.Set(SettingsFile.OnlineKey, "true");
                SettingsStatus.Text = "Saved. Online play is on.";
            }
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
        }
        ShowValues();
    }

    /// <summary>
    /// The home screen's online line: hidden with online play off (nothing is sent), else the
    /// server's host and whether it answers. Runs when the home screen opens, after an online
    /// setting changes (<paramref name="force"/> false: only when online play or the server
    /// differ from the last check) and on Retry. The request runs on a pool thread after a short
    /// pause, so typing a server address sends one check, and a newer check cancels an older one.
    /// </summary>
    private async void CheckGameServer(bool force = false)
    {
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = new SettingsFile(_layout.SettingsFile, SettingsSchema.Committed()).Read();
        }
        catch (LauncherException)
        {
            OnlineStatus.IsVisible = false;
            return;
        }
        var server = values.GetValueOrDefault(SettingsFile.GameServerKey, "");
        var checkedFor = (values.GetValueOrDefault(SettingsFile.OnlineKey, ""), server);
        if (!force && _serverCheckedFor == checkedFor) return;
        _serverCheckedFor = checkedFor;
        _serverCheckCancel?.Cancel();
        _serverCheckCancel?.Dispose();
        _serverCheckCancel = null;
        if (values.GetValueOrDefault(SettingsFile.OnlineKey) != "true")
        {
            OnlineStatus.IsVisible = false;
            return;
        }
        var cancel = _serverCheckCancel = new CancellationTokenSource();
        ShowServerStatus(GameServerStatus.Checking, server);
        try
        {
            if (!force) await Task.Delay(TimeSpan.FromMilliseconds(600), cancel.Token);
            var status = await Task.Run(() => _serverCheck.CheckAsync(values, cancel.Token), cancel.Token);
            if (!cancel.IsCancellationRequested) ShowServerStatus(status, server);
        }
        catch (OperationCanceledException)
        {
            // A newer check, or the launcher closing.
        }
        // An exception leaving an async void method would close the launcher.
        catch (Exception)
        {
            if (!cancel.IsCancellationRequested) ShowServerStatus(GameServerStatus.NotAnswering, server);
        }
    }

    private void ShowServerStatus(GameServerStatus status, string server)
    {
        OnlineStatusText.Text = GameServerCheck.StatusLine(status, server);
        OnlineStatus.IsVisible = OnlineStatusText.Text.Length > 0;
        OnlineRetryButton.IsVisible = status is GameServerStatus.NotAnswering or GameServerStatus.NotAGameServer;
    }

    /// <summary>
    /// A new player starts on the Graphics preset the game suggests for the chosen GPU; a saved
    /// choice is never overwritten. Without a GPU list the game applies it at its first start.
    /// </summary>
    private async Task ApplySuggestedPreset()
    {
        try
        {
            var gpus = await GpusAsync();
            _gpus ??= gpus;
            var file = new SettingsFile(_layout.SettingsFile, SettingsSchema.Committed());
            file.ApplySuggestedPresetAtSetup(GpuList.SuggestionFor(gpus,
                file.Read().GetValueOrDefault(GpuKey, GpuList.Automatic)));
        }
        catch (LauncherException)
        {
            // Not writing the settings is no reason to stop the setup: the game applies it later.
        }
    }

    private async void OnResetSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            // The Graphics preset goes back to the suggestion for the GPU after the reset:
            // Automatic, the GPU setting's default.
            var gpus = _gpus ?? await GpusAsync();
            _settings!.ResetToDefaults(GpuList.SuggestionFor(gpus, GpuList.Automatic));
            SettingsStatus.Text = "Settings reset to their defaults.";
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
        }
        ShowValues();
    }
}
