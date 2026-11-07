using System.Buffers.Binary;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// The physical layout of a cooked UE3 package's LZO chunks, shared by every reader that expands
/// one (docs/research/shader-containers-from-disc.md): the summary's chunk table, and each chunk's
/// header {tag, block size, compressed size, size}, then {compressed, size} per block, then the blocks.
/// </summary>
internal static class Ue3Chunks
{
    /// <summary>A package's first word: big-endian for a plain or chunked package, little-endian for one compressed as a whole.</summary>
    public const uint Tag = 0x9E2A83C1;

    private const int Lzo = 2;
    private const int MaxCount = 1 << 18;

    /// <summary>One chunk table entry; <see cref="TableAt"/> is where the entry sits in the file.</summary>
    public readonly record struct Entry(int TableAt, int Offset, int Size, int CompressedOffset, int CompressedSize);

    /// <summary>
    /// One chunk's header. <see cref="RepeatedTag"/> is the word UE3 calls the block size; the disc
    /// stores the tag there, so no reader trusts it. The blocks' sizes add up to <see cref="Size"/>,
    /// and their data runs from <see cref="DataAt"/> to <see cref="End"/>, inside the file.
    /// </summary>
    public sealed record Header(int RepeatedTag, int Size, IReadOnlyList<(int Compressed, int Size)> Blocks, long DataAt, long End);

    /// <summary>The summary's chunk table, after the tag, version, header size, folder name and table counts; empty for a plain package.</summary>
    /// <exception cref="InvalidDataException">A count is impossible, or the chunks are not LZO.</exception>
    public static List<Entry> ReadTable<T>(T data) where T : IPackageReader
    {
        var p = 20;
        data.FString(ref p); // folder name
        p += 4 * 7 + 16; // flags, table counts and offsets, GUID
        var generations = data.I32(ref p);
        if (generations is < 0 or > MaxCount) throw Damaged("it has an impossible number of generations");
        p += generations * 12 + 8; // generations, engine and cooker version
        var compression = data.I32(ref p);
        var count = data.I32(ref p);
        if (count is < 0 or > MaxCount) throw Damaged("it has an impossible number of chunks");
        if (count > 0 && compression != Lzo) throw Damaged($"compression {compression} is not LZO");
        var chunks = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = p;
            chunks.Add(new Entry(at, data.I32(ref p), data.I32(ref p), data.I32(ref p), data.I32(ref p)));
        }
        return chunks;
    }

    /// <summary>The header of the chunk at <paramref name="at"/>, without reading its blocks.</summary>
    /// <exception cref="InvalidDataException">The header is damaged, or the chunk runs past the file's end.</exception>
    public static Header ReadHeader(Stream file, long at, bool bigEndian)
    {
        var word = new byte[16];
        int Word(ReadOnlySpan<byte> bytes) =>
            bigEndian ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes);

        ReadExactly(file, at, word);
        if (unchecked((uint)Word(word)) != Tag) throw Damaged("a compressed chunk has no tag");
        var repeatedTag = Word(word.AsSpan(4));
        var size = Word(word.AsSpan(12));
        if (size < 0) throw Damaged("a compressed chunk has a negative size");
        var blocks = new List<(int Compressed, int Size)>();
        var p = at + 16;
        long sum = 0, compressed = 0;
        for (; sum < size; p += 8)
        {
            ReadExactly(file, p, word.AsSpan(0, 8));
            var block = (Word(word), Word(word.AsSpan(4)));
            if (block.Item1 < 0 || block.Item2 <= 0) throw Damaged("a compressed block has a bad size");
            blocks.Add(block);
            sum += block.Item2;
            compressed += block.Item1;
        }
        if (sum != size) throw Damaged("a compressed chunk's blocks run past its size");
        if (p + compressed > file.Length) throw Damaged("a compressed chunk ends early");
        return new Header(repeatedTag, size, blocks, p, p + compressed);
    }

    /// <summary>Expands the chunk <paramref name="header"/> describes into the start of <paramref name="output"/>, and returns its size.</summary>
    /// <exception cref="InvalidDataException">A block is damaged, or the chunk does not fit.</exception>
    public static int Expand(ReadOnlySpan<byte> file, Header header, Span<byte> output)
    {
        if (header.Size > output.Length) throw Damaged("a compressed chunk is larger than its place");
        var (p, written) = ((int)header.DataAt, 0);
        foreach (var (compressed, size) in header.Blocks)
        {
            if (Lzo1x.Decompress(file.Slice(p, compressed), output.Slice(written, size)) != size) throw Damaged("a compressed block has the wrong size");
            p += compressed;
            written += size;
        }
        return written;
    }

    private static void ReadExactly(Stream file, long at, Span<byte> into)
    {
        if (at < 0 || at + into.Length > file.Length) throw Damaged("a compressed chunk ends early");
        file.Position = at;
        file.ReadExactly(into);
    }

    /// <summary>"The package is damaged: …", with <paramref name="what"/> kept for <see cref="WhatIsDamaged"/>.</summary>
    public static InvalidDataException Damaged(string what) => new($"The package is damaged: {what}.") { Data = { [nameof(Damaged)] = what } };

    /// <summary>What <see cref="Damaged"/> said is wrong, such as "a compressed chunk has no tag"; null for another error.</summary>
    public static string? WhatIsDamaged(InvalidDataException error) => error.Data[nameof(Damaged)] as string;
}
