using System.Globalization;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>One placeholder slot a Play fills, and the mod whose roster entry won it (load report).</summary>
public sealed record RosterFill(int Id, string ModId, string ModName, string ModVersion);

/// <summary>
/// Roster entries in the combined content (spec, "Roster entries, written by the launcher"): every
/// content mod's <c>content/roster.toml</c> is written into one <c>CombinedGlobalCharacters.xxx</c>.
/// </summary>
public sealed partial class ModLibrary
{
    private const string RosterPackageName = "CombinedGlobalCharacters.xxx";

    /// <summary>The language file a wrestler's shown name is in: <c>[CharacterNames] &lt;CodeName&gt;_Name</c> (research, "Ticket 18").</summary>
    private const string CharacterNames = "CharacterNames";

    private string DiscRosterPath => Path.Combine(layout.GameDataDirectory, "Game", "CookedXenon", RosterPackageName);

    /// <summary>The base roster's names, kept while the package is unchanged (path, size, write time).</summary>
    private (string Key, IReadOnlyList<RosterName>? Names)? _baseRosterNames;

    /// <summary>Checks a mod's roster-entry file when the mods are scanned; false when it has a problem.</summary>
    private static bool RosterFileIsGood(string text, string shown, List<string> problems)
    {
        var before = problems.Count;
        RosterFile.Read(text, shown, problems);
        return problems.Count == before;
    }

    /// <summary>
    /// A problem for each entry of the mod's roster file whose <c>CodeName</c> an entry of the base
    /// roster already has (other than a slot the file fills): the game takes the first entry with a
    /// code name (<c>0x82B09BA0</c>), so the new one would show and play as that wrestler. With no
    /// readable base roster this is left to Play.
    /// </summary>
    private void CheckCodeNames(IReadOnlyList<ModContentFile> content, List<string> problems)
    {
        if (content.FirstOrDefault(file => IsNamed(file, RosterFile.FileName)) is not { } roster) return;
        var entries = RosterFile.Read(File.ReadAllText(roster.Path), roster.Shown, []);
        if (entries.Count == 0 || BaseRosterNames() is not { } names) return;
        var filled = entries.Select(entry => entry.Id).ToHashSet();
        foreach (var entry in entries)
            if (names.FirstOrDefault(name => !filled.Contains(name.Id) && RosterFile.SameCodeName(name.CodeName, entry.CodeName)) is { } taken)
                problems.Add($"{roster.Shown}: [{entry.Id}] has CodeName {entry.CodeName}, which {taken.CharacterName} (roster entry {taken.Id}) " +
                    "already has; the game would show and play that wrestler. Give it a CodeName of its own.");
    }

    /// <summary>The names of the roster the entries are written into: the highest official pack's, else the disc's; null when it can't be read.</summary>
    private IReadOnlyList<RosterName>? BaseRosterNames()
    {
        var path = PackFiles(Dlc).SelectMany(pack => pack.Files)
            .LastOrDefault(file => Path.GetFileName(file).Equals(RosterPackageName, StringComparison.OrdinalIgnoreCase)) ?? DiscRosterPath;
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        var key = string.Create(CultureInfo.InvariantCulture, $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        if (_baseRosterNames is { } cached && cached.Key == key) return cached.Names;
        IReadOnlyList<RosterName>? names;
        try
        {
            names = RosterWriter.Names(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            names = null; // Play says what is wrong with it.
        }
        _baseRosterNames = (key, names);
        return names;
    }

    /// <summary>A roster entry that a Play writes, from the content mod at <see cref="Number"/> in load order.</summary>
    private sealed record RosterSlot(ModInfo Mod, int Number, RosterEntry Entry);

    /// <summary>
    /// The entries a Play writes, by slot: a later mod wins a slot, then a later mod wins a
    /// <c>CodeName</c> two slots share (ticket 21), and the earlier entry is left out. Returns the
    /// code-name clashes too, as conflicts.
    /// </summary>
    private static (SortedDictionary<int, RosterSlot> Slots, List<ModConflict> Clashes) RosterSlots(
        IEnumerable<(ModInfo Mod, int Number, ModContentFile File)> rosterFiles)
    {
        var slots = new SortedDictionary<int, RosterSlot>();
        foreach (var (mod, number, file) in rosterFiles)
            foreach (var entry in RosterFile.Read(File.ReadAllText(file.Path), file.Shown, []))
                slots[entry.Id] = new RosterSlot(mod, number, entry);
        var clashes = new List<ModConflict>();
        foreach (var shared in slots.Values.GroupBy(slot => slot.Entry.CodeName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList())
        {
            var ordered = shared.OrderBy(slot => slot.Number).ToList();
            var winner = ordered[^1];
            foreach (var loser in ordered.Where(slot => slot.Number != winner.Number)) slots.Remove(loser.Entry.Id);
            clashes.Add(new ModConflict(ordered[0].Entry.CodeName, [.. ordered.Select(slot => slot.Mod.Id).Distinct(StringComparer.OrdinalIgnoreCase)],
                winner.Mod.Id) { Kind = ModConflictKind.RosterCodeName });
        }
        return (slots, clashes);
    }

    /// <summary>
    /// Puts the roster package in <paramref name="files"/>: the entries of <paramref name="rosterFiles"/>
    /// (load order, as <see cref="RosterSlots"/> picks them) written into the roster the content would
    /// carry otherwise, the highest official pack's or a mod's whole one, else the disc's. Each slot
    /// gets its <c>CharacterData_&lt;id&gt;=1</c> line and its shown name (<see cref="AddNames"/>).
    /// The package is written only when the content is built.
    /// </summary>
    /// <exception cref="LauncherException">There is no roster to write into, or an entry doesn't fit it.</exception>
    private IReadOnlyList<RosterFill> AddRoster(Dictionary<string, CombinedFile> files,
        List<(ModInfo Mod, int Number, ModContentFile File)> rosterFiles, Action<IEnumerable<string>> addCharacters)
    {
        var bySlot = RosterSlots(rosterFiles).Slots;
        if (bySlot.Count == 0) return [];

        var winner = files.GetValueOrDefault(RosterPackageName);
        var basePath = winner?.Source ?? DiscRosterPath;
        if (!File.Exists(basePath))
            throw new LauncherException($"The content mods' roster entries need the game's {RosterPackageName}, which is not in {Path.GetDirectoryName(basePath)}.");
        var entries = bySlot.Values.Select(slot => slot.Entry).ToList();
        files[RosterPackageName] = (winner ?? new CombinedFile(null, null, null, null)) with
        {
            Source = null,
            Make = () =>
            {
                try
                {
                    return RosterWriter.Write(File.ReadAllBytes(basePath), entries);
                }
                catch (InvalidDataException error)
                {
                    throw new LauncherException($"The content mods' roster entries could not be written into {basePath}: {error.Message}", error);
                }
            },
        };
        addCharacters(bySlot.Keys.Select(id => string.Create(CultureInfo.InvariantCulture, $"CharacterData_{id}=1")));
        AddNames(files, bySlot.Values);
        return [.. bySlot.Select(slot => new RosterFill(slot.Key, slot.Value.Mod.Id, slot.Value.Mod.Manifest!.Name, slot.Value.Mod.Manifest.Version))];
    }

    /// <summary>
    /// The select screen's label for each written entry: <c>&lt;CodeName&gt;_Name</c> and
    /// <c>_Short</c> as its <c>CharacterName</c> in capitals (the game's own names are), in a
    /// <c>M&lt;n&gt;_Roster__CharacterNames.&lt;lang&gt;</c> fragment for every language TU2 ships, since
    /// the installer merges only the running language's (ticket 15). A language for which the mod
    /// ships the <c>_Name</c> itself gets none.
    /// </summary>
    private void AddNames(Dictionary<string, CombinedFile> files, IEnumerable<RosterSlot> slots)
    {
        foreach (var byMod in slots.GroupBy(slot => slot.Number))
        {
            var mod = byMod.First().Mod;
            foreach (var language in LanguageExtensions)
            {
                var target = $"{CharacterNames}.{language}";
                if (!_known.ShipsLanguage(language) || _known.KnowsConfigFiles && _known.ConfigPathOf(target) is null) continue;
                var given = NamesGiven(mod, target);
                var text = new StringBuilder();
                foreach (var slot in byMod.Where(slot => !given.Contains(slot.Entry.CodeName + "_Name")))
                {
                    var shown = slot.Entry.CharacterName.ToUpperInvariant();
                    text.Append(CultureInfo.InvariantCulture, $"{slot.Entry.CodeName}_Name={shown}\r\n{slot.Entry.CodeName}_Short={shown}\r\n");
                }
                if (text.Length == 0) continue;
                files[FragmentName(byMod.Key, $"Roster__{target}", files)] =
                    new(null, Encoding.ASCII.GetBytes($"[{CharacterNames}]\r\n{text}"), null, null);
            }
        }
    }

    /// <summary>The <c>[CharacterNames]</c> keys the mod's own fragments for <paramref name="target"/> set.</summary>
    private static HashSet<string> NamesGiven(ModInfo mod, string target)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in mod.Content.Where(file => FragmentTarget(Path.GetFileName(file.Path)).Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            var section = "";
            foreach (var raw in File.ReadAllText(file.Path).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
                else if (section.Equals(CharacterNames, StringComparison.OrdinalIgnoreCase) && DlcIni.KeyOf(line) is { Length: > 0 } key) keys.Add(key);
            }
        }
        return keys;
    }
}
