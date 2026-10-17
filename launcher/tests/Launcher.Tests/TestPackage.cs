using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// Builds a tiny cooked UE3 package (file version 398, licensee 0x25, big-endian) with a
/// <c>SeekFreeShaderCache</c> export, in the layout docs/research/shader-containers-from-disc.md
/// describes, so the shader reader is tested without the real disc.
/// </summary>
internal sealed class TestPackage
{
    public enum Compression
    {
        None,
        /// <summary>The body after the summary is one LZO chunk listed in the summary.</summary>
        Chunks,
        /// <summary>The whole file is a run of LZO chunks with little-endian headers, then zero padding.</summary>
        WholeFile,
    }

    private sealed record Record(string Type, byte[] Container, int Platform, byte[] BeforeTarget);

    private readonly List<Record> _records = [];

    public Compression Compressed { get; init; } = Compression.None;

    /// <summary>False: the package has exports but none of class ShaderCache.</summary>
    public bool HasShaderCache { get; init; } = true;

    /// <summary>
    /// The name the package was made under, as a top-level <c>Package</c> export the way the disc's
    /// cooked packages carry it; <see cref="Write"/> and <see cref="Bytes(string)"/> use the file name when this is null.
    /// </summary>
    public string? MadeAs { get; init; }

    /// <summary>A Xenos shader container: the 36-byte header, then the microcode.</summary>
    public static byte[] Container(bool vertex, params byte[] microcode)
    {
        var bytes = new byte[36 + microcode.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, vertex ? 0x102A1101u : 0x102A1100u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 36);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)microcode.Length);
        microcode.CopyTo(bytes, 36);
        return bytes;
    }

    /// <summary>Adds a shader record; <paramref name="beforeTarget"/> are type bytes some Bink shaders put first.</summary>
    public TestPackage Add(byte[] container, string type = "FSD_UberPixelShader", int platform = 2, byte[]? beforeTarget = null)
    {
        _records.Add(new Record(type, container, platform, beforeTarget ?? []));
        return this;
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Bytes(Path.GetFileName(path)));
    }

    /// <summary>The package as made under <paramref name="fileName"/> (such as <c>CHR_101_A.xxx</c>), unless <see cref="MadeAs"/> says otherwise.</summary>
    public byte[] Bytes(string fileName) => Build(MadeAs ?? Path.GetFileNameWithoutExtension(fileName));

    /// <summary>
    /// The package; it names no package it was made as unless <see cref="MadeAs"/> is set, so
    /// mod tests pass it through <see cref="ForFile"/> where it is written.
    /// </summary>
    public byte[] Bytes()
    {
        var bytes = Build(MadeAs);
        if (MadeAs is null) Unnamed.AddOrUpdate(bytes, this);
        return bytes;
    }

    private static readonly ConditionalWeakTable<byte[], TestPackage> Unnamed = new();

    /// <summary>
    /// <paramref name="bytes"/> as made under <paramref name="fileName"/> when they came from
    /// <see cref="Bytes()"/> without a name, the way a mod's package is made under its own name
    /// (ticket 20); any other bytes as they are.
    /// </summary>
    public static byte[] ForFile(byte[] bytes, string fileName) =>
        Unnamed.TryGetValue(bytes, out var package) ? package.Bytes(fileName) : bytes;

    private byte[] Build(string? madeAs)
    {
        // Shared by tests running in parallel; _madeAs holds for one build.
        lock (_records)
        {
            _madeAs = madeAs;
            var plain = Plain(out var bodyStart);
            return Compressed switch
            {
                Compression.Chunks => ChunkCompressed(plain, bodyStart),
                Compression.WholeFile => [.. Chunk(plain, littleEndian: true), .. new byte[100]], // the disc pads with zeros
                _ => plain,
            };
        }
    }

    private static readonly string[] BaseNames =
        ["None", "Core", "Class", "ShaderCache", "SeekFreeShaderCache", "Texture2D", "SomeTexture", "FSD_UberPixelShader", "FBinkYCrCbToRGBNoPixelAlphaPixelShader"];

    private string? _madeAs;

    private string[] Names => _madeAs is null ? BaseNames : [.. BaseNames, "Package", _madeAs];

    private int NameIndex(string name)
    {
        var index = Array.IndexOf(Names, name);
        if (index < 0) throw new ArgumentException("Add the name to TestPackage.Names: " + name);
        return index;
    }

    /// <summary>The uncompressed package; <paramref name="bodyStart"/> is where the name table starts.</summary>
    private byte[] Plain(out int bodyStart)
    {
        var chunked = Compressed == Compression.Chunks;
        var summary = new Writer();
        summary.U32(0x9E2A83C1).U32(0x0025018E).U32(0).U32(0x20414E54).U32(0x0F);
        summary.FString("None");
        var counts = summary.Position;
        summary.Zeros(7 * 4); // flags, names, exports, imports: patched below
        summary.Zeros(16).U32(0).U32(0xA2D).U32(0x21);
        summary.U32(chunked ? 2u : 0u).U32(chunked ? 1u : 0u);
        var chunkTable = summary.Position;
        if (chunked) summary.Zeros(16);
        bodyStart = summary.Position;

        var file = summary;
        var nameOffset = file.Position;
        foreach (var name in Names) file.FString(name).U32(0).U32(0);
        var importOffset = file.Position;
        // Import 1: Core.Class ShaderCache (the export's class).
        file.FName(NameIndex("Core")).FName(NameIndex("Class")).U32(0).FName(NameIndex("ShaderCache"));
        // Import 2: Core.Class Package, the class of the package it was made as.
        if (_madeAs is not null) file.FName(NameIndex("Core")).FName(NameIndex("Class")).U32(0).FName(NameIndex("Package"));

        var exportOffset = file.Position;
        var exportCount = _madeAs is null ? 2 : 3;
        // Export table: a texture with a component map and net objects (a longer entry), the
        // cache, then the top-level package it was made as.
        var tableSize = (40 + 4 + 12 + 4 + 4 + 8 + 16) + (40 + 4 + 4 + 4 + 16) + (_madeAs is null ? 0 : 40 + 4 + 4 + 4 + 16);
        var dataStart = exportOffset + tableSize;
        var textureData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var cacheData = CacheExport(dataStart + textureData.Length);

        file.U32(0).U32(0).U32(0).FName(NameIndex("SomeTexture")).U32(0).U64(0)
            .U32((uint)textureData.Length).U32((uint)dataStart)
            .U32(1).FName(NameIndex("None")).U32(7) // component map: one entry
            .U32(0).U32(2).U32(0).U32(0).Zeros(16);
        file.U32(HasShaderCache ? unchecked((uint)-1) : 0).U32(0).U32(0)
            .FName(NameIndex(HasShaderCache ? "SeekFreeShaderCache" : "SomeTexture")).U32(0).U64(0)
            .U32((uint)cacheData.Length).U32((uint)(dataStart + textureData.Length))
            .U32(0).U32(1).U32(0).Zeros(16);
        if (_madeAs is not null)
            file.U32(unchecked((uint)-2)).U32(0).U32(0).FName(NameIndex(_madeAs)).U32(0).U64(0)
                .U32(0).U32(0).U32(0).U32(1).U32(0).Zeros(16);
        if (file.Position != dataStart) throw new InvalidOperationException("export table size");
        file.Bytes(textureData).Bytes(cacheData);

        var bytes = file.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)bodyStart);
        var c = counts;
        foreach (var value in new[] { 0, Names.Length, nameOffset, exportCount, exportOffset, _madeAs is null ? 1 : 2, importOffset })
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(c), value);
            c += 4;
        }
        if (chunked)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(chunkTable), bodyStart);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(chunkTable + 4), bytes.Length - bodyStart);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(chunkTable + 8), bodyStart);
            // CompressedSize is patched by ChunkCompressed.
        }
        return bytes;
    }

    /// <summary>The SeekFreeShaderCache export's data; <paramref name="start"/> is its package offset.</summary>
    private byte[] CacheExport(int start)
    {
        var cache = new Writer();
        cache.FName(NameIndex("None")).U32(4).U32((uint)_records.Count);
        foreach (var record in _records)
        {
            var recordStart = cache.Position;
            var length = 8 + 16 + 4 + record.BeforeTarget.Length + 2 + 4 + record.Container.Length + 4;
            cache.FName(NameIndex(record.Type)).Zeros(16).U32((uint)(start + recordStart + length))
                .Bytes(record.BeforeTarget)
                .Bytes([(byte)record.Platform, (byte)(1 - record.Container[3])]) // frequency: 0 vertex, 1 pixel
                .U32((uint)record.Container.Length).Bytes(record.Container)
                .U32(0xAABBCCDD); // type-specific parameters
        }
        return cache.ToArray();
    }

    private static byte[] ChunkCompressed(byte[] plain, int bodyStart)
    {
        var chunk = Chunk(plain[bodyStart..], littleEndian: false);
        var file = plain[..bodyStart];
        // The chunk table is the summary's last 16 bytes.
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(bodyStart - 4), chunk.Length);
        return [.. file, .. chunk];
    }

    /// <summary>One chunk: {tag, block size, sizes}, block sizes, then literal-only LZO blocks.</summary>
    private static byte[] Chunk(byte[] data, bool littleEndian)
    {
        const int blockSize = 0x20000;
        var blocks = data.Chunk(blockSize).Select(block => (Raw: block, Lzo: LiteralLzo(block))).ToList();
        var w = new Writer(littleEndian);
        // The real disc stores the tag where the block size belongs; readers must not trust it.
        w.U32(0x9E2A83C1).U32(0x9E2A83C1).U32((uint)blocks.Sum(b => b.Lzo.Length)).U32((uint)data.Length);
        foreach (var (raw, lzo) in blocks) w.U32((uint)lzo.Length).U32((uint)raw.Length);
        foreach (var (_, lzo) in blocks) w.Bytes(lzo);
        return w.ToArray();
    }

    /// <summary>An LZO1X stream that stores <paramref name="data"/> (4 bytes or more) as one literal run.</summary>
    public static byte[] LiteralLzo(byte[] data)
    {
        var stream = new List<byte>();
        if (data.Length <= 18) stream.Add((byte)(data.Length - 3));
        else
        {
            var rest = data.Length - 18;
            stream.Add(0);
            for (; rest > 255; rest -= 255) stream.Add(0);
            stream.Add((byte)rest);
        }
        stream.AddRange(data);
        stream.AddRange([0x11, 0, 0]);
        return [.. stream];
    }

    private sealed class Writer(bool littleEndian = false)
    {
        private readonly List<byte> _bytes = [];

        public int Position => _bytes.Count;

        public Writer U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            else BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _bytes.AddRange(b);
            return this;
        }

        public Writer U64(ulong value) => U32((uint)(value >> 32)).U32((uint)value);
        public Writer FName(int index) => U32((uint)index).U32(0);
        public Writer Zeros(int count) => Bytes(new byte[count]);

        public Writer Bytes(byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Writer FString(string text) =>
            U32((uint)(text.Length + 1)).Bytes([.. Encoding.ASCII.GetBytes(text), 0]);

        public byte[] ToArray() => [.. _bytes];
    }
}
