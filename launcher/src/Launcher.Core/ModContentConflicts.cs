namespace WweAllStars.Launcher.Core;

/// <summary>
/// Content-mod conflicts (ticket 17; spec, "Conflicts" under content mods): the same roster slot,
/// a whole roster package, and the same file, beside the package conflicts of ticket 06.
/// </summary>
public sealed partial class ModLibrary
{
    /// <summary>The packages there is one of for the whole roster; Pack 1 ships both (research, "Content mods").</summary>
    private static readonly string[] WholeRosterPackages = [RosterPackageName, "Shared_Characters_DLC.xxx"];

    /// <summary>
    /// The clashes among the content mods of <paramref name="mods"/> (load order) and with the
    /// installed official packs, in the order slots, code names, whole roster packages, files. A later mod wins;
    /// a mod's file always wins over a pack's, as the combined content takes the packs first.
    /// </summary>
    private List<ModConflict> ContentConflicts(IEnumerable<ModInfo> mods)
    {
        var content = mods.Where(mod => mod.IsContentMod).ToList();
        if (content.Count == 0) return [];
        var packs = PackFileNames();
        List<string> PacksShipping(string name) => [.. packs.Where(pack => pack.Names.Contains(name)).Select(pack => pack.DisplayName)];
        var conflicts = new List<ModConflict>();

        var bySlot = new SortedDictionary<int, List<string>>();
        foreach (var mod in content)
            foreach (var file in mod.Content.Where(file => IsNamed(file, RosterFile.FileName)))
                foreach (var entry in RosterFile.Read(File.ReadAllText(file.Path), file.Shown, []))
                {
                    if (!bySlot.TryGetValue(entry.Id, out var slotMods)) bySlot[entry.Id] = slotMods = [];
                    if (!slotMods.Contains(mod.Id)) slotMods.Add(mod.Id);
                }
        conflicts.AddRange(bySlot.Where(slot => slot.Value.Count > 1).Select(slot =>
            new ModConflict($"CharacterData_{slot.Key}", slot.Value, slot.Value[^1]) { Kind = ModConflictKind.RosterSlot }));
        conflicts.AddRange(RosterSlots(content.SelectMany((mod, i) => mod.Content.Where(file => IsNamed(file, RosterFile.FileName))
            .Select(file => (mod, i + 1, file)))).Clashes);

        foreach (var name in WholeRosterPackages)
        {
            var shippers = content.Where(mod => mod.Content.Any(file => IsNamed(file, name))).ToList();
            if (shippers.Count == 0) continue;
            var packsShipping = PacksShipping(name);
            if (content.Count == 1 && packsShipping.Count == 0) continue;
            conflicts.Add(new ModConflict(name, [.. content.Select(mod => mod.Id)], shippers[^1].Id)
            {
                Kind = ModConflictKind.RosterPackage,
                Packs = packsShipping,
            });
        }

        var byFile = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in content)
            foreach (var file in mod.Content)
            {
                var name = Path.GetFileName(file.Path);
                // Merged, not replaced: the roster file by slot, the game ini and fragments by the installer's rules.
                if (IsNamed(file, RosterFile.FileName) || IsGameIni(name) || IsFragment(name)
                    || WholeRosterPackages.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (!byFile.TryGetValue(name, out var fileMods)) byFile[name] = fileMods = [];
                fileMods.Add(mod.Id);
            }
        foreach (var (name, fileMods) in byFile)
        {
            var packsShipping = PacksShipping(name);
            if (fileMods.Count + packsShipping.Count < 2) continue;
            conflicts.Add(new ModConflict(name, fileMods, fileMods[^1]) { Kind = ModConflictKind.ContentFile, Packs = packsShipping });
        }
        return conflicts;
    }

    private static bool IsNamed(ModContentFile file, string name) =>
        Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Each installed official pack's display name and the file names the combined content takes from it.</summary>
    private List<(string DisplayName, HashSet<string> Names)> PackFileNames()
    {
        var dlc = Dlc;
        return [.. dlc.Installed().Select(pack =>
        {
            var folder = Path.Combine(dlc.ContentDirectory, pack.FileName);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(path);
                    if (!name.Equals(DlcTocFileName, StringComparison.OrdinalIgnoreCase) && !name.Equals(GameIni, StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            return (pack.DisplayName, names);
        })];
    }
}

/// <summary>What a <see cref="ModConflict"/> is about.</summary>
public enum ModConflictKind
{
    /// <summary>A package or update file two mods in use replace (ticket 06).</summary>
    Package,

    /// <summary>A roster slot (<c>CharacterData_&lt;id&gt;</c>) two content mods fill.</summary>
    RosterSlot,

    /// <summary>A <c>CodeName</c> two content mods give entries in different slots; only the later mod's entry is written.</summary>
    RosterCodeName,

    /// <summary>
    /// A whole <c>CombinedGlobalCharacters.xxx</c> or <c>Shared_Characters_DLC.xxx</c> shipped by a
    /// content mod: it replaces the roster for every other content mod and the installed packs.
    /// </summary>
    RosterPackage,

    /// <summary>A <c>content/</c> file two content mods ship, or one with an official pack file's name.</summary>
    ContentFile,
}
