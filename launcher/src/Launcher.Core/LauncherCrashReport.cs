using System.IO.Compression;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// What a launcher crash report says about the machine; the GPU is null when unknown. <see cref="Mods"/>
/// are the mods turned on, in load order (spec, user story 45), or null when they could not be read.
/// </summary>
public sealed record LauncherCrashFacts(string Version, string OperatingSystem, string? Gpu, IReadOnlyList<string>? Mods = null);

/// <summary>
/// The launcher's own crash report, <c>logs/crash-yyyyMMdd-HHmmss-launcher.zip</c>: a copy of the
/// launcher log and <c>manifest.txt</c> (version, OS, GPU, the exception and its stack), plus a
/// dump when the caller made one. The name keeps the game reports' time order, so
/// <see cref="CrashReports"/> lists it at the next start. The exception goes into the launcher
/// log first and the log is only ever copied, so a report that fails never loses the log.
/// </summary>
public static class LauncherCrashReport
{
    public static string ZipPath(PortableLayout layout, DateTimeOffset now) =>
        Path.Combine(layout.LogsDirectory, $"crash-{now:yyyyMMdd-HHmmss}-launcher.zip");

    // Creates ZipPath, or "...-launcher-2.zip" and on when a report of the same second is already
    // there; CreateNew, so two reports written at once never take the same file.
    private static FileStream CreateZip(PortableLayout layout, DateTimeOffset now, out string path)
    {
        var first = ZipPath(layout, now);
        for (var n = 1; ; n++)
        {
            path = n == 1 ? first : Path.ChangeExtension(first, null) + $"-{n}.zip";
            if (Directory.Exists(path)) continue;
            try { return new FileStream(path, FileMode.CreateNew); }
            catch (IOException) when (File.Exists(path) && n < 100) { }
        }
    }

    /// <summary>Writes the report; returns its path, or null when it could not be written. Never throws.</summary>
    public static string? Write(PortableLayout layout, LauncherLog log, Exception error, LauncherCrashFacts facts,
        DateTimeOffset now, string? dumpPath = null)
    {
        log.Write($"Unhandled launcher exception: {error}");
        // Set once this call created the file: the cleanup below never removes another report.
        string? created = null;
        try
        {
            Directory.CreateDirectory(layout.LogsDirectory);
            using (var file = CreateZip(layout, now, out var zipPath))
            {
                created = zipPath;
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                AddText(zip, "manifest.txt", Manifest(error, facts, now));
                if (log.FilePath is { } logPath && File.Exists(logPath)) AddShared(zip, logPath, LauncherLog.FileName);
                if (dumpPath is not null && File.Exists(dumpPath)) AddShared(zip, dumpPath, "launcher.dmp");
            }
            log.Write($"crash report: saved {created}");
            return created;
        }
        catch (Exception failure)
        {
            log.Write($"crash report: could not be written: {failure.Message}");
            try { if (created is not null) File.Delete(created); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return null;
        }
        finally
        {
            try { if (dumpPath is not null && File.Exists(dumpPath)) File.Delete(dumpPath); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Manifest(Exception error, LauncherCrashFacts facts, DateTimeOffset now) =>
        $"""
        reason: launcher exception
        time: {now:yyyy-MM-dd HH:mm:ss zzz}
        version: {facts.Version}
        os: {facts.OperatingSystem}
        gpu: {facts.Gpu ?? "unknown"}
        mods: {facts.Mods switch { null => "unknown", [] => "none", var mods => string.Join(", ", mods) }}

        {error}

        """;

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    // The log stays open for appends by other threads; read it shared, never move it.
    private static void AddShared(ZipArchive zip, string path, string name)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        source.CopyTo(target);
    }
}
