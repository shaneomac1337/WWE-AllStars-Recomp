namespace WweAllStars.Launcher.Core;

/// <summary>What the Mods page does with the library (ticket 06): load order, conflicts, the "new" mark.</summary>
public sealed partial class ModLibrary
{
    /// <summary>Moves a mod one place earlier in the load order; saved, so it applies at the next Play.</summary>
    /// <exception cref="LauncherException">No such mod.</exception>
    public void MoveUp(string id) => Move(id, -1);

    /// <summary>Moves a mod one place later in the load order, so it wins over the one it passes.</summary>
    /// <exception cref="LauncherException">No such mod.</exception>
    public void MoveDown(string id) => Move(id, +1);

    private void Move(string id, int step)
    {
        lock (_lock)
        {
            var entries = Sync();
            var index = entries.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new LauncherException($"There is no mod named {id} in {layout.ModsDirectory}.");
            var target = index + step;
            if (target < 0 || target >= entries.Count) return;
            (entries[index], entries[target]) = (entries[target], entries[index]);
            LoadOrder.Write(LoadOrderPath, entries);
        }
    }

    /// <summary>The mods turned on, in load order, as <c>Name Version (folder)</c>: what a crash report lists.</summary>
    public IReadOnlyList<string> EnabledMods()
    {
        lock (_lock)
            return [.. Scan().Where(mod => mod.Enabled).Select(mod => $"{mod.Manifest!.Name} {mod.Manifest.Version} ({mod.Id})")];
    }

    /// <summary>Clears every "new" mark: the player has seen the page. Toggling a mod clears its own.</summary>
    public void MarkAllSeen()
    {
        lock (_lock)
        {
            var entries = Sync();
            if (!entries.Any(entry => entry.IsNew)) return;
            LoadOrder.Write(LoadOrderPath, entries.Select(entry => entry with { IsNew = false }));
        }
    }

    /// <summary>
    /// Each file two or more mods in use replace (spec, user story 14), by file name: the mods in
    /// load order, and the one that wins, the last of them. A mod that is off clashes with nothing.
    /// The content mods' clashes follow (ticket 17).
    /// </summary>
    public IReadOnlyList<ModConflict> Conflicts()
    {
        lock (_lock)
        {
            var byFile = new Dictionary<string, (string File, List<string> Mods)>(StringComparer.OrdinalIgnoreCase);
            var enabled = Scan().Where(mod => mod.Enabled).ToList();
            foreach (var mod in enabled)
                // A package a pack ships goes into the combined content; its clashes are content conflicts.
                foreach (var package in mod.OverlayPackages)
                {
                    if (!byFile.TryGetValue(package.Name, out var clash))
                        byFile[package.Name] = clash = (package.Name, []);
                    clash.Mods.Add(mod.Id);
                }
            return [.. byFile.Values.Where(clash => clash.Mods.Count > 1)
                .OrderBy(clash => clash.File, StringComparer.OrdinalIgnoreCase)
                .Select(clash => new ModConflict(clash.File, clash.Mods, clash.Mods[^1])),
                .. ContentConflicts(enabled)];
        }
    }
}

/// <summary>A file more than one mod in use replaces: the mods in load order, and the one whose file the game gets.</summary>
public sealed record ModConflict(string File, IReadOnlyList<string> Mods, string Winner)
{
    /// <summary>What clashes: a package (ticket 06), or a roster slot, roster package or content file (ticket 17).</summary>
    public ModConflictKind Kind { get; init; } = ModConflictKind.Package;

    /// <summary>The installed official packs, by display name, that the mods' file replaces.</summary>
    public IReadOnlyList<string> Packs { get; init; } = [];
}
