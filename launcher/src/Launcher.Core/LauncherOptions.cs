using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// The launcher's own command line. With no arguments it opens its window on the folder it
/// sits in, using the game data folder chosen at setup. <c>--root</c>, <c>--game-data</c> and
/// <c>--game</c> point it at other folders (a developer checkout). <c>--print-arguments</c>,
/// <c>--verify &lt;iso&gt;</c>, <c>--extract &lt;iso&gt;</c> and <c>--repair &lt;iso&gt;</c> run headless; <c>--restart</c>
/// makes <c>--extract</c> discard a partial folder instead of resuming it, and
/// <c>--manifest</c> replaces the built-in disc manifest. <c>--prepare-shaders</c> runs setup's
/// shader step headless (after <c>--extract</c> when both are given); <c>--xenosrecomp</c> names
/// another XenosRecomp.exe, with <c>shader_common.h</c> beside it. <c>--prepare-pipelines</c> then runs the
/// game's prepare-pipelines mode headless (zero-stutter ticket 07). <c>--title-update &lt;path&gt;</c>
/// names Title Update 2 (a package, a folder or an archive): <c>--verify</c> checks it too,
/// <c>--extract</c> unpacks it next to the game data (it requires TU2), <c>--repair</c> repairs the
/// update folder from it, and alone it adds TU2 to a finished install (the 0.9.0 upgrade).
/// <c>--tu-manifest</c> replaces the built-in TU2 manifest. <c>--add-dlc &lt;path&gt;</c> checks and
/// installs the DLC packs in a package, folder or archive. <c>--reduce-effects</c> opens the
/// window without Mica and animations, for a launcher that freezes before its settings open.
/// <c>--debug-crash</c> and <c>--debug-crash-task</c> make the window launcher throw on its UI
/// thread or on a background task, to test its crash report.
/// </summary>
public sealed record LauncherOptions(PortableLayout Layout, bool PrintArguments)
{
    public string? VerifyImage { get; init; }
    public string? ExtractImage { get; init; }
    public string? RepairImage { get; init; }
    public bool Restart { get; init; }
    public string? ManifestPath { get; init; }
    public bool PrepareShaders { get; init; }
    public bool PreparePipelines { get; init; }
    public string? TitleUpdate { get; init; }
    public string? TitleUpdateManifestPath { get; init; }
    public string? AddDlc { get; init; }
    public bool ReduceEffects { get; init; }
    public LauncherDebugCrash DebugCrash { get; init; }

    /// <summary>XenosRecomp: in the release, <c>xenosrecomp/XenosRecomp.exe</c> beside the launcher.</summary>
    public string ShaderConverterExecutable
    {
        get => _shaderConverter ?? Path.Combine(Layout.ShaderConverterDirectory, XenosRecompConverter.ExecutableName);
        init => _shaderConverter = Path.GetFullPath(value);
    }

    private readonly string? _shaderConverter;

    public bool Headless => PrintArguments || VerifyImage is not null || ExtractImage is not null || RepairImage is not null ||
        PrepareShaders || PreparePipelines || TitleUpdate is not null || AddDlc is not null;

    public DiscManifest Manifest => ManifestPath is null ? DiscManifest.Supported : LoadManifest(ManifestPath);

    public TitleUpdateManifest TitleUpdateManifest => TitleUpdateManifestPath is null
        ? TitleUpdateManifest.Supported
        : Load(TitleUpdateManifestPath, "title update manifest", path => TitleUpdateManifest.Parse(File.ReadAllText(path)));

    public static LauncherOptions Parse(IReadOnlyList<string> args, string launcherDirectory)
    {
        string root = launcherDirectory;
        string? gameData = null, game = null, verify = null, extract = null, repair = null, manifest = null, xenosRecomp = null,
            titleUpdate = null, titleUpdateManifest = null, addDlc = null;
        bool printArguments = false, restart = false, prepareShaders = false, preparePipelines = false, reduceEffects = false;
        var debugCrash = LauncherDebugCrash.None;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--print-arguments": printArguments = true; break;
                case "--verify": verify = Value(args, ref i); break;
                case "--extract": extract = Value(args, ref i); break;
                case "--repair": repair = Value(args, ref i); break;
                case "--restart": restart = true; break;
                case "--manifest": manifest = Value(args, ref i); break;
                case "--root": root = Value(args, ref i); break;
                case "--game-data": gameData = Value(args, ref i); break;
                case "--game": game = Value(args, ref i); break;
                case "--prepare-shaders": prepareShaders = true; break;
                case "--prepare-pipelines": preparePipelines = true; break;
                case "--xenosrecomp": xenosRecomp = Value(args, ref i); break;
                case "--title-update": titleUpdate = Value(args, ref i); break;
                case "--tu-manifest": titleUpdateManifest = Value(args, ref i); break;
                case "--add-dlc": addDlc = Value(args, ref i); break;
                case "--reduce-effects": reduceEffects = true; break;
                case "--debug-crash": debugCrash = LauncherDebugCrash.UiThread; break;
                case "--debug-crash-task": debugCrash = LauncherDebugCrash.BackgroundTask; break;
                default: throw new LauncherException($"Unknown option: {args[i]}");
            }
        }
        // Without --game-data, the folder chosen at setup, remembered in userdata.
        gameData ??= LauncherState.RememberedGameData(new PortableLayout(root).UserDataDirectory);
        var options = new LauncherOptions(new PortableLayout(root, gameData, game), printArguments)
        {
            VerifyImage = verify,
            ExtractImage = extract,
            RepairImage = repair,
            Restart = restart,
            ManifestPath = manifest,
            PrepareShaders = prepareShaders,
            PreparePipelines = preparePipelines,
            TitleUpdate = titleUpdate,
            TitleUpdateManifestPath = titleUpdateManifest,
            AddDlc = addDlc,
            ReduceEffects = reduceEffects,
            DebugCrash = debugCrash,
        };
        return xenosRecomp is null ? options : options with { ShaderConverterExecutable = xenosRecomp };
    }

    /// <summary>The hard links the mod library makes; the App sets the platform's, and headless runs copy.</summary>
    public IFileLinker FileLinker { get; init; } = new CopyingLinker();

    /// <summary>Where the mod library sends a removed mod; the App sets the platform's Recycle Bin.</summary>
    public IRecycleBin RecycleBin { get; init; } = new PlainDelete();

    /// <summary>The mod library on these options' folders and manifests, with TU2's inner inis.</summary>
    public ModLibrary NewModLibrary() => new(Layout,
        KnownGameFiles.From(Manifest, TitleUpdateManifest, ModWrapping.ConfigFilesIn(Layout.UpdateDataDirectory)),
        FileLinker, RecycleBin);

    private sealed class CopyingLinker : IFileLinker
    {
        public bool TryHardLink(string existingFile, string newLink) => false;
    }

    private sealed class PlainDelete : IRecycleBin
    {
        public void Recycle(string folder) => Directory.Delete(folder, recursive: true);
    }

    /// <summary>Setup's shader step, with the XenosRecomp these options name.</summary>
    public ShaderPreparation NewShaderPreparation() =>
        new(new XenosRecompConverter(ShaderConverterExecutable), ShaderConverter.Id);

    private static DiscManifest LoadManifest(string path) => Load(path, "disc manifest", DiscManifest.Load);

    private static T Load<T>(string path, string what, Func<string, T> load)
    {
        try
        {
            return load(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LauncherException($"The {what} cannot be read: {path}. {error.Message}", error);
        }
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new LauncherException($"{args[i]} needs a path after it.");
        return args[++i];
    }
}

/// <summary>The launcher's modes that print and exit without a window.</summary>
public static class HeadlessCommands
{
    public static int Run(LauncherOptions options, TextWriter output, Func<DateTimeOffset> clock)
    {
        try
        {
            if (options.VerifyImage is { } verify)
            {
                NewSetup(options).Verify(verify);
                output.WriteLine($"{verify}: the supported WWE All Stars disc.");
                if (options.TitleUpdate is { } update) VerifyTitleUpdate(options, update, output);
            }
            if (options.ExtractImage is { } extract)
            {
                Extract(options, extract, output);
                AddTitleUpdate(options, output);
            }
            else if (options.TitleUpdate is not null && options.VerifyImage is null && options.RepairImage is null)
            {
                // The 0.9.0 upgrade: TU2 for an install that has the disc files already.
                if (NewSetup(options).StatusOf(options.Layout.GameDataDirectory) != SetupStatus.Done)
                    throw new LauncherException($"The game data folder is not set up: {options.Layout.GameDataDirectory}. Run --extract first.");
                AddTitleUpdate(options, output);
            }
            if (options.RepairImage is { } repair)
            {
                Repair(options, repair, output);
                RepairTitleUpdate(options, output);
            }
            if (options.AddDlc is { } dlc) AddDlc(options, dlc, output);
            if (options.PrepareShaders) PrepareShaders(options, output);
            if (options.PreparePipelines) PreparePipelines(options, output);
        }
        catch (SetupException error)
        {
            throw new LauncherException(error.Message, error);
        }
        catch (ShaderPreparationException error)
        {
            throw new LauncherException(error.Message, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LauncherException("Setup failed: " + error.Message, error);
        }
        if (options.PrintArguments)
        {
            var arguments = LaunchArguments.Build(options.Layout, options.Layout.NewSessionLogPath(clock()));
            output.WriteLine(string.Join(' ', new[] { options.Layout.GameExecutable }.Concat(arguments).Select(Quote)));
        }
        return 0;
    }

    private static GameSetup NewSetup(LauncherOptions options) =>
        new(options.Manifest, new DriveDiskSpace(), TimeProvider.System);

    private static TitleUpdateSetup NewTitleUpdateSetup(LauncherOptions options) =>
        new(options.TitleUpdateManifest, new DriveDiskSpace(), TimeProvider.System);

    private static void VerifyTitleUpdate(LauncherOptions options, string input, TextWriter output)
    {
        using var intake = PackageIntake.Scan([input], Path.GetTempPath(), CancellationToken.None);
        var package = TitleUpdateSetup.Choose(intake);
        NewTitleUpdateSetup(options).Verify(package, new ConsoleProgress(output), CancellationToken.None);
        output.WriteLine($"{input}: Title Update 2, every block verified.");
    }

    /// <summary>Unpacks TU2 next to the game data; without --title-update, says what is missing.</summary>
    private static void AddTitleUpdate(LauncherOptions options, TextWriter output)
    {
        var setup = NewTitleUpdateSetup(options);
        var update = options.Layout.UpdateDataDirectory;
        if (options.TitleUpdate is not { } input)
        {
            if (setup.StatusOf(update) == SetupStatus.Done) return;
            throw new LauncherException(TitleUpdateSetup.WhatTitleUpdate2Is + " Pass it with --title-update <file or folder>.");
        }
        using var intake = PackageIntake.Scan([input], Path.GetTempPath(), CancellationToken.None);
        setup.Install(TitleUpdateSetup.Choose(intake), update, options.Restart ? SetupStart.Restart : SetupStart.Resume,
            new ConsoleProgress(output), CancellationToken.None);
        output.WriteLine($"Title Update 2 ready: {update}");
    }

    /// <summary>Re-verifies the update folder; repairs it from --title-update, which only a bad file needs.</summary>
    private static void RepairTitleUpdate(LauncherOptions options, TextWriter output)
    {
        var setup = NewTitleUpdateSetup(options);
        var update = options.Layout.UpdateDataDirectory;
        var check = setup.CheckInstalled(update, new ConsoleProgress(output), CancellationToken.None);
        if (check.NothingToRepair)
        {
            // A folder that lost its marker but whose files verify is set up again.
            setup.MarkVerified(update, check);
            output.WriteLine($"Nothing to repair: every Title Update 2 file in {update} verifies.");
            return;
        }
        foreach (var path in check.Missing) output.WriteLine($"missing: {path}");
        foreach (var path in check.Damaged) output.WriteLine($"damaged: {path}");
        if (options.TitleUpdate is not { } input)
            throw new LauncherException($"{check.Summary()} Repairing Title Update 2 needs the update: pass --title-update <file or folder>.");
        using var intake = PackageIntake.Scan([input], Path.GetTempPath(), CancellationToken.None);
        var repaired = setup.Repair(TitleUpdateSetup.Choose(intake), update, check, new ConsoleProgress(output), CancellationToken.None);
        output.WriteLine($"Repaired {RepairCheck.FileCount(repaired.Count)} in {update}");
    }

    private static void Extract(LauncherOptions options, string image, TextWriter output)
    {
        var layout = options.Layout;
        LauncherState.RememberGameData(layout.UserDataDirectory, layout.GameDataDirectory);
        NewSetup(options).Run(image, layout.GameDataDirectory, options.Restart ? SetupStart.Restart : SetupStart.Resume,
            new ConsoleProgress(output), CancellationToken.None);
        output.WriteLine($"Game data ready: {layout.GameDataDirectory}");
    }

    /// <summary>Re-verifies an installed game data folder and re-extracts only the files that fail.</summary>
    private static void Repair(LauncherOptions options, string image, TextWriter output)
    {
        var gameData = options.Layout.GameDataDirectory;
        var setup = NewSetup(options);
        var marked = setup.StatusOf(gameData) == SetupStatus.Done;
        if (!marked && !setup.HoldsGameFiles(gameData))
            throw new LauncherException($"The game data folder is not set up: {gameData}. Run --extract first.");
        var check = setup.CheckInstalled(gameData, new ConsoleProgress(output), CancellationToken.None);
        if (check.NothingToRepair)
        {
            // A folder that lost its marker still goes through Repair, which checks the disc and marks it.
            if (!marked) setup.Repair(image, gameData, check, new ConsoleProgress(output), CancellationToken.None);
            output.WriteLine($"Nothing to repair: every game file in {gameData} verifies.");
            return;
        }
        foreach (var path in check.Missing) output.WriteLine($"missing: {path}");
        foreach (var path in check.Damaged) output.WriteLine($"damaged: {path}");
        var repaired = setup.Repair(image, gameData, check, new ConsoleProgress(output), CancellationToken.None);
        output.WriteLine($"Repaired {RepairCheck.FileCount(repaired.Count)} in {gameData}");
    }

    private static void AddDlc(LauncherOptions options, string input, TextWriter output)
    {
        var library = new DlcLibrary(options.Layout.UserDataDirectory);
        using var intake = PackageIntake.Scan([input], Path.GetTempPath(), CancellationToken.None);
        foreach (var package in DlcLibrary.Choose(intake))
        {
            var last = -1;
            var pack = library.Install(package, new ProgressRelay<DlcProgress>(value =>
            {
                var percent = (int)(value.Fraction * 100) + (value.Unpacking ? 1000 : 0);
                if (percent == last) return;
                last = percent;
                output.WriteLine($"[{percent % 1000,3}%] {(value.Unpacking ? "unpacking" : "checking")} {Path.GetFileName(package)}");
            }), CancellationToken.None);
            var wrestlers = pack.Wrestlers.Count > 0 ? $" ({string.Join(", ", pack.Wrestlers)})" : "";
            output.WriteLine($"DLC installed: {pack.DisplayName}{wrestlers}, {pack.Bytes / 1e6:0} MB");
        }
    }

    /// <summary>Setup's shader step over the game data and the mods turned on, recorded as the App's is (ticket 09).</summary>
    private static void PrepareShaders(LauncherOptions options, TextWriter output)
    {
        var layout = options.Layout;
        var library = options.NewModLibrary();
        var mods = library.ShaderSources();
        var last = (Step: (ShaderStep)(-1), Percent: -1);
        options.NewShaderPreparation().Run(layout.GameDataDirectory, layout.CacheDirectory, new ProgressRelay<ShaderProgress>(value =>
        {
            var percent = (int)(value.Fraction * 100);
            if ((value.Step, percent) == last) return;
            last = (value.Step, percent);
            output.WriteLine($"[{percent,3}%] {ShaderStepName(value.Step)}");
        }), CancellationToken.None, mods.Folders);
        library.ShadersPrepared(mods);
        output.WriteLine($"Shaders ready: {Path.Combine(layout.CacheDirectory, ShaderPackFile.FileName)}");
    }

    /// <summary>The prepare's longest wait: past it the game is taken to hang and is ended.</summary>
    public static readonly TimeSpan PipelineTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The game's prepare mode on the update folder Play would start it on: the mods in use under
    /// the player's online setting (ticket 09). The combined content Play builds is removed after.
    /// </summary>
    private static void PreparePipelines(LauncherOptions options, TextWriter output)
    {
        var layout = options.Layout;
        var library = options.NewModLibrary();
        var prepared = library.PrepareForPlay(layout.UpdateDataDirectory, SettingsFile.OnlinePlayOn(layout.SettingsFile));
        PipelineResult result;
        try
        {
            var last = (Stage: (PipelineStage)(-1), Percent: -1);
            result = new PipelinePreparation(layout, new StreamingPrepareRunner(PipelineTimeout), prepared.UpdateDataRoot).RunAsync(
                new ProgressRelay<PipelineProgress>(value =>
                {
                    var percent = (int)(value.Fraction * 100);
                    if ((value.Stage, percent) == last) return;
                    last = (value.Stage, percent);
                    output.WriteLine($"[{percent,3}%] {PipelineStageName(value.Stage)} ({value.Done}/{value.Total})");
                }), CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            library.RemoveCombinedContent();
        }
        if (!result.Usable) throw new LauncherException(result.Message);
        output.WriteLine(result.Message);
    }

    public static string PipelineStageName(PipelineStage stage) => stage switch
    {
        PipelineStage.Modules => "Loading shaders",
        PipelineStage.Libraries => "Building shader libraries",
        _ => "Building pipelines",
    };

    public static string ShaderStepName(ShaderStep step) => step switch
    {
        ShaderStep.ReadingGameFiles => "Reading shaders from the game files",
        ShaderStep.Converting => "Converting shaders",
        ShaderStep.WritingPack => "Writing the shader pack",
        _ => "Done",
    };

    /// <summary>One line per step and per whole percent.</summary>
    private sealed class ConsoleProgress(TextWriter output) : IProgress<SetupProgress>
    {
        private (SetupStep, int) _last = ((SetupStep)(-1), -1);

        public void Report(SetupProgress value)
        {
            var percent = (int)(value.Fraction * 100);
            if ((value.Step, percent) == _last) return;
            _last = (value.Step, percent);
            var left = value.TimeLeft is { } time ? $" ({time:h\\:mm\\:ss} left)" : "";
            output.WriteLine($"[{percent,3}%] {StepName(value.Step)} {value.CurrentFile}{left}".TrimEnd());
        }

        private static string StepName(SetupStep step) => step switch
        {
            SetupStep.CheckingImage => "Checking the disc image",
            SetupStep.CheckingExistingFiles => "Checking files from the last setup",
            SetupStep.Extracting => "Extracting",
            SetupStep.Finishing => "Finishing",
            SetupStep.VerifyingGameFiles => "Verifying game files",
            _ => "Done",
        };
    }

    /// <summary>Quotes one argument the way CommandLineToArgvW reads it back.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            text.Append(c);
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}

/// <summary>Where <c>--debug-crash</c> / <c>--debug-crash-task</c> make the launcher throw.</summary>
public enum LauncherDebugCrash { None, UiThread, BackgroundTask }
