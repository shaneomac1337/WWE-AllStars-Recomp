using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// One roster entry a content mod gives: the placeholder slot it fills, the complete entry it
/// copies, and the properties it sets by path (<see cref="RosterFile"/>), as the file spells them.
/// </summary>
public sealed record RosterEntry(int Id, int Template, IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>The entry's <c>CodeName</c>, which <see cref="RosterFile"/> requires: the key of its label and pick.</summary>
    public string CodeName => Properties["CodeName"];

    /// <summary>The entry's <c>CharacterName</c>, which <see cref="RosterFile"/> requires.</summary>
    public string CharacterName => Properties["CharacterName"];
}

/// <summary>A roster entry of a package, by the two names a mod's entry is checked against.</summary>
public sealed record RosterName(int Id, string CodeName, string CharacterName);

/// <summary>
/// The narrow roster-entry writer (spec, "Roster entries, written by the launcher"; research,
/// "Spike 12"). It fills placeholder <c>GameCharacterData.CharacterData_&lt;id&gt;</c> exports of a
/// <c>CombinedGlobalCharacters.xxx</c> from a complete template entry, keeps each slot's own
/// <c>LegendId</c>, sets the mod's values, and writes the package uncompressed with every table and
/// absolute offset fixed up.
/// </summary>
public static class RosterWriter
{
    /// <summary>
    /// The package (plain or LZO, as the disc and the packs ship it) with <paramref name="entries"/>
    /// written in, uncompressed. With no entries it is the unpacked package, byte for byte.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is damaged or not a roster, or an entry doesn't fit its template.</exception>
    public static byte[] Write(byte[] package, IReadOnlyList<RosterEntry> entries)
    {
        var roster = RosterPackage.Read(Ue3Package.Unpacked(package));
        var writer = new RosterEntryWriter(roster);
        foreach (var entry in entries) writer.Fill(entry);
        return writer.Write();
    }

    /// <summary>
    /// One roster entry's properties by path (<c>CharacterName</c>, <c>CostumesArray.0.Model</c>,
    /// <c>LegendId.b20</c>), an asset reference as its asset name: what the game reads.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is damaged, or has no such entry.</exception>
    public static IReadOnlyDictionary<string, string> ReadEntry(byte[] package, int id)
    {
        var roster = RosterPackage.Read(Ue3Package.Unpacked(package));
        var index = roster.EntryIndex(id);
        var body = roster.Body(index);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var leaf in roster.Walk(body).Leaves)
        {
            values[leaf.Path] = leaf.Type switch
            {
                "StrProperty" => RosterPackage.ReadString(body, leaf.Value).Text,
                "IntProperty" => I32(body, leaf.Value).ToString(CultureInfo.InvariantCulture),
                "FloatProperty" => BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(leaf.Value)).ToString("R", CultureInfo.InvariantCulture),
                "ByteProperty" when leaf.Size == 1 => body[leaf.Value].ToString(CultureInfo.InvariantCulture),
                "BoolProperty" => I32(body, leaf.Value) != 0 ? "true" : "false",
                "ObjectProperty" => roster.AssetName(I32(body, leaf.Value)),
                "NameProperty" or "ByteProperty" => roster.NameAt(body, leaf.Value),
                _ => Convert.ToHexString(body, leaf.Value, leaf.Size),
            };
        }
        return values;
    }

    /// <summary>Every roster entry's <c>CodeName</c> and <c>CharacterName</c> (empty when it has none), in export order.</summary>
    /// <exception cref="InvalidDataException">The package is damaged or not a roster.</exception>
    public static IReadOnlyList<RosterName> Names(byte[] package)
    {
        var roster = RosterPackage.Read(Ue3Package.Unpacked(package));
        var names = new List<RosterName>();
        foreach (var (id, index) in roster.Entries)
        {
            var body = roster.Body(index);
            var top = roster.Walk(body).Top;
            string Text(string key) =>
                top.TryGetValue(key, out var property) && property.Type == "StrProperty" ? RosterPackage.ReadString(body, property.Value).Text : "";
            names.Add(new RosterName(id, Text("CodeName"), Text("CharacterName")));
        }
        return names;
    }

    internal static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at, 4));

    internal static InvalidDataException Damaged(string what) => new($"The roster package is damaged: {what}.");
}

/// <summary>
/// An unpacked cooked package (file version 398, big-endian) read as far as the writer needs:
/// the summary's counts, the names, the imports, and the export entries as raw bytes.
/// </summary>
internal sealed class RosterPackage
{
    private const int ImportSize = 28;

    public byte[] Data { get; }
    public int FlagsAt { get; }
    public int ExportOffset { get; }
    public int HeaderSize { get; }
    public int LastGenerationAt { get; }
    public string[] Names { get; }
    public List<byte[]> Exports { get; } = [];
    private readonly int[] _importNames;
    private readonly Dictionary<int, int> _entries = [];

    private RosterPackage(byte[] d)
    {
        Data = d;
        var bytes = new PackageBytes(d);
        var summary = Ue3Summary.Read(bytes);
        FlagsAt = summary.FlagsAt;
        var (exportCount, importCount, importOffset) = (summary.ExportCount, summary.ImportCount, summary.ImportOffset);
        ExportOffset = summary.ExportOffset;
        var generations = I32(FlagsAt + 44);
        LastGenerationAt = generations > 0 ? FlagsAt + 48 + (generations - 1) * 12 : -1;
        HeaderSize = I32(8);
        if (summary.NameCount < 0 || exportCount < 0 || importCount < 0) throw RosterWriter.Damaged("a table has a negative count");

        Names = [.. Ue3Summary.Names(bytes, summary.NameCount, summary.NameOffset, out var p)];
        if (p != importOffset || importOffset + importCount * ImportSize != ExportOffset)
            throw RosterWriter.Damaged("the name, import and export tables are not back to back");
        _importNames = new int[importCount];
        for (var i = 0; i < importCount; i++) _importNames[i] = I32(importOffset + i * ImportSize + 20);

        p = ExportOffset;
        for (var i = 0; i < exportCount; i++)
        {
            var start = p;
            p += 40;
            p += 4 + I32(p) * 12 + 4; // component map, export flags
            p += 4 + I32(p) * 4 + 16; // generation net objects, package GUID (no package flags at v398)
            if (p > d.Length) throw RosterWriter.Damaged("the export table ends early");
            Exports.Add(d[start..p]);
        }
        if (p != HeaderSize) throw RosterWriter.Damaged("the export table does not end where the bodies start");
        for (var i = 1; i <= Exports.Count; i++)
            if (ClassName(Exports[i - 1]) == "GameCharacterData" && ObjectName(Exports[i - 1]) is var name
                && name.StartsWith("CharacterData_", StringComparison.Ordinal)
                && int.TryParse(name["CharacterData_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                _entries.TryAdd(id, i);
    }

    public static RosterPackage Read(byte[] unpacked)
    {
        try
        {
            return new RosterPackage(unpacked);
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw RosterWriter.Damaged("a table lies outside the file");
        }
    }

    private int I32(int at) => RosterWriter.I32(Data, at);

    public static int Size(byte[] entry) => RosterWriter.I32(entry, 32);
    public static int Offset(byte[] entry) => RosterWriter.I32(entry, 36);
    public static int Outer(byte[] entry) => RosterWriter.I32(entry, 8);

    public string Name(int index, int number)
    {
        if ((uint)index >= (uint)Names.Length) throw RosterWriter.Damaged("a name index is out of range");
        return number == 0 ? Names[index] : $"{Names[index]}_{(number - 1).ToString(CultureInfo.InvariantCulture)}";
    }

    public string ObjectName(byte[] entry) => Name(RosterWriter.I32(entry, 12), RosterWriter.I32(entry, 16));

    public string ClassName(byte[] entry)
    {
        var index = RosterWriter.I32(entry, 0);
        if (index < 0)
            return -index - 1 < _importNames.Length ? Name(_importNames[-index - 1], 0) : throw RosterWriter.Damaged("a class index is out of range");
        return index == 0 ? "Class" : ObjectName(Export(index));
    }

    public byte[] Export(int index) =>
        (uint)(index - 1) < (uint)Exports.Count ? Exports[index - 1] : throw RosterWriter.Damaged("an object index is out of range");

    public byte[] Body(int index)
    {
        var entry = Export(index);
        var (offset, size) = (Offset(entry), Size(entry));
        if (offset < HeaderSize || size < 0 || (long)offset + size > Data.Length) throw RosterWriter.Damaged("an export lies outside the file");
        return Data[offset..(offset + size)];
    }

    /// <summary>Each roster entry's id and 1-based export index, in export order.</summary>
    public IEnumerable<(int Id, int Index)> Entries => _entries.OrderBy(entry => entry.Value).Select(entry => (entry.Key, entry.Value));

    /// <summary>The 1-based export index of <c>CharacterData_&lt;id&gt;</c>.</summary>
    public int EntryIndex(int id) =>
        _entries.TryGetValue(id, out var index) ? index : throw new InvalidDataException($"The roster package has no CharacterData_{id}.");

    public bool IsAssetReference(int index) =>
        index > 0 && index <= Exports.Count && ClassName(Exports[index - 1]) == "SD_StringAssetReference";

    /// <summary>An object reference as the game resolves it: an asset reference's asset name, else the object's name.</summary>
    public string AssetName(int index)
    {
        if (index == 0) return "None";
        if (!IsAssetReference(index)) return index > 0 ? ObjectName(Export(index)) : "import " + (-index).ToString(CultureInfo.InvariantCulture);
        var body = Body(index);
        return Walk(body).Top.TryGetValue("m_AssetName", out var name) && name.Type == "StrProperty"
            ? ReadString(body, name.Value).Text
            : throw RosterWriter.Damaged($"{ObjectName(Export(index))} has no m_AssetName");
    }

    public string NameAt(byte[] body, int at) => Name(RosterWriter.I32(body, at), RosterWriter.I32(body, at + 4));

    /// <summary>An FString: i32 length with the NUL, then the characters; negative means UTF-16.</summary>
    public static (string Text, int End) ReadString(byte[] d, int at)
    {
        var text = new PackageBytes(d).FString(ref at);
        return (text, at);
    }

    public static byte[] StringBytes(string text)
    {
        var bytes = new byte[4 + text.Length + 1];
        BinaryPrimitives.WriteInt32BigEndian(bytes, text.Length + 1);
        Encoding.Latin1.GetBytes(text, bytes.AsSpan(4));
        return bytes;
    }

    /// <summary>One tagged property: where its tag and value start, its value size and type.</summary>
    public sealed record Property(string Path, string Type, int Tag, int Value, int Size)
    {
        public int End => Value + (Type == "BoolProperty" ? 4 : Size);
    }

    public sealed class Properties
    {
        /// <summary>The top-level properties by name (with <c>.&lt;n&gt;</c> for a static array element).</summary>
        public Dictionary<string, Property> Top { get; } = new(StringComparer.Ordinal);

        /// <summary>Every property that is not a struct or array, nested ones by path.</summary>
        public List<Property> Leaves { get; } = [];
    }

    /// <summary>The tagged properties of a body that holds only them (an entry or an asset reference).</summary>
    public Properties Walk(byte[] body)
    {
        var properties = new Properties();
        try
        {
            if (Walk(body, 0, body.Length, "", properties) != body.Length) throw RosterWriter.Damaged("a roster entry has bytes after its properties");
        }
        catch (Exception error) when (error is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw RosterWriter.Damaged("a roster entry's properties run past it");
        }
        return properties;
    }

    private int Walk(byte[] b, int q, int end, string path, Properties into)
    {
        while (q < end)
        {
            var tag = q;
            var name = NameAt(b, q);
            q += 8;
            if (name == "None") return q;
            var type = NameAt(b, q);
            q += 8;
            var size = RosterWriter.I32(b, q);
            var arrayIndex = RosterWriter.I32(b, q + 4);
            q += 8;
            var key = path + name + (arrayIndex == 0 ? "" : "." + arrayIndex.ToString(CultureInfo.InvariantCulture));
            if (size < 0 || q + size > end) throw RosterWriter.Damaged($"property {key} runs past its object");
            if (type == "StructProperty")
            {
                q += 8; // struct name
                if (path.Length == 0) into.Top[key] = new Property(key, type, tag, q, size);
                if (LooksTagged(b, q, q + size)) Walk(b, q, q + size, key + ".", into);
                else into.Leaves.Add(new Property(key, type, tag, q, size));
                q += size;
                continue;
            }
            var property = new Property(key, type, tag, q, size);
            if (path.Length == 0) into.Top[key] = property;
            if (type == "BoolProperty")
            {
                into.Leaves.Add(property);
                q += 4; // a bool's value is 4 bytes after the tag in this package version
                continue;
            }
            if (type == "ArrayProperty")
            {
                var count = RosterWriter.I32(b, q);
                var element = q + 4;
                if (size > 4 && LooksTagged(b, element, q + size))
                    for (var i = 0; i < count; i++)
                        element = Walk(b, element, q + size, $"{key}.{i.ToString(CultureInfo.InvariantCulture)}.", into);
                else into.Leaves.Add(property);
                q += size;
                continue;
            }
            into.Leaves.Add(property);
            q += size;
        }
        return q;
    }

    /// <summary>Whether tagged properties start at <paramref name="at"/>: a name, then <c>None</c> or a property type.</summary>
    private bool LooksTagged(byte[] b, int at, int end)
    {
        if (at + 8 > end) return false;
        var index = RosterWriter.I32(b, at);
        if ((uint)index >= (uint)Names.Length || RosterWriter.I32(b, at + 4) != 0) return false;
        if (Names[index] == "None") return true;
        if (at + 16 > end) return false;
        var type = RosterWriter.I32(b, at + 8);
        return (uint)type < (uint)Names.Length && Names[type].EndsWith("Property", StringComparison.Ordinal);
    }
}

/// <summary>
/// The roster entries written into one roster package, and its write-back (research, "Spike 12", "The fix-ups a size
/// change needs"): rewritten bodies, appended exports, then every body laid out again.
/// </summary>
internal sealed class RosterEntryWriter(RosterPackage package)
{
    private readonly Dictionary<int, byte[]> _bodies = [];
    private readonly List<(byte[] Entry, byte[] Body)> _added = [];

    private static readonly string[] BulkClasses = ["Texture2D", "SkeletalMesh", "StaticMesh", "FmodEventFile"];

    private int NextIndex => package.Exports.Count + _added.Count + 1;

    /// <summary>
    /// Fills one placeholder: the template's properties and asset references, the slot's own
    /// <c>LegendId</c>, then the entry's values. The template's references the slot lacks (a
    /// placeholder has no audio ones) become new asset-reference exports with the slot as Outer.
    /// </summary>
    public void Fill(RosterEntry entry)
    {
        var target = package.EntryIndex(entry.Id);
        var template = package.EntryIndex(entry.Template);
        var templateBody = package.Body(template);
        var targetBody = package.Body(target);
        var templateProperties = package.Walk(templateBody);
        var targetProperties = package.Walk(targetBody);
        var targetObjects = targetProperties.Leaves.Where(leaf => leaf.Type == "ObjectProperty").ToDictionary(leaf => leaf.Path, StringComparer.Ordinal);

        var usedNumbers = package.Exports.Concat(_added.Select(added => added.Entry))
            .Where(export => RosterPackage.Outer(export) == target).Select(export => RosterWriter.I32(export, 16)).ToHashSet();
        var body = (byte[])templateBody.Clone();
        var assets = new Dictionary<string, int>(StringComparer.Ordinal);
        // By path, as Spike 12's writer added them (the package the game was seen to load).
        foreach (var reference in templateProperties.Leaves.Where(leaf => leaf.Type == "ObjectProperty").OrderBy(leaf => leaf.Path, StringComparer.Ordinal))
        {
            var templateAsset = RosterWriter.I32(templateBody, reference.Value);
            if (!package.IsAssetReference(templateAsset)) continue;
            int asset;
            if (targetObjects.TryGetValue(reference.Path, out var own) && RosterWriter.I32(targetBody, own.Value) is var existing
                && package.IsAssetReference(existing))
            {
                asset = existing;
                _bodies[asset] = package.Body(templateAsset);
            }
            else
            {
                var export = (byte[])package.Export(templateAsset).Clone();
                BinaryPrimitives.WriteInt32BigEndian(export.AsSpan(8), target);
                var number = RosterWriter.I32(export, 16);
                while (!usedNumbers.Add(number)) number++;
                BinaryPrimitives.WriteInt32BigEndian(export.AsSpan(16), number);
                asset = NextIndex;
                _added.Add((export, package.Body(templateAsset)));
            }
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(reference.Value), asset);
            assets[reference.Path] = asset;
        }

        // The slot keeps its own LegendId (one bool per slot).
        var legend = templateProperties.Top.GetValueOrDefault("LegendId")
            ?? throw new InvalidDataException($"Roster entry {entry.Template} has no LegendId to copy.");
        var ownLegend = targetProperties.Top.GetValueOrDefault("LegendId")
            ?? throw new InvalidDataException($"Roster entry {entry.Id} has no LegendId.");
        body = [.. body[..legend.Tag], .. targetBody[ownLegend.Tag..ownLegend.End], .. body[legend.End..]];

        foreach (var (path, value) in entry.Properties)
        {
            var kind = RosterFile.KindOf(path) ?? throw new InvalidDataException($"A roster entry can't set {path}.");
            if (kind == RosterValueKind.Asset)
            {
                if (!assets.TryGetValue(path, out var asset))
                    throw new InvalidDataException($"Roster entry {entry.Template} has no asset reference {path} to copy for {entry.Id}.");
                SetAsset(asset, value);
                continue;
            }
            var property = package.Walk(body).Top.GetValueOrDefault(path)
                ?? throw new InvalidDataException($"Roster entry {entry.Template} has no {path} to copy for {entry.Id}.");
            body = Set(body, property, kind, value, entry);
        }
        _bodies[target] = body;
    }

    private void SetAsset(int asset, string name)
    {
        var added = asset - package.Exports.Count - 1;
        var body = added >= 0 ? _added[added].Body : _bodies.GetValueOrDefault(asset) ?? package.Body(asset);
        var property = package.Walk(body).Top.GetValueOrDefault("m_AssetName");
        if (property is not { Type: "StrProperty" }) throw RosterWriter.Damaged("an asset reference has no m_AssetName");
        body = SetString(body, property, name);
        if (added >= 0) _added[added] = (_added[added].Entry, body);
        else _bodies[asset] = body;
    }

    private static byte[] Set(byte[] body, RosterPackage.Property property, RosterValueKind kind, string value, RosterEntry entry)
    {
        var expected = kind switch
        {
            RosterValueKind.String => "StrProperty",
            RosterValueKind.Int => "IntProperty",
            RosterValueKind.Float => "FloatProperty",
            _ => "ByteProperty",
        };
        if (property.Type != expected || (kind == RosterValueKind.Byte && property.Size != 1)
            || (kind is RosterValueKind.Int or RosterValueKind.Float && property.Size != 4))
            throw new InvalidDataException($"Roster entry {entry.Template}'s {property.Path} is a {property.Type}, not what {entry.Id} sets.");
        switch (kind)
        {
            case RosterValueKind.String:
                return SetString(body, property, value);
            case RosterValueKind.Int:
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(property.Value), int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
                return body;
            case RosterValueKind.Float:
                BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(property.Value), float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                return body;
            default:
                body[property.Value] = byte.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
                return body;
        }
    }

    private static byte[] SetString(byte[] body, RosterPackage.Property property, string value)
    {
        var raw = RosterPackage.StringBytes(value);
        var result = (byte[])[.. body[..property.Value], .. raw, .. body[(property.Value + property.Size)..]];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(property.Tag + 16), raw.Length);
        return result;
    }

    /// <summary>
    /// Summary, names and imports as they were, the export table, then every body in the original
    /// order and the added ones after the last. A moved body's absolute offsets are fixed up.
    /// </summary>
    public byte[] Write()
    {
        var d = package.Data;
        var entries = package.Exports.Select(entry => (byte[])entry.Clone()).Concat(_added.Select(added => (byte[])added.Entry.Clone())).ToList();
        var tableSize = entries.Sum(entry => entry.Length);
        var output = new MemoryStream(d.Length + 4096);
        output.Write(d, 0, package.ExportOffset);
        output.Write(new byte[tableSize]);

        var expect = package.HeaderSize;
        var order = Enumerable.Range(0, package.Exports.Count)
            .Where(k => RosterPackage.Size(package.Exports[k]) > 0)
            .OrderBy(k => RosterPackage.Offset(package.Exports[k])).ThenBy(k => k);
        foreach (var k in order)
        {
            var (offset, size) = (RosterPackage.Offset(package.Exports[k]), RosterPackage.Size(package.Exports[k]));
            if (offset != expect) throw RosterWriter.Damaged("the export bodies are not back to back");
            expect = offset + size;
            if (expect > d.Length) throw RosterWriter.Damaged("an export lies outside the file");
            var at = (int)output.Position;
            byte[] body;
            if (_bodies.TryGetValue(k + 1, out var edited)) body = edited;
            else
            {
                body = d[offset..expect];
                var delta = at - offset;
                if (delta != 0)
                    foreach (var field in AbsoluteOffsets(k + 1, offset, size))
                        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(field - offset), RosterWriter.I32(d, field) + delta);
            }
            Place(entries[k], at, body.Length);
            output.Write(body);
        }
        if (expect != d.Length) throw RosterWriter.Damaged("there are bytes after the last export");
        for (var i = 0; i < _added.Count; i++)
        {
            Place(entries[package.Exports.Count + i], (int)output.Position, _added[i].Body.Length);
            output.Write(_added[i].Body);
        }

        var result = output.ToArray();
        var entryAt = package.ExportOffset;
        foreach (var entry in entries)
        {
            entry.CopyTo(result, entryAt);
            entryAt += entry.Length;
        }
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(8), package.ExportOffset + tableSize);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(package.FlagsAt + 12), entries.Count);
        if (package.LastGenerationAt >= 0) BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(package.LastGenerationAt), entries.Count);
        return result;
    }

    private static void Place(byte[] entry, int offset, int size)
    {
        BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(32), size);
        BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(36), offset);
    }

    /// <summary>
    /// The absolute file offsets in an export's body (research, "Spike 12", fix-up 3): inline bulk
    /// data <c>{flags, count, sizeOnDisk, offsetInFile}</c> whose offset is its own position + 4, and
    /// the shader cache's record ends and material-map skips. Other matches are data, left alone.
    /// </summary>
    private IEnumerable<int> AbsoluteOffsets(int index, int offset, int size)
    {
        var d = package.Data;
        var type = package.ClassName(package.Export(index));
        var end = offset + size;
        if (type == "ShaderCache")
        {
            if (size < 16 || RosterWriter.I32(d, offset + 8) != 4) throw RosterWriter.Damaged("the shader cache has an unknown layout");
            var q = offset + 16;
            var records = RosterWriter.I32(d, offset + 12);
            for (var i = 0; i < records; i++)
            {
                yield return q + 0x18;
                var next = RosterWriter.I32(d, q + 0x18);
                if (next <= q || next > end) throw RosterWriter.Damaged("a shader cache record is out of order");
                q = next;
            }
            var maps = RosterWriter.I32(d, q);
            q += 4;
            for (var i = 0; i < maps; i++)
            {
                yield return q + 16;
                var next = RosterWriter.I32(d, q + 16);
                if (next <= q || next > end) throw RosterWriter.Damaged("a material shader map is out of order");
                q = next;
            }
            yield break;
        }
        if (!BulkClasses.Contains(type)) yield break;
        for (var q = offset + 12; q + 4 <= end; q++)
        {
            if (RosterWriter.I32(d, q) != q + 4) continue;
            var sizeOnDisk = RosterWriter.I32(d, q - 4);
            if (sizeOnDisk >= 0 && (long)q + 4 + sizeOnDisk <= end) yield return q;
        }
    }
}
