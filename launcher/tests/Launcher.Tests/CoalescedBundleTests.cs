using System.Buffers.Binary;
using System.Text;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class CoalescedBundleTests
{
    /// <summary>
    /// A bundle as the game ships it: a big-endian count of strings, then name/body FStrings,
    /// each a big-endian length with its NUL (negative for UTF-16LE).
    /// </summary>
    internal static byte[] Bundle(params (string Name, string Body, bool Wide)[] inis)
    {
        var bytes = new List<byte>();
        void Int(int value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            bytes.AddRange(buffer);
        }
        void Ansi(string text)
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            Int(encoded.Length + 1);
            bytes.AddRange(encoded);
            bytes.Add(0);
        }
        Int(inis.Length * 2);
        foreach (var (name, body, wide) in inis)
        {
            Ansi(name);
            if (!wide)
            {
                Ansi(body);
                continue;
            }
            Int(-(body.Length + 1));
            bytes.AddRange(Encoding.Unicode.GetBytes(body));
            bytes.AddRange([0, 0]);
        }
        return [.. bytes];
    }

    internal static byte[] Bundle(params (string Name, string Body)[] inis) =>
        Bundle([.. inis.Select(ini => (ini.Name, ini.Body, false))]);

    private static readonly byte[] Synthetic = Bundle(
        (@"..\Game\Config\Xenon\Cooked\Xe-Engine.ini", "[URL]\r\nProtocol=unreal\r\n", false),
        (@"..\Game\Localization\INT\Credits.int", "﻿[Credits]\nLine=Café\n", false),
        (@"..\Game\Localization\JPN\Core.jpn", "[Language]\nLanguage=日本語\n", true));

    [Fact]
    public void A_bundle_reads_its_inner_inis_by_name_and_short_name()
    {
        var bundle = CoalescedBundle.Parse(Synthetic);

        Assert.Equal(["Xe-Engine.ini", "Credits.int", "Core.jpn"], bundle.Inis.Select(ini => ini.ShortName));
        Assert.Equal(@"..\Game\Config\Xenon\Cooked\Xe-Engine.ini", bundle.Inis[0].Name);
        Assert.Equal("[URL]\r\nProtocol=unreal\r\n", bundle.Inis[0].Body);
        Assert.Equal("﻿[Credits]\nLine=Café\n", bundle.Inis[1].Body);
        Assert.Equal("[Language]\nLanguage=日本語\n", bundle.Inis[2].Body);
        Assert.Same(bundle.Inis[1], bundle.Find("credits.INT"));
        Assert.Null(bundle.Find("Missing.ini"));
    }

    [Fact]
    public void An_unchanged_bundle_writes_back_the_same_bytes()
    {
        Assert.Equal(Synthetic, CoalescedBundle.Parse(Synthetic).ToBytes());
    }

    [Fact]
    public void A_changed_body_is_written_in_its_own_encoding_and_the_rest_is_kept()
    {
        var bundle = CoalescedBundle.Parse(Synthetic);
        bundle.Inis[1].Body = "[Credits]\nLine=Olé\n";
        bundle.Inis[2].Body = "[Language]\nLanguage=英語\n";

        var expected = Bundle(
            (@"..\Game\Config\Xenon\Cooked\Xe-Engine.ini", "[URL]\r\nProtocol=unreal\r\n", false),
            (@"..\Game\Localization\INT\Credits.int", "[Credits]\nLine=Olé\n", false),
            (@"..\Game\Localization\JPN\Core.jpn", "[Language]\nLanguage=英語\n", true));
        Assert.Equal(expected, bundle.ToBytes());
    }

    [Fact]
    public void A_body_that_is_not_UTF8_round_trips_byte_for_byte()
    {
        var bytes = Bundle((@"Xe-Game.ini", "[A]\nB=1\n"));
        bytes[^3] = 0xE9; // the "1": a lone Latin-1 byte, as one of TU2's inis has.
        var bundle = CoalescedBundle.Parse(bytes);
        Assert.Equal(bytes, bundle.ToBytes());

        bundle.Inis[0].Body = bundle.Inis[0].Body.Replace("[A]", "[C]", StringComparison.Ordinal);
        var written = bundle.ToBytes();
        Assert.Equal(0xE9, written[^3]);
        Assert.Equal((byte)'C', written[^8]);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 3 })]
    [InlineData(new byte[] { 0, 0, 0, 2, 0, 0, 0, 9, 65 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFE })]
    public void A_damaged_bundle_is_refused(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => CoalescedBundle.Parse(bytes));
    }

    public static TheoryData<string> RealBundles() =>
        ["Coalesced.ini", "Coalesced.int", "Coalesced.deu", "Coalesced.fra", "Coalesced.ita", "Coalesced.spa"];

    [Theory]
    [MemberData(nameof(RealBundles))]
    public void TU2s_bundles_round_trip_byte_for_byte(string name)
    {
        var path = RealUpdateFile(name);
        Assert.SkipUnless(path != null, "TU2's update folder (updatedata/) is not beside the repo");

        var bytes = File.ReadAllBytes(path!);
        var bundle = CoalescedBundle.Parse(bytes);

        Assert.NotEmpty(bundle.Inis);
        Assert.Equal(bytes, bundle.ToBytes());
        foreach (var ini in bundle.Inis) ini.Body = ini.Body + "";
        Assert.Equal(bytes, bundle.ToBytes());
    }

    /// <summary>TU2's file in the nearest <c>updatedata/</c> above the tests, so a worktree finds the main checkout's.</summary>
    private static string? RealUpdateFile(string name)
    {
        for (var folder = AppContext.BaseDirectory; folder != null; folder = Path.GetDirectoryName(folder))
        {
            var path = Path.Combine(folder, "updatedata", name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
