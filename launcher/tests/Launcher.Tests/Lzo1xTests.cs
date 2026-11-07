using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>Hand-made LZO1X streams, decoded by hand from the format description.</summary>
public sealed class Lzo1xTests
{
    private static string Decode(byte[] stream, int size)
    {
        var output = new byte[size];
        var written = Lzo1x.Decompress(stream, output);
        return Encoding.ASCII.GetString(output, 0, written);
    }

    [Fact]
    public void A_literal_run_then_the_end_marker()
    {
        byte[] stream = [0, 1, .. "abcdefghijklmnopqrs"u8, 0x11, 0, 0]; // 15 + 1 + 3 = 19 literals

        Assert.Equal("abcdefghijklmnopqrs", Decode(stream, 19));
    }

    [Fact]
    public void A_short_match_copies_overlapping_bytes()
    {
        // First byte 17 + 3: three literals; 0xE8: copy 8 bytes from 3 back.
        byte[] stream = [20, .. "abc"u8, 0xE8, 0x00, 0x11, 0, 0];

        Assert.Equal("abcabcabcab", Decode(stream, 11));
    }

    [Fact]
    public void A_medium_match_then_trailing_literals()
    {
        // 12 literals; 35: copy 5 bytes from 12 back (offset field 11), then 2 literals.
        byte[] stream = [29, .. "hello world "u8, 35, 11 << 2 | 2, 0x00, .. "!!"u8, 0x11, 0, 0];

        Assert.Equal("hello world hello!!", Decode(stream, 19));
    }

    [Fact]
    public void A_stream_that_points_before_the_output_is_refused()
    {
        byte[] stream = [20, .. "abc"u8, 0xFC, 0xFF, 0x11, 0, 0];

        Assert.Throws<InvalidDataException>(() => Decode(stream, 64));
    }

    [Fact]
    public void A_stream_that_ends_early_is_refused()
    {
        byte[] stream = [29, .. "hello"u8];

        Assert.Throws<InvalidDataException>(() => Decode(stream, 64));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(238)]
    [InlineData(239)]
    [InlineData(273)] // 18 + 255: the length's one byte is full
    [InlineData(274)]
    [InlineData(0x20000)] // a cooked package's block
    public void Stored_literals_decode_back(int length)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();
        var output = new byte[length];

        Assert.Equal(length, Lzo1x.Decompress(Lzo1x.StoreLiterals(data), output));
        Assert.Equal(data, output);
    }

    [Fact]
    public void Stored_literals_are_the_hand_made_stream()
    {
        Assert.Equal([20, .. "abc"u8, 0x11, 0, 0], Lzo1x.StoreLiterals("abc"u8));
        var long239 = new byte[239];
        Assert.Equal([0, 221, .. long239, 0x11, 0, 0], Lzo1x.StoreLiterals(long239)); // 15 + 3 + 221 literals
    }
}
