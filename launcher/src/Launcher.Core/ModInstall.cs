using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// A mod found by <see cref="ModLibrary.Inspect"/>, ready to install into <c>mods/&lt;Id&gt;</c>.
/// <see cref="Installed"/> is the mod already in that folder, which makes this an update.
/// </summary>
public sealed record ModInstallCandidate(string Source, string Folder, ModManifest Manifest, string Id, ModInfo? Installed)
{
    public bool IsUpdate => Installed is not null;
}

/// <summary>
/// An input with no <c>mod.toml</c> whose files are all game packages or TU2 inner inis, offered
/// for wrapping into a mod (spec, "Install"). <see cref="Files"/> are the paths the mod would
/// get, such as <c>packages/CHR_101_A.xxx</c> or <c>config/WWEGame.ini</c>.
/// </summary>
public sealed record ModWrapCandidate(string Source, string SuggestedName, IReadOnlyList<string> Files)
{
    internal IReadOnlyList<(string From, string To)> Copies { get; init; } = [];
}

/// <summary>An input, or a mod inside one, that can't be installed, with a plain reason.</summary>
public sealed record ModRefusal(string Source, string Reason);

/// <summary>
/// What a set of dropped or picked inputs holds (spec, "Install"). Disposing removes the folder
/// archives were unpacked to, so install from it before disposing.
/// </summary>
public sealed class ModIntake(
    IReadOnlyList<ModInstallCandidate> mods,
    IReadOnlyList<ModWrapCandidate> wraps,
    IReadOnlyList<ModRefusal> refusals,
    string? tempDirectory) : IDisposable
{
    public IReadOnlyList<ModInstallCandidate> Mods { get; } = mods;
    public IReadOnlyList<ModWrapCandidate> Wraps { get; } = wraps;
    public IReadOnlyList<ModRefusal> Refusals { get; } = refusals;

    public void Dispose() => ModFiles.TryDelete(tempDirectory);
}

/// <summary>Installing, updating and wrapping downloaded mods (ticket 07; spec, "Identity", "Install").</summary>
public sealed partial class ModLibrary
{
    /// <summary>The most one archive may unpack to: the package intake's guard (tests lower it).</summary>
    public long MaxUnpackedBytes { get; init; } = PackageIntake.MaxUnpackedBytes;

    private const string TooLarge = "The archive unpacks to more than 8 GB, far more than any mod.";

    /// <summary>
    /// Unpacks <paramref name="inputs"/> (<c>.zip</c>, <c>.rar</c>, <c>.7z</c>, folders or loose
    /// game files) and reports every folder with a <c>mod.toml</c> as a new mod or an update,
    /// every input of loose game files as a wrap candidate, and the rest as refusals. Nothing in
    /// <c>mods/</c> changes; archives unpack under <c>mods/.intake-*</c>.
    /// </summary>
    public ModIntake Inspect(IEnumerable<string> inputs, CancellationToken cancel = default)
    {
        var mods = new List<ModInstallCandidate>();
        var wraps = new List<ModWrapCandidate>();
        var refusals = new List<ModRefusal>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? temp = null;
        try
        {
            List<ModInfo> installed;
            lock (_lock) installed = Scan();
            var count = 0;
            foreach (var input in inputs)
            {
                cancel.ThrowIfCancellationRequested();
                string folder;
                if (Directory.Exists(input))
                {
                    folder = input;
                }
                else if (File.Exists(input))
                {
                    temp ??= Directory.CreateDirectory(Path.Combine(layout.ModsDirectory, ".intake-" + Guid.NewGuid().ToString("N"))).FullName;
                    folder = Directory.CreateDirectory(Path.Combine(temp, (count++).ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
                    if (PackageIntake.IsArchive(input))
                    {
                        if (!PackageIntake.TryUnpack(input, folder, cancel, out var problem, MaxUnpackedBytes, TooLarge))
                        {
                            refusals.Add(new ModRefusal(input, problem!));
                            continue;
                        }
                    }
                    else
                    {
                        File.Copy(input, Path.Combine(folder, Path.GetFileName(input)));
                    }
                }
                else
                {
                    refusals.Add(new ModRefusal(input, "Not found."));
                    continue;
                }
                InspectFolder(input, folder, installed, claimed, mods, wraps, refusals);
            }
            return new ModIntake(mods, wraps, refusals, temp);
        }
        catch
        {
            ModFiles.TryDelete(temp);
            throw;
        }
    }

    private void InspectFolder(string source, string folder, List<ModInfo> installed, HashSet<string> claimed,
        List<ModInstallCandidate> mods, List<ModWrapCandidate> wraps, List<ModRefusal> refusals)
    {
        var modFolders = new List<string>();
        foreach (var manifest in Directory.EnumerateFiles(folder, ManifestFileName, ModFiles.Recursive)
            .Select(Path.GetDirectoryName).OfType<string>().OrderBy(path => path.Length))
            if (!modFolders.Any(outer => ModFiles.IsInside(outer, manifest))) modFolders.Add(manifest);

        if (modFolders.Count == 0)
        {
            if (ModWrapping.Candidate(source, folder, _known) is { } wrap)
            {
                if (PackageOrigin.RenamedIn(wrap, _known) is { } renamed) refusals.Add(new ModRefusal(source, renamed));
                else wraps.Add(wrap);
            }
            else refusals.Add(new ModRefusal(source, ModWrapping.Refusal(folder, _known)));
            return;
        }
        foreach (var modFolder in modFolders.Order(StringComparer.OrdinalIgnoreCase))
        {
            // A rooted id makes Read look at the folder itself: the same checks a mod in mods/ gets.
            var found = Read(new LoadOrderEntry(modFolder, Enabled: false, IsNew: false));
            var label = found.Manifest is { Name.Length: > 0 } named ? named.Name : Path.GetFileName(modFolder);
            if (found.Problems.Count > 0 || found.Manifest is null)
            {
                refusals.Add(new ModRefusal(source, $"{label}: {string.Join(" ", found.Problems)}"));
                continue;
            }
            if (FolderNameFor(found.Manifest.Name) is not { } id)
            {
                refusals.Add(new ModRefusal(source, $"{label}: its name can't be used as a folder name."));
                continue;
            }
            if (!claimed.Add(id))
            {
                refusals.Add(new ModRefusal(source, $"{label} is in here twice; only the first one is installed."));
                continue;
            }
            var current = installed.Find(mod => string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase));
            mods.Add(new ModInstallCandidate(source, modFolder, found.Manifest, id, current));
        }
    }

    /// <summary>
    /// Installs a mod from an intake: copied to <c>mods/.staging-*</c>, checked, then moved into
    /// <c>mods/&lt;Id&gt;</c>. A new mod starts off and new; an update replaces the folder and
    /// keeps its on/off state and place in the load order. On failure <c>mods/</c> is unchanged.
    /// </summary>
    /// <returns>The mod's id.</returns>
    /// <exception cref="LauncherException">The mod failed its checks, or a file couldn't be written.</exception>
    public string Install(ModInstallCandidate candidate, CancellationToken cancel = default)
    {
        Commit(candidate.Id, candidate.Manifest.Name, staging => ModFiles.CopyFolder(candidate.Folder, staging, cancel), cancel);
        return candidate.Id;
    }

    /// <summary>
    /// Wraps loose game files into a mod named <paramref name="name"/>: the launcher writes its
    /// <c>mod.toml</c> (<c>kind = "cosmetic"</c> when <paramref name="cosmetic"/>, otherwise
    /// <c>"gameplay"</c>) and lays the files out as <see cref="ModWrapCandidate.Files"/>. A mod of
    /// that name already installed is updated, as <see cref="Install"/> does.
    /// </summary>
    /// <returns>The mod's id.</returns>
    /// <exception cref="LauncherException">The name can't be a folder name, or the install failed.</exception>
    public string Wrap(ModWrapCandidate candidate, string name, bool cosmetic, CancellationToken cancel = default)
    {
        name = name.Trim();
        var id = FolderNameFor(name) ?? throw new LauncherException("Give the mod a name made of letters or digits.");
        Commit(id, name, staging =>
        {
            File.WriteAllText(Path.Combine(staging, ManifestFileName), ModWrapping.Manifest(name, Path.GetFileName(candidate.Source), cosmetic));
            foreach (var (from, to) in candidate.Copies)
            {
                cancel.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, to);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(from, target);
            }
        }, cancel);
        return id;
    }

    /// <summary>The installed mod a manifest or wrap name would update, or null when it would be new.</summary>
    public ModInfo? Installed(string name) =>
        FolderNameFor(name) is { } id ? List().FirstOrDefault(mod => string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>
    /// The folder a mod named <paramref name="name"/> installs into (spec, "Identity"): characters
    /// Windows can't use, and <c>|</c> (the load-order file's separator), become <c>_</c>; runs of
    /// spaces become one; leading dots and trailing dots and spaces go; device names get a
    /// <c>_</c>; at most 64 characters. Null when nothing is left.
    /// </summary>
    public static string? FolderNameFor(string name)
    {
        var safe = new StringBuilder();
        foreach (var c in name.Trim())
        {
            var next = char.IsControl(c) || ModFiles.Unsafe.Contains(c) ? '_' : char.IsWhiteSpace(c) ? ' ' : c;
            if (next == ' ' && safe.Length > 0 && safe[^1] == ' ') continue;
            safe.Append(next);
        }
        var text = safe.ToString().TrimStart('.', ' ');
        if (text.Length > 64) text = text[..64];
        text = text.TrimEnd('.', ' ');
        if (text.Length == 0 || text.All(c => c == '_')) return null;
        var stem = text.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && stem[..3] is "COM" or "LPT" && char.IsAsciiDigit(stem[3])))
            text += "_";
        return text;
    }

    private void Commit(string id, string name, Action<string> fill, CancellationToken cancel)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(layout.ModsDirectory);
            var staging = Path.Combine(layout.ModsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            string? old = null;
            try
            {
                Directory.CreateDirectory(staging);
                fill(staging);
                var check = Read(new LoadOrderEntry(staging, Enabled: false, IsNew: false));
                if (check.Problems.Count > 0)
                    throw new LauncherException($"{name} wasn't installed: {string.Join(" ", check.Problems)}");
                cancel.ThrowIfCancellationRequested();

                // The load-order entry of an update stays as it is: Sync runs only while a folder is in place.
                Sync();
                var existing = Directory.EnumerateDirectories(layout.ModsDirectory).Select(Path.GetFileName)
                    .FirstOrDefault(folder => string.Equals(folder, id, StringComparison.OrdinalIgnoreCase));
                var target = Path.Combine(layout.ModsDirectory, existing ?? id);
                if (existing is not null)
                {
                    old = Path.Combine(layout.ModsDirectory, ".old-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(target, old);
                }
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    if (old is not null) Directory.Move(old, target);
                    old = null;
                    throw;
                }
                Sync();
            }
            catch (UnauthorizedAccessException error)
            {
                throw new LauncherException($"{name} wasn't installed: the launcher may not write to {layout.ModsDirectory}.", error);
            }
            catch (IOException error)
            {
                throw new LauncherException($"{name} wasn't installed: {error.Message}", error);
            }
            finally
            {
                ModFiles.TryDelete(staging);
                ModFiles.TryDelete(old);
            }
        }
    }
}

/// <summary>Wrapping loose game files into a mod (spec, "Install"), and the TU2 inner inis that may be wrapped.</summary>
public static class ModWrapping
{
    /// <summary>Read-me and picture files a download often carries; a wrap leaves them out instead of refusing.</summary>
    private static readonly string[] Extras = [".txt", ".md", ".nfo", ".pdf", ".url", ".htm", ".html", ".jpg", ".jpeg", ".png", ".gif", ".bmp"];

    internal static ModWrapCandidate? Candidate(string source, string folder, KnownGameFiles known)
    {
        var copies = new List<(string From, string To)>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", ModFiles.Recursive).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Destination(file, known) is { } to) copies.Add((file, to));
            else if (!IsExtra(file)) return null;
        }
        if (copies.Count == 0 || copies.GroupBy(copy => copy.To, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return null;
        var suggested = Directory.Exists(source)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source))
            : Path.GetFileNameWithoutExtension(source);
        return new ModWrapCandidate(source, suggested, [.. copies.Select(copy => copy.To)]) { Copies = copies };
    }

    internal static string Refusal(string folder, KnownGameFiles known)
    {
        var files = Directory.EnumerateFiles(folder, "*", ModFiles.Recursive).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.FirstOrDefault(file => Destination(file, known) is null && !IsExtra(file)) is { } stranger)
            return $"This is not a mod: it has no {ModLibrary.ManifestFileName}, and {Path.GetRelativePath(folder, stranger).Replace('\\', '/')} is not a game package or config file.";
        var twice = files.Select(file => Destination(file, known)).OfType<string>()
            .GroupBy(to => to, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (twice is not null)
            return $"{Path.GetFileName(twice.Key)} is in here more than once, so it isn't clear which one to use.";
        return $"This is not a mod: it has no {ModLibrary.ManifestFileName} and no game files.";
    }

    private static string? Destination(string file, KnownGameFiles known)
    {
        var name = Path.GetFileName(file);
        if (known.IsPackage(name)) return ModLibrary.PackagesFolderName + "/" + name;
        return known.ConfigPathOf(name) is { } config ? ModLibrary.ConfigFolderName + "/" + config : null;
    }

    private static bool IsExtra(string file) =>
        Extras.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    internal static string Manifest(string name, string source, bool cosmetic) =>
        "# Written by the WWE All Stars launcher when this mod was installed from loose files.\r\n" +
        $"name = {Quoted(name)}\r\n" +
        "author = \"\"\r\n" +
        "version = \"\"\r\n" +
        $"description = {Quoted("Installed from " + source + ".")}\r\n" +
        "game = \"TU2\"\r\n" +
        $"kind = \"{(cosmetic ? "cosmetic" : "gameplay")}\"\r\n";

    private static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static Dictionary<string, string> ConfigFilesByName(IEnumerable<string> configFiles)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in configFiles) byName.TryAdd(path[(path.LastIndexOf('/') + 1)..], path);
        return byName;
    }

    /// <summary>
    /// TU2's inner inis, as <see cref="KnownGameFiles"/> takes them, from the <c>Coalesced.*</c>
    /// bundles in <paramref name="updateFolder"/>: <c>Name.ini</c> from <c>Coalesced.ini</c>,
    /// <c>lang/Name.lang</c> from <c>Coalesced.lang</c>. Only the names are read; a bundle that
    /// can't be read adds what was read before the damage.
    /// </summary>
    public static IReadOnlyList<string> ConfigFilesIn(string updateFolder)
    {
        var files = new List<string>();
        if (!Directory.Exists(updateFolder)) return files;
        foreach (var bundle in Directory.EnumerateFiles(updateFolder, "Coalesced.*").Order(StringComparer.OrdinalIgnoreCase))
        {
            var language = Path.GetExtension(bundle).TrimStart('.').ToLowerInvariant();
            try
            {
                foreach (var inner in CoalescedBundle.ReadNames(File.ReadAllBytes(bundle)))
                {
                    var name = inner[(inner.LastIndexOfAny(['\\', '/']) + 1)..];
                    if (name.Length > 0) files.Add(language == "ini" ? name : $"{language}/{name}");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An unreadable bundle only means its inis can't be wrapped.
            }
        }
        return files;
    }
}

/// <summary>File helpers for installing mods.</summary>
internal static class ModFiles
{
    public static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    public static readonly char[] Unsafe = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static bool IsInside(string outer, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(outer) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void CopyFolder(string from, string to, CancellationToken cancel)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", Recursive))
        {
            cancel.ThrowIfCancellationRequested();
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public static void TryDelete(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open elsewhere; a dot-folder in mods/ is never read as a mod.
        }
    }
}
