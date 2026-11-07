using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>The shader converter this launcher ships with; <c>Id</c> is generated at build time.</summary>
public static partial class ShaderConverter;

/// <summary>
/// Runs XenosRecomp, which lives inside the game exe (<c>--wwe_convert_shaders</c>, ADR-0029), with
/// <c>shader_common.h</c> and <c>dxcompiler.dll</c> from one folder (scripts/convert-shaders.ps1 runs it
/// the same way).
/// </summary>
public sealed partial class XenosRecompConverter(string gameExecutable, string folder) : IShaderConverter
{
    public const string Flag = "--wwe_convert_shaders";
    public const string IncludeName = "shader_common.h";
    public const string CompilerName = "dxcompiler.dll";

    public string Executable { get; } = Path.GetFullPath(gameExecutable);
    public string Folder { get; } = Path.GetFullPath(folder);

    public void Convert(string inputDirectory, string outputDirectory, IProgress<double>? progress, CancellationToken cancel)
    {
        var include = Path.Combine(Folder, IncludeName);
        var missing = new[] { Executable, include, Path.Combine(Folder, CompilerName) }.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
            throw new ShaderConverterException($"the shader converter is missing ({missing}). Unzip the launcher's release again");

        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     Flag, inputDirectory, Path.Combine(outputDirectory, "shader_cache.cpp"), include, Path.Combine(outputDirectory, "hlsl"),
                 })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var errors = new List<string>();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is not null && Percent().Match(line.Data) is { Success: true } match)
                progress?.Report(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (errors) errors.Add(line.Data);
        };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ShaderConverterException($"the shader converter could not start: {error.Message}", error);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancel.Register(() =>
               {
                   try { process.Kill(entireProcessTree: true); }
                   // Already gone, or it cannot be ended: then the wait lasts until it finishes.
                   catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
               }))
        {
            process.WaitForExit();
        }
        cancel.ThrowIfCancellationRequested();
        if (process.ExitCode == 0) return;

        string detail;
        lock (errors)
        {
            var failures = errors.Select(line => Failures().Match(line)).FirstOrDefault(match => match.Success);
            detail = failures is not null
                ? $"XenosRecomp could not convert {failures.Groups[1].Value} shaders"
                : $"XenosRecomp stopped with exit code {process.ExitCode}";
        }
        throw new ShaderConverterException(detail);
    }

    // "Recompiling shaders... 42.5%" on stdout; "Recompile failures (3):" on stderr.
    [GeneratedRegex(@"Recompiling shaders\.\.\. ([0-9.]+)%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^Recompile failures \((\d+)\):")]
    private static partial Regex Failures();
}
