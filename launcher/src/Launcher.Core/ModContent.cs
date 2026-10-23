using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>One file of a mod's <c>content/</c>; <see cref="Shown"/> is its path as the mod lays it out.</summary>
public sealed record ModContentFile(string Shown, string Path);

/// <summary>The combined content a Play installs (load report): its phase and the official packs it took in.</summary>
public sealed record CombinedContentReport(int Phase, IReadOnlyList<DlcPack> Packs)
{
    /// <summary>The placeholder slots the content mods' roster entries fill, and which mod won each.</summary>
    public IReadOnlyList<RosterFill> Roster { get; init; } = [];
}

/// <summary>
/// Content mods (spec, "Content mods (new wrestlers): one combined content, built at Play";
/// research, "Spike 11"): the game installs only the DLC content with the highest
/// <c>PhaseID</c>, so the content mods in use and every official pack are built into one content
/// above them. The player's pack folders are only read.
/// </summary>
public sealed partial class ModLibrary
{
    /// <summary>The combined content's folder and header name in the DLC content folder (a console content name).</summary>
    public const string CombinedContentName = "WWEALLSTARSRECOMPMODCONTENT";

    private const string CombinedDisplayName = "MODS (COMBINED CONTENT)";
    private const string ContentFolderName = "content";
    private const string GameIni = "Xe-Game.ini";
    private const string DlcTocFileName = "DLCTOC.txt";

    // In the cache, not in the DLC folder: the game lists 00000002 whole.
    private string ContentStampPath => Path.Combine(layout.CacheDirectory, "mod-content.stamp");

    private DlcLibrary Dlc => new(layout.UserDataDirectory);

    /// <summary>
    /// Deletes the combined content and its header, so the game started on its own never sees
    /// it. Play calls it when no content mod is in use, and the launcher after the game exits.
    /// </summary>
    /// <exception cref="LauncherException">It could not be deleted.</exception>
    public void RemoveCombinedContent()
    {
        lock (_lock)
        {
            try
            {
                RemoveCombined();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException($"The mods' combined content could not be removed: {error.Message}", error);
            }
        }
    }

    private void RemoveCombined()
    {
        Dlc.Remove(CombinedContentName);
        DeleteOrStop(ContentStampPath);
    }

    /// <summary>
    /// The mod's <c>content/</c>: files in any folder layout, flattened by name into the combined
    /// content (a flat content installs the same, Spike 11). The launcher writes the TOC and the
    /// phase, so a mod's own are problems.
    /// </summary>
    private static List<ModContentFile> ReadContent(string folder, KnownGameFiles known, List<string> problems)
    {
        var files = new List<ModContentFile>();
        if (!Directory.Exists(folder)) return files;
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            var shown = $"{ContentFolderName}/{Path.GetRelativePath(folder, path).Replace('\\', '/')}";
            if (!SafeContentName().IsMatch(name))
            {
                problems.Add($"{shown} has a file name the game can't use.");
                continue;
            }
            if (name.Equals(DlcTocFileName, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{shown}: a mod doesn't ship {DlcTocFileName}; the launcher writes it.");
                continue;
            }
            if (byName.TryGetValue(name, out var first))
            {
                problems.Add($"{shown} and {first} have the same name; the content goes in one folder.");
                continue;
            }
            byName[name] = shown;
            if (IsFragment(name) && known.KnowsConfigFiles && known.ConfigPathOf(FragmentTarget(name)) is null)
            {
                problems.Add($"{shown} merges into {FragmentTarget(name)}, which the game doesn't have.");
                continue;
            }
            try
            {
                if (name.Equals(RosterFile.FileName, StringComparison.OrdinalIgnoreCase)
                    && !RosterFileIsGood(File.ReadAllText(path), shown, problems)) continue;
                if (IsGameIni(name) && DlcIni.Read(File.ReadAllText(path)).PhaseId is not null)
                {
                    problems.Add($"{shown} sets [GameDLCManager] PhaseID; the launcher sets the phase.");
                    continue;
                }
                if (name.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = File.OpenRead(path);
                    if (Ue3Package.TocUncompressedSize(stream) is null)
                    {
                        problems.Add($"{shown} is not a game package.");
                        continue;
                    }
                    if (PackageOrigin.Check(stream, name, known.IsPackage) is { } renamed)
                    {
                        problems.Add(PackageOrigin.Reason(shown, renamed));
                        continue;
                    }
                }
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
            files.Add(new ModContentFile(shown, path));
        }
        return files;
    }

    /// <summary>A file name as DLC TOC lines carry them: letters, digits, <c>_</c>, <c>-</c> and dots.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$")]
    private static partial Regex SafeContentName();

    /// <summary>Every file whose name contains <c>Xe-Game.ini</c> is read for the phase and the characters (research, step 2).</summary>
    private static bool IsGameIni(string name) => name.Contains(GameIni, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ini and localisation fragments the installer merges by the name after <c>__</c>.</summary>
    private static bool IsFragment(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.');
        return extension.Equals("ini", StringComparison.OrdinalIgnoreCase) || LanguageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The language files' extensions, <c>int</c> for English.</summary>
    private static readonly string[] LanguageExtensions =
        ["int", "deu", "fra", "ita", "esn", "esm", "spa", "jpn", "kor", "chn", "cht", "pol", "rus", "cze", "hun", "ptb", "nld"];

    /// <summary>
    /// The ini or language file a fragment merges into: the installer cuts its name after the
    /// first <c>__</c> (<c>wcsstr</c> <c>0x82C36090</c> on <c>"__"</c> <c>0x821A55B0</c>, in
    /// <c>0x82A32A88</c>); a name with none is its own target.
    /// </summary>
    private static string FragmentTarget(string name)
    {
        var split = name.IndexOf("__", StringComparison.Ordinal);
        return split < 0 ? name : name[(split + 2)..];
    }

    /// <summary>
    /// A mod's fragment under a name no other mod's or pack's has, with one <c>__</c>, right
    /// before the same target: <c>M&lt;n&gt;_&lt;tag&gt;__&lt;target&gt;</c>, or
    /// <c>M&lt;n&gt;__&lt;target&gt;</c> with no tag. A name already taken in
    /// <paramref name="taken"/> gets <c>x2</c>, <c>x3</c>... after the tag (never a bare digit, which
    /// would read as another mod's number).
    /// </summary>
    private static string FragmentName(int modNumber, string name, IReadOnlyDictionary<string, CombinedFile> taken)
    {
        var split = name.IndexOf("__", StringComparison.Ordinal);
        var tag = split < 0 ? "" : name[..split].TrimStart('_');
        var target = FragmentTarget(name);
        var prefix = tag.Length == 0 ? $"M{modNumber}" : $"M{modNumber}_{tag}";
        var renamed = $"{prefix}__{target}";
        for (var copy = 2; taken.ContainsKey(renamed); copy++) renamed = $"{prefix}x{copy}__{target}";
        return renamed;
    }

    /// <summary>One file of the combined content: a link to <see cref="Source"/>, or <see cref="Bytes"/> written new.</summary>
    private sealed record CombinedFile(string? Source, byte[]? Bytes, ModInfo? Mod, string? Shown)
    {
        /// <summary>Writes the file's bytes when the content is built (the roster package), instead of <see cref="Bytes"/>.</summary>
        public Func<byte[]>? Make { get; init; }
    }

    /// <summary>
    /// Builds the combined content for <paramref name="contentMods"/> (in load order, with their
    /// <see cref="ModInfo.DlcPackages"/>), or keeps
    /// the one built for the same packs and files. Returns its report and each mod's winning files.
    /// </summary>
    private (CombinedContentReport Report, ILookup<string, string> Supplied) PrepareCombined(List<ModInfo> contentMods)
    {
        var dlc = Dlc;
        var packFiles = PackFiles(dlc);
        var phase = packFiles.Count == 0 ? 1 : packFiles.Max(entry => entry.Phase) + 1;

        var files = new Dictionary<string, CombinedFile>(StringComparer.OrdinalIgnoreCase);
        var characters = new List<string>();
        void AddCharacters(IEnumerable<string> lines)
        {
            foreach (var line in lines)
                if (!characters.Any(existing => string.Equals(DlcIni.KeyOf(existing), DlcIni.KeyOf(line), StringComparison.OrdinalIgnoreCase)))
                    characters.Add(line);
        }
        var packNumber = 0;
        foreach (var (_, paths, _) in packFiles)
        {
            packNumber++;
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                if (name.Equals(DlcTocFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(GameIni, StringComparison.OrdinalIgnoreCase))
                {
                    var ini = DlcIni.Read(File.ReadAllText(path));
                    AddCharacters(ini.Characters);
                    if (ini.Rest.Length > 0) files[$"P{packNumber}__{GameIni}"] = new(null, Encoding.ASCII.GetBytes(ini.Rest), null, null);
                    continue;
                }
                files[name] = new(path, null, null, null);
            }
        }
        var readOnly = new List<CombinedFile>();
        var rosterFiles = new List<(ModInfo Mod, int Number, ModContentFile File)>();
        var modNumber = 0;
        foreach (var mod in contentMods)
        {
            modNumber++;
            foreach (var package in mod.DlcPackages) files[package.Name] = new(package.Path, null, mod, package.Name);
            foreach (var file in mod.Content)
            {
                var name = Path.GetFileName(file.Path);
                if (name.Equals(RosterFile.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    rosterFiles.Add((mod, modNumber, file));
                    readOnly.Add(new(null, null, mod, file.Shown));
                    continue;
                }
                if (IsGameIni(name))
                {
                    var ini = DlcIni.Read(File.ReadAllText(file.Path));
                    AddCharacters(ini.Characters);
                    var target = FragmentName(modNumber, name, files);
                    if (ini.Rest.Length > 0) files[target] = new(null, Encoding.ASCII.GetBytes(ini.Rest), mod, file.Shown);
                    // Its characters went into the generated ini; nothing else is left to write.
                    else readOnly.Add(new(null, null, mod, file.Shown));
                    continue;
                }
                files[IsFragment(name) ? FragmentName(modNumber, name, files) : name] = new(file.Path, null, mod, file.Shown);
            }
        }
        var roster = AddRoster(files, rosterFiles, AddCharacters);
        var gameIni = new StringBuilder("[GameDLCManager]\r\n")
            .Append(CultureInfo.InvariantCulture, $"PhaseID={phase}\r\n\r\n[AvailableCharacters]\r\n");
        foreach (var line in characters) gameIni.Append(line).Append("\r\n");
        files[GameIni] = new(null, Encoding.ASCII.GetBytes(gameIni.ToString()), null, null);

        var supplied = files.Values.Concat(readOnly).Where(file => file.Mod is not null)
            .ToLookup(file => file.Mod!.Id, file => file.Shown!, StringComparer.OrdinalIgnoreCase);
        var report = new CombinedContentReport(phase, [.. packFiles.Select(entry => entry.Pack)]) { Roster = roster };

        var stamp = ContentStamp(packFiles.Select(entry => (entry.Pack.FileName, entry.Files)), contentMods);
        var built = Path.Combine(dlc.ContentDirectory, CombinedContentName);
        if (!Directory.Exists(built) || !File.Exists(Path.Combine(dlc.HeadersDirectory, CombinedContentName + ".header"))
            || !File.Exists(ContentStampPath) || File.ReadAllText(ContentStampPath) != stamp)
            BuildCombined(dlc, files, stamp);
        return (report, supplied);
    }

    /// <summary>The installed official packs with their files, in the order the combined content takes them in (phase, then name).</summary>
    private static List<(DlcPack Pack, List<string> Files, int Phase)> PackFiles(DlcLibrary dlc) =>
        [.. dlc.Installed().Select(pack => (Pack: pack, Files: Directory.Exists(Path.Combine(dlc.ContentDirectory, pack.FileName))
                ? Directory.EnumerateFiles(Path.Combine(dlc.ContentDirectory, pack.FileName), "*", SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase).ToList()
                : []))
            .Select(entry => (entry.Pack, entry.Files, Phase: PhaseOf(entry.Files)))
            .OrderBy(entry => entry.Phase).ThenBy(entry => entry.Pack.FileName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A pack's phase: the highest <c>[GameDLCManager] PhaseID</c> among its game inis, 0 without one.</summary>
    private static int PhaseOf(List<string> files) =>
        files.Where(path => IsGameIni(Path.GetFileName(path)))
            .Select(path => DlcIni.Read(File.ReadAllText(path)).PhaseId ?? 0)
            .DefaultIfEmpty(0).Max();

    private static string ContentStamp(IEnumerable<(string Name, List<string> Files)> packs, List<ModInfo> mods)
    {
        var stamp = new StringBuilder("mod-content 1\n");
        void Add(string label, string path)
        {
            var info = new FileInfo(path);
            stamp.Append(CultureInfo.InvariantCulture, $"{label} {info.Length} {info.LastWriteTimeUtc.Ticks}\n");
        }
        foreach (var (name, files) in packs)
        {
            stamp.Append("pack ").Append(name).Append('\n');
            foreach (var path in files) Add(path, path);
        }
        foreach (var mod in mods)
        {
            stamp.Append("mod ").Append(mod.Id).Append('\n');
            foreach (var package in mod.DlcPackages) Add($"{PackagesFolderName}/{package.Name}", package.Path);
            foreach (var file in mod.Content) Add(file.Shown, file.Path);
        }
        return stamp.ToString();
    }

    /// <summary>
    /// Builds the content in the DLC staging folder, which the game never lists, then swaps it
    /// in and writes its header. Linked files are never written: a link's bytes are the pack's.
    /// </summary>
    private void BuildCombined(DlcLibrary dlc, Dictionary<string, CombinedFile> files, string stamp)
    {
        var staged = Path.Combine(dlc.StagingDirectory, CombinedContentName);
        DeleteOrStop(staged);
        Directory.CreateDirectory(staged);
        var toc = new List<string>();
        foreach (var (name, file) in files.OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(staged, name);
            if ((file.Bytes ?? file.Make?.Invoke()) is { } bytes) File.WriteAllBytes(target, bytes);
            else LinkOrCopy(file.Source!, target);
            if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            toc.Add(string.Create(CultureInfo.InvariantCulture, $"{new FileInfo(target).Length} {UncompressedSize(target)} 0 .\\{name} 0"));
        }
        File.WriteAllText(Path.Combine(staged, DlcTocFileName), string.Concat(toc.Select(line => line + "\r\n")));

        DeleteOrStop(ContentStampPath);
        dlc.Remove(CombinedContentName);
        Directory.CreateDirectory(dlc.ContentDirectory);
        Directory.Move(staged, Path.Combine(dlc.ContentDirectory, CombinedContentName));
        dlc.WriteHeader(CombinedContentName, CombinedDisplayName);
        Directory.CreateDirectory(layout.CacheDirectory);
        File.WriteAllText(ContentStampPath, stamp);
    }

    /// <summary>The TOC's second column: what a whole-file LZO package expands to, else 0.</summary>
    private static long UncompressedSize(string path)
    {
        if (!path.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase)) return 0;
        try
        {
            using var stream = File.OpenRead(path);
            return Ue3Package.TocUncompressedSize(stream) ?? 0;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }
}

/// <summary>
/// A DLC <c>Xe-Game.ini</c> fragment, split the way the combined content needs it: the
/// <c>[GameDLCManager] PhaseID</c>, the <c>[AvailableCharacters]</c> lines, and the other sections.
/// The rest of <c>[GameDLCManager]</c> is dropped: the game derives <c>InstalledPhases</c> and
/// ignores <c>AllAvailable</c> (research, "What the DLC installer reads").
/// </summary>
internal sealed record DlcIni(int? PhaseId, IReadOnlyList<string> Characters, string Rest)
{
    public static DlcIni Read(string text)
    {
        int? phase = null;
        var characters = new List<string>();
        var rest = new StringBuilder();
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!IsDlcManager(section) && !IsCharacters(section)) rest.Append(line).Append("\r\n");
                continue;
            }
            if (IsDlcManager(section))
            {
                if (string.Equals(KeyOf(line), "PhaseID", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(line.IndexOf('=') + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    phase = value;
            }
            else if (IsCharacters(section))
            {
                if (line.Length > 0 && !line.StartsWith(';') && line.Contains('=')) characters.Add(line);
            }
            else if (line.Length > 0 || rest.Length > 0) rest.Append(line).Append("\r\n");
        }
        return new DlcIni(phase, characters, rest.ToString().TrimEnd() is { Length: > 0 } kept ? kept + "\r\n" : "");
    }

    public static string KeyOf(string line)
    {
        var equals = line.IndexOf('=');
        return equals < 0 ? "" : line[..equals].Trim();
    }

    private static bool IsDlcManager(string section) => section.Equals("GameDLCManager", StringComparison.OrdinalIgnoreCase);

    private static bool IsCharacters(string section) => section.Equals("AvailableCharacters", StringComparison.OrdinalIgnoreCase);
}
