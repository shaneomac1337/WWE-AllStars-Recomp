using SharpCompress.Archives;

namespace WweAllStars.Launcher.Core;

/// <summary>What an intake input turned out to be.</summary>
public enum PackageKind
{
    /// <summary>A title update for this game (content type 0x000B0000).</summary>
    TitleUpdate,
    /// <summary>Marketplace content (DLC) for this game (content type 0x00000002).</summary>
    Dlc,
    /// <summary>An Xbox 360 package for another title.</summary>
    OtherGame,
    /// <summary>This game's package of another kind (a save, a gamer picture...).</summary>
    OtherContent,
    /// <summary>Not a package, not found, an unreadable archive, or a folder without packages.</summary>
    NotAPackage,
}

/// <summary>One intake finding: a package (with the file to read it from) or an input that held none.</summary>
public sealed record PackageIntakeItem(string Source, PackageKind Kind, string? PackagePath, StfsHeader? Header, string? Problem);

/// <summary>Intake findings; disposing removes the temp folder that archives were unpacked to.</summary>
public sealed class PackageIntakeResult(IReadOnlyList<PackageIntakeItem> items, string? tempDirectory) : IDisposable
{
    public IReadOnlyList<PackageIntakeItem> Items { get; } = items;

    public void Dispose()
    {
        if (tempDirectory == null || !Directory.Exists(tempDirectory)) return;
        try
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open elsewhere; the OS temp cleanup gets it later.
        }
    }
}

/// <summary>
/// Turns whatever a player points the launcher at into Xbox 360 packages: a package file (a
/// <c>tu…</c> or console cache <c>TU_…</c> file, a DLC package), a folder searched recursively
/// (a USB or console <c>Content</c> tree too), or a <c>.zip</c>, <c>.rar</c> or <c>.7z</c>, which
/// unpacks to a temp folder. Packages are told apart by their STFS header, never by file name.
/// Identifying is header-only; callers verify hashes (<see cref="StfsPackage.Verify"/>).
/// </summary>
public static class PackageIntake
{
    /// <summary>WWE All Stars.</summary>
    public const uint GameTitleId = 0x54510866;

    /// <summary>The most an archive may unpack to (Pack 1 is 0.2 GB): a guard against archive bombs.</summary>
    public const long MaxUnpackedBytes = 8L << 30;

    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z"];

    // A USB drive's root holds folders nobody may read (System Volume Information): skip them.
    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    /// <summary>
    /// Scans <paramref name="inputs"/> (files or folders). Archives unpack under a new folder in
    /// <paramref name="tempRoot"/>, removed when the result is disposed.
    /// </summary>
    public static PackageIntakeResult Scan(IEnumerable<string> inputs, string tempRoot, CancellationToken cancel)
    {
        var items = new List<PackageIntakeItem>();
        var seen = new HashSet<(long Length, string Head)>();
        string? temp = null;
        try
        {
            foreach (var input in inputs)
            {
                cancel.ThrowIfCancellationRequested();
                var found = new List<PackageIntakeItem>();
                string? problem;
                if (Directory.Exists(input))
                {
                    problem = "No Xbox 360 packages in this folder.";
                    foreach (var file in Directory.EnumerateFiles(input, "*", Recursive))
                        ScanFile(file, input, found, ref temp, tempRoot, archiveDepth: 0, cancel);
                }
                else if (File.Exists(input))
                {
                    problem = IsArchive(input) ? "No Xbox 360 packages in this archive." : "Not an Xbox 360 package.";
                    ScanFile(input, input, found, ref temp, tempRoot, archiveDepth: 0, cancel, explicitFile: true);
                    var archiveProblem = found.FirstOrDefault(f => f.Kind == PackageKind.NotAPackage)?.Problem;
                    if (archiveProblem != null) problem = archiveProblem;
                    found.RemoveAll(f => f.Kind == PackageKind.NotAPackage);
                }
                else
                {
                    problem = "Not found.";
                }

                foreach (var item in found)
                    if (seen.Add(Identity(item.PackagePath!))) items.Add(item);
                if (found.Count == 0) items.Add(new PackageIntakeItem(input, PackageKind.NotAPackage, null, null, problem));
            }
            return new PackageIntakeResult(items, temp);
        }
        catch
        {
            new PackageIntakeResult([], temp).Dispose();
            throw;
        }
    }

    /// <summary>What a package header is, for this game.</summary>
    public static PackageKind Classify(StfsHeader header) =>
        header.TitleId != GameTitleId ? PackageKind.OtherGame
        : header.ContentType == StfsContentType.TitleUpdate ? PackageKind.TitleUpdate
        : header.ContentType == StfsContentType.MarketplaceContent ? PackageKind.Dlc
        : PackageKind.OtherContent;

    private static void ScanFile(string file, string source, List<PackageIntakeItem> found, ref string? temp,
        string tempRoot, int archiveDepth, CancellationToken cancel, bool explicitFile = false)
    {
        cancel.ThrowIfCancellationRequested();
        if (IsArchive(file))
        {
            if (archiveDepth > 1) return; // an archive inside an archive inside an archive: stop
            temp ??= Directory.CreateDirectory(Path.Combine(tempRoot, "intake-" + Guid.NewGuid().ToString("N"))).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(temp, found.Count + "-" + Guid.NewGuid().ToString("N")[..8])).FullName;
            if (!TryUnpack(file, folder, cancel, out var problem))
            {
                if (explicitFile) found.Add(new PackageIntakeItem(source, PackageKind.NotAPackage, null, null, problem));
                return;
            }
            foreach (var inner in Directory.EnumerateFiles(folder, "*", Recursive))
                ScanFile(inner, source, found, ref temp, tempRoot, archiveDepth + 1, cancel);
            return;
        }
        var header = StfsPackage.TryReadHeader(file);
        if (header != null) found.Add(new PackageIntakeItem(source, Classify(header), file, header, null));
    }

    internal static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unpacks an archive into <paramref name="folder"/>, skipping entries that climb out of it,
    /// and stops past <paramref name="limit"/> bytes with <paramref name="tooLarge"/> as the
    /// problem. The mod library unpacks through it too (ticket 07).
    /// </summary>
    internal static bool TryUnpack(string archivePath, string folder, CancellationToken cancel, out string? problem,
        long limit = MaxUnpackedBytes,
        string tooLarge = "The archive unpacks to more than 8 GB, far more than a title update or DLC pack.")
    {
        problem = null;
        long unpacked = 0;
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                // Entries that climb out of the folder ("../") are skipped.
                if (SafePath.Inside(folder, entry.Key) is not { } target) continue;
                unpacked += entry.Size;
                if (entry.Size < 0 || unpacked > limit)
                {
                    problem = tooLarge;
                    return false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(target);
                input.CopyTo(output);
            }
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            problem = "The archive can't be read: " + error.Message;
            return false;
        }
    }

    // Two finds are the same package when their sizes and first 64 KiB match.
    private static (long Length, string Head) Identity(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[Math.Min(file.Length, 0x10000)];
        file.ReadExactly(head);
        return (file.Length, Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(head)));
    }
}
