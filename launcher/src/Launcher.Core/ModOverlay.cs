using System.Globalization;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>Prepare for Play: the update folder built from Title Update 2 and the mods in use.</summary>
public sealed partial class ModLibrary
{
    /// <summary>The update folder built for mods, under the cache; rebuilt only when its stamp changes.</summary>
    public string OverlayDirectory => Path.Combine(layout.CacheDirectory, OverlayFolderName);

    private const string OverlayFolderName = "mod-update";
    private const string PatchTocFileName = "PatchTOC.txt";
    // Beside the folder, not in it: every file in the update folder is visible to the game.
    private string StampPath => OverlayDirectory + ".stamp";

    /// <summary>
    /// The update folder to start the game on (spec, "Overlay folder"; ADR-0026). With no mod in
    /// use it is <paramref name="titleUpdateFolder"/> itself. Otherwise it is
    /// <see cref="OverlayDirectory"/>: TU2's files and each winning mod package, hard-linked or
    /// copied, and a <c>PatchTOC.txt</c> with one line per mod package. Only those files go in:
    /// any <c>.xxx</c> there is live (modding research §2.3). The TU2 folder is never written.
    /// </summary>
    /// <param name="online">The player's <c>wwe_online</c> setting.</param>
    /// <param name="skipStartupLogos">
    /// The player's <c>wwe_skip_startup_logos</c> setting: the update folder is built with
    /// <see cref="LauncherConfigEdit.SkipStartupLogos"/> applied after the mods, online or not (it
    /// changes only the boot).
    /// </param>
    /// <exception cref="LauncherException">The update folder could not be built.</exception>
    public PreparedMods PrepareForPlay(string titleUpdateFolder, bool online, bool skipStartupLogos = false)
    {
        lock (_lock)
        {
            var enabled = Scan().Where(mod => mod.Enabled).ToList();
            // The online rule: with online play on, only cosmetic mods load.
            var leftOut = enabled.Where(mod => mod.IsOffForOnline(online)).ToList();
            var inUse = enabled.Except(leftOut).ToList();
            // A package an installed pack also ships goes into the combined content: the game finds it there first.
            var winners = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in inUse)
                foreach (var package in mod.OverlayPackages)
                    winners[package.Name] = package;
            var leftOutMods = leftOut.Select(mod => new LeftOutMod(mod.Id, mod.Manifest!.Name, mod.Manifest.Version,
                mod.ForcedGameplayReason ?? (mod.IsContentMod ? "a content mod (new wrestlers), offline only."
                    : mod.DlcPackages.Count > 0 ? "it replaces a package an installed DLC pack ships, offline only."
                    : "a gameplay mod."))).ToList();

            // The combined content: built with a content mod or a pack's package in use, otherwise gone (spec, "Removed when not wanted").
            var contentMods = inUse.Where(mod => mod.UsesCombinedContent).ToList();
            CombinedContentReport? content = null;
            ILookup<string, string> supplied = Array.Empty<string>().ToLookup(id => id);
            List<ModConflict> contentConflicts = [];
            try
            {
                if (contentMods.Count == 0) RemoveCombined();
                else (content, supplied) = PrepareCombined(contentMods);
                contentConflicts = ContentConflicts(contentMods);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException($"The content mods could not be prepared in {Dlc.ContentDirectory}: {error.Message}", error);
            }

            var overlayMods = inUse.Where(mod => mod.OverlayPackages.Any() || mod.Config.Count > 0).ToList();
            List<LauncherConfigEdit> launcherEdits = skipStartupLogos ? [LauncherConfigEdit.SkipStartupLogos] : [];
            var updateRoot = titleUpdateFolder;
            ConfigMerge? config = null;
            if (overlayMods.Count > 0 || launcherEdits.Count > 0)
            {
                try
                {
                    config = MergeConfig(titleUpdateFolder, overlayMods, launcherEdits);
                    var tu2Files = Directory.EnumerateFiles(titleUpdateFolder).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    var stamp = Stamp(titleUpdateFolder, tu2Files, overlayMods, launcherEdits);
                    if (!Directory.Exists(OverlayDirectory) || !File.Exists(StampPath) || File.ReadAllText(StampPath) != stamp)
                        Build(titleUpdateFolder, tu2Files, [.. winners.Values], config.Bundles, stamp);
                    updateRoot = OverlayDirectory;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new LauncherException($"The mods could not be prepared in {OverlayDirectory}: {error.Message}", error);
                }
            }
            var report = new ModLoadReport([.. inUse.Select(mod => new UsedMod(mod.Id, mod.Manifest!.Name, mod.Manifest.Version,
            [
                .. mod.Packages.Where(package => winners.TryGetValue(package.Name, out var winner) && ReferenceEquals(winner, package)
                    || mod.DlcPackages.Contains(package) && supplied[mod.Id].Contains(package.Name)).Select(package => package.Name),
                .. config is null ? [] : mod.Config.Where(config.Applied.Contains).Select(file => file.Shown),
                .. mod.Content.Select(file => file.Shown).Where(supplied[mod.Id].Contains),
            ]))])
            {
                LeftOut = leftOutMods,
                ConfigEdits = config?.Edits ?? [],
                ConfigConflicts = config?.Conflicts ?? [],
                Content = content,
                ContentConflicts = contentConflicts,
            };
            return WithShaders(new PreparedMods(updateRoot, report));
        }
    }

    /// <summary>What the update folder is built from: TU2's files, the mods in use, in order, by size and time, and the launcher's edits.</summary>
    private static string Stamp(string titleUpdateFolder, List<string> tu2Files, List<ModInfo> inUse,
        List<LauncherConfigEdit> launcherEdits)
    {
        var stamp = new StringBuilder("mod-update 1\n").Append("tu2 ").Append(Path.GetFullPath(titleUpdateFolder)).Append('\n');
        void Add(string label, string path)
        {
            var info = new FileInfo(path);
            stamp.Append(CultureInfo.InvariantCulture, $"{label} {info.Name} {info.Length} {info.LastWriteTimeUtc.Ticks}\n");
        }
        foreach (var path in tu2Files) Add("tu2", path);
        foreach (var mod in inUse)
        {
            stamp.Append("mod ").Append(mod.Id).Append('\n');
            // Not the pack's packages: installing or removing a pack moves them, and must rebuild the folder.
            foreach (var package in mod.OverlayPackages) Add("package", package.Path);
            foreach (var file in mod.Config) Add($"config {file.Bundle}", file.Path);
        }
        foreach (var edit in launcherEdits) stamp.Append("launcher ").Append(edit.Name).Append('\n');
        return stamp.ToString();
    }

    /// <summary>Builds the folder beside the old one and swaps it in, so a failed build never leaves half a folder in use.</summary>
    private void Build(string titleUpdateFolder, List<string> tu2Files, List<ModPackage> packages,
        IReadOnlyDictionary<string, byte[]> bundles, string stamp)
    {
        var partial = OverlayDirectory + ".partial";
        DeleteOrStop(partial);
        Directory.CreateDirectory(partial);
        var replaced = new HashSet<string>(packages.Select(package => package.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var path in tu2Files)
        {
            var name = Path.GetFileName(path);
            if (name.Equals(PatchTocFileName, StringComparison.OrdinalIgnoreCase) || replaced.Contains(name) || bundles.ContainsKey(name)) continue;
            LinkOrCopy(path, Path.Combine(partial, name));
        }
        foreach (var package in packages) LinkOrCopy(package.Path, Path.Combine(partial, package.Name));
        // The rebuilt bundles are new files, and need their new sizes: TU2 lists its bundles in PatchTOC.txt.
        var tocEntries = new List<ModPackage>(packages);
        foreach (var (name, bytes) in bundles)
        {
            var target = Path.Combine(partial, name);
            File.WriteAllBytes(target, bytes);
            tocEntries.Add(new ModPackage(name, target, bytes.Length, 0));
        }
        // Written new, never through a link: a link's bytes are TU2's.
        var tocPath = Path.Combine(titleUpdateFolder, PatchTocFileName);
        var toc = File.Exists(tocPath) ? File.ReadAllText(tocPath) : "";
        File.WriteAllText(Path.Combine(partial, PatchTocFileName), PatchToc.Merge(toc, tocEntries));

        DeleteOrStop(StampPath);
        DeleteOrStop(OverlayDirectory);
        Directory.Move(partial, OverlayDirectory);
        File.WriteAllText(StampPath, stamp);
    }

    private void LinkOrCopy(string source, string target)
    {
        if (!_linker.TryHardLink(source, target)) File.Copy(source, target);
    }

    /// <summary>
    /// Deletes a stamp, or a folder the next step needs out of the way. Unlike a try-delete
    /// (<see cref="ModFiles.TryDelete"/>) a failure stops Play: a stamp that outlives its folder,
    /// or an old folder left in the way of the swap, would start the game on stale files.
    /// </summary>
    /// <exception cref="LauncherException">The file or folder could not be deleted.</exception>
    private static void DeleteOrStop(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"{path} could not be deleted: {error.Message} Close any program using it, then choose Play again.", error);
        }
    }
}

/// <summary>One mod Play uses, with the files it supplies that no later mod replaces.</summary>
public sealed record UsedMod(string Id, string Name, string Version, IReadOnlyList<string> Files);

/// <summary>An enabled mod the online rule left out of a Play, and why.</summary>
public sealed record LeftOutMod(string Id, string Name, string Version, string Reason);

/// <summary>What a Play loads from mods (spec, "Logging"), for the launcher and session logs.</summary>
public sealed record ModLoadReport(IReadOnlyList<UsedMod> Used)
{
    /// <summary>Enabled mods left out because online play is on (spec, "Online rule").</summary>
    public IReadOnlyList<LeftOutMod> LeftOut { get; init; } = [];

    /// <summary>What each config file of the mods in use did, in load order.</summary>
    public IReadOnlyList<ConfigEdit> ConfigEdits { get; init; } = [];

    /// <summary>The combined content this Play installs; null with no content mod in use.</summary>
    public CombinedContentReport? Content { get; init; }

    /// <summary>Whole inner inis replaced by more than one mod in use.</summary>
    public IReadOnlyList<ConfigConflict> ConfigConflicts { get; init; } = [];

    /// <summary>The content mods' clashes this Play: roster slots, whole roster packages, files (ticket 17).</summary>
    public IReadOnlyList<ModConflict> ContentConflicts { get; init; } = [];

    /// <summary>The report as log lines: a count, one line per mod in load order, the mods left out, then the config edits and conflicts.</summary>
    public IReadOnlyList<string> Lines() =>
    [
        $"mods: {Used.Count} in use",
        .. Used.Select((mod, i) => $"mod {i + 1}: {mod.Name} {mod.Version} ({mod.Id}): " +
            (mod.Files.Count == 0 ? "no files (all replaced by later mods)" : string.Join(", ", mod.Files))),
        .. Content is null ? [] : (string[])[$"content: phase {Content.Phase}, " + (Content.Packs.Count == 0
            ? "no official pack"
            : "official packs " + string.Join(", ", Content.Packs.Select(pack => $"{pack.DisplayName} ({pack.FileName})")))],
        .. Content?.Roster.Select(fill => $"roster: CharacterData_{fill.Id} filled by {fill.ModName} {fill.ModVersion} ({fill.ModId})") ?? [],
        .. LeftOut.Count == 0 ? [] : (string[])[$"mods: {LeftOut.Count} off for online play"],
        .. LeftOut.Select(mod => $"off for online play: {mod.Name} {mod.Version} ({mod.Id}): {mod.Reason}"),
        .. ConfigEdits.Select(edit => $"config: {edit.ModName}: {edit.Bundle} {edit.Ini}: {edit.Outcome}"),
        .. ConfigConflicts.Select(conflict =>
            $"config conflict: {conflict.Bundle} {conflict.Ini} is replaced whole by {string.Join(", ", conflict.Mods)}; {conflict.Winner} wins"),
        .. ContentConflicts.Select(conflict => "content conflict: " + conflict.Kind switch
        {
            ModConflictKind.RosterSlot => $"roster slot {conflict.File}: {string.Join(", ", conflict.Mods)} fill it",
            ModConflictKind.RosterCodeName => $"CodeName {conflict.File}: {string.Join(", ", conflict.Mods)} give it",
            ModConflictKind.RosterPackage => $"{conflict.File} is a whole roster package; it replaces the roster of " +
                string.Join(", ", [.. conflict.Packs, .. conflict.Mods.Where(id => id != conflict.Winner)]),
            _ => $"{conflict.File} is shipped by {string.Join(", ", [.. conflict.Packs, .. conflict.Mods])}",
        } + $"; {conflict.Winner} wins"),
    ];
}

/// <summary>What prepare-for-Play returns: the folder to pass as <c>--update_data_root</c>, and the load report.</summary>
public sealed record PreparedMods(string UpdateDataRoot, ModLoadReport Report)
{
    /// <summary>The mods' packages for the shader pack (ticket 09).</summary>
    public ModShaderSources Shaders { get; init; } = new([], []);

    /// <summary>
    /// The mods in use bring packages the shader pack was not built with: prepare shaders with
    /// <see cref="Shaders"/> before this Play, then record it with <see cref="ModLibrary.ShadersPrepared"/>.
    /// </summary>
    public bool ShadersChanged { get; init; }
}

/// <summary>
/// <c>PatchTOC.txt</c>: lines of <c>&lt;size&gt; &lt;uncompressed size or 0&gt; 0 .\&lt;name&gt; 0 </c>,
/// CRLF, as TU2 ships it (modding research §1.2, §2.3).
/// </summary>
internal static class PatchToc
{
    /// <summary>
    /// TU2's lines with one line per mod package: a line naming the same package is replaced in
    /// place, the others are appended.
    /// </summary>
    public static string Merge(string tu2Toc, IReadOnlyList<ModPackage> packages)
    {
        var lines = tu2Toc.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
        foreach (var package in packages)
        {
            var line = Line(package);
            var index = lines.FindIndex(existing => string.Equals(NameOf(existing), package.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) lines[index] = line;
            else lines.Add(line);
        }
        return string.Concat(lines.Select(line => line + "\r\n"));
    }

    private static string Line(ModPackage package) => string.Create(CultureInfo.InvariantCulture,
        $"{package.Size} {package.UncompressedSize} 0 .\\{package.Name} 0 ");

    /// <summary>The short name a <c>.\Name</c> line lists; null for other lines.</summary>
    private static string? NameOf(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 && parts[3].StartsWith(@".\", StringComparison.Ordinal) ? parts[3][2..] : null;
    }
}
