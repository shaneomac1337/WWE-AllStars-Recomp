using System.Buffers.Binary;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// The roster-entry writer over a synthetic <c>CombinedGlobalCharacters.xxx</c>
/// (<see cref="TestRosterPackage"/>): research "Spike 12" for what a filled placeholder is.
/// </summary>
public sealed class RosterWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wwe-roster-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static RosterEntry Entry(params (string Key, string Value)[] properties) =>
        new(120, 110, properties.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void With_no_entries_the_package_is_written_back_byte_for_byte()
    {
        var package = TestRosterPackage.Bytes();

        Assert.Equal(package, RosterWriter.Write(package, []));
    }

    [Fact]
    public void A_compressed_package_is_written_back_unpacked()
    {
        var package = TestRosterPackage.Bytes();
        var lzo = WholeFile(package);

        Assert.Equal(package, RosterWriter.Write(lzo, []));
    }

    [Fact]
    public void A_placeholder_takes_the_templates_properties_and_assets_and_keeps_its_own_legend_id()
    {
        var written = RosterWriter.Write(TestRosterPackage.Bytes(), [Entry(("CharacterName", "Spike Twelve"))]);

        var entry = RosterWriter.ReadEntry(written, 120);
        Assert.Equal("Spike Twelve", entry["CharacterName"]);
        Assert.Equal("TedDibiase", entry["CodeName"]);
        Assert.Equal("CHR110", entry["CharacterCode"]);
        Assert.Equal("Intro_CHR110", entry["IntroCinemaLevelName"]);
        Assert.Equal("3", entry["ClassType"]);
        Assert.Equal("true", entry["bCT_DLC"]);
        Assert.Equal("AnimSet_Million_Dollar_Man.AnimSet_Million_Dollar_Man_AnimSet", entry["AnimSetAsset"]);
        Assert.Equal("CHR_110_A.Meshes.CHR_110_A", entry["CostumesArray.0.Model"]);
        // The placeholder had no audio reference: it is a new sub-object of its own.
        Assert.Equal("Audio_Set_CHR110.Audio_Set_CHR110", entry["AudioAsset"]);
        Assert.Equal("true", entry["LegendId.b20"]);
        Assert.False(entry.ContainsKey("LegendId.b10"));
    }

    [Fact]
    public void The_mods_strings_assets_and_numbers_replace_the_templates_longer_and_shorter()
    {
        var written = RosterWriter.Write(TestRosterPackage.Bytes(),
        [
            Entry(("CharacterName", "A Much Longer Wrestler Name Than Before"), ("CodeName", "Ab"), ("CharacterCode", "CHR120"),
                ("IntroCinemaLevelName", "Intro_CHR120"), ("AnimSetAsset", "AnimSet_X.AnimSet_X_AnimSet"),
                ("AudioAsset", "Audio_Set_CHR120_Brand_New_And_Long.Audio_Set_CHR120_Brand_New_And_Long"),
                ("CostumesArray.0.Model", "CHR_120_A.Meshes.CHR_120_A"), ("nHealth", "75"), ("Scale", "0.98"), ("ClassType", "1")),
        ]);

        var entry = RosterWriter.ReadEntry(written, 120);
        Assert.Equal("A Much Longer Wrestler Name Than Before", entry["CharacterName"]);
        Assert.Equal("Ab", entry["CodeName"]);
        Assert.Equal("CHR120", entry["CharacterCode"]);
        Assert.Equal("Intro_CHR120", entry["IntroCinemaLevelName"]);
        Assert.Equal("AnimSet_X.AnimSet_X_AnimSet", entry["AnimSetAsset"]);
        Assert.Equal("Audio_Set_CHR120_Brand_New_And_Long.Audio_Set_CHR120_Brand_New_And_Long", entry["AudioAsset"]);
        Assert.Equal("CHR_120_A.Meshes.CHR_120_A", entry["CostumesArray.0.Model"]);
        Assert.Equal("75", entry["nHealth"]);
        Assert.Equal("0.98", entry["Scale"]);
        Assert.Equal("1", entry["ClassType"]);
        // The template itself is untouched.
        var template = RosterWriter.ReadEntry(written, 110);
        Assert.Equal("Million Dollar Man", template["CharacterName"]);
        Assert.Equal("AnimSet_Million_Dollar_Man.AnimSet_Million_Dollar_Man_AnimSet", template["AnimSetAsset"]);
        Assert.Equal("true", template["LegendId.b10"]);
    }

    [Fact]
    public void A_placeholder_name_shorter_than_before_shrinks_the_entry()
    {
        var package = TestRosterPackage.Bytes(placeholderName: "A Placeholder Name Longer Than The New One");

        var written = RosterWriter.Write(package, [Entry(("CharacterName", "Al"))]);

        Assert.Equal("Al", RosterWriter.ReadEntry(written, 120)["CharacterName"]);
        Assert.Equal("Million Dollar Man", RosterWriter.ReadEntry(written, 110)["CharacterName"]);
        Assert.True(Assert.Single(BulkFields(written)).Points);
    }

    [Fact]
    public void Later_exports_bulk_data_and_shader_cache_offsets_follow_their_moved_bodies()
    {
        var package = TestRosterPackage.Bytes();
        var written = RosterWriter.Write(package, [Entry(("CharacterName", "A Name Long Enough To Move Every Later Body"))]);

        Assert.NotEqual(package.Length, written.Length);
        var (field, points) = Assert.Single(BulkFields(written));
        Assert.True(points, $"the bulk offset at {field} does not point at its payload");
        // The shader reader walks the cache's record ends; a stale one is refused there.
        File.WriteAllBytes(Path.Combine(_folder, "CombinedGlobalCharacters.xxx"), written);
        Assert.Single(DiscShaders.Read(_folder, null, CancellationToken.None));
    }

    [Fact]
    public void The_written_package_reads_back_with_the_header_counts_fixed()
    {
        var package = TestRosterPackage.Bytes();
        var written = RosterWriter.Write(package, [Entry(("CharacterName", "Spike Twelve"))]);

        // One audio sub-object added: one more export, and the bodies start one entry later.
        Assert.Equal(Int(package, ExportCountAt(package)) + 1, Int(written, ExportCountAt(written)));
        Assert.Equal(Int(package, 8) + 68, Int(written, 8)); // a short export entry is 68 bytes
        // A second write over the written package needs nothing more and changes nothing.
        Assert.Equal(written, RosterWriter.Write(written, []));
    }

    [Fact]
    public void A_template_without_the_property_the_mod_sets_is_an_error()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            RosterWriter.Write(TestRosterPackage.Bytes(), [Entry(("CharacterName", "X"), ("AudioGruntAsset", "A.B"))]));

        Assert.Contains("AudioGruntAsset", error.Message);
    }

    [Fact]
    public void A_template_the_package_does_not_have_is_an_error()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            RosterWriter.Write(TestRosterPackage.Bytes(), [new RosterEntry(120, 121, new Dictionary<string, string> { ["CharacterName"] = "X" })]));

        Assert.Contains("CharacterData_121", error.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_package_is_an_error()
    {
        Assert.Throws<InvalidDataException>(() => RosterWriter.Write([1, 2, 3, 4, 5, 6], []));
    }

    private static int Int(byte[] d, int at) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at));

    /// <summary>The summary's export count: after the folder name, the flags, the name count and offset.</summary>
    private static int ExportCountAt(byte[] d) => 20 + 4 + Int(d, 20) + 12;

    /// <summary>Each bulk offset field (found by the payload after it) and whether it points just past itself.</summary>
    private static List<(int Field, bool Points)> BulkFields(byte[] d)
    {
        var found = new List<(int, bool)>();
        for (var at = d.AsSpan().IndexOf(TestRosterPackage.BulkPayload); at >= 0;)
        {
            found.Add((at - 4, Int(d, at - 4) == at));
            var next = d.AsSpan(at + 1).IndexOf(TestRosterPackage.BulkPayload);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return found;
    }

    /// <summary>A whole-file LZO package as the disc ships it: one chunk of literal-only blocks, little-endian headers.</summary>
    private static byte[] WholeFile(byte[] plain)
    {
        var blocks = plain.Chunk(0x20000).Select(block => (Raw: block, Lzo: TestPackage.LiteralLzo(block))).ToList();
        var header = new List<byte>();
        void U32(int value)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            header.AddRange(b);
        }
        U32(unchecked((int)0x9E2A83C1));
        U32(unchecked((int)0x9E2A83C1));
        U32(blocks.Sum(b => b.Lzo.Length));
        U32(plain.Length);
        foreach (var (raw, lzo) in blocks)
        {
            U32(lzo.Length);
            U32(raw.Length);
        }
        return [.. header, .. blocks.SelectMany(b => b.Lzo)];
    }
}
