namespace WweAllStars.Launcher.Core;

/// <summary>
/// LZO1X decompression, written from the format description (liblzo2 and minilzo are GPL,
/// ADR-0007). The game's cooked packages compress their blocks with it
/// (docs/research/shader-containers-from-disc.md). <see cref="StoreLiterals"/> writes valid
/// streams with no compression, for renamed packages (package-rename spec).
/// </summary>
public static class Lzo1x
{
    /// <summary>
    /// An LZO1X stream that holds <paramref name="data"/> as one literal run, then the end marker:
    /// a first byte of 17 + n for up to 238 bytes, else a long literal instruction.
    /// </summary>
    public static byte[] StoreLiterals(ReadOnlySpan<byte> data)
    {
        var stream = new List<byte>(data.Length + 3 + data.Length / 255 + 3);
        if (data.Length is > 0 and <= 238) stream.Add((byte)(17 + data.Length));
        else if (data.Length > 238)
        {
            // Instruction 0: 18 literals plus a length extended by zero bytes of 255 each.
            stream.Add(0);
            var rest = data.Length - 18;
            for (; rest > 255; rest -= 255) stream.Add(0);
            stream.Add((byte)rest);
        }
        stream.AddRange(data);
        stream.AddRange([0x11, 0, 0]);
        return [.. stream];
    }

    /// <summary>Decodes <paramref name="input"/> into <paramref name="output"/> and returns the bytes written.</summary>
    /// <exception cref="InvalidDataException">The stream is damaged or does not fit the output.</exception>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var ip = 0;
        var op = 0;
        // What the previous instruction left: 0 = an instruction follows, 1-3 = that many
        // literals were copied after a match, 4 = a literal run of four or more was copied.
        var state = 0;

        if (Byte(input, ref ip) is var first && first > 17)
        {
            var count = first - 17;
            Literals(input, ref ip, output, ref op, count);
            state = count < 4 ? count : 4;
        }
        else ip = 0;

        while (true)
        {
            var t = Byte(input, ref ip);
            int length, distance, next;
            if (t < 16)
            {
                if (state == 0)
                {
                    if (t == 0) t = Extended(input, ref ip, 15);
                    Literals(input, ref ip, output, ref op, t + 3);
                    state = 4;
                    continue;
                }
                next = t & 3;
                // After a long literal run a short instruction is a 3-byte match beyond 0x800.
                distance = 1 + (t >> 2) + (Byte(input, ref ip) << 2) + (state == 4 ? 0x800 : 0);
                length = state == 4 ? 3 : 2;
            }
            else if (t >= 64)
            {
                next = t & 3;
                distance = 1 + ((t >> 2) & 7) + (Byte(input, ref ip) << 3);
                length = (t >> 5) + 1;
            }
            else if (t >= 32)
            {
                length = t & 31;
                if (length == 0) length = Extended(input, ref ip, 31);
                var word = Word(input, ref ip);
                distance = 1 + (word >> 2);
                next = word & 3;
                length += 2;
            }
            else
            {
                length = t & 7;
                if (length == 0) length = Extended(input, ref ip, 7);
                var word = Word(input, ref ip);
                distance = ((t & 8) << 11) + (word >> 2);
                if (distance == 0) return op; // the end marker
                distance += 0x4000;
                next = word & 3;
                length += 2;
            }

            if (distance > op || length > output.Length - op)
                throw new InvalidDataException("The LZO stream refers outside its output.");
            // Byte by byte: a match may overlap the bytes it is writing.
            for (var i = 0; i < length; i++, op++) output[op] = output[op - distance];
            if (next > 0) Literals(input, ref ip, output, ref op, next);
            state = next;
        }
    }

    private static int Byte(ReadOnlySpan<byte> input, ref int ip)
    {
        if (ip >= input.Length) throw Truncated();
        return input[ip++];
    }

    private static int Word(ReadOnlySpan<byte> input, ref int ip) => Byte(input, ref ip) | Byte(input, ref ip) << 8;

    /// <summary>A length that did not fit its field: zero bytes add 255 each, then a final byte.</summary>
    private static int Extended(ReadOnlySpan<byte> input, ref int ip, int fieldMax)
    {
        var length = 0;
        int b;
        while ((b = Byte(input, ref ip)) == 0)
        {
            length += 255;
            if (length > int.MaxValue / 2) throw new InvalidDataException("The LZO stream holds an impossible length.");
        }
        return length + fieldMax + b;
    }

    private static void Literals(ReadOnlySpan<byte> input, ref int ip, Span<byte> output, ref int op, int count)
    {
        if (count > input.Length - ip) throw Truncated();
        if (count > output.Length - op) throw new InvalidDataException("The LZO stream is longer than its output.");
        input.Slice(ip, count).CopyTo(output[op..]);
        ip += count;
        op += count;
    }

    private static InvalidDataException Truncated() => new("The LZO stream ends early.");
}
