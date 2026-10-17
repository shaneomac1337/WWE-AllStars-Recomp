using System.Buffers.Binary;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// A <c>Coalesced.ini</c> or <c>Coalesced.&lt;lang&gt;</c> bundle (modding research §1.3): a
/// big-endian count of strings, then name/body FString pairs, one pair per inner ini. An
/// FString is a big-endian length that counts its NUL, then the text; a negative length means
/// UTF-16LE. A body nobody changed is written back with its original bytes, so a bundle read
/// and written unchanged is the same file.
/// </summary>
public sealed class CoalescedBundle
{
    private readonly List<CoalescedIni> _inis;

    private CoalescedBundle(List<CoalescedIni> inis) => _inis = inis;

    /// <summary>The inner inis in bundle order.</summary>
    public IReadOnlyList<CoalescedIni> Inis => _inis;

    /// <summary>The inner ini with this short name (<c>Xe-Game.ini</c>), ignoring case; null when the bundle has none.</summary>
    public CoalescedIni? Find(string shortName) =>
        _inis.Find(ini => string.Equals(ini.ShortName, shortName, StringComparison.OrdinalIgnoreCase));

    /// <exception cref="InvalidDataException">The bytes are not a whole bundle.</exception>
    public static CoalescedBundle Parse(ReadOnlySpan<byte> bytes)
    {
        var position = 0;
        var count = ReadInt(bytes, ref position);
        if (count < 0 || count % 2 != 0) throw new InvalidDataException($"A Coalesced bundle can't hold {count} strings.");
        var inis = new List<CoalescedIni>(count / 2);
        for (var i = 0; i < count; i += 2)
        {
            var name = FString.Read(bytes, ref position);
            var body = FString.Read(bytes, ref position);
            inis.Add(new CoalescedIni(name, body));
        }
        if (position != bytes.Length) throw new InvalidDataException("A Coalesced bundle has bytes after its last string.");
        return new CoalescedBundle(inis);
    }

    /// <summary>
    /// The inner ini names of a bundle, decoded as <see cref="Parse"/> decodes them. A damaged
    /// bundle gives the names read before the damage.
    /// </summary>
    public static IReadOnlyList<string> ReadNames(ReadOnlySpan<byte> bytes)
    {
        var names = new List<string>();
        try
        {
            var position = 0;
            var count = ReadInt(bytes, ref position);
            for (var i = 0; i + 1 < count; i += 2)
            {
                names.Add(FString.Read(bytes, ref position).Text);
                FString.Read(bytes, ref position);
            }
        }
        catch (InvalidDataException)
        {
            // Keep what was read before the damage.
        }
        return names;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, _inis.Count * 2);
        stream.Write(buffer);
        foreach (var ini in _inis)
        {
            stream.Write(ini.NameString.Bytes);
            stream.Write(ini.BodyString.Bytes);
        }
        return stream.ToArray();
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, ref int position)
    {
        if (bytes.Length - position < 4) throw new InvalidDataException("A Coalesced bundle ends inside a length.");
        var value = BinaryPrimitives.ReadInt32BigEndian(bytes[position..]);
        position += 4;
        return value;
    }

    /// <summary>One serialised FString: its text, and the bytes it was read from or encoded to.</summary>
    internal sealed record FString(string Text, byte[] Bytes, FStringEncoding Encoding)
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

        public static FString Read(ReadOnlySpan<byte> bytes, ref int position)
        {
            var start = position;
            var length = ReadInt(bytes, ref position);
            if (length == 0) return new FString("", bytes[start..position].ToArray(), FStringEncoding.Utf8);
            var wide = length < 0;
            var size = wide ? -(long)length * 2 : length;
            if (bytes.Length - position < size) throw new InvalidDataException("A Coalesced bundle ends inside a string.");
            var text = bytes.Slice(position, (int)size);
            position += (int)size;
            var raw = bytes[start..position].ToArray();
            if (wide) return new FString(System.Text.Encoding.Unicode.GetString(text[..^2]), raw, FStringEncoding.Utf16);
            try
            {
                return new FString(StrictUtf8.GetString(text[..^1]), raw, FStringEncoding.Utf8);
            }
            catch (DecoderFallbackException)
            {
                return new FString(System.Text.Encoding.Latin1.GetString(text[..^1]), raw, FStringEncoding.Latin1);
            }
        }

        /// <summary>This string with new text, encoded the way it was read.</summary>
        public FString With(string text)
        {
            if (text == Text) return this;
            var encoded = Encoding switch
            {
                FStringEncoding.Utf16 => System.Text.Encoding.Unicode.GetBytes(text + "\0"),
                FStringEncoding.Latin1 => System.Text.Encoding.Latin1.GetBytes(text + "\0"),
                _ => System.Text.Encoding.UTF8.GetBytes(text + "\0"),
            };
            var bytes = new byte[4 + encoded.Length];
            BinaryPrimitives.WriteInt32BigEndian(bytes,
                Encoding == FStringEncoding.Utf16 ? -(encoded.Length / 2) : encoded.Length);
            encoded.CopyTo(bytes, 4);
            return new FString(text, bytes, Encoding);
        }
    }

    /// <summary>
    /// How a string's text is stored. The game writes 8-bit text, mostly UTF-8 and once Latin-1,
    /// and a few strings as UTF-16LE; an edited string keeps its kind.
    /// </summary>
    internal enum FStringEncoding
    {
        Utf8,
        Latin1,
        Utf16,
    }
}

/// <summary>One inner ini of a <see cref="CoalescedBundle"/>: its path in the bundle and its text.</summary>
public sealed class CoalescedIni
{
    internal CoalescedIni(CoalescedBundle.FString name, CoalescedBundle.FString body)
    {
        NameString = name;
        BodyString = body;
    }

    internal CoalescedBundle.FString NameString { get; }
    internal CoalescedBundle.FString BodyString { get; private set; }

    /// <summary>The path the bundle files it under, such as <c>..\Game\Config\Xenon\Cooked\Xe-Game.ini</c>.</summary>
    public string Name => NameString.Text;

    /// <summary>The file name alone (<c>Xe-Game.ini</c>), the name a mod's <c>config/</c> uses.</summary>
    public string ShortName => Name[(Name.LastIndexOf('\\') + 1)..];

    public string Body
    {
        get => BodyString.Text;
        set => BodyString = BodyString.With(value);
    }
}
