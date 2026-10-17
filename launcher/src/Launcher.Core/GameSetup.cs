using System.Security.Cryptography;

namespace WweAllStars.Launcher.Core;

/// <summary>What went wrong in setup, in the player's terms.</summary>
public enum SetupError
{
    ImageNotFound,
    NotAnXboxImage,
    WrongDisc,
    DamagedFile,
    DiskFull,
    NoPermission,
    GameDataFolderInUse,
    /// <summary>Nothing chosen holds a title update.</summary>
    NoTitleUpdate,
    /// <summary>A package, but not Title Update 2 for this disc (DLC, another game, another update).</summary>
    WrongPackage,
}

/// <summary>A setup failure with a plain message that says what to do.</summary>
public sealed class SetupException(SetupError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SetupError Error { get; } = error;
}

/// <summary>Where a game data folder stands.</summary>
public enum SetupStatus
{
    NeedsSetup,
    /// <summary>A previous setup stopped part way: resume or restart it.</summary>
    Partial,
    Done,
}

/// <summary>How to treat a partial folder left by an interrupted setup.</summary>
public enum SetupStart
{
    /// <summary>Keep the files that verify and extract the rest.</summary>
    Resume,
    /// <summary>Delete the partial folder and start from nothing.</summary>
    Restart,
}

public enum SetupStep
{
    CheckingImage,
    CheckingExistingFiles,
    Extracting,
    Finishing,
    /// <summary>Repair: re-hashing the files of an installed game data folder.</summary>
    VerifyingGameFiles,
    /// <summary>Checking every block of the Title Update 2 package.</summary>
    VerifyingTitleUpdate,
    /// <summary>Unpacking Title Update 2 into the update folder, verifying each file.</summary>
    UnpackingTitleUpdate,
    /// <summary>Repair: re-hashing the files of the update folder.</summary>
    VerifyingTitleUpdateFiles,
    Done,
}

/// <summary>Setup's progress: the step, bytes done of the total, the current file and the time left.</summary>
public sealed record SetupProgress(SetupStep Step, long BytesDone, long BytesTotal, string? CurrentFile, TimeSpan? TimeLeft)
{
    public double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;
}

/// <summary>
/// What Repair found in an installed game data folder: manifest files that are absent, and files
/// whose size or SHA-256 differs from the manifest. Paths use '/' separators.
/// </summary>
public sealed record RepairCheck(IReadOnlyList<string> Missing, IReadOnlyList<string> Damaged)
{
    public bool NothingToRepair => Missing.Count == 0 && Damaged.Count == 0;

    /// <summary>What the player is told: the count and up to three paths, missing ones first.</summary>
    public string Summary()
    {
        if (NothingToRepair) return "Every game file verifies. Nothing to repair.";
        var bad = Missing.Count + Damaged.Count;
        var listed = string.Join(", ", Missing.Concat(Damaged).Take(3)) + (bad > 3 ? $" and {bad - 3} more" : "");
        return $"{FileCount(bad)} missing or damaged: {listed}.";
    }

    public static string FileCount(int count) => count == 1 ? "1 file" : $"{count} files";
}

/// <summary>Free space on the drive that holds a folder; tests replace it with a fake.</summary>
public interface IDiskSpace
{
    long FreeBytes(string directory);
}

public sealed class DriveDiskSpace : IDiskSpace
{
    public long FreeBytes(string directory) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
}

/// <summary>
/// The setup marker: a small file in the game data folder naming the manifest version it was
/// verified against. Its presence is what "setup done" means for the launcher and the game
/// (spec, "Setup pipeline"). Content: one line, <c>manifest_version=&lt;id&gt;</c>.
/// </summary>
public static class SetupMarker
{
    public const string FileName = ".wweallstars-setup";
    private const string Key = "manifest_version=";

    public static void Write(string gameDataDirectory, string manifestVersion) =>
        File.WriteAllText(Path.Combine(gameDataDirectory, FileName), Key + manifestVersion + "\n");

    /// <summary>The manifest version the marker names, or null when there is no readable marker.</summary>
    public static string? Read(string gameDataDirectory)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(gameDataDirectory, FileName)).Trim();
            return text.StartsWith(Key, StringComparison.Ordinal) ? text[Key.Length..] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// First-run setup (spec, "Setup pipeline"): detect XDVDFS → check <c>default.xex</c> and the
/// file list against the manifest → free space → extract into <c>&lt;gamedata&gt;.partial</c>,
/// verifying every file → write the marker → rename the partial folder to the game data folder.
/// The rename is the commit point, so a game data folder is either complete or absent. The disc
/// image is only ever opened for reading.
/// </summary>
public sealed class GameSetup(DiscManifest manifest, IDiskSpace space, TimeProvider time)
{
    /// <summary>Head room kept free beyond the game files (logs, caches, the shader pack).</summary>
    public const long SpareBytes = 512L << 20;

    private const string TempSuffix = ".tmp";
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    public static string PartialDirectory(string gameDataDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory)) + ".partial";

    public SetupStatus StatusOf(string gameDataDirectory)
    {
        if (SetupMarker.Read(gameDataDirectory) == manifest.Version) return SetupStatus.Done;
        return Directory.Exists(PartialDirectory(gameDataDirectory)) ? SetupStatus.Partial : SetupStatus.NeedsSetup;
    }

    /// <summary>
    /// True when the folder holds at least one manifest file: an earlier install, whatever its
    /// marker says. Setup takes such a folder over instead of refusing it.
    /// </summary>
    public bool HoldsGameFiles(string gameDataDirectory) =>
        manifest.Files.Any(entry => File.Exists(Path.Combine(gameDataDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>
    /// Checks that <paramref name="isoPath"/> is the supported disc, without extracting.
    /// <paramref name="step"/> hears each step before it starts, for the launcher log (issue #1).
    /// </summary>
    public void Verify(string isoPath, Action<string>? step = null)
    {
        step?.Invoke("Opening the disc image: " + isoPath);
        using var image = OpenImage(isoPath);
        Identify(image, CancellationToken.None, step);
        step?.Invoke("The disc is the supported one");
    }

    public void Run(string isoPath, string gameDataDirectory, SetupStart start,
        IProgress<SetupProgress>? progress, CancellationToken cancel)
    {
        var gameData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory));
        var partial = PartialDirectory(gameData);
        var reporter = new Reporter(progress, time, manifest.TotalBytes);
        try
        {
            reporter.Report(SetupStep.CheckingImage, 0, null, force: true);
            using var image = OpenImage(isoPath);
            var files = Identify(image, cancel);
            if (StatusOf(gameData) == SetupStatus.Done)
            {
                reporter.Report(SetupStep.Done, manifest.TotalBytes, null, force: true);
                return;
            }
            if (HoldsGameFiles(gameData))
            {
                // An install whose marker was deleted or names another manifest: verify it in
                // place and re-extract only what fails, the way Repair does. Other files stay.
                Repair(isoPath, gameData, CheckInstalled(gameData, progress, cancel), progress, cancel);
                return;
            }
            if (Directory.Exists(gameData) && Directory.EnumerateFileSystemEntries(gameData).Any())
                throw new SetupException(SetupError.GameDataFolderInUse,
                    $"The game data folder already holds other files: {gameData}. Choose an empty or new folder.");

            if (start == SetupStart.Restart && Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            var kept = Directory.Exists(partial) ? KeepVerifiedFiles(partial, reporter, cancel) : [];
            var pending = files.Where(file => !kept.Contains(file.Path)).ToList();

            var needed = pending.Sum(file => file.Size) + SpareBytes;
            var parent = Path.GetDirectoryName(gameData)!;
            Directory.CreateDirectory(parent);
            var free = space.FreeBytes(parent);
            if (free < needed)
                throw new SetupException(SetupError.DiskFull,
                    $"Not enough disk space. Setup needs {Gigabytes(needed)} free on {Path.GetPathRoot(gameData)}" +
                    $" and only {Gigabytes(free)} is free. Free some space or choose another game data folder.");

            Directory.CreateDirectory(partial);
            reporter.StartExtracting(manifest.TotalBytes - pending.Sum(file => file.Size));
            foreach (var file in pending) Extract(image, file, partial, reporter, cancel);

            reporter.Report(SetupStep.Finishing, manifest.TotalBytes, null, force: true);
            SetupMarker.Write(partial, manifest.Version);
            if (Directory.Exists(gameData)) Directory.Delete(gameData);
            Directory.Move(partial, gameData);
            reporter.Report(SetupStep.Done, manifest.TotalBytes, null, force: true);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new SetupException(SetupError.NoPermission,
                $"Setup is not allowed to write to {Path.GetDirectoryName(gameData)}. " +
                "Choose a game data folder in a place you can write to, such as your Documents or another drive.", error);
        }
        catch (IOException error) when (IsDiskFull(error))
        {
            throw new SetupException(SetupError.DiskFull,
                $"The disk ran out of space while copying game files to {gameData}. Free some space and resume setup.", error);
        }
    }

    /// <summary>
    /// Repair's first half: re-hashes every manifest file in an installed game data folder and
    /// lists the ones that are missing or differ. Needs no disc image and changes nothing.
    /// </summary>
    public RepairCheck CheckInstalled(string gameDataDirectory, IProgress<SetupProgress>? progress, CancellationToken cancel)
    {
        var gameData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory));
        var reporter = new Reporter(progress, time, manifest.TotalBytes);
        List<string> missing = [], damaged = [];
        long done = 0;
        foreach (var entry in manifest.Files)
        {
            cancel.ThrowIfCancellationRequested();
            reporter.Report(SetupStep.VerifyingGameFiles, done, entry.Path);
            var path = Path.Combine(gameData, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) missing.Add(entry.Path);
            else if (new FileInfo(path).Length != entry.Size || HashFile(path, cancel) != entry.Sha256) damaged.Add(entry.Path);
            done += entry.Size;
        }
        reporter.Report(SetupStep.VerifyingGameFiles, done, null, force: true);
        return new RepairCheck(missing, damaged);
    }

    /// <summary>
    /// Repair's second half: checks that <paramref name="isoPath"/> is the supported disc, then
    /// re-extracts only the files <paramref name="check"/> lists, each verified and swapped in
    /// through a temp file. Every other file is left untouched. Every manifest file then verifies,
    /// so it (re)writes the setup marker. Returns the repaired paths.
    /// </summary>
    public IReadOnlyList<string> Repair(string isoPath, string gameDataDirectory, RepairCheck check,
        IProgress<SetupProgress>? progress, CancellationToken cancel)
    {
        var gameData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory));
        var bad = new HashSet<string>(check.Missing.Concat(check.Damaged), StringComparer.OrdinalIgnoreCase);
        var needed = manifest.Files.Where(entry => bad.Contains(entry.Path)).Sum(entry => entry.Size);
        var reporter = new Reporter(progress, time, needed);
        try
        {
            reporter.Report(SetupStep.CheckingImage, 0, null, force: true);
            using var image = OpenImage(isoPath);
            var files = Identify(image, cancel).Where(file => bad.Contains(file.Path)).ToList();

            var free = space.FreeBytes(gameData);
            if (free < needed)
                throw new SetupException(SetupError.DiskFull,
                    $"Not enough disk space. Repair needs {Gigabytes(needed)} free on {Path.GetPathRoot(gameData)}" +
                    $" and only {Gigabytes(free)} is free. Free some space and try again.");

            reporter.StartExtracting(0);
            foreach (var file in files) Extract(image, file, gameData, reporter, cancel);
            SetupMarker.Write(gameData, manifest.Version);
            reporter.Report(SetupStep.Done, needed, null, force: true);
            return files.Select(file => file.Path).ToList();
        }
        catch (UnauthorizedAccessException error)
        {
            throw new SetupException(SetupError.NoPermission,
                $"Repair is not allowed to write to {gameData}. Close anything that uses the game files and try again.", error);
        }
        catch (IOException error) when (IsDiskFull(error))
        {
            throw new SetupException(SetupError.DiskFull,
                $"The disk ran out of space while repairing game files in {gameData}. Free some space and try again.", error);
        }
    }

    private static XdvdfsImage OpenImage(string isoPath)
    {
        if (!File.Exists(isoPath))
            throw new SetupException(SetupError.ImageNotFound, $"The disc image was not found: {isoPath}");
        try
        {
            return XdvdfsImage.TryOpen(isoPath) ?? throw NotAnXboxImage(null);
        }
        catch (InvalidDataException error)
        {
            throw NotAnXboxImage(error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new SetupException(SetupError.NoPermission, $"The disc image cannot be read: {isoPath}. {error.Message}", error);
        }
    }

    private static SetupException NotAnXboxImage(Exception? inner) =>
        new(SetupError.NotAnXboxImage,
            "This file is not an Xbox 360 disc image. Choose the .iso you made from your WWE All Stars disc.", inner);

    private SetupException WrongDisc() =>
        new(SetupError.WrongDisc,
            "This disc image is not the supported version of WWE All Stars (Xbox 360, USA/Europe). " +
            "The port only runs that disc; a different version or region will not work.");

    /// <summary>The disc's files when its file list, sizes and <c>default.xex</c> match the manifest.</summary>
    private IReadOnlyList<DiscFile> Identify(XdvdfsImage image, CancellationToken cancel, Action<string>? step = null)
    {
        IReadOnlyList<DiscFile> files;
        step?.Invoke("Reading the disc's file list");
        try
        {
            files = image.Files();
        }
        catch (InvalidDataException error)
        {
            throw NotAnXboxImage(error);
        }
        if (files.Count != manifest.Files.Count) throw WrongDisc();
        foreach (var file in files)
            if (manifest.Find(file.Path) is not { } entry || entry.Size != file.Size) throw WrongDisc();

        var executable = files.First(file => string.Equals(file.Path, manifest.Executable.Path, StringComparison.OrdinalIgnoreCase));
        step?.Invoke($"Hashing default.xex: {files.Count} files match the manifest, {executable.Size} bytes to hash");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        image.CopyTo(executable, hash.AppendData, cancel);
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != manifest.Executable.Sha256) throw WrongDisc();
        return files;
    }

    /// <summary>
    /// Resume: keeps the files in the partial folder whose size and hash match the manifest and
    /// deletes everything else (half-written files, strays). Returns the kept paths.
    /// </summary>
    private HashSet<string> KeepVerifiedFiles(string partial, Reporter reporter, CancellationToken cancel)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = Directory.EnumerateFiles(partial, "*", SearchOption.AllDirectories).ToList();
        long done = 0;
        foreach (var path in existing)
        {
            cancel.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(partial, path).Replace(Path.DirectorySeparatorChar, '/');
            reporter.Report(SetupStep.CheckingExistingFiles, done, relative);
            var entry = manifest.Find(relative);
            var info = new FileInfo(path);
            if (entry is not null && info.Length == entry.Size && HashFile(path, cancel) == entry.Sha256)
            {
                kept.Add(entry.Path);
                done += entry.Size;
            }
            else File.Delete(path);
        }
        return kept;
    }

    private static string HashFile(string path, CancellationToken cancel) => FileHashes.Sha256(path, cancel);

    /// <summary>Copies one file into the partial folder through a temp file, verifying its hash.</summary>
    private void Extract(XdvdfsImage image, DiscFile file, string partial, Reporter reporter, CancellationToken cancel)
    {
        var root = Path.TrimEndingDirectorySeparator(partial) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A file on the disc points outside the game data folder: " + file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + TempSuffix;
        var expected = manifest.Find(file.Path)!;
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                reporter.Report(SetupStep.Extracting, reporter.Done, file.Path);
                image.CopyTo(file, block =>
                {
                    hash.AppendData(block);
                    output.Write(block);
                    reporter.Advance(block.Length, file.Path);
                }, cancel);
                output.Flush(flushToDisk: true);
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected.Sha256)
                    throw new SetupException(SetupError.DamagedFile,
                        $"A game file failed verification: {file.Path}. Your disc image may be damaged; " +
                        "make a new copy of your disc and try again.");
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool IsDiskFull(IOException error) =>
        (error.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    private static string Gigabytes(long bytes) => $"{bytes / 1e9:0.0} GB";

    /// <summary>Turns byte counts into progress reports at most every 100 ms, with the time left.</summary>
    private sealed class Reporter(IProgress<SetupProgress>? progress, TimeProvider time, long total)
    {
        private long _lastReport = long.MinValue;
        private long _extractStart;
        private long _doneAtStart;

        public long Done { get; private set; }

        public void StartExtracting(long alreadyDone)
        {
            Done = _doneAtStart = alreadyDone;
            _extractStart = time.GetTimestamp();
        }

        public void Advance(long bytes, string file)
        {
            Done += bytes;
            Report(SetupStep.Extracting, Done, file);
        }

        public void Report(SetupStep step, long done, string? file, bool force = false)
        {
            if (progress is null) return;
            var now = time.GetTimestamp();
            if (!force && _lastReport != long.MinValue && time.GetElapsedTime(_lastReport, now) < ReportInterval) return;
            _lastReport = now;
            progress.Report(new SetupProgress(step, done, total, file, step == SetupStep.Extracting ? TimeLeft(now) : null));
        }

        private TimeSpan? TimeLeft(long now)
        {
            var copied = Done - _doneAtStart;
            if (copied <= 0) return null;
            var elapsed = time.GetElapsedTime(_extractStart, now);
            return TimeSpan.FromTicks((long)(elapsed.Ticks * ((double)(total - Done) / copied)));
        }
    }
}
