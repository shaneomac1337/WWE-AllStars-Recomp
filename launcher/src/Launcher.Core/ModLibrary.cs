using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>Makes a hard link; the platform layer implements it (<c>CreateHardLinkW</c> on Windows).</summary>
public interface IFileLinker
{
    /// <summary>Links <paramref name="newLink"/> to <paramref name="existingFile"/>; false when the drive can't, so the caller copies.</summary>
    bool TryHardLink(string existingFile, string newLink);
}

/// <summary>Moves a folder to the Recycle Bin; the platform layer implements it.</summary>
public interface IRecycleBin
{
    void Recycle(string folder);
}

/// <summary>Whether a mod may be used in online play (CONTEXT.md, "Cosmetic mod").</summary>
public enum ModKind
{
    Gameplay,
    Cosmetic,
}

/// <summary>A mod's <c>mod.toml</c> (spec, "Manifest"). <see cref="Kind"/> is the declared kind.</summary>
public sealed record ModManifest(string Name, string Author, string Version, string Description, string Game, ModKind Kind);

/// <summary>One replacement package a mod supplies, by the short name the game knows it by.</summary>
public sealed record ModPackage(string Name, string Path, long Size, long UncompressedSize);

/// <summary>
/// One mod in <c>mods/</c>, in load order. A mod with a problem is never used, and reads as off.
/// </summary>
public sealed record ModInfo(
    string Id,
    string Folder,
    ModManifest? Manifest,
    bool Enabled,
    bool IsNew,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ModPackage> Packages)
{
    /// <summary>The mod's <c>config/</c> files, which Play merges into the Coalesced bundles.</summary>
    public IReadOnlyList<ModConfigFile> Config { get; init; } = [];

    /// <summary>The mod's <c>content/</c> files, which Play builds into the combined content.</summary>
    public IReadOnlyList<ModContentFile> Content { get; init; } = [];

    /// <summary>A mod with a <c>content/</c> part: it adds content (new wrestlers), offline only.</summary>
    public bool IsContentMod => Content.Count > 0;

    /// <summary>Cosmetic or gameplay, after the gameplay list (<see cref="ModOnlineRule"/>).</summary>
    public ModKind Kind { get; init; } = ModKind.Gameplay;

    /// <summary>Why a mod declared cosmetic counts as gameplay; null otherwise.</summary>
    public string? ForcedGameplayReason { get; init; }

    /// <summary>Whether the online rule leaves this mod out of a Play: it is on, gameplay, and online play is on.</summary>
    public bool IsOffForOnline(bool online) => online && Enabled && Kind == ModKind.Gameplay;
}

/// <summary>
/// The mods in the portable <c>mods/</c> folder (spec, "Mod library"; ADR-0026): scanning, the
/// load-order file, validation, and the update folder Play starts the game on.
/// </summary>
public sealed partial class ModLibrary(PortableLayout layout, KnownGameFiles known, IFileLinker linker, IRecycleBin recycleBin)
{
    public const string ManifestFileName = "mod.toml";
    public const string LoadOrderFileName = "load-order.txt";

    private readonly Lock _lock = new();
    private readonly KnownGameFiles _known = known;
    private readonly IFileLinker _linker = linker;
    private readonly IRecycleBin _recycleBin = recycleBin;

    /// <summary>
    /// Every mod in load order. A folder missing from the load-order file is added at the end,
    /// off and new; an entry whose folder is gone is dropped.
    /// </summary>
    public IReadOnlyList<ModInfo> List()
    {
        lock (_lock) return Scan();
    }

    /// <summary>
    /// Turns a mod on or off and clears its "new" mark; saved to the load-order file, so it
    /// applies at the next Play. A mod with a problem can't be turned on.
    /// </summary>
    /// <exception cref="LauncherException">No such mod, or it has a problem.</exception>
    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var entries = Sync();
            var index = entries.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new LauncherException($"There is no mod named {id} in {layout.ModsDirectory}.");
            var mod = Read(entries[index]);
            if (enabled && mod.Problems.Count > 0)
                throw new LauncherException($"{mod.Id} can't be turned on: {mod.Problems[0]}");
            entries[index] = entries[index] with { Enabled = enabled, IsNew = false };
            LoadOrder.Write(LoadOrderPath, entries);
        }
    }

    /// <summary>Moves a mod's folder to the Recycle Bin, so a mistaken removal can be undone, and drops its entry.</summary>
    /// <exception cref="LauncherException">No such mod.</exception>
    public void Remove(string id)
    {
        lock (_lock)
        {
            var entries = Sync();
            var entry = entries.Find(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new LauncherException($"There is no mod named {id} in {layout.ModsDirectory}.");
            _recycleBin.Recycle(Path.Combine(layout.ModsDirectory, entry.Id));
            entries.Remove(entry);
            LoadOrder.Write(LoadOrderPath, entries);
        }
    }

    private string LoadOrderPath => Path.Combine(layout.ModsDirectory, LoadOrderFileName);

    private List<ModInfo> Scan()
    {
        var entries = Sync();
        return [.. entries.Select(Read)];
    }

    private ModInfo Read(LoadOrderEntry entry)
    {
        var folder = Path.Combine(layout.ModsDirectory, entry.Id);
        var problems = new List<string>();
        var manifest = ModManifestReader.Read(Path.Combine(folder, ManifestFileName), problems);
        var packages = ReadPackages(Path.Combine(folder, PackagesFolderName), problems);
        var config = ReadConfig(Path.Combine(folder, ConfigFolderName), problems);
        var contentFolder = Path.Combine(folder, ContentFolderName);
        var content = ReadContent(contentFolder, _known, problems);
        CheckCodeNames(content, problems);
        var addsContent = Directory.Exists(contentFolder) && Directory.EnumerateFiles(contentFolder, "*", SearchOption.AllDirectories).Any();
        var (kind, forcedReason) = ModOnlineRule.Classify(manifest, packages, EditsConfig(folder), addsContent);
        return new ModInfo(entry.Id, folder, manifest, entry.Enabled && problems.Count == 0, entry.IsNew, problems, packages)
        {
            Kind = kind,
            ForcedGameplayReason = forcedReason,
            Config = config,
            Content = content,
        };
    }

    internal const string PackagesFolderName = "packages";

    /// <summary>A non-empty <c>config/</c> folder: any config edit makes a mod gameplay (spec, "Cosmetic or gameplay").</summary>
    private static bool EditsConfig(string folder)
    {
        var config = Path.Combine(folder, ConfigFolderName);
        return Directory.Exists(config) && Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// The mod's <c>packages/</c>: flat <c>.xxx</c> files with safe names, each a package (the
    /// tag in either byte order) replacing one the disc or TU2 has.
    /// </summary>
    private List<ModPackage> ReadPackages(string folder, List<string> problems)
    {
        var packages = new List<ModPackage>();
        if (!Directory.Exists(folder)) return packages;
        foreach (var sub in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            problems.Add($"{PackagesFolderName}/{Path.GetFileName(sub)} is a folder; packages go straight in {PackagesFolderName}/ by their short name.");
        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            var shown = $"{PackagesFolderName}/{name}";
            if (!name.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{shown} is not a package (.xxx).");
                continue;
            }
            if (!SafeName().IsMatch(name))
            {
                problems.Add($"{shown} has a file name the game can't use.");
                continue;
            }
            if (!_known.IsPackage(name))
            {
                problems.Add($"{shown} replaces no package the game has; a mod can only replace packages.");
                continue;
            }
            long? uncompressed;
            long size;
            RenamedPackage? renamed;
            try
            {
                using var stream = File.OpenRead(path);
                size = stream.Length;
                uncompressed = Ue3Package.TocUncompressedSize(stream);
                renamed = PackageOrigin.Check(stream, name, _known.IsPackage);
            }
            catch (InvalidDataException)
            {
                problems.Add($"{shown} is damaged.");
                continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{shown} could not be read: {error.Message}");
                continue;
            }
            if (uncompressed is null)
            {
                problems.Add($"{shown} is not a game package.");
                continue;
            }
            if (renamed is not null)
            {
                problems.Add(PackageOrigin.Reason(shown, renamed));
                continue;
            }
            packages.Add(new ModPackage(name, path, size, uncompressed.Value));
        }
        return packages;
    }

    /// <summary>A short name as TOC lines carry them: letters, digits, <c>_</c>, <c>-</c> and dots.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*\.xxx$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeName();

    /// <summary>The load-order file brought in line with the folders on disk, saved when it changed.</summary>
    private List<LoadOrderEntry> Sync()
    {
        var folders = Directory.Exists(layout.ModsDirectory)
            ? Directory.EnumerateDirectories(layout.ModsDirectory).Select(Path.GetFileName).OfType<string>()
                .Where(name => !name.StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var saved = LoadOrder.Read(LoadOrderPath);
        var entries = saved.Where(entry => folders.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var folder in folders)
            if (!entries.Any(entry => string.Equals(entry.Id, folder, StringComparison.OrdinalIgnoreCase)))
                entries.Add(new LoadOrderEntry(folder, Enabled: false, IsNew: true));
        if (!entries.SequenceEqual(saved)) LoadOrder.Write(LoadOrderPath, entries);
        return entries;
    }
}

/// <summary>One line of the load-order file.</summary>
internal sealed record LoadOrderEntry(string Id, bool Enabled, bool IsNew);

/// <summary>
/// The load-order file (spec, "Load-order file"): one mod per line, first loads first and the
/// later one wins, as <c>on | folder</c> or <c>off | folder</c>, with <c>| new</c> on a mod the
/// player hasn't looked at yet. <c>#</c> starts a comment. Readable and editable by hand.
/// </summary>
internal static class LoadOrder
{
    private const string Header =
        "# WWE All Stars mods, in load order: a later mod wins when two replace the same file.\r\n" +
        "# One line per mod folder: \"on | folder\" or \"off | folder\"; \"| new\" marks a mod not looked at yet.\r\n";

    public static List<LoadOrderEntry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        var entries = new List<LoadOrderEntry>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0) continue;
            var enabled = parts[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            var isNew = parts.Skip(2).Any(part => part.Equals("new", StringComparison.OrdinalIgnoreCase));
            if (!entries.Any(entry => string.Equals(entry.Id, parts[1], StringComparison.OrdinalIgnoreCase)))
                entries.Add(new LoadOrderEntry(parts[1], enabled, isNew));
        }
        return entries;
    }

    public static void Write(string path, IEnumerable<LoadOrderEntry> entries)
    {
        var text = new StringBuilder(Header);
        foreach (var entry in entries)
            text.Append(entry.Enabled ? "on" : "off").Append(" | ").Append(entry.Id)
                .Append(entry.IsNew ? " | new" : "").Append("\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>
/// Reads <c>mod.toml</c>: flat <c>key = value</c> lines, basic <c>"…"</c> strings, <c>#</c>
/// comments. That is all the manifest format needs, so no TOML library is pulled in.
/// </summary>
internal static class ModManifestReader
{
    /// <summary>The only game version mods are accepted for (spec, "Manifest").</summary>
    public const string SupportedGame = "TU2";

    public static ModManifest? Read(string path, List<string> problems)
    {
        if (!File.Exists(path))
        {
            problems.Add($"It has no {ModLibrary.ManifestFileName}.");
            return null;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            values[line[..equals].Trim()] = Value(line[(equals + 1)..]);
        }
        var name = values.GetValueOrDefault("name", "");
        if (name.Trim().Length == 0) problems.Add($"Its {ModLibrary.ManifestFileName} has no name.");
        var game = values.GetValueOrDefault("game", "");
        if (problems.Count == 0 && game != SupportedGame)
            problems.Add(game.Length == 0
                ? $"Its {ModLibrary.ManifestFileName} doesn't say which game version it is for (game = \"{SupportedGame}\")."
                : $"It is made for {game}; this launcher runs Title Update 2 (game = \"{SupportedGame}\").");
        var declared = values.GetValueOrDefault("kind", "gameplay");
        var kind = declared switch
        {
            "cosmetic" => ModKind.Cosmetic,
            "gameplay" => ModKind.Gameplay,
            _ => (ModKind?)null,
        };
        if (kind is null) problems.Add($"Its kind is \"{declared}\"; it must be \"cosmetic\" or \"gameplay\".");
        return new ModManifest(name, values.GetValueOrDefault("author", ""),
            values.GetValueOrDefault("version", ""), values.GetValueOrDefault("description", ""),
            game, kind ?? ModKind.Gameplay);
    }

    private static string Value(string literal)
    {
        literal = literal.Trim();
        if (!literal.StartsWith('"'))
        {
            var comment = literal.IndexOf('#');
            return (comment < 0 ? literal : literal[..comment]).TrimEnd();
        }
        var value = new StringBuilder();
        for (var i = 1; i < literal.Length && literal[i] != '"'; i++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length)
            {
                i++;
                value.Append(literal[i] switch { 'n' => '\n', 't' => '\t', var c => c });
                continue;
            }
            value.Append(literal[i]);
        }
        return value.ToString();
    }
}

/// <summary>
/// The package names a mod may replace (spec, "Manifest"; ADR-0026): the short names of the
/// disc's packages and TU2's. Built from the disc and TU2 manifests, or given in tests.
/// <paramref name="configFiles"/> are TU2's inner inis as a mod's <c>config/</c> holds them:
/// <c>Name.ini</c>, or <c>lang/Name.lang</c> for a localised bundle (ticket 07's wrapping).
/// <paramref name="bundleLanguages"/> are the languages whose <c>Coalesced.&lt;lang&gt;</c> TU2
/// ships, which a config mod may edit; null for Title Update 2's.
/// </summary>
public sealed class KnownGameFiles(IEnumerable<string> packageNames, IEnumerable<string>? configFiles = null,
    IEnumerable<string>? bundleLanguages = null)
{
    private readonly HashSet<string> _packages = new(packageNames, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _configFiles = ModWrapping.ConfigFilesByName(configFiles ?? []);
    private readonly HashSet<string> _languages = new(bundleLanguages ?? LanguagesOf(TitleUpdateManifest.Supported), StringComparer.OrdinalIgnoreCase);

    /// <summary>The supported disc's and Title Update 2's packages.</summary>
    public static KnownGameFiles From(DiscManifest disc, TitleUpdateManifest update, IEnumerable<string>? configFiles = null) =>
        new(disc.Files.Concat(update.Files).Select(file => file.Path)
            .Where(path => path.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase))
            .Select(path => path[(path.LastIndexOf('/') + 1)..]), configFiles, LanguagesOf(update));

    public bool IsPackage(string shortName) => _packages.Contains(shortName);

    /// <summary>Where an inner ini of this file name goes in a mod's <c>config/</c>, or null when TU2 has none.</summary>
    public string? ConfigPathOf(string fileName) => _configFiles.GetValueOrDefault(fileName);

    /// <summary>Whether TU2's inner inis were given; without them a fragment's target can't be checked.</summary>
    public bool KnowsConfigFiles => _configFiles.Count > 0;

    /// <summary>Whether TU2 ships <c>Coalesced.&lt;<paramref name="language"/>&gt;</c> (<c>int</c>, <c>deu</c>...).</summary>
    public bool ShipsLanguage(string language) => _languages.Contains(language);

    private static IEnumerable<string> LanguagesOf(TitleUpdateManifest update) =>
        update.Files.Select(file => file.Path)
            .Where(path => path.StartsWith("Coalesced.", StringComparison.OrdinalIgnoreCase)
                && !path.Equals(ModLibrary.ConfigBundle, StringComparison.OrdinalIgnoreCase))
            .Select(path => path["Coalesced.".Length..]);
}
