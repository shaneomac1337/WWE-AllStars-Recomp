using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>Converts Xenos shader containers to SPIR-V; the real one runs XenosRecomp.</summary>
public interface IShaderConverter
{
    /// <summary>
    /// Converts every container file in <paramref name="inputDirectory"/>, writing
    /// <c>shader_cache.cpp</c> and an <c>hlsl</c> folder into <paramref name="outputDirectory"/>.
    /// Progress runs from 0 to 1.
    /// </summary>
    /// <exception cref="ShaderConverterException">The conversion failed.</exception>
    void Convert(string inputDirectory, string outputDirectory, IProgress<double>? progress, CancellationToken cancel);
}

public sealed class ShaderConverterException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The shader step failed; the message says why and that the game runs on Emulated.</summary>
public sealed class ShaderPreparationException(string message, Exception? inner = null) : Exception(message, inner);

public enum ShaderStep
{
    ReadingGameFiles,
    Converting,
    WritingPack,
    Done,
}

/// <summary>The shader step's progress: the step, and the fraction of the whole step done.</summary>
public sealed record ShaderProgress(ShaderStep Step, double Fraction);

/// <summary>
/// Where the shaders stand: the pack's state, and how many containers the game left in the
/// top-up folder that the pack does not hold yet.
/// </summary>
public sealed record ShaderStatus(ShaderPackCheck Pack, int WaitingShaders)
{
    public bool NeedsPreparing => !Pack.Ready || WaitingShaders > 0;

    /// <summary>
    /// A usable pack lacks only shaders the game found (the autosave icon, the splash, the movie
    /// shaders): the launcher adds them by itself once the session ends, so the next start has
    /// them without a click (owner decision 2026-10-04). A missing or stale pack is still offered.
    /// </summary>
    public bool AddsAfterSession => Pack.Ready && WaitingShaders > 0;

    /// <summary>What the launcher tells the player, or empty when nothing is needed.</summary>
    public string Message => Pack.Problem switch
    {
        ShaderPackProblem.None when WaitingShaders > 0 =>
            $"The game found {WaitingShaders} new shader{(WaitingShaders == 1 ? "" : "s")}. Prepare shaders to add them to Native.",
        ShaderPackProblem.None => "",
        ShaderPackProblem.Stale =>
            "The Native shaders were prepared for another version of the game, so it runs on Emulated. Prepare shaders again to play on Native.",
        ShaderPackProblem.Damaged =>
            "The Native shaders are damaged, so the game runs on Emulated. Prepare shaders again to play on Native.",
        _ => "The Native shaders are not prepared, so the game runs on Emulated. Prepare shaders to play on Native.",
    };
}

/// <summary>
/// Setup's last step, "Preparing shaders" (spec, "Shader pack"; ADR-0012): read every shader
/// container from the extracted disc's packages, add any the game found at runtime (the
/// top-up folder), convert them with XenosRecomp and write the shader pack into the cache
/// folder. A failure or a cancel leaves any earlier pack in place; without a usable pack the
/// game runs on Emulated.
/// </summary>
public sealed partial class ShaderPreparation(IShaderConverter converter, ulong converterId)
{
    /// <summary>Where the game leaves containers it compiled at runtime that the pack lacks.</summary>
    public const string TopUpFolderName = "shader-topup";

    /// <summary>
    /// A top-up the game converted live (ticket 22): a one-shader pack beside its container,
    /// <c>ps_&lt;fnv&gt;.shaderpack</c>. The game merges it into the pack at startup.
    /// </summary>
    public const string ConvertedExtension = ".shaderpack";

    private const string WorkFolderName = "shader-build";

    // Progress: reading the packages, then XenosRecomp (the long part), then the pack.
    private const double ReadShare = 0.3;
    private const double ConvertShare = 0.65;

    public ShaderStatus StatusOf(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, ShaderPackFile.FileName);
        var check = ShaderPackFile.Check(path, converterId);
        var topUps = TopUpContainers(cacheDirectory);
        if (!check.Ready || topUps.Count == 0) return new ShaderStatus(check, check.Ready ? 0 : topUps.Count);
        var packed = ShaderPackFile.ConvertedShaders(path);
        return new ShaderStatus(check,
            topUps.Count(file => !packed.Contains(ContainerHash(file)) && !ShaderPackFile.Check(ConvertedPath(file), converterId).Ready));
    }

    /// <summary>Where the game puts a top-up container it converted live.</summary>
    public static string ConvertedPath(string containerFile) => Path.ChangeExtension(containerFile, ConvertedExtension);

    /// <param name="modPackageFolders">The mods' <c>packages/</c> folders, read like the disc's (ticket 09).</param>
    /// <exception cref="ShaderPreparationException">The step failed; the game runs on Emulated.</exception>
    /// <exception cref="OperationCanceledException">The step was cancelled.</exception>
    public void Run(string gameDataDirectory, string cacheDirectory, IProgress<ShaderProgress>? progress, CancellationToken cancel,
        IReadOnlyList<string>? modPackageFolders = null)
    {
        var work = Path.Combine(cacheDirectory, WorkFolderName);
        var pack = Path.Combine(cacheDirectory, ShaderPackFile.FileName);
        var temp = pack + ".tmp";
        try
        {
            progress?.Report(new ShaderProgress(ShaderStep.ReadingGameFiles, 0));
            var containers = DiscShaders.Read([gameDataDirectory, .. modPackageFolders ?? []],
                Scaled<(int Done, int Total)>(progress, ShaderStep.ReadingGameFiles, 0, ReadShare, count => (double)count.Done / count.Total),
                cancel);
            if (containers.Count == 0)
                throw new ShaderPreparationException(Failed("no shaders were found in the game files"));

            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            var input = Path.Combine(work, "containers");
            var output = Path.Combine(work, "converted");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(output);
            foreach (var container in containers)
                File.WriteAllBytes(Path.Combine(input, container.Name + ".bin"), container.Bytes);
            foreach (var file in TopUpContainers(cacheDirectory))
                File.Copy(file, Path.Combine(input, Path.GetFileName(file)), overwrite: true);

            cancel.ThrowIfCancellationRequested();
            converter.Convert(input, output, Scaled<double>(progress, ShaderStep.Converting, ReadShare, ConvertShare, fraction => fraction), cancel);
            cancel.ThrowIfCancellationRequested();

            progress?.Report(new ShaderProgress(ShaderStep.WritingPack, ReadShare + ConvertShare));
            File.WriteAllBytes(temp, ShaderPackFile.Build(output, converterId));
            File.Move(temp, pack, overwrite: true);
            progress?.Report(new ShaderProgress(ShaderStep.Done, 1));
        }
        catch (ShaderConverterException error)
        {
            throw new ShaderPreparationException(Failed(error.Message), error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ShaderPreparationException(Failed(error.Message), error);
        }
        finally
        {
            TryDelete(temp);
            if (Directory.Exists(work))
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static string Failed(string why) =>
        $"The Native shaders could not be prepared: {why.TrimEnd('.')}. " +
        "The game runs on Emulated until they are; you can prepare them again from the home screen.";

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The top-up folder's container files, named like <c>--wwe_shader_dump_dir</c>.</summary>
    private static List<string> TopUpContainers(string cacheDirectory)
    {
        var folder = Path.Combine(cacheDirectory, TopUpFolderName);
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*.bin").Where(file => ContainerName().IsMatch(Path.GetFileName(file)))];
    }

    private static ulong ContainerHash(string file) =>
        ulong.Parse(ContainerName().Match(Path.GetFileName(file)).Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

    private static IProgress<T>? Scaled<T>(IProgress<ShaderProgress>? progress, ShaderStep step, double start, double share,
        Func<T, double> fraction) =>
        progress is null ? null : new ProgressRelay<T>(value => progress.Report(new ShaderProgress(step, start + share * Math.Clamp(fraction(value), 0, 1))));

    [GeneratedRegex(@"^(?:ps|vs)_([0-9a-f]{16})\.bin$")]
    private static partial Regex ContainerName();
}
