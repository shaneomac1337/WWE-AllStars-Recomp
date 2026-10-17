using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>An installed DLC pack: its package file name (the content folder's name), what it is called and holds.</summary>
public sealed record DlcPack(string FileName, string DisplayName, IReadOnlyList<string> Wrestlers, long Bytes);

/// <summary>An install's progress: checking blocks (<see cref="Unpacking"/> false), then unpacking bytes.</summary>
public readonly record struct DlcProgress(bool Unpacking, long Done, long Total)
{
    public double Fraction => Total > 0 ? (double)Done / Total : 0;
}

/// <summary>
/// The player's DLC (tu2-dlc spec, "DLC install"): packs checked block by block and unpacked into
/// the marketplace content folder ReXGlue reads, <c>userdata/0000000000000000/54510866/00000002/
/// &lt;package file name&gt;/</c>, with <c>Headers/00000002/&lt;name&gt;.header</c> beside it: an
/// <c>XCONTENT_AGGREGATE_DATA</c> and a full licence mask (SDK <c>content_manager.cpp</c>). The
/// source package is only read, never copied. Re-adding a pack replaces it. Unpacking happens in a
/// staging folder beside the title's folders, not in <c>00000002</c>, which the game lists whole.
/// </summary>
public sealed class DlcLibrary(string userDataDirectory)
{
    /// <summary>The Million Dollar Pack's package file name.</summary>
    public const string Pack1FileName = "F5B3E0579C2BD2939FD88B0E0A936E8095E284D754";

    private const uint MarketplaceContent = 0x00000002;
    private const uint HardDrive = 1;
    private const int AggregateDataSize = 0x148;
    private const int MaxFileName = 42;
    private const uint FullLicense = 0xFFFFFFFF;

    // Packs known by their console file name: the display name their header carries and their
    // wrestlers (the package's Xe-Game.ini lists only character IDs).
    private static readonly Dictionary<string, (string Name, string[] Wrestlers)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [Pack1FileName] = ("WWE ALL STARS PACK 1",
            ["Million Dollar Man", "Honky Tonk Man", "Dusty Rhodes", "Ted DiBiase Jr", "Cody Rhodes"]),
    };

    private string TitleRoot => Path.Combine(userDataDirectory, "0000000000000000", "54510866");
    public string ContentDirectory => Path.Combine(TitleRoot, "00000002");
    public string HeadersDirectory => Path.Combine(TitleRoot, "Headers", "00000002");
    internal string StagingDirectory => Path.Combine(TitleRoot, ".wweallstars-dlc-staging");

    /// <summary>
    /// The DLC packages among intake findings, or a <see cref="SetupException"/> that says what was
    /// found instead: Title Update 2 (added in setup), another game's package (by name), nothing.
    /// </summary>
    public static IReadOnlyList<string> Choose(PackageIntakeResult intake)
    {
        var packs = intake.Items.Where(item => item.Kind == PackageKind.Dlc).Select(item => item.PackagePath!).ToList();
        if (packs.Count > 0) return packs;
        if (intake.Items.Any(item => item.Kind == PackageKind.TitleUpdate))
            throw new SetupException(SetupError.WrongPackage,
                "That is a title update, not DLC. Title Update 2 is added in setup (or with Add Title Update 2), not here.");
        if (intake.Items.FirstOrDefault(item => item.Kind == PackageKind.OtherGame) is { } other)
            throw new SetupException(SetupError.WrongPackage,
                $"\"{other.Header!.DisplayName}\" is for another game, not WWE All Stars.");
        if (intake.Items.Any(item => item.Kind == PackageKind.OtherContent))
            throw new SetupException(SetupError.WrongPackage, "That is WWE All Stars content, but not DLC.");
        throw new SetupException(SetupError.WrongPackage,
            "No DLC found there. Choose a DLC package you dumped from your own console (a file named like " +
            Pack1FileName + "), a folder with packs, or a zip, 7z or rar.");
    }

    /// <summary>The installed packs, by display name; the mods' combined content is not a pack.</summary>
    public IReadOnlyList<DlcPack> Installed()
    {
        if (!Directory.Exists(ContentDirectory)) return [];
        return Directory.EnumerateDirectories(ContentDirectory)
            .Where(folder => !Path.GetFileName(folder).Equals(ModLibrary.CombinedContentName, StringComparison.OrdinalIgnoreCase))
            .Select(folder => Describe(Path.GetFileName(folder)))
            .OrderBy(pack => pack.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Checks that <paramref name="packagePath"/> is DLC for this game, checks every block,
    /// unpacks it into the staging folder and swaps it in, then writes its header. A known pack
    /// installs under its console file name even when the file was renamed.
    /// </summary>
    public DlcPack Install(string packagePath, IProgress<DlcProgress>? progress, CancellationToken cancel)
    {
        using var package = Open(packagePath);
        switch (PackageIntake.Classify(package.Header))
        {
            case PackageKind.Dlc: break;
            case PackageKind.TitleUpdate:
                throw new SetupException(SetupError.WrongPackage, "That is a title update, not DLC. Title Update 2 is added in setup.");
            case PackageKind.OtherGame:
                throw new SetupException(SetupError.WrongPackage, $"\"{package.DisplayName}\" is for another game, not WWE All Stars.");
            default:
                throw new SetupException(SetupError.WrongPackage, "That is WWE All Stars content, but not DLC.");
        }
        var fileName = Known.FirstOrDefault(pack => pack.Value.Name == package.DisplayName).Key ?? Path.GetFileName(packagePath);
        if (fileName.Length is 0 or > MaxFileName || fileName.Any(c => c > 0x7E || c < 0x20))
            throw new SetupException(SetupError.WrongPackage,
                $"The DLC file's name, {fileName}, is not a console content name. Use the file's original name.");
        try
        {
            package.Verify(progress is null ? null : new ProgressRelay<StfsProgress>(p =>
                progress.Report(new DlcProgress(false, p.Done, p.Total))), cancel);
        }
        catch (InvalidDataException error)
        {
            throw Damaged(package, error);
        }

        var target = Path.Combine(ContentDirectory, fileName);
        var staged = Path.Combine(StagingDirectory, fileName);
        var aside = Path.Combine(StagingDirectory, fileName + ".old");
        try
        {
            foreach (var folder in new[] { staged, aside })
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            package.Extract(staged, progress is null ? null : new ProgressRelay<StfsProgress>(p =>
                progress.Report(new DlcProgress(true, p.Done, p.Total))), cancel);
            Directory.CreateDirectory(ContentDirectory);
            // The old copy steps aside first, so a failed swap can put it back.
            if (Directory.Exists(target)) Directory.Move(target, aside);
            try
            {
                Directory.Move(staged, target);
            }
            catch
            {
                if (Directory.Exists(aside) && !Directory.Exists(target)) Directory.Move(aside, target);
                throw;
            }
            WriteHeader(fileName, package.DisplayName);
            if (Directory.Exists(aside)) TryDelete(aside);
        }
        catch (InvalidDataException error)
        {
            TryDelete(staged);
            throw Damaged(package, error);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            if (error is UnauthorizedAccessException)
                throw new SetupException(SetupError.NoPermission, $"The launcher is not allowed to write to {ContentDirectory}.", error);
            throw;
        }
        return Describe(fileName);
    }

    private static SetupException Damaged(StfsPackage package, InvalidDataException error) =>
        new(SetupError.DamagedFile, $"\"{package.DisplayName}\" is damaged: {error.Message} Dump it from your console again.", error);

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left in the staging folder, which the game never lists; the next install clears it.
        }
    }

    /// <summary>Deletes a pack's folder and its header.</summary>
    public void Remove(string fileName)
    {
        var folder = Path.Combine(ContentDirectory, fileName);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var header = HeaderPath(fileName);
        if (File.Exists(header)) File.Delete(header);
    }

    private static StfsPackage Open(string packagePath)
    {
        try
        {
            return StfsPackage.Open(packagePath);
        }
        catch (InvalidDataException error)
        {
            throw new SetupException(SetupError.DamagedFile, "That DLC package is damaged or not a package: " + error.Message, error);
        }
    }

    private DlcPack Describe(string fileName)
    {
        var folder = Path.Combine(ContentDirectory, fileName);
        var bytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var name = ReadHeaderName(fileName);
        if (Known.TryGetValue(fileName, out var known)) return new DlcPack(fileName, name ?? known.Name, known.Wrestlers, bytes);
        return new DlcPack(fileName, name ?? fileName, [], bytes);
    }

    private string HeaderPath(string fileName) => Path.Combine(HeadersDirectory, fileName + ".header");

    // The XCONTENT_AGGREGATE_DATA ReXGlue reads, big-endian, then the licence mask it reads as a
    // host (little-endian) uint32.
    internal void WriteHeader(string fileName, string displayName)
    {
        var header = new byte[AggregateDataSize + 4];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, HardDrive);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], MarketplaceContent);
        var name = Encoding.BigEndianUnicode.GetBytes(displayName);
        name.AsSpan(0, Math.Min(name.Length, 254)).CopyTo(span[8..]);
        Encoding.ASCII.GetBytes(fileName).CopyTo(span[0x108..]);
        BinaryPrimitives.WriteUInt64BigEndian(span[0x138..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x140..], PackageIntake.GameTitleId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[AggregateDataSize..], FullLicense);
        Directory.CreateDirectory(HeadersDirectory);
        var path = HeaderPath(fileName);
        File.WriteAllBytes(path + ".tmp", header);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private string? ReadHeaderName(string fileName)
    {
        try
        {
            var header = File.ReadAllBytes(HeaderPath(fileName));
            if (header.Length < AggregateDataSize) return null;
            var name = Encoding.BigEndianUnicode.GetString(header, 8, 256);
            var end = name.IndexOf('\0');
            name = (end < 0 ? name : name[..end]).Trim();
            return name.Length > 0 ? name : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
