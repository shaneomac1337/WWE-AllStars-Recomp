using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// Reads big-endian words and FStrings from an unpacked package. <see cref="PackageBytes"/>
/// reads a package held whole; <see cref="PackageOrigin"/> expands a file only as far as it reads.
/// </summary>
internal interface IPackageReader
{
    int I32(ref int p);

    /// <summary>An FString: i32 length including the NUL, then the characters; negative means UTF-16.</summary>
    string FString(ref int p);
}

/// <summary>A package held whole in memory. Out-of-range reads throw <see cref="ArgumentException"/>, as the callers expect.</summary>
internal readonly struct PackageBytes(byte[] data) : IPackageReader
{
    public int I32(ref int p)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p, 4));
        p += 4;
        return value;
    }

    public string FString(ref int p)
    {
        var length = I32(ref p);
        string text;
        if (length >= 0)
        {
            text = Encoding.Latin1.GetString(data, p, Math.Max(0, length - 1));
            p += length;
        }
        else
        {
            text = Encoding.BigEndianUnicode.GetString(data, p, Math.Max(0, -2 * length - 2));
            p += -2 * length;
        }
        return text;
    }
}

/// <summary>
/// The start of a cooked UE3 package's summary (file version 398, licensee 0x25, big-endian)
/// that every reader of its tables needs: the package flags, and the name, export and import
/// tables' counts and offsets. The counts are as the file gives them; each reader checks them.
/// </summary>
internal readonly record struct Ue3Summary(
    int FlagsAt, int Flags, int NameCount, int NameOffset, int ExportCount, int ExportOffset, int ImportCount, int ImportOffset)
{
    /// <summary>Reads the summary after the tag, version, header size and folder name.</summary>
    public static Ue3Summary Read<T>(T data) where T : IPackageReader
    {
        var p = 20;
        data.FString(ref p); // folder name
        var flagsAt = p;
        return new Ue3Summary(flagsAt, data.I32(ref p), data.I32(ref p), data.I32(ref p), data.I32(ref p), data.I32(ref p),
            data.I32(ref p), data.I32(ref p));
    }

    /// <summary>
    /// The name table: <paramref name="count"/> FStrings, each followed by 8 bytes of flags.
    /// <paramref name="end"/> is where the table ends.
    /// </summary>
    public static List<string> Names<T>(T data, int count, int offset, out int end) where T : IPackageReader
    {
        var names = new List<string>(Math.Clamp(count, 0, 4096));
        var p = offset;
        for (var i = 0; i < count; i++)
        {
            names.Add(data.FString(ref p));
            p += 8; // flags
        }
        end = p;
        return names;
    }
}
