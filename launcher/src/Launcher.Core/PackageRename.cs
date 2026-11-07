using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// Renames a cooked package to another slot whose name has the same length (package-rename spec,
/// Solution 2; docs/research/package-rename.md). Only the name-table entry that names the package
/// itself changes, so no offset moves. Call it for a package <see cref="PackageOrigin.Check"/>
/// found <see cref="RenamedPackage.Renamable"/>.
/// </summary>
/// <remarks>
/// A plain package is written with the entry rewritten. A chunked one is expanded, rewritten and
/// re-packed as literal-only LZO1X blocks (no compressor, decided 2026-10-25), each chunk with its
/// original block sizes. The physical header (summary and chunk table) runs up to the first
/// compressed chunk and is kept; only the chunk table's compressed offsets and sizes change. The
/// file ends after its last chunk, as the 2026-10-25 prototype's output did: the disc's padding
/// to 64 KB is not written.
/// </remarks>
public static class PackageRename
{
    /// <summary>Writes <paramref name="file"/>, made as <paramref name="madeAs"/>, to <paramref name="output"/> as the package <paramref name="fileName"/>.</summary>
    /// <exception cref="ArgumentException">The names differ in length.</exception>
    /// <exception cref="InvalidDataException">The package is damaged, compressed as a whole, or has no one entry of its own name.</exception>
    public static void Write(Stream file, string madeAs, string fileName, Stream output)
    {
        var (old, name) = (Path.GetFileNameWithoutExtension(madeAs), Path.GetFileNameWithoutExtension(fileName));
        if (old.Length != name.Length) throw new ArgumentException("Names of different lengths can't be renamed.", nameof(fileName));
        var bytes = new byte[file.Length];
        file.Position = 0;
        file.ReadExactly(bytes);
        try
        {
            if (bytes.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != Ue3Chunks.Tag)
                throw Damaged("it is no big-endian package (one compressed as a whole can't be renamed)");
            var chunks = Ue3Chunks.ReadTable(new PackageBytes(bytes));
            if (chunks.Count == 0)
            {
                RenameEntry(bytes, old, name);
                output.Write(bytes);
                return;
            }
            WriteChunked(bytes, chunks, old, name, output);
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw Damaged(error.Message);
        }
    }

    /// <summary>A chunk table entry and its chunk's header, which the re-packed chunk keeps but for the compressed sizes.</summary>
    private sealed record Chunk(Ue3Chunks.Entry Entry, Ue3Chunks.Header Header);

    private static void WriteChunked(byte[] file, List<Ue3Chunks.Entry> entries, string old, string name, Stream output)
    {
        if (entries.Any(entry => entry.Offset < 0 || entry.Size < 0 || entry.CompressedOffset < 0)) throw Damaged("a chunk has a negative offset");
        var headerEnd = entries.Min(entry => entry.CompressedOffset);
        var dataStart = entries.Min(entry => entry.Offset);
        var data = new byte[entries.Max(entry => (long)entry.Offset + entry.Size)];
        file.AsSpan(0, Math.Min(dataStart, file.Length)).CopyTo(data);
        var chunks = entries.Select(entry => Expand(file, entry, data)).ToList();

        RenameEntry(data, old, name);

        // The kept header gets the rewritten bytes the game reads from it, then the new chunk table.
        var header = file[..headerEnd];
        data.AsSpan(0, Math.Min(dataStart, headerEnd)).CopyTo(header);
        var packed = new List<byte[]>();
        var position = headerEnd;
        foreach (var chunk in chunks.OrderBy(chunk => chunk.Entry.CompressedOffset))
        {
            var bytes = Pack(data, chunk);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(chunk.Entry.TableAt + 8), position);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(chunk.Entry.TableAt + 12), bytes.Length);
            packed.Add(bytes);
            position += bytes.Length;
        }
        output.Write(header);
        foreach (var bytes in packed) output.Write(bytes);
    }

    /// <summary>Expands the chunk <paramref name="entry"/> lists into <paramref name="data"/> at its offset.</summary>
    private static Chunk Expand(byte[] file, Ue3Chunks.Entry entry, byte[] data)
    {
        using var stream = new MemoryStream(file, writable: false);
        var header = Ue3Chunks.ReadHeader(stream, entry.CompressedOffset, bigEndian: true);
        if (header.Size != entry.Size) throw Damaged("a compressed chunk's size differs from the chunk table");
        Ue3Chunks.Expand(file, header, data.AsSpan(entry.Offset, entry.Size));
        return new Chunk(entry, header);
    }

    /// <summary>One chunk of <paramref name="data"/> as literal-only LZO blocks of its original sizes.</summary>
    private static byte[] Pack(byte[] data, Chunk chunk)
    {
        var blocks = new List<byte[]>();
        var at = chunk.Entry.Offset;
        foreach (var (_, size) in chunk.Header.Blocks)
        {
            blocks.Add(Lzo1x.StoreLiterals(data.AsSpan(at, size)));
            at += size;
        }
        var bytes = new byte[16 + 8 * blocks.Count + blocks.Sum(block => block.Length)];
        var p = 0;
        void Word(int value) { BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(p), value); p += 4; }
        Word(unchecked((int)Ue3Chunks.Tag));
        Word(chunk.Header.RepeatedTag); // kept as the disc has it
        Word(blocks.Sum(block => block.Length));
        Word(chunk.Entry.Size);
        for (var i = 0; i < blocks.Count; i++)
        {
            Word(blocks[i].Length);
            Word(chunk.Header.Blocks[i].Size);
        }
        foreach (var block in blocks)
        {
            block.CopyTo(bytes, p);
            p += block.Length;
        }
        return bytes;
    }

    /// <summary>Rewrites the one name-table entry <paramref name="old"/> (any case) as <paramref name="name"/>, in place.</summary>
    private static void RenameEntry(byte[] data, string old, string name)
    {
        var reader = new PackageBytes(data);
        var summary = Ue3Summary.Read(reader);
        if (summary.NameCount is < 0 or > 1 << 22) throw Damaged("its name table has an impossible size");
        var found = -1;
        var p = summary.NameOffset;
        for (var i = 0; i < summary.NameCount; i++)
        {
            var at = p;
            if (string.Equals(reader.FString(ref p), old, StringComparison.OrdinalIgnoreCase))
            {
                if (found >= 0) throw Damaged("its name table holds its own name more than once");
                found = at;
            }
            p += 8; // flags
        }
        if (found < 0) throw Damaged("its name table does not hold its own name whole");
        // Same length in characters, so the same length in bytes in either encoding.
        var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(found));
        var text = length >= 0 ? Encoding.Latin1.GetBytes(name) : Encoding.BigEndianUnicode.GetBytes(name);
        text.CopyTo(data, found + 4);
    }

    private static InvalidDataException Damaged(string what) => new($"The package can't be renamed: {what}.");
}
