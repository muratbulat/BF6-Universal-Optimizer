using System.Text;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;

namespace BF6Optimizer.Tests;

public class ProfileDocumentTests
{
    private static readonly Dictionary<string, string> Change = new() { ["GstRender.ShadowQuality"] = "0" };

    private static byte[] Bytes(Encoding e, string text, bool bom)
    {
        var body = e.GetBytes(text);
        return bom ? [.. e.GetPreamble(), .. body] : body;
    }

    public static TheoryData<string, ProfileEncoding, bool> Encodings => new()
    {
        { "utf-8", ProfileEncoding.Utf8, false },
        { "utf-8", ProfileEncoding.Utf8Bom, true },
        { "utf-16", ProfileEncoding.Utf16LeBom, true },
        { "utf-16BE", ProfileEncoding.Utf16BeBom, true },
        { "utf-32", ProfileEncoding.Utf32LeBom, true },
        { "utf-32BE", ProfileEncoding.Utf32BeBom, true },
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Encoding_and_bom_survive_edit(string name, ProfileEncoding expected, bool bom)
    {
        var e = Encoding.GetEncoding(name);
        var doc = ProfileDocument.Parse(Bytes(e, "A 1\r\nGstRender.ShadowQuality 2\r\nÖzel ş\r\n", bom));
        Assert.Equal(expected, doc.FileEncoding);

        var output = doc.WithChanges(Change);
        Assert.Equal(Bytes(e, "A 1\r\nGstRender.ShadowQuality 0\r\nÖzel ş\r\n", bom), output);
    }

    [Fact]
    public void Non_utf8_bytes_round_trip_exactly()
    {
        byte[] bytes = [.. "X "u8.ToArray(), 0xFD, 0xFE, (byte)'\n', .. "GstRender.ShadowQuality 2\n"u8.ToArray()];
        var doc = ProfileDocument.Parse(bytes);
        Assert.Equal(ProfileEncoding.SingleByte, doc.FileEncoding);
        var output = doc.WithChanges(Change);
        Assert.Equal(bytes.Length, output.Length);
        Assert.Equal(0xFD, output[2]);
        Assert.Equal((byte)'0', output[^2]);
    }

    [Theory]
    [InlineData("A 1\nB 2\n", NewLineStyle.Lf)]
    [InlineData("A 1\r\nB 2\r\n", NewLineStyle.CrLf)]
    [InlineData("A 1\r\nB 2\n", NewLineStyle.Mixed)]
    [InlineData("A 1", NewLineStyle.None)]
    public void New_line_style_is_detected(string text, NewLineStyle style) =>
        Assert.Equal(style, ProfileDocument.Parse(Encoding.ASCII.GetBytes(text)).NewLine);

    [Fact]
    public void Only_value_span_changes()
    {
        const string text = "# comment line\n  GstRender.ShadowQuality\t 2  \nGstRender.ShadowQualityX 5\nTail 1";
        var doc = ProfileDocument.Parse(Encoding.ASCII.GetBytes(text));
        var output = Encoding.ASCII.GetString(doc.WithChanges(Change));
        Assert.Equal("# comment line\n  GstRender.ShadowQuality\t 0  \nGstRender.ShadowQualityX 5\nTail 1", output);
    }

    [Fact]
    public void Missing_keys_are_never_inserted()
    {
        var input = Encoding.ASCII.GetBytes("A 1\n");
        Assert.Equal(input, ProfileDocument.Parse(input).WithChanges(Change));
    }

    [Fact]
    public void All_duplicates_are_updated_without_adding_lines()
    {
        var doc = ProfileDocument.Parse(Encoding.ASCII.GetBytes("GstRender.ShadowQuality 2\nGstRender.ShadowQuality 1\n"));
        Assert.Equal(["2", "1"], doc.GetAll("GstRender.ShadowQuality"));
        Assert.Equal("GstRender.ShadowQuality 0\nGstRender.ShadowQuality 0\n", Encoding.ASCII.GetString(doc.WithChanges(Change)));
    }

    [Fact]
    public void Keys_are_case_sensitive() =>
        Assert.Null(ProfileDocument.Parse(Encoding.ASCII.GetBytes("gstrender.shadowquality 2\n")).Get("GstRender.ShadowQuality"));

    [Fact]
    public void Key_without_value_is_ignored() =>
        Assert.False(ProfileDocument.Parse(Encoding.ASCII.GetBytes("Lonely\nA 1\n")).Contains("Lonely"));

    [Fact]
    public void Value_with_spaces_is_kept_whole() =>
        Assert.Equal("a b c", ProfileDocument.Parse(Encoding.ASCII.GetBytes("K a b c\n")).Get("K"));

    [Fact]
    public void Binary_save_is_refused()
    {
        var ex = Assert.Throws<OptimizerException>(() => ProfileDocument.Parse([0x41, 0x00, 0x42, 0x00, 0x00, 0x01]));
        Assert.Equal("error.profile_binary", ex.UserMessage.Key);
    }

    [Fact]
    public void Empty_file_parses() => Assert.Empty(ProfileDocument.Parse([]).Keys);

    [Fact]
    public void Catalog_needs_signature_and_enough_keys()
    {
        var catalog = SettingCatalog.LoadEmbedded();
        var doc = (string t) => ProfileDocument.Parse(Encoding.ASCII.GetBytes(t));
        Assert.True(catalog.Evaluate(doc(Fixtures.Profile)).IsCompatible);
        Assert.False(catalog.Evaluate(doc("GstRender.TextureQuality 1\n")).IsCompatible);
        Assert.False(catalog.Evaluate(doc("GstRender.FilmGrain 1\nGstRender.Vignette 1\nGstRender.WeaponDOF 1\nGstRender.LensDistortion 1\n")).IsCompatible);
    }
}
