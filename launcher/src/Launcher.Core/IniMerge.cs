namespace WweAllStars.Launcher.Core;

/// <summary>
/// A mod's ini file applied to an inner ini of a Coalesced bundle, with UE3's edit rules
/// (spec, "Coalesced bundle"): <c>Key=</c> sets, <c>+Key=</c> adds a value not there yet,
/// <c>-Key=</c> removes that value, <c>.Key=</c> adds a duplicate. Lines are edited in place,
/// so everything a mod doesn't touch, comments and line endings included, stays as it was.
/// </summary>
internal static class IniMerge
{
    private enum Op
    {
        Set,
        AddUnique,
        Remove,
        AddDuplicate,
    }

    private sealed record Edit(string Section, Op Op, string Key, string Value);

    /// <summary>
    /// Whether <paramref name="modText"/> is the inner ini shipped complete, so it replaces the
    /// original whole: it has no <c>+</c>, <c>-</c> or <c>.</c> lines and names every key of
    /// every section the original has. A file of a few keys is edits.
    /// </summary>
    public static bool IsComplete(string original, string modText)
    {
        var edits = Parse(modText);
        if (edits.Any(edit => edit.Op != Op.Set)) return false;
        var originalKeys = Parse(original);
        if (originalKeys.Count == 0) return false;
        var named = edits.Select(edit => (edit.Section.ToUpperInvariant(), edit.Key.ToUpperInvariant())).ToHashSet();
        return originalKeys.All(key => named.Contains((key.Section.ToUpperInvariant(), key.Key.ToUpperInvariant())));
    }

    /// <summary>The original with every edit of <paramref name="modText"/> applied in order; <paramref name="count"/> is how many edits it holds.</summary>
    public static string Apply(string original, string modText, out int count)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines[^1].Length != 0) lines.Add("");
        var edits = Parse(modText);
        foreach (var edit in edits) Apply(lines, edit);
        count = edits.Count;
        return string.Join(newline, lines);
    }

    private static void Apply(List<string> lines, Edit edit)
    {
        var header = lines.FindIndex(line => SectionOf(line) is { } name && name.Equals(edit.Section, StringComparison.OrdinalIgnoreCase));
        if (header < 0)
        {
            // A new section goes at the end, after a blank line; lines[^1] is the empty line after the last newline.
            var at = lines.Count - 1;
            if (at > 0 && lines[at - 1].Trim().Length != 0) lines.Insert(at++, "");
            lines.Insert(at, $"[{edit.Section}]");
            header = at;
        }
        var end = lines.FindIndex(header + 1, line => SectionOf(line) != null);
        if (end < 0) end = lines.Count;
        var matches = Enumerable.Range(header + 1, end - header - 1)
            .Where(i => KeyOf(lines[i]) is { } key && key.Key.Equals(edit.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        var line = $"{edit.Key}={edit.Value}";
        bool HasValue(int i) => KeyOf(lines[i])!.Value.Value == edit.Value;
        switch (edit.Op)
        {
            case Op.Set when matches.Count > 0:
                lines[matches[0]] = line;
                foreach (var i in matches.Skip(1).Reverse()) lines.RemoveAt(i);
                return;
            case Op.AddUnique when matches.Any(HasValue):
                return;
            case Op.Remove:
                foreach (var i in matches.Where(HasValue).Reverse()) lines.RemoveAt(i);
                return;
        }
        var last = end - 1;
        while (last > header && lines[last].Trim().Length == 0) last--;
        lines.Insert(last + 1, line);
    }

    /// <summary>The edit lines of a mod's ini: <c>[Section]</c> headers and key lines, without comments or lines outside a section.</summary>
    private static List<Edit> Parse(string text)
    {
        var edits = new List<Edit>();
        string? section = null;
        foreach (var raw in text.Split('\n'))
        {
            if (SectionOf(raw) is { } name)
            {
                section = name;
                continue;
            }
            if (section == null) continue;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == ';') continue;
            var op = line[0] switch
            {
                '+' => Op.AddUnique,
                '-' => Op.Remove,
                '.' => Op.AddDuplicate,
                _ => Op.Set,
            };
            if (KeyOf(op == Op.Set ? line : line[1..]) is not { } key) continue;
            edits.Add(new Edit(section, op, key.Key, key.Value));
        }
        return edits;
    }

    private static string? SectionOf(string line)
    {
        var trimmed = line.Trim().TrimStart('﻿');
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1].Trim() : null;
    }

    private static (string Key, string Value)? KeyOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == ';') return null;
        var equals = trimmed.IndexOf('=');
        if (equals <= 0) return null;
        return (trimmed[..equals].Trim(), trimmed[(equals + 1)..].Trim());
    }
}
