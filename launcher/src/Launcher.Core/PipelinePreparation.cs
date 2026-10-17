using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>Why the launcher rebuilds pipelines before the next Play, or why it does not.</summary>
public enum PipelineRebuild
{
    /// <summary>The last prepare still matches its inputs.</summary>
    None,
    /// <summary>No usable shader pack: Prepare shaders is offered first, as before.</summary>
    NoShaderPack,
    /// <summary>Repair rebuilds whatever the state.</summary>
    Repair,
    /// <summary>No pipeline store, or none the launcher prepared.</summary>
    NoStore,
    /// <summary>The game met a store or driver cache from another GPU or driver and left its marker.</summary>
    Stale,
    PackChanged,
    TopUpsChanged,
    /// <summary>A new game build ships another key list (<c>pipeline-keys.txt</c>).</summary>
    KeyListChanged,
    /// <summary>Play starts the game on another update folder, or its files changed: other mods are in use.</summary>
    UpdateFolderChanged,
}

public static class PipelineRebuildExtensions
{
    public static bool Rebuilds(this PipelineRebuild rebuild) => rebuild is not (PipelineRebuild.None or PipelineRebuild.NoShaderPack);
}

/// <summary>Runs one process and hands each standard output line over as it arrives; tests replace it.</summary>
public interface IPrepareRunner
{
    /// <summary>Returns the exit code.</summary>
    /// <exception cref="LauncherException">The process did not start or did not exit in time.</exception>
    Task<int> RunAsync(ProcessStart start, Action<string> onLine, CancellationToken cancel);
}

/// <summary>
/// Setup's pipeline step (spec, "When the launcher rebuilds pipelines"): runs the game's
/// headless prepare-pipelines mode (ticket 06), which writes the pipeline store and the
/// driver cache under <c>cache/native-pipelines</c>. After a finished prepare the launcher
/// records what it was built from (the pack, the top-ups, the shipped key list and the update
/// folder with its files); a change
/// to any of them, a missing store, or the game's "pipelines stale" marker means a rebuild
/// before the next Play, without a click. Repair always rebuilds.
/// </summary>
/// <param name="updateDataRoot">The update folder Play starts the game on (the mods' folder when mods are in use,
/// ticket 09), so the prepare plans the materials the game will load; TU2's own folder when null.</param>
public sealed class PipelinePreparation(PortableLayout layout, IPrepareRunner runner, string? updateDataRoot = null)
{
    public const string StoreFolderName = "native-pipelines";
    public const string KeyListFileName = "pipeline-keys.txt";
    private const string StaleMarkerName = "pipelines-stale";
    private const string InputsFileName = "launcher-inputs";
    private const string InputsHeader = "wwe-pipelines-inputs 1";
    private static readonly string[] StoreFiles = ["keys.bin", "driver-cache.bin"];

    private string Store => Path.Combine(layout.CacheDirectory, StoreFolderName);

    /// <summary>Whether the next Play needs a prepare first, and why.</summary>
    public PipelineRebuild Check(bool shaderPackReady, bool repair = false)
    {
        if (!shaderPackReady) return PipelineRebuild.NoShaderPack;
        if (repair) return PipelineRebuild.Repair;
        if (StoreFiles.Any(file => !File.Exists(Path.Combine(Store, file)))) return PipelineRebuild.NoStore;
        if (File.Exists(Path.Combine(Store, StaleMarkerName))) return PipelineRebuild.Stale;
        var recorded = ReadInputs();
        if (recorded is null) return PipelineRebuild.NoStore;
        var now = CurrentInputs();
        if (now.Pack != recorded.Value.Pack) return PipelineRebuild.PackChanged;
        if (now.TopUps != recorded.Value.TopUps) return PipelineRebuild.TopUpsChanged;
        if (now.Keys != recorded.Value.Keys) return PipelineRebuild.KeyListChanged;
        if (now.Update != recorded.Value.Update) return PipelineRebuild.UpdateFolderChanged;
        return PipelineRebuild.None;
    }

    /// <summary>Runs the prepare. A failure is a result, never an exception, except a cancel.</summary>
    public async Task<PipelineResult> RunAsync(IProgress<PipelineProgress>? progress, CancellationToken cancel)
    {
        var inputs = CurrentInputs();
        var output = new PipelineOutput();
        int exitCode;
        try
        {
            Directory.CreateDirectory(layout.LogsDirectory);
            exitCode = await runner.RunAsync(
                new ProcessStart(layout.GameExecutable, layout.GameDirectory, LaunchArguments.PreparePipelines(layout, updateDataRoot)),
                line =>
                {
                    if (output.Read(line) is { } report) progress?.Report(report);
                }, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
        {
            return new PipelineResult(PipelineOutcome.Crashed, -1, 0, 0, null) { StartError = error.Message };
        }
        var result = output.Finish(exitCode);
        if (result.Usable) WriteInputs(inputs);
        return result;
    }

    private (string Pack, string TopUps, string Keys, string Update) CurrentInputs()
    {
        var topUps = Path.Combine(layout.CacheDirectory, ShaderPreparation.TopUpFolderName);
        // A top-up the game converted live had its pipelines made in that session (ticket 22).
        var names = Directory.Exists(topUps)
            ? string.Join('\n', Directory.EnumerateFiles(topUps, "*.bin")
                .Where(file => !File.Exists(ShaderPreparation.ConvertedPath(file)))
                .Select(Path.GetFileName).Order(StringComparer.Ordinal))
            : "";
        return (FileHash(Path.Combine(layout.CacheDirectory, ShaderPackFile.FileName)),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(names))),
            FileHash(Path.Combine(layout.GameDirectory, KeyListFileName)),
            UpdateStamp(updateDataRoot ?? layout.UpdateDataDirectory));
    }

    /// <summary>
    /// The update folder the game plans from: its path and each file's name, size and time. The
    /// mods' folder is rebuilt with new files when other mods are in use (ADR-0026).
    /// </summary>
    private static string UpdateStamp(string folder)
    {
        var stamp = new StringBuilder(Path.GetFullPath(folder)).Append('\n');
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles().OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
                stamp.Append(CultureInfo.InvariantCulture, $"{file.Name} {file.Length} {file.LastWriteTimeUtc.Ticks}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A missing folder is stamped by its path alone.
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
    }

    private static string FileHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return "none";
        }
    }

    private (string Pack, string TopUps, string Keys, string Update)? ReadInputs()
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(Store, InputsFileName));
            if (lines.Length < 4 || lines[0] != InputsHeader) return null;
            var fields = lines.Skip(1).Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);
            if (!fields.TryGetValue("pack", out var pack) || !fields.TryGetValue("topups", out var topUps) ||
                !fields.TryGetValue("keys", out var keys))
                return null;
            // A prepare recorded before the update folder was recorded (1.3.0) planned from TU2's folder.
            return (pack, topUps, keys, fields.GetValueOrDefault("update") ?? UpdateStamp(layout.UpdateDataDirectory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteInputs((string Pack, string TopUps, string Keys, string Update) inputs)
    {
        try
        {
            Directory.CreateDirectory(Store);
            File.WriteAllText(Path.Combine(Store, InputsFileName),
                $"{InputsHeader}\npack={inputs.Pack}\ntopups={inputs.TopUps}\nkeys={inputs.Keys}\nupdate={inputs.Update}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Without the record the next Play prepares again; nothing else is lost.
        }
    }
}

/// <summary>
/// Runs the game's prepare mode with its standard output redirected and read line by line.
/// The mode exits by itself in seconds. The launcher never kills the game (a killed GPU
/// process can take the driver down with it): past <paramref name="timeout"/> it asks the
/// game to close, waits <paramref name="closeGrace"/>, then stops waiting and reports it,
/// leaving the process alone. A cancel stops waiting too, also without ending the process.
/// </summary>
public sealed class StreamingPrepareRunner(TimeSpan timeout, TimeSpan? closeGrace = null) : IPrepareRunner
{
    private readonly TimeSpan _closeGrace = closeGrace ?? TimeSpan.FromSeconds(30);

    public async Task<int> RunAsync(ProcessStart start, Action<string> onLine, CancellationToken cancel)
    {
        if (!File.Exists(start.Executable))
            throw new LauncherException($"The game exe is missing: {start.Executable}");
        var info = new ProcessStartInfo(start.Executable)
        {
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new LauncherException($"The game did not start: {start.Executable}");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new LauncherException($"The game did not start: {error.Message}", error);
        }
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout);
            try
            {
                while (await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false) is { } line)
                    onLine(line);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // Same as the window's X; the mode's window is hidden, so this may find none.
                process.CloseMainWindow();
                using var grace = new CancellationTokenSource(_closeGrace);
                try
                {
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new LauncherException(
                        $"Preparing pipelines did not finish within {timeout.TotalMinutes:0} minutes and did not close when asked. " +
                        $"{Path.GetFileName(start.Executable)} may still be running; close it from Task Manager before you play.");
                }
                throw new LauncherException($"Preparing pipelines did not finish within {timeout.TotalMinutes:0} minutes and was closed.");
            }
            return process.ExitCode;
        }
    }
}
