using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// One file of a mod's <c>config/</c>: edits to, or the whole of, inner ini <see cref="Ini"/> of
/// bundle <see cref="Bundle"/> (<c>Coalesced.ini</c>, or <c>Coalesced.&lt;lang&gt;</c> for a
/// file in <c>config/&lt;lang&gt;/</c>).
/// </summary>
public sealed record ModConfigFile(string Bundle, string Ini, string Path)
{
    /// <summary>The file as the mod lays it out, such as <c>config/int/CharacterNames.int</c>.</summary>
    public string Shown => Bundle.Equals(ModLibrary.ConfigBundle, StringComparison.OrdinalIgnoreCase)
        ? $"config/{Ini}"
        : $"config/{Bundle[(Bundle.IndexOf('.') + 1)..]}/{Ini}";
}

/// <summary>What one mod's config file did at Play, for the load report.</summary>
public sealed record ConfigEdit(string ModName, string Bundle, string Ini, string Outcome);

/// <summary>
/// An edit the launcher itself makes to inner ini <see cref="Ini"/> of bundle <see cref="Bundle"/> at
/// Play, in a mod's edit syntax, after every mod's (Faster starts: the startup logos).
/// </summary>
public sealed record LauncherConfigEdit(string Name, string Bundle, string Ini, string Text)
{
    /// <summary>Skip startup logos: the THQ and WWE movies out of the startup list; the disclaimer stays.</summary>
    public static readonly LauncherConfigEdit SkipStartupLogos = new("Skip startup logos", ModLibrary.ConfigBundle,
        "Xe-Game.ini", "[VendorStartup]\n-Movies=../Game/Movies/THQ.bik\n-Movies=../Game/Movies/WWE.bik\n");
}

/// <summary>Two or more mods in use replacing the same whole inner ini; the last in the load order wins.</summary>
public sealed record ConfigConflict(string Bundle, string Ini, IReadOnlyList<string> Mods, string Winner);

/// <summary>Config mods: reading <c>config/</c> and merging it into TU2's Coalesced bundles.</summary>
public sealed partial class ModLibrary
{
    /// <summary>The config bundle of the <c>Xe-*.ini</c> files; the localised ones are <c>Coalesced.&lt;lang&gt;</c>.</summary>
    public const string ConfigBundle = "Coalesced.ini";

    internal const string ConfigFolderName = "config";

    /// <summary>
    /// The mod's <c>config/</c>: files straight in it edit <c>Coalesced.ini</c>, files in
    /// <c>config/&lt;lang&gt;/</c> edit <c>Coalesced.&lt;lang&gt;</c>, which TU2 must ship.
    /// </summary>
    private List<ModConfigFile> ReadConfig(string folder, List<string> problems)
    {
        var files = new List<ModConfigFile>();
        if (!Directory.Exists(folder)) return files;
        void AddFiles(string from, string bundle, string shownFolder)
        {
            foreach (var path in Directory.EnumerateFiles(from).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(path);
                if (SafeIniName().IsMatch(name)) files.Add(new ModConfigFile(bundle, name, path));
                else problems.Add($"{shownFolder}{name} has a file name the game can't use.");
            }
        }
        AddFiles(folder, ConfigBundle, $"{ConfigFolderName}/");
        foreach (var sub in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var language = System.IO.Path.GetFileName(sub).ToLowerInvariant();
            var shown = $"{ConfigFolderName}/{System.IO.Path.GetFileName(sub)}/";
            if (!_known.ShipsLanguage(language))
            {
                problems.Add($"{shown} edits Coalesced.{language}, which Title Update 2 doesn't ship.");
                continue;
            }
            foreach (var nested in Directory.EnumerateDirectories(sub).Order(StringComparer.OrdinalIgnoreCase))
                problems.Add($"{shown}{System.IO.Path.GetFileName(nested)} is a folder; a language's inis go straight in {shown}.");
            AddFiles(sub, $"Coalesced.{language}", shown);
        }
        return files;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$")]
    private static partial Regex SafeIniName();

    /// <summary>The merged bundles of a Play and what each config file did.</summary>
    private sealed record ConfigMerge(
        Dictionary<string, byte[]> Bundles,
        List<ConfigEdit> Edits,
        List<ConfigConflict> Conflicts,
        HashSet<ModConfigFile> Applied);

    /// <summary>
    /// TU2's bundles with the config files of the mods in use applied in load order, then the
    /// launcher's own edits. A whole inner ini replaces the one before it; two mods doing that to
    /// the same ini is a conflict. Only bundles a mod or the launcher edits are rebuilt.
    /// </summary>
    /// <exception cref="LauncherException">A TU2 bundle is damaged.</exception>
    private static ConfigMerge MergeConfig(string titleUpdateFolder, List<ModInfo> inUse,
        IReadOnlyList<LauncherConfigEdit> launcherEdits)
    {
        var merge = new ConfigMerge(new(StringComparer.OrdinalIgnoreCase), [], [], []);
        var bundles = new Dictionary<string, CoalescedBundle?>(StringComparer.OrdinalIgnoreCase);
        var replacers = new Dictionary<(string, string), List<(ModInfo Mod, ModConfigFile File)>>();
        // The inner ini an edit goes to, or null with the reason reported.
        CoalescedIni? Target(string bundleName, string iniName, Action<string> report)
        {
            if (!bundles.TryGetValue(bundleName, out var bundle))
                bundles[bundleName] = bundle = ReadBundle(Path.Combine(titleUpdateFolder, bundleName));
            if (bundle == null) report("left out, Title Update 2's folder has no such bundle");
            else if (bundle.Find(iniName) is { } found) return found;
            else report("left out, the bundle has no such ini");
            return null;
        }
        foreach (var mod in inUse)
            foreach (var file in mod.Config)
            {
                void Report(string outcome) => merge.Edits.Add(new ConfigEdit(mod.Manifest!.Name, file.Bundle, file.Ini, outcome));
                if (Target(file.Bundle, file.Ini, Report) is not { } ini) continue;
                var text = File.ReadAllText(file.Path);
                if (IniMerge.IsComplete(ini.Body, text))
                {
                    ini.Body = text;
                    var key = (file.Bundle.ToUpperInvariant(), ini.ShortName.ToUpperInvariant());
                    if (!replacers.TryGetValue(key, out var earlier)) replacers[key] = earlier = [];
                    foreach (var (_, lost) in earlier) merge.Applied.Remove(lost);
                    earlier.Add((mod, file));
                    Report("replaced whole");
                }
                else
                {
                    ini.Body = IniMerge.Apply(ini.Body, text, out var count);
                    Report(count == 1 ? "1 edit" : $"{count} edits");
                }
                merge.Applied.Add(file);
                merge.Bundles[file.Bundle] = [];
            }
        foreach (var edit in launcherEdits)
        {
            void Report(string outcome) => merge.Edits.Add(new ConfigEdit($"launcher ({edit.Name})", edit.Bundle, edit.Ini, outcome));
            if (Target(edit.Bundle, edit.Ini, Report) is not { } ini) continue;
            ini.Body = IniMerge.Apply(ini.Body, edit.Text, out var count);
            Report(count == 1 ? "1 edit" : $"{count} edits");
            merge.Bundles[edit.Bundle] = [];
        }
        foreach (var name in merge.Bundles.Keys.ToList()) merge.Bundles[name] = bundles[name]!.ToBytes();
        foreach (var ((_, _), replaced) in replacers)
            if (replaced.Count > 1)
                merge.Conflicts.Add(new ConfigConflict(replaced[0].File.Bundle, replaced[0].File.Ini,
                    [.. replaced.Select(entry => entry.Mod.Id)], replaced[^1].Mod.Id));
        return merge;
    }

    private static CoalescedBundle? ReadBundle(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return CoalescedBundle.Parse(File.ReadAllBytes(path));
        }
        catch (InvalidDataException error)
        {
            throw new LauncherException($"Title Update 2's {Path.GetFileName(path)} is damaged ({error.Message}); repair the game in the launcher.", error);
        }
    }
}
