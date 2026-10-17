using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// A small <c>CombinedGlobalCharacters.xxx</c> in the layout research "Spike 12" found: summary,
/// names, imports, exports, then every body back to back. It holds a complete template entry
/// (110, with an audio reference and <c>CodeName</c>), a placeholder (120, without them), their
/// <c>SD_StringAssetReference</c> sub-objects, a texture with inline bulk data and a shader cache
/// after them, so a size change has absolute offsets to fix up.
/// </summary>
internal static class TestRosterPackage
{
    public static readonly byte[] BulkPayload = [0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5];

    private static readonly string[] Names =
    [
        "None", "Core", "Class", "Package", "Engine", "GameCharacterData", "CharacterData", "SD_StringAssetReference",
        "m_AssetName", "StrProperty", "IntProperty", "FloatProperty", "ByteProperty", "BoolProperty", "ObjectProperty",
        "StructProperty", "ArrayProperty", "CharacterIdentifier", "CharacterName", "CodeName", "CharacterCode",
        "IntroCinemaLevelName", "ClassType", "nHealth", "Scale", "SkeletalMeshAsset", "AnimSetAsset", "AudioAsset",
        "CostumesArray", "Model", "LegendId", "b10", "b20", "bCT_DLC", "Texture2D", "SomeTexture", "ShaderCache",
        "SeekFreeShaderCache", "FSD_UberPixelShader",
        // Its own name, which a package made under that name holds (ticket 20).
        "CombinedGlobalCharacters",
    ];

    private static int N(string name)
    {
        var index = Array.IndexOf(Names, name);
        return index >= 0 ? index : throw new ArgumentException("Add the name to TestRosterPackage.Names: " + name);
    }

    // Imports (negative class indices): 1 Package, 2 GameCharacterData, 3 SD_StringAssetReference, 4 Texture2D, 5 ShaderCache.
    private static readonly string[] Imports = ["Package", "GameCharacterData", "SD_StringAssetReference", "Texture2D", "ShaderCache"];

    private sealed record Export(int Class, int Outer, string Name, int Number, byte[] Body, bool LongEntry = false);

    /// <summary>
    /// The package; <paramref name="placeholderName"/> is 120's <c>CharacterName</c>, <paramref name="templateName"/>
    /// 110's and <paramref name="templateCode"/> 110's <c>CodeName</c>.
    /// </summary>
    public static byte[] Bytes(string placeholderName = "Undefined", string templateName = "Million Dollar Man", string templateCode = "TedDibiase")
    {
        // Exports, 1-based: 1 the GameCharacterData group, 2 entry 110, 3 entry 120, then sub-objects.
        var exports = new List<Export>
        {
            new(-1, 0, "GameCharacterData", 0, Props(w => w)),
        };
        exports.Add(new(-2, 1, "CharacterData", 111, []));
        exports.Add(new(-2, 1, "CharacterData", 121, []));
        int Sub(int outer, int number, string asset)
        {
            exports.Add(new(-3, outer, "SD_StringAssetReference", number, Props(w => w.Str("m_AssetName", asset))));
            return exports.Count;
        }
        var t1 = Sub(2, 31, "AnimSet_Million_Dollar_Man.AnimSet_Million_Dollar_Man_AnimSet");
        var t2 = Sub(2, 11, "Audio_Set_CHR110.Audio_Set_CHR110");
        var t3 = Sub(2, 28, "CHR_110_A.Meshes.CHR_110_A");
        var t4 = Sub(2, 313, "CHR_Base_Skeleton.Meshes.CHR_Base_Skeleton_Tpose");
        var p1 = Sub(3, 91, "AnimSet_Template.AnimSet_Template_AnimSet");
        var p2 = Sub(3, 4, "CHR_120_A.Meshes.CHR_120_A");
        var p3 = Sub(3, 323, "CHR_Base_Skeleton.Meshes.CHR_Base_Skeleton_Tpose");

        exports[1] = exports[1] with
        {
            Body = Props(w => w
                .Obj("AudioAsset", t2).Obj("SkeletalMeshAsset", t4).Float("Scale", 1.023f).Obj("AnimSetAsset", t1)
                .Str("CharacterName", templateName).Byte("ClassType", 3).Str("IntroCinemaLevelName", "Intro_CHR110")
                .Array("CostumesArray", 1, e => e.Obj("Model", t3)).Int("nHealth", 99)
                .Str("CodeName", templateCode).Str("CharacterCode", "CHR110").Bool("bCT_DLC", true)
                .Struct("LegendId", "CharacterIdentifier", s => s.Bool("b10", true))),
        };
        exports[2] = exports[2] with
        {
            Body = Props(w => w
                .Obj("SkeletalMeshAsset", p3).Float("Scale", 1.036f).Obj("AnimSetAsset", p1)
                .Str("CharacterName", placeholderName).Byte("ClassType", 2).Str("IntroCinemaLevelName", "Intro_CHR120")
                .Array("CostumesArray", 1, e => e.Obj("Model", p2)).Int("nHealth", 99).Str("CharacterCode", "CHR120")
                .Struct("LegendId", "CharacterIdentifier", s => s.Bool("b20", true))),
        };
        exports.Add(new(-4, 0, "SomeTexture", 0, [], LongEntry: true)); // bulk data: body built once its offset is known
        exports.Add(new(-5, 0, "SeekFreeShaderCache", 0, []));

        var summary = new W();
        summary.U32(0x9E2A83C1).U32(0x0025018E).U32(0).U32(0x20414E54).U32(0x0F).FString("None");
        var counts = summary.Position;
        summary.Zeros(7 * 4).Zeros(16).U32(1);
        var generation = summary.Position;
        summary.Zeros(12).U32(0xA2D).U32(0x21).U32(0).U32(0);

        var file = summary;
        var nameOffset = file.Position;
        foreach (var name in Names) file.FString(name).U32(0).U32(0x00070010);
        var importOffset = file.Position;
        foreach (var name in Imports) file.FName(N("Core")).FName(N("Class")).U32(0).FName(N(name));
        var exportOffset = file.Position;
        var tableSize = exports.Sum(export => export.LongEntry ? 40 + 4 + 12 + 4 + 4 + 8 + 16 : 40 + 4 + 4 + 4 + 16);
        var bodyStart = exportOffset + tableSize;

        // The last two bodies need their own file offsets.
        var at = bodyStart + exports.Take(exports.Count - 2).Sum(export => export.Body.Length);
        exports[^2] = exports[^2] with { Body = Texture(at) };
        at += exports[^2].Body.Length;
        exports[^1] = exports[^1] with { Body = ShaderCache(at) };

        var offset = bodyStart;
        foreach (var export in exports)
        {
            file.U32((uint)export.Class).U32(0).U32((uint)export.Outer).FName(N(export.Name), export.Number).U32(0).U64(0x000F0004)
                .U32((uint)export.Body.Length).U32((uint)offset);
            if (export.LongEntry) file.U32(1).FName(N("None")).U32(7).U32(0).U32(2).U32(0).U32(0);
            else file.U32(0).U32(0).U32(0);
            file.Zeros(16);
            offset += export.Body.Length;
        }
        if (file.Position != bodyStart) throw new InvalidOperationException("export table size");
        foreach (var export in exports) file.Bytes(export.Body);

        var bytes = file.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), bodyStart);
        var c = counts;
        foreach (var value in new[] { 0, Names.Length, nameOffset, exports.Count, exportOffset, Imports.Length, importOffset })
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(c), value);
            c += 4;
        }
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(generation), exports.Count);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(generation + 4), Names.Length);
        return bytes;
    }

    /// <summary>
    /// A texture body: a few properties' worth of bytes, then inline bulk data {flags, count,
    /// sizeOnDisk, offsetInFile} whose offset points just past itself, then the payload.
    /// </summary>
    private static byte[] Texture(int start)
    {
        var w = new W();
        w.FName(N("None")).U32(0x11223344);
        w.U32(0).U32((uint)BulkPayload.Length).U32((uint)BulkPayload.Length);
        w.U32((uint)(start + w.Position + 4)).Bytes(BulkPayload).U32(0);
        return w.ToArray();
    }

    /// <summary>A shader cache body: one record with its end offset, then one material map with its skip offset.</summary>
    private static byte[] ShaderCache(int start)
    {
        var container = new byte[36 + 8];
        BinaryPrimitives.WriteUInt32BigEndian(container, 0x102A1100u);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(4), 36);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(8), 8);
        var w = new W();
        w.FName(N("None")).U32(4).U32(1);
        var record = w.Position;
        var length = 8 + 16 + 4 + 2 + 4 + container.Length + 4;
        w.FName(N("FSD_UberPixelShader")).Zeros(16).U32((uint)(start + record + length))
            .Bytes([2, 1]).U32((uint)container.Length).Bytes(container).U32(0xAABBCCDD);
        var map = w.Position + 4;
        w.U32(1).Zeros(16).U32((uint)(start + map + 16 + 4 + 4)).U32(0x5EED);
        return w.ToArray();
    }

    private static byte[] Props(Func<P, P> build) => build(new P()).None().ToArray();

    /// <summary>Tagged properties: {name, type, size, array index}, then the value.</summary>
    private sealed class P : W
    {
        private P Tag(string name, string type, int size)
        {
            FName(N(name)).FName(N(type)).U32((uint)size).U32(0);
            return this;
        }

        public P None()
        {
            FName(N("None"));
            return this;
        }

        public P Str(string name, string value)
        {
            Tag(name, "StrProperty", value.Length + 5).FString(value);
            return this;
        }

        public P Int(string name, int value)
        {
            Tag(name, "IntProperty", 4).U32((uint)value);
            return this;
        }

        public P Float(string name, float value)
        {
            Tag(name, "FloatProperty", 4).U32(BitConverter.SingleToUInt32Bits(value));
            return this;
        }

        public P Byte(string name, byte value)
        {
            Tag(name, "ByteProperty", 1).Bytes([value]);
            return this;
        }

        public P Bool(string name, bool value)
        {
            Tag(name, "BoolProperty", 0).U32(value ? 1u : 0u);
            return this;
        }

        public P Obj(string name, int index)
        {
            Tag(name, "ObjectProperty", 4).U32((uint)index);
            return this;
        }

        public P Struct(string name, string structName, Func<P, P> fields)
        {
            var inner = fields(new P()).None().ToArray();
            Tag(name, "StructProperty", inner.Length).FName(N(structName)).Bytes(inner);
            return this;
        }

        public P Array(string name, int count, Func<P, P> element)
        {
            var inner = new W().U32((uint)count);
            for (var i = 0; i < count; i++) inner.Bytes(element(new P()).None().ToArray());
            var bytes = inner.ToArray();
            Tag(name, "ArrayProperty", bytes.Length).Bytes(bytes);
            return this;
        }
    }

    private class W
    {
        private readonly List<byte> _bytes = [];

        public int Position => _bytes.Count;

        public W U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _bytes.AddRange(b);
            return this;
        }

        public W U64(ulong value) => U32((uint)(value >> 32)).U32((uint)value);
        public W FName(int index, int number = 0) => U32((uint)index).U32((uint)number);
        public W Zeros(int count) => Bytes(new byte[count]);

        public W Bytes(byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public W FString(string text) => U32((uint)(text.Length + 1)).Bytes([.. Encoding.ASCII.GetBytes(text), 0]);

        public byte[] ToArray() => [.. _bytes];
    }
}
