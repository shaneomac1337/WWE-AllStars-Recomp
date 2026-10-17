using System.Buffers.Binary;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// One Xenos shader container (header and microcode), named <c>ps_</c>/<c>vs_</c> plus the
/// FNV-1a-64 of its bytes: the name <c>--wwe_shader_dump_dir</c> gives it.
/// </summary>
public sealed record ShaderContainer(string Name, byte[] Bytes)
{
    public static ShaderContainer Of(byte[] bytes) =>
        new($"{(bytes[3] == 1 ? "vs" : "ps")}_{Fnv1a64(bytes):x16}", bytes);

    public static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037ul;
        foreach (var b in bytes) hash = (hash ^ b) * 1099511628211ul;
        return hash;
    }
}

/// <summary>
/// Reads the game's shader containers statically from the extracted disc: every cooked UE3
/// package carries a <c>SeekFreeShaderCache</c> export whose records hold complete Xenos
/// containers (docs/research/shader-containers-from-disc.md, ADR-0012).
/// </summary>
public static class DiscShaders
{
    private const string PackageExtension = "*.xxx";

    /// <summary>
    /// The unique containers of every package under <paramref name="gameDataDirectory"/>.
    /// Progress reports packages done of the total.
    /// </summary>
    /// <exception cref="InvalidDataException">A package is damaged; the message names it.</exception>
    public static IReadOnlyList<ShaderContainer> Read(string gameDataDirectory, IProgress<(int Done, int Total)>? progress,
        CancellationToken cancel) =>
        Read([gameDataDirectory], progress, cancel);

    /// <summary>
    /// The unique containers of every package under each of <paramref name="folders"/>, such as
    /// the game data and the mods' <c>packages/</c> folders (ticket 09). Progress counts all of them.
    /// </summary>
    /// <exception cref="InvalidDataException">A package is damaged; the message names it.</exception>
    public static IReadOnlyList<ShaderContainer> Read(IReadOnlyList<string> folders, IProgress<(int Done, int Total)>? progress,
        CancellationToken cancel)
    {
        var files = folders.SelectMany(folder =>
                Directory.EnumerateFiles(folder, PackageExtension, SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase).Select(file => (Folder: folder, File: file)))
            .ToList();
        var found = new Dictionary<string, ShaderContainer>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                foreach (var container in Ue3Package.ShaderContainers(File.ReadAllBytes(files[i].File)))
                    found.TryAdd(container.Name, container);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException(
                    $"A game file could not be read for shaders: {Path.GetRelativePath(files[i].Folder, files[i].File)}. {error.Message}", error);
            }
            progress?.Report((i + 1, files.Count));
        }
        return [.. found.Values.OrderBy(container => container.Name, StringComparer.Ordinal)];
    }
}

/// <summary>
/// The parts of a cooked UE3 package (file version 398, licensee 0x25, big-endian) that the
/// shader reader needs: decompression, the name, import and export tables, and the
/// <c>SeekFreeShaderCache</c> records.
/// </summary>
internal static class Ue3Package
{
    private const uint Tag = 0x9E2A83C1;
    private const uint LzoCompression = 2;
    private const uint XenonPlatform = 2;

    /// <summary>The containers in a package's shader cache; none for other files.</summary>
    public static IEnumerable<ShaderContainer> ShaderContainers(byte[] file)
    {
        if (file.Length < 4) return [];
        byte[] package;
        if (BinaryPrimitives.ReadUInt32LittleEndian(file) == Tag) package = WholeFile(file);
        else if (BinaryPrimitives.ReadUInt32BigEndian(file) == Tag) package = Expand(file);
        else return []; // not a package, such as AIFSM_Strategies.xxx
        return Read(package);
    }

    /// <summary>A package as the game reads it: a whole-file or chunked LZO package expanded, a plain one as it is.</summary>
    /// <exception cref="InvalidDataException">It is not a package, or it is damaged.</exception>
    public static byte[] Unpacked(byte[] file)
    {
        if (file.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(file) == Tag) return WholeFile(file);
        if (file.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(file) == Tag)
        {
            try
            {
                return Expand(file);
            }
            catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw Damaged("its summary lies outside the file");
            }
        }
        throw new InvalidDataException("The file is not a game package.");
    }

    /// <summary>
    /// The second column of a package's TOC line: for a whole-file LZO package the size it
    /// expands to, the sum of its chunks' uncompressed sizes, read from the chunk headers alone;
    /// 0 for a plain package. Null when the file is not a package (no tag in either byte order).
    /// The game hangs at boot on a whole-file package listed without it (modding research §2.3).
    /// </summary>
    /// <exception cref="InvalidDataException">A chunk header is cut short.</exception>
    public static long? TocUncompressedSize(Stream file)
    {
        Span<byte> word = stackalloc byte[8];
        int ReadAt(long offset, Span<byte> into)
        {
            file.Position = offset;
            return file.ReadAtLeast(into, into.Length, throwOnEndOfStream: false);
        }

        if (ReadAt(0, word[..4]) < 4) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(word) == Tag) return 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(word) != Tag) return null;

        long total = 0;
        long at = 0;
        Span<byte> header = stackalloc byte[16];
        while (ReadAt(at, header) == 16 && BinaryPrimitives.ReadUInt32LittleEndian(header) == Tag)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            if (size < 0) throw Damaged("a compressed chunk has a negative size");
            total += size;
            var p = at + 16;
            long compressed = 0;
            for (long sum = 0; sum < size; p += 8)
            {
                if (ReadAt(p, word) < 8) throw Damaged("a compressed chunk ends early");
                var blockCompressed = BinaryPrimitives.ReadInt32LittleEndian(word);
                var blockSize = BinaryPrimitives.ReadInt32LittleEndian(word[4..]);
                if (blockCompressed < 0 || blockSize <= 0) throw Damaged("a compressed block has a bad size");
                compressed += blockCompressed;
                sum += blockSize;
            }
            at = p + compressed;
            if (at > file.Length) throw Damaged("a compressed block ends early");
        }
        return total;
    }

    private static List<ShaderContainer> Read(byte[] d)
    {
        var bytes = new PackageBytes(d);
        var summary = Ue3Summary.Read(bytes);
        if (summary.NameCount < 0) throw Damaged("the name table has a negative count");
        var names = Ue3Summary.Names(bytes, summary.NameCount, summary.NameOffset, out _);
        string Name(int index) => (uint)index < (uint)names.Count ? names[index] : throw Damaged("a name index is out of range");

        // Import entry: class package, class name, outer, object name; the export's class is
        // the import with ObjectName "ShaderCache".
        var importNames = new string[summary.ImportCount];
        for (var i = 0; i < summary.ImportCount; i++)
            importNames[i] = Name(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(summary.ImportOffset + i * 28 + 20)));

        var containers = new List<ShaderContainer>();
        var p = summary.ExportOffset;
        for (var i = 0; i < summary.ExportCount; i++)
        {
            var classIndex = I32(d, ref p);
            p += 4 + 4 + 8 + 4 + 8; // super, outer, object name, archetype, object flags
            var serialSize = I32(d, ref p);
            var serialOffset = I32(d, ref p);
            var componentMapCount = I32(d, ref p);
            p += componentMapCount * 12 + 4; // component map, export flags
            var netObjectCount = I32(d, ref p);
            p += netObjectCount * 4 + 16; // generation net objects, package GUID
            if (classIndex < 0 && -classIndex - 1 < importNames.Length && importNames[-classIndex - 1] == "ShaderCache")
                ReadCache(d, serialOffset, serialOffset + serialSize, containers);
        }
        return containers;
    }

    /// <summary>The cache's records: type, GUID, end offset, then target and Code (see the research doc).</summary>
    private static void ReadCache(byte[] d, int start, int end, List<ShaderContainer> containers)
    {
        if (start < 0 || end > d.Length || end < start + 16) throw Damaged("the shader cache lies outside the package");
        var p = start + 12;
        var count = I32(d, ref p);
        for (var i = 0; i < count; i++)
        {
            var recordEnd = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 24));
            if (recordEnd <= p || recordEnd > end) throw Damaged("a shader cache record is out of order");
            if (FindContainer(d, p + 28, recordEnd) is var (target, code, length) && d[target] == XenonPlatform)
                containers.Add(ShaderContainer.Of(d[code..(code + length)]));
            p = recordEnd;
        }
    }

    /// <summary>
    /// Finds {platform, frequency, u32 length, container} in a record. Most records put it right
    /// after the end offset; some Bink shaders put type bytes first, so it is searched for.
    /// </summary>
    private static (int Target, int Code, int Length)? FindContainer(byte[] d, int from, int to)
    {
        for (var target = from; target + 6 + 36 <= to; target++)
        {
            var code = target + 6;
            if (d[code] != 0x10 || d[code + 1] != 0x2A || d[code + 2] != 0x11 || d[code + 3] > 1) continue;
            if (d[target + 1] != 1 - d[code + 3]) continue; // frequency 0 vertex, 1 pixel; the container flag is the reverse
            var length = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(target + 2));
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(code + 4)) +
                       BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(code + 8));
            if (length == size && code + length <= to) return (target, code, length);
        }
        return null;
    }

    /// <summary>A package whose body is in LZO chunks listed by the summary; others are returned as they are.</summary>
    private static byte[] Expand(byte[] file)
    {
        var p = 20;
        new PackageBytes(file).FString(ref p); // folder name
        p += 4 * 7 + 16;
        var generations = I32(file, ref p);
        p += generations * 12 + 8; // generations, engine and cooker version
        var flags = (uint)I32(file, ref p);
        var chunkCount = I32(file, ref p);
        if (chunkCount == 0) return file;
        if (flags != LzoCompression) throw Damaged($"compression {flags} is not LZO");
        var chunks = new (int Offset, int Size, int CompressedOffset)[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var offset = I32(file, ref p);
            var size = I32(file, ref p);
            var compressedOffset = I32(file, ref p);
            p += 4;
            chunks[i] = (offset, size, compressedOffset);
        }
        var first = chunks.Min(chunk => chunk.Offset);
        var package = new byte[chunks.Max(chunk => (long)chunk.Offset + chunk.Size)];
        file.AsSpan(0, first).CopyTo(package);
        foreach (var (offset, size, compressedOffset) in chunks)
        {
            var at = compressedOffset;
            if (Chunk(file, ref at, bigEndian: true, package.AsSpan(offset, size)) != size)
                throw Damaged("a compressed chunk has the wrong size");
        }
        return package;
    }

    /// <summary>A file compressed as a whole: back-to-back chunks with little-endian headers, then zero padding.</summary>
    private static byte[] WholeFile(byte[] file)
    {
        var parts = new List<byte[]>();
        var at = 0;
        while (at + 16 <= file.Length && BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)) == Tag)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 12));
            if (size < 0) throw Damaged("a compressed chunk has a negative size");
            var part = new byte[size];
            if (Chunk(file, ref at, bigEndian: false, part) != size) throw Damaged("a compressed chunk has the wrong size");
            parts.Add(part);
        }
        // The disc pads these files with zeros to a 32 KB boundary.
        if (file.AsSpan(at).ContainsAnyExcept((byte)0)) throw Damaged("the file does not end after its last chunk");
        return [.. parts.SelectMany(part => part)];
    }

    /// <summary>
    /// One chunk: {tag, block size (unreliable on this disc), compressed size, uncompressed size},
    /// {compressed, uncompressed} per block, then the LZO blocks. Returns the bytes written.
    /// </summary>
    private static int Chunk(byte[] file, ref int at, bool bigEndian, Span<byte> output)
    {
        int Read(int offset)
        {
            if (offset < 0 || offset + 4 > file.Length) throw Damaged("a compressed chunk ends early");
            return bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset))
                : BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));
        }

        if ((uint)Read(at) != Tag) throw Damaged("a compressed chunk has no tag");
        var total = Read(at + 12);
        var blocks = new List<(int Compressed, int Uncompressed)>();
        var p = at + 16;
        for (var sum = 0L; sum < total; p += 8)
        {
            var block = (Read(p), Read(p + 4));
            if (block.Item1 < 0 || block.Item2 <= 0) throw Damaged("a compressed block has a bad size");
            blocks.Add(block);
            sum += block.Item2;
        }
        var written = 0;
        foreach (var (compressed, uncompressed) in blocks)
        {
            if (p + compressed > file.Length || written + uncompressed > output.Length)
                throw Damaged("a compressed block ends early");
            if (Lzo1x.Decompress(file.AsSpan(p, compressed), output.Slice(written, uncompressed)) != uncompressed)
                throw Damaged("a compressed block has the wrong size");
            p += compressed;
            written += uncompressed;
        }
        at = p;
        return written;
    }

    private static int I32(byte[] d, ref int p)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p, 4));
        p += 4;
        return value;
    }

    private static InvalidDataException Damaged(string what) => new($"The package is damaged: {what}.");
}
