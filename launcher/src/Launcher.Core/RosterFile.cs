using System.Globalization;
using System.Text.RegularExpressions;

namespace WweAllStars.Launcher.Core;

/// <summary>How a roster property's value is written: the game's property type behind it.</summary>
public enum RosterValueKind
{
    /// <summary>A <c>StrProperty</c> of the entry, given as <c>"…"</c>.</summary>
    String,
    /// <summary>An asset reference (an <c>SD_StringAssetReference</c> sub-object), given as its asset name <c>"Package.Object"</c>.</summary>
    Asset,
    Int,
    Float,
    /// <summary>A one-byte <c>ByteProperty</c>, 0–255.</summary>
    Byte,
}

/// <summary>
/// A content mod's roster-entry file, <c>content/roster.toml</c> (spec, "Roster entries, written by
/// the launcher"): one <c>[&lt;id&gt;]</c> section per placeholder slot it fills, then
/// <c>key = value</c> lines. <c>template</c> names the complete entry copied (110 by default); every
/// other key is a property path of the entry, as the format notes list them. Everything the file
/// doesn't set comes from the template, except the slot's own <c>LegendId</c>.
/// </summary>
public static partial class RosterFile
{
    public const string FileName = "roster.toml";

    /// <summary>The complete entry copied when the file names none (Pack 1's Million Dollar Man, on the disc too).</summary>
    public const int DefaultTemplate = 110;

    /// <summary>The 11 true placeholders (<c>CharacterName</c> = <c>Undefined</c>, research "The unused superstar ids").</summary>
    public static readonly IReadOnlyList<int> Placeholders = [120, 125, 127, 128, 129, 130, 215, 217, 228, 229, 230];

    private const string TemplateKey = "template";
    private const string NameKey = "CharacterName";
    private const string CodeNameKey = "CodeName";

    private static readonly Dictionary<string, RosterValueKind> Fixed = new(StringComparer.Ordinal)
    {
        ["CharacterName"] = RosterValueKind.String,
        ["CodeName"] = RosterValueKind.String,
        ["CharacterCode"] = RosterValueKind.String,
        ["IntroCinemaLevelName"] = RosterValueKind.String,
        ["ClassType"] = RosterValueKind.Byte,
        ["nHealth"] = RosterValueKind.Int,
        ["nStun"] = RosterValueKind.Int,
        ["nPower"] = RosterValueKind.Int,
        ["nStamina"] = RosterValueKind.Int,
        ["nGrappling"] = RosterValueKind.Int,
        ["nAgility"] = RosterValueKind.Int,
        ["Scale"] = RosterValueKind.Float,
        ["broadPhaseRadius"] = RosterValueKind.Float,
        ["SkeletalMeshAsset"] = RosterValueKind.Asset,
        ["PhysicsMeshAsset"] = RosterValueKind.Asset,
        ["MeshTreeAsset"] = RosterValueKind.Asset,
        ["BruiseResources"] = RosterValueKind.Asset,
        ["AnimSetAsset"] = RosterValueKind.Asset,
        ["AnimSetFinisherAsset"] = RosterValueKind.Asset,
        ["AnimFSMAsset"] = RosterValueKind.Asset,
        ["AIFSMAsset"] = RosterValueKind.Asset,
        ["MomentumData"] = RosterValueKind.Asset,
        ["AudioAsset"] = RosterValueKind.Asset,
        ["AudioCommentaryAsset"] = RosterValueKind.Asset,
        ["AudioVictoryMusicAsset"] = RosterValueKind.Asset,
        ["AudioCrowdChantAsset"] = RosterValueKind.Asset,
        ["AudioGruntAsset"] = RosterValueKind.Asset,
    };

    /// <summary>What kind of value a property path takes; null for a property a roster entry can't set.</summary>
    public static RosterValueKind? KindOf(string path) =>
        Fixed.TryGetValue(path, out var kind) ? kind
        : IndexedAsset().IsMatch(path) ? RosterValueKind.Asset
        : null;

    /// <summary>A costume's model, or one of the four HUD portraits.</summary>
    [GeneratedRegex(@"^(CostumesArray\.[0-9]\.Model|CharacterPortraits\.[0-3]\.PortraitTexture)$")]
    private static partial Regex IndexedAsset();

    [GeneratedRegex(@"^\[\s*([^\]]*?)\s*\]$")]
    private static partial Regex Section();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*(.*)$")]
    private static partial Regex KeyValue();

    /// <summary>
    /// The file's entries in file order. Each problem goes to <paramref name="problems"/>, prefixed
    /// with <paramref name="shown"/>; an entry with a problem is left out.
    /// </summary>
    public static IReadOnlyList<RosterEntry> Read(string text, string shown, List<string> problems)
    {
        var entries = new List<RosterEntry>();
        var before = problems.Count;
        void Problem(string what) => problems.Add($"{shown}: {what}");

        var sections = new List<(int Id, Dictionary<string, string> Values, int? Template, bool Bad)>();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = StripComment(raw.TrimEnd('\r')).Trim();
            if (line.Length == 0) continue;
            if (Section().Match(line) is { Success: true } section)
            {
                var label = section.Groups[1].Value;
                if (!int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !Placeholders.Contains(id))
                {
                    Problem($"[{label}] is not a free roster slot; a roster entry fills one of {string.Join(", ", Placeholders)}.");
                    sections.Add((0, [], null, true));
                    continue;
                }
                if (sections.Any(existing => existing.Id == id))
                {
                    Problem($"[{id}] is given twice.");
                    sections.Add((id, [], null, true));
                    continue;
                }
                sections.Add((id, new Dictionary<string, string>(StringComparer.Ordinal), null, false));
                continue;
            }
            if (KeyValue().Match(line) is not { Success: true } pair)
            {
                Problem($"line {lineNumber} is not [id] or key = value.");
                if (sections.Count > 0) sections[^1] = sections[^1] with { Bad = true };
                continue;
            }
            if (sections.Count == 0)
            {
                Problem($"line {lineNumber} comes before any [id].");
                continue;
            }
            var current = sections[^1];
            if (current.Bad) continue;
            var (key, literal) = (pair.Groups[1].Value, pair.Groups[2].Value.Trim());
            if (key == TemplateKey ? current.Template is not null : current.Values.ContainsKey(key))
            {
                Problem($"[{current.Id}] sets {key} twice.");
                sections[^1] = current with { Bad = true };
                continue;
            }
            if (key == TemplateKey)
            {
                if (!int.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var template) || !IsTemplate(template))
                {
                    Problem($"[{current.Id}] template = {literal} is not a complete roster entry to copy (101–130 or 201–230, not a placeholder).");
                    sections[^1] = current with { Bad = true };
                }
                else sections[^1] = current with { Template = template };
                continue;
            }
            if (KindOf(key) is not { } kind)
            {
                Problem($"[{current.Id}] sets {key}, which is not a property a roster entry can set.");
                sections[^1] = current with { Bad = true };
                continue;
            }
            if (Parse(kind, literal) is not { } value || key == CodeNameKey && !CodeNameText().IsMatch(value))
            {
                Problem($"[{current.Id}] {key} = {literal} is not {Expected(kind, key)}.");
                sections[^1] = current with { Bad = true };
                continue;
            }
            current.Values[key] = value;
        }
        foreach (var (id, values, template, bad) in sections)
        {
            if (bad) continue;
            if (!values.ContainsKey(NameKey))
            {
                Problem($"[{id}] has no {NameKey}.");
                continue;
            }
            // The game takes the first roster entry with a code name (0x82B09BA0), so a kept one is that wrestler.
            if (!values.TryGetValue(CodeNameKey, out var codeName))
            {
                Problem($"[{id}] has no {CodeNameKey}, so it would keep template {template ?? DefaultTemplate}'s and show and play as that wrestler. " +
                    $"Give it a {CodeNameKey} of its own (letters and digits).");
                continue;
            }
            if (entries.FirstOrDefault(entry => SameCodeName(entry.CodeName, codeName)) is { } earlier)
            {
                Problem($"[{id}] has {CodeNameKey} {codeName}, as [{earlier.Id}] does; each entry needs a {CodeNameKey} of its own.");
                continue;
            }
            entries.Add(new RosterEntry(id, template ?? DefaultTemplate, values));
        }
        if (sections.Count == 0 && problems.Count == before) Problem("it has no roster entry ([id] sections).");
        return entries;
    }

    /// <summary>Whether two code names are one to the game: its lookup ignores case (<c>0x82B09BA0</c>).</summary>
    public static bool SameCodeName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9]+$")]
    private static partial Regex CodeNameText();

    private static bool IsTemplate(int id) => (id is >= 101 and <= 130 or >= 201 and <= 230) && !Placeholders.Contains(id);

    /// <summary>A <c>#</c> outside a quoted string starts a comment.</summary>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }

    /// <summary>The value as the writer takes it (a string unquoted, a number in invariant form), or null when it is bad.</summary>
    private static string? Parse(RosterValueKind kind, string literal)
    {
        switch (kind)
        {
            case RosterValueKind.String or RosterValueKind.Asset:
                if (literal.Length < 2 || literal[0] != '"' || literal[^1] != '"') return null;
                var text = literal[1..^1];
                // The game stores these as one-byte strings; quotes and escapes have no use in them.
                if (text.Length > 255 || text.Any(c => c is < ' ' or > '~' or '"' or '\\')) return null;
                if (kind == RosterValueKind.Asset && !AssetName().IsMatch(text)) return null;
                return text;
            case RosterValueKind.Int:
                return int.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString(CultureInfo.InvariantCulture) : null;
            case RosterValueKind.Byte:
                return byte.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
                    ? b.ToString(CultureInfo.InvariantCulture) : null;
            default:
                return float.TryParse(literal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var f)
                    && float.IsFinite(f) && f > 0 ? f.ToString("R", CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>An asset name as the game's references hold them: <c>Package.Object</c>, with groups between.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)+$")]
    private static partial Regex AssetName();

    private static string Expected(RosterValueKind kind, string key) => kind switch
    {
        RosterValueKind.String => key switch
        {
            NameKey => "a quoted name",
            CodeNameKey => "a quoted CodeName of letters and digits",
            _ => "a quoted string (letters, digits and punctuation)",
        },
        RosterValueKind.Asset => "a quoted asset name such as \"Package.Object\"",
        RosterValueKind.Int => "a whole number of 0 or more",
        RosterValueKind.Byte => "a whole number from 0 to 255",
        _ => "a number above 0, such as 1.02",
    };
}
