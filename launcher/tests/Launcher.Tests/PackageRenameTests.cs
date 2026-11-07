using System.Buffers.Binary;
using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// A package made as another slot's package (such as <c>Arena_005_Ring</c> under
/// <c>Arena_003_Ring.xxx</c>) is renamed when the names are the same length and only its own
/// name entry holds the old prefix, such as <c>Arena_005</c> (package-rename spec, Solution 1-2).
/// </summary>
public sealed class PackageRenameTests
{
    private static readonly KnownGameFiles Known = new(["Arena_003_Ring.xxx", "Arena_005_Ring.xxx", "CHR_101_B.xxx"]);

    private static RenamedPackage? Check(byte[] package, string fileName)
    {
        using var stream = new MemoryStream(package);
        return PackageOrigin.Check(stream, fileName, Known.IsPackage);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Chunks")]
    [InlineData("TwoChunks")]
    public void A_package_made_as_another_slot_of_the_same_length_is_renamable(string compression)
    {
        var package = new TestPackage { Compressed = Enum.Parse<TestPackage.Compression>(compression), MadeAs = "Arena_005_Ring" }.Bytes();

        var renamed = Check(package, "Arena_003_Ring.xxx");

        Assert.NotNull(renamed);
        Assert.Equal("Arena_005_Ring.xxx", renamed.MadeAs);
        Assert.Null(renamed.CannotRename);
        Assert.True(renamed.Renamable);
    }

    [Fact]
    public void Names_of_different_lengths_are_not_renamable()
    {
        var package = new TestPackage { MadeAs = "Arena_005_Ring" }.Bytes();

        var renamed = Check(package, "Arena_03_Ring.xxx");

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "names of different lengths can't be renamed"), renamed);
        Assert.False(renamed!.Renamable);
    }

    [Fact]
    public void Another_entry_naming_the_old_prefix_is_named_in_the_refusal()
    {
        var package = new TestPackage { MadeAs = "Arena_005_Ring", ExtraNames = ["Arena_001_FX", "Arena_005_FX"] }.Bytes();

        var renamed = Check(package, "Arena_003_Ring.xxx");

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "its name table also names Arena_005 in Arena_005_FX"), renamed);
    }

    [Fact]
    public void An_entry_that_shares_only_the_unchanged_start_is_no_obstacle()
    {
        var package = new TestPackage { MadeAs = "Arena_005_Ring", ExtraNames = ["Arena_00", "Ring"] }.Bytes();

        Assert.True(Check(package, "Arena_003_Ring.xxx")!.Renamable);
    }

    [Fact]
    public void A_package_that_already_names_the_new_slot_is_not_renamable()
    {
        var package = new TestPackage { MadeAs = "Arena_005_Ring", ExtraNames = ["Arena_003_Ring"] }.Bytes();

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "it already names Arena_003_Ring"), Check(package, "Arena_003_Ring.xxx"));
    }

    [Fact]
    public void A_package_compressed_as_a_whole_is_not_renamable()
    {
        var package = new TestPackage { Compressed = TestPackage.Compression.WholeFile, MadeAs = "Arena_005_Ring" }.Bytes();

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "packages compressed as a whole can't be renamed"),
            Check(package, "Arena_003_Ring.xxx"));
    }

    /// <summary>A two-chunk package made as <c>Arena_005_Ring</c> with its second chunk's header edited; the check reads only the first to read the tables.</summary>
    private static byte[] WithSecondChunk(Action<Span<byte>> edit)
    {
        var package = new TestPackage { Compressed = TestPackage.Compression.TwoChunks, MadeAs = "Arena_005_Ring" }.Bytes();
        edit(package.AsSpan(Chunks(package)[1].CompressedOffset));
        return package;
    }

    [Fact]
    public void A_chunk_without_its_tag_is_not_renamable()
    {
        var package = WithSecondChunk(header => header[..4].Clear());

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "a compressed chunk has no tag"), Check(package, "Arena_003_Ring.xxx"));
    }

    [Fact]
    public void A_chunk_whose_header_and_chunk_table_disagree_on_its_size_is_not_renamable()
    {
        var package = new TestPackage { Compressed = TestPackage.Compression.TwoChunks, MadeAs = "Arena_005_Ring" }.Bytes();
        var second = Chunks(package)[1];
        BinaryPrimitives.WriteInt32BigEndian(package.AsSpan(second.TableAt + 4), second.Size + 1);

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "a compressed chunk's size differs from the chunk table"),
            Check(package, "Arena_003_Ring.xxx"));
    }

    [Fact]
    public void A_chunk_whose_block_sizes_run_past_its_size_is_not_renamable()
    {
        var package = WithSecondChunk(header => BinaryPrimitives.WriteInt32BigEndian(header[20..], BinaryPrimitives.ReadInt32BigEndian(header[20..]) + 1));

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "a compressed chunk's blocks run past its size"),
            Check(package, "Arena_003_Ring.xxx"));
    }

    [Fact]
    public void A_chunk_cut_short_is_not_renamable()
    {
        var package = WithSecondChunk(_ => { })[..^10];

        Assert.Equal(new RenamedPackage("Arena_005_Ring.xxx", "a compressed chunk ends early"), Check(package, "Arena_003_Ring.xxx"));
    }

    [Fact]
    public void A_chunked_package_not_compressed_with_LZO_is_damaged()
    {
        var package = new TestPackage { Compressed = TestPackage.Compression.Chunks, MadeAs = "Arena_005_Ring" }.Bytes();
        var compression = Chunks(package)[0].TableAt - 8;
        BinaryPrimitives.WriteInt32BigEndian(package.AsSpan(compression), 1);

        Assert.Throws<InvalidDataException>(() => Check(package, "Arena_003_Ring.xxx"));
    }

    private static byte[] Rename(byte[] package, string madeAs, string fileName)
    {
        using var input = new MemoryStream(package);
        using var output = new MemoryStream();
        PackageRename.Write(input, madeAs, fileName, output);
        return output.ToArray();
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Chunks")]
    [InlineData("TwoChunks")]
    public void The_renamed_package_is_the_new_slots_and_expands_to_the_input_with_only_the_name_changed(string compression)
    {
        var package = new TestPackage
        {
            Compressed = Enum.Parse<TestPackage.Compression>(compression), MadeAs = "Arena_005_Ring", ExtraNames = ["Arena_00", "Ring"],
        }.Bytes();

        var renamed = Rename(package, "Arena_005_Ring.xxx", "Arena_003_Ring.xxx");

        Assert.Null(Check(renamed, "Arena_003_Ring.xxx"));
        Assert.Equal(Renamed(Expand(package), "Arena_005_Ring", "Arena_003_Ring"), Expand(renamed));
    }

    [Fact]
    public void A_chunk_of_several_blocks_keeps_its_block_sizes()
    {
        // 300 KB of texture data spreads the body over three 128 KB blocks.
        var bulk = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
        var package = new TestPackage { Compressed = TestPackage.Compression.Chunks, MadeAs = "Arena_005_Ring" }
            .Add(TestPackage.Container(vertex: false, bulk)).Bytes();

        var renamed = Rename(package, "Arena_005_Ring.xxx", "Arena_003_Ring.xxx");

        Assert.Equal(Blocks(package), Blocks(renamed));
        Assert.Equal(3, Blocks(package).Count);
        Assert.Equal(Renamed(Expand(package), "Arena_005_Ring", "Arena_003_Ring"), Expand(renamed));
    }

    [Theory]
    [InlineData("Arena_005_Cin_Entrance.xxx")]
    [InlineData("Arena_005_Cin_Ring.xxx")]
    [InlineData("Arena_005_Cin_Ropes.xxx")]
    [InlineData("Arena_005_Entrance.xxx")]
    [InlineData("Arena_005_Ring.xxx")]
    public void The_discs_SmackDown_arena_sublevels_rename_to_the_All_Stars_slot(string package)
    {
        var path = Path.Combine(Disc, package);
        Assert.SkipUnless(File.Exists(path), "the disc's CookedXenon is not in assets/");
        var known = new KnownGameFiles(Directory.EnumerateFiles(Disc, "*.xxx").Select(Path.GetFileName).OfType<string>());
        var target = package.Replace("Arena_005", "Arena_003", StringComparison.Ordinal);
        var bytes = File.ReadAllBytes(path);

        using (var stream = new MemoryStream(bytes))
        {
            var check = PackageOrigin.Check(stream, target, known.IsPackage);
            Assert.Equal(package, check?.MadeAs, ignoreCase: true);
            Assert.True(check!.Renamable, check.CannotRename);
        }
        var renamed = Rename(bytes, package, target);

        using (var stream = new MemoryStream(renamed)) Assert.Null(PackageOrigin.Check(stream, target, known.IsPackage));
        var oldName = Path.GetFileNameWithoutExtension(package);
        Assert.Equal(Renamed(Expand(bytes), oldName, Path.GetFileNameWithoutExtension(target)), Expand(renamed));
        Assert.Equal(Blocks(bytes), Blocks(renamed));
    }

    private static readonly string Disc = FindDisc();

    /// <summary>The extracted disc's packages (gitignored), found above the tests, a worktree's checkout included.</summary>
    private static string FindDisc()
    {
        for (var folder = AppContext.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
            if (Directory.Exists(Path.Combine(folder, "assets", "Game", "CookedXenon"))) return Path.Combine(folder, "assets", "Game", "CookedXenon");
        return Path.Combine(AppContext.BaseDirectory, "assets", "Game", "CookedXenon");
    }

    /// <summary><paramref name="expanded"/> with its one FString <paramref name="from"/> (any case) made <paramref name="to"/>.</summary>
    private static byte[] Renamed(byte[] expanded, string from, string to)
    {
        byte[] Entry(string name) => [.. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(name.Length + 1)), .. Encoding.ASCII.GetBytes(name), 0];
        var upper = expanded.Select(b => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b).ToArray();        var at = upper.AsSpan().IndexOf(Entry(from.ToUpperInvariant()));
        Assert.True(at >= 0, $"{from} is in the package");
        Assert.Equal(-1, upper.AsSpan(at + 1).IndexOf(Entry(from.ToUpperInvariant())));
        var result = expanded.ToArray();
        Entry(to).CopyTo(result, at);
        return result;
    }

    internal sealed record Chunk(int Offset, int Size, int CompressedOffset, int CompressedSize, int TableAt);

    /// <summary>The summary's chunk table of a big-endian package (empty for a plain one), read from the file's start.</summary>
    internal static List<Chunk> Chunks(byte[] file)
    {
        int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
        var p = 20;
        p += 4 + I32(p); // folder name, an ASCII FString
        p += 7 * 4 + 16; // flags, table counts and offsets, GUID
        p += 4 + I32(p) * 12 + 8; // generations, engine and cooker version
        var count = I32(p + 4);
        p += 8;
        return [.. Enumerable.Range(0, count).Select(i => p + i * 16).Select(at => new Chunk(I32(at), I32(at + 4), I32(at + 8), I32(at + 12), at))];
    }

    /// <summary>Each chunk's header word that repeats the tag, and its blocks' uncompressed sizes.</summary>
    private static List<(int Chunk, int RepeatedTag, int Size)> Blocks(byte[] file)
    {
        var blocks = new List<(int, int, int)>();
        foreach (var (chunk, index) in Chunks(file).Select((chunk, index) => (chunk, index)))
        {
            int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(chunk.CompressedOffset + at));
            for (int p = 16, sum = 0; sum < I32(12); p += 8, sum += I32(p - 4)) blocks.Add((index, I32(4), I32(p + 4)));
        }
        return blocks;
    }

    /// <summary>
    /// The package as the game reads it: the file's start up to the first chunk, then each chunk
    /// expanded at its offset. The chunk table's compressed offsets and sizes are zeroed where
    /// they fall in that start (the disc's run past it), as only they may change.
    /// </summary>
    private static byte[] Expand(byte[] file)
    {
        var chunks = Chunks(file);
        if (chunks.Count == 0) return file;
        var data = new byte[chunks.Max(chunk => chunk.Offset + chunk.Size)];
        var start = chunks.Min(chunk => chunk.Offset);
        file.AsSpan(0, start).CopyTo(data);
        foreach (var chunk in chunks)
        {
            if (chunk.TableAt + 8 < start) data.AsSpan(chunk.TableAt + 8, Math.Min(8, start - chunk.TableAt - 8)).Clear();
            int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            var total = I32(chunk.CompressedOffset + 12);
            var sizes = new List<(int Compressed, int Size)>();
            var p = chunk.CompressedOffset + 16;
            for (var sum = 0; sum < total; p += 8, sum += sizes[^1].Size) sizes.Add((I32(p), I32(p + 4)));
            var at = chunk.Offset;
            foreach (var (compressed, size) in sizes)
            {
                Assert.Equal(size, Lzo1x.Decompress(file.AsSpan(p, compressed), data.AsSpan(at, size)));
                (p, at) = (p + compressed, at + size);
            }
            Assert.Equal(chunk.Offset + chunk.Size, at);
        }
        return data;
    }
}
