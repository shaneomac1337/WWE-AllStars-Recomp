using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// A package found under a name it was not made under; <see cref="MadeAs"/> is its own name when
/// known. <see cref="CannotRename"/> says why the launcher can't rename it to its file's name
/// (package-rename spec), and is null when it can; it is never null when <see cref="MadeAs"/> is.
/// </summary>
public sealed record RenamedPackage(string? MadeAs, string? CannotRename = null)
{
    /// <summary>Whether <see cref="PackageRename.Write"/> can make it the package of its file's name.</summary>
    public bool Renamable => CannotRename is null;
}

/// <summary>
/// Whether a cooked package carries the name it was made under (ticket 20). A cooked package
/// names its objects after its original file, so a copy under another package's name loads and
/// then hangs the game (modding research §2.3, "Owner check, attire swap by rename").
/// </summary>
/// <remarks>
/// The marker, checked on all 5,413 disc packages under their own names (whole-file LZO ones
/// included) and on the renamed attire pair: a package with a name table either has a top-level
/// export of its file name, or has its file name in its name table and, unless it holds script,
/// no top-level <c>Package</c> export named after another of the game's packages. Only the
/// summary and the name, import and export tables are read; LZO blocks past them stay packed.
/// </remarks>
public static class PackageOrigin
{
    private const int ContainsScript = 0x200000;
    private const int MaxEntries = 1 << 22;

    /// <summary>
    /// Null when <paramref name="fileName"/> (such as <c>CHR_101_A.xxx</c>) is the package's own
    /// name, or the file is no package (other checks report that); otherwise what it was made as.
    /// </summary>
    /// <param name="isKnownPackage">Whether a short name such as <c>CHR_101_B.xxx</c> is one of the game's packages.</param>
    /// <exception cref="InvalidDataException">The package's tables are damaged.</exception>
    public static RenamedPackage? Check(Stream file, string fileName, Func<string, bool> isKnownPackage)
    {
        try
        {
            if (Tables.Read(file) is not { } tables) return null;
            var renamed = Judge(tables, Path.GetFileNameWithoutExtension(fileName), isKnownPackage);
            return renamed is { Renamable: true } && ChunkProblem(file, tables.Chunks) is { } problem ? renamed with { CannotRename = problem } : renamed;
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            throw Damaged(error.Message);
        }
    }

    /// <summary>
    /// The page's reason for a renamed package the launcher can't rename, such as
    /// "packages/CHR_101_A.xxx was made as CHR_101_B.xxx: …, and the launcher can't rename this one: …".
    /// </summary>
    internal static string Reason(string shown, RenamedPackage renamed) =>
        renamed.MadeAs is { } original
            ? $"{shown} was made as {original}: renaming a package does not replace it"
                + (renamed.CannotRename is { } why ? $", and the launcher can't rename this one: {why}." : ".")
            : $"{shown} was made under another name: renaming a package does not replace it.";

    /// <summary>The Mods tab's note for a package Play renames, such as "CHR_101_A.xxx was made as CHR_101_B.xxx; …".</summary>
    internal static string Note(string fileName, RenamedPackage renamed) =>
        $"{fileName} was made as {renamed.MadeAs}; the launcher renames it for this slot.";

    /// <summary>
    /// The reason a wrap is refused when one of its loose packages is a renamed copy the launcher
    /// can't rename; null otherwise. A package that can't be read is left to the install's own checks.
    /// </summary>
    internal static string? RenamedIn(ModWrapCandidate wrap, KnownGameFiles known)
    {
        foreach (var (from, to) in wrap.Copies)
        {
            if (!to.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = File.OpenRead(from);
                if (Check(stream, Path.GetFileName(to), known.IsPackage) is { Renamable: false } renamed) return Reason(Path.GetFileName(to), renamed);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // The install reads it again and reports it.
            }
        }
        return null;
    }

    private static RenamedPackage? Judge(Tables tables, string name, Func<string, bool> isKnownPackage)
    {
        if (tables.Names.Count == 0) return null; // an empty package names nothing to check
        var top = tables.Exports.Where(export => export.Outer == 0).ToList();
        if (top.Any(export => Same(export.Name, name))) return null;
        var madeAs = top.Where(export => export.ClassName == "Package")
            .Select(export => export.Name + ".xxx").FirstOrDefault(isKnownPackage);
        if (!tables.HasName(name))
            return new RenamedPackage(madeAs, madeAs is null ? "the name it was made under is unknown" : CannotRename(tables, madeAs, name));
        if ((tables.Flags & ContainsScript) != 0 || madeAs is null) return null;
        return new RenamedPackage(madeAs, $"it already names {name}");
    }

    /// <summary>
    /// Why the package made as <paramref name="madeAs"/> can't be renamed to <paramref name="name"/>,
    /// or null: the names are the same length, and the old prefix (the old name up to its last
    /// character that differs, <c>Arena_005</c> for <c>Arena_005_Ring</c> to <c>Arena_003_Ring</c>)
    /// is in the name table only as the package's own name.
    /// </summary>
    private static string? CannotRename(Tables tables, string madeAs, string name)
    {
        var old = Path.GetFileNameWithoutExtension(madeAs);
        if (old.Length != name.Length) return "names of different lengths can't be renamed";
        if (tables.WholeFile) return "packages compressed as a whole can't be renamed";
        var differs = old.Length - 1;
        while (differs > 0 && char.ToUpperInvariant(old[differs]) == char.ToUpperInvariant(name[differs])) differs--;
        var oldPrefix = old[..(differs + 1)];
        var own = tables.Names.Count(entry => Same(entry, old));
        // An FName with a number (Foo_1) is stored without it, so it has no entry to rewrite.
        if (own == 0) return "its name table holds its own name only with a number";
        if (own > 1) return "its name table holds its own name more than once";
        var other = tables.Names.FirstOrDefault(entry => !Same(entry, old) && entry.Contains(oldPrefix, StringComparison.OrdinalIgnoreCase));
        return other is null ? null : $"its name table also names {oldPrefix} in {other}";
    }

    /// <summary>
    /// What in the chunk table or the chunk headers <see cref="PackageRename.Write"/> would refuse,
    /// or null. The headers are read, not the blocks, so a block that does not expand still fails at Play.
    /// </summary>
    private static string? ChunkProblem(Stream file, IReadOnlyList<Ue3Chunks.Entry> chunks)
    {
        if (chunks.Count == 0) return null;
        if (chunks.Any(chunk => chunk.Offset < 0 || chunk.Size < 0 || chunk.CompressedOffset < 0)) return "a chunk has a negative offset";
        // The renamed copy keeps the file up to its first compressed chunk, chunk table included.
        if (chunks.Max(chunk => chunk.TableAt) + 16 > chunks.Min(chunk => chunk.CompressedOffset)) return "its chunk table runs into its first chunk";
        try
        {
            foreach (var chunk in chunks)
                if (Ue3Chunks.ReadHeader(file, chunk.CompressedOffset, bigEndian: true).Size != chunk.Size)
                    return "a compressed chunk's size differs from the chunk table";
        }
        catch (InvalidDataException error) when (Ue3Chunks.WhatIsDamaged(error) is { } what)
        {
            return what;
        }
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed record Export(int Outer, string Name, string? ClassName);

    private sealed class Tables
    {
        public required int Flags { get; init; }
        public required List<string> Names { get; init; }
        public required List<Export> Exports { get; init; }

        /// <summary>Whether the whole file is LZO chunks, rather than the summary listing its chunks.</summary>
        public required bool WholeFile { get; init; }

        /// <summary>The summary's chunk table; empty for a plain package or one compressed as a whole.</summary>
        public required IReadOnlyList<Ue3Chunks.Entry> Chunks { get; init; }

        /// <summary>
        /// Whether <paramref name="name"/> is in the name table: as it is, or as an FName with a
        /// number, which the table stores without its <c>_N</c> suffix (<c>AnimFSM_Andre_0</c>).
        /// </summary>
        public bool HasName(string name)
        {
            if (Names.Any(entry => Same(entry, name))) return true;
            var split = name.LastIndexOf('_');
            if (split <= 0) return false;
            var suffix = name[(split + 1)..];
            if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit) || (suffix.Length > 1 && suffix[0] == '0')) return false;
            return Names.Any(entry => Same(entry, name[..split]));
        }

        /// <summary>The tables of a package, or null when the file has the tag in neither byte order.</summary>
        public static Tables? Read(Stream file)
        {
            var head = new byte[4];
            file.Position = 0;
            if (file.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4) return null;
            Expanded data;
            List<Ue3Chunks.Entry> chunks = [];
            var wholeFile = false;
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == Ue3Chunks.Tag) data = Expanded.Chunked(file, out chunks);
            else if (BinaryPrimitives.ReadUInt32LittleEndian(head) == Ue3Chunks.Tag) (data, wholeFile) = (Expanded.WholeFile(file), true);
            else return null;

            var summary = Ue3Summary.Read(data);
            var nameCount = Count(summary.NameCount);
            var exportCount = Count(summary.ExportCount);
            var importCount = Count(summary.ImportCount);
            var (flags, importOffset) = (summary.Flags, summary.ImportOffset);

            var names = Ue3Summary.Names(data, nameCount, summary.NameOffset, out _);
            string Name(int index, int number)
            {
                if ((uint)index >= (uint)names.Count) throw Damaged("a name index is out of range");
                return number == 0 ? names[index] : $"{names[index]}_{number - 1}";
            }

            // Import entry: class package, class name, outer, object name (each FName is index, number).
            var imports = new string[importCount];
            for (var i = 0; i < importCount; i++)
            {
                var at = importOffset + i * 28 + 20;
                imports[i] = Name(data.I32(ref at), data.I32(ref at));
            }

            var exports = new List<Export>(Math.Min(exportCount, 4096));
            var p = summary.ExportOffset;
            for (var i = 0; i < exportCount; i++)
            {
                var classIndex = data.I32(ref p);
                p += 4; // super
                var outer = data.I32(ref p);
                var name = Name(data.I32(ref p), data.I32(ref p));
                p += 4 + 8 + 4 + 4; // archetype, object flags, serial size and offset
                var componentMapCount = Count(data.I32(ref p));
                p += componentMapCount * 12 + 4; // component map, export flags
                var netObjectCount = Count(data.I32(ref p));
                p += netObjectCount * 4 + 16; // generation net objects, package GUID
                var className = classIndex == 0 ? "Class"
                    : classIndex < 0 && -classIndex - 1 < imports.Length ? imports[-classIndex - 1] : null;
                exports.Add(new Export(outer, name, className));
            }
            return new Tables { Flags = flags, Names = names, Exports = exports, WholeFile = wholeFile, Chunks = chunks };
        }

        private static int Count(int value) =>
            value is >= 0 and <= MaxEntries ? value : throw Damaged("a table has an impossible size");
    }

    /// <summary>
    /// The start of a package as the game reads it, expanded only as far as it is read: plain
    /// bytes, or LZO blocks from the summary's chunks or the whole-file chunks.
    /// </summary>
    private sealed class Expanded : IPackageReader
    {
        private const int Piece = 0x10000;
        private readonly Stream _file;
        private readonly IEnumerator<(long At, int Compressed, int Size)> _blocks;
        private byte[] _data = new byte[Piece];
        private int _length;

        private Expanded(Stream file, IEnumerable<(long At, int Compressed, int Size)> blocks)
        {
            _file = file;
            _blocks = blocks.GetEnumerator();
        }

        /// <summary>A big-endian package: plain, or with its body in the LZO <paramref name="chunks"/> the summary lists.</summary>
        public static Expanded Chunked(Stream file, out List<Ue3Chunks.Entry> chunks)
        {
            var plain = new Expanded(file, Plain(file));
            chunks = Ue3Chunks.ReadTable(plain);
            if (chunks.Count == 0) return plain;
            var ordered = chunks.OrderBy(chunk => chunk.Offset).ToList();
            var first = ordered[0].Offset;
            plain.Ensure(first);
            var expanded = new Expanded(file, ordered.SelectMany(chunk => Blocks(file, chunk.CompressedOffset, bigEndian: true, out _)));
            expanded.Append(plain._data.AsSpan(0, first));
            return expanded;
        }

        /// <summary>A package compressed as a whole: back-to-back chunks with little-endian headers.</summary>
        public static Expanded WholeFile(Stream file) => new(file, WholeFileBlocks(file));

        private static IEnumerable<(long, int, int)> Plain(Stream file)
        {
            for (long at = 0; at < file.Length; at += Piece) yield return (at, -1, (int)Math.Min(Piece, file.Length - at));
        }

        private static IEnumerable<(long, int, int)> WholeFileBlocks(Stream file)
        {
            long at = 0;
            var word = new byte[4];
            while (at + 16 <= file.Length)
            {
                file.Position = at;
                if (file.ReadAtLeast(word, 4, throwOnEndOfStream: false) < 4 || BinaryPrimitives.ReadUInt32LittleEndian(word) != Ue3Chunks.Tag) yield break;
                var blocks = Blocks(file, at, bigEndian: false, out var end);
                foreach (var block in blocks) yield return block;
                at = end;
            }
        }

        /// <summary>Where each of one chunk's blocks lies, from its header.</summary>
        private static List<(long, int, int)> Blocks(Stream file, long at, bool bigEndian, out long end)
        {
            var header = Ue3Chunks.ReadHeader(file, at, bigEndian);
            var blocks = new List<(long, int, int)>();
            var p = header.DataAt;
            foreach (var (compressed, size) in header.Blocks)
            {
                blocks.Add((p, compressed, size));
                p += compressed;
            }
            end = header.End;
            return blocks;
        }

        private static void ReadExactly(Stream file, long at, byte[] into)
        {
            if (at < 0 || at + into.Length > file.Length) throw Damaged("a compressed chunk ends early");
            file.Position = at;
            file.ReadExactly(into);
        }

        /// <summary>Expands until the first <paramref name="end"/> bytes are there.</summary>
        private void Ensure(long end)
        {
            while (_length < end)
            {
                if (!_blocks.MoveNext()) throw Damaged("its tables run past its end");
                var (at, compressed, size) = _blocks.Current;
                if (compressed < 0)
                {
                    var raw = new byte[size];
                    ReadExactly(_file, at, raw);
                    Append(raw);
                    continue;
                }
                var packed = new byte[compressed];
                ReadExactly(_file, at, packed);
                var block = new byte[size];
                if (Lzo1x.Decompress(packed, block) != size) throw Damaged("a compressed block has the wrong size");
                Append(block);
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (_length + bytes.Length > _data.Length) Array.Resize(ref _data, Math.Max(_data.Length * 2, _length + bytes.Length));
            bytes.CopyTo(_data.AsSpan(_length));
            _length += bytes.Length;
        }

        public int I32(ref int p)
        {
            if (p < 0) throw Damaged("an offset is negative");
            Ensure((long)p + 4);
            var value = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(p, 4));
            p += 4;
            return value;
        }

        /// <summary>An FString: i32 length including the NUL, then the characters; negative means UTF-16.</summary>
        public string FString(ref int p)
        {
            var length = I32(ref p);
            var bytes = length >= 0 ? (long)length : -2L * length;
            if (bytes > 1 << 20) throw Damaged("a name is impossibly long");
            Ensure(p + bytes);
            var text = length >= 0
                ? Encoding.Latin1.GetString(_data, p, (int)Math.Max(0, bytes - 1))
                : Encoding.BigEndianUnicode.GetString(_data, p, (int)Math.Max(0, bytes - 2));
            p += (int)bytes;
            return text;
        }
    }

    private static InvalidDataException Damaged(string what) => Ue3Chunks.Damaged(what);
}
