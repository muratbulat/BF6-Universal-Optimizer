using System.Text;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Configuration;

public enum ProfileEncoding { Utf8, Utf8Bom, Utf16LeBom, Utf16BeBom, Utf32LeBom, Utf32BeBom, SingleByte }

public enum NewLineStyle { None, Lf, CrLf, Mixed }

/// <summary>
/// A "Key Value" per-line BF6 profile. Edits splice only the value span, so encoding, BOM, line endings,
/// spacing and every unrelated line stay byte-identical. Keys are never added.
/// </summary>
public sealed class ProfileDocument
{
    private readonly record struct Entry(string Key, int ValueStart, int ValueLength);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, List<Entry>> _byKey = new(StringComparer.Ordinal);

    public string Text { get; }
    public ProfileEncoding FileEncoding { get; }
    public NewLineStyle NewLine { get; }

    private ProfileDocument(string text, ProfileEncoding encoding)
    {
        Text = text;
        FileEncoding = encoding;
        NewLine = DetectNewLine(text);
        Scan();
    }

    public static ProfileDocument Parse(byte[] bytes)
    {
        var (encoding, bomLength, codec) = DetectEncoding(bytes);
        string text;
        try { text = codec.GetString(bytes, bomLength, bytes.Length - bomLength); }
        catch (ArgumentException ex) { throw new OptimizerException(new Msg("error.profile_encoding"), ex); }
        if (text.Contains('\0')) throw new OptimizerException(new Msg("error.profile_binary"));

        // A lossy decode would corrupt unrelated bytes on write, so require an exact round trip first.
        var doc = new ProfileDocument(text, encoding);
        if (!doc.Encode(text).AsSpan().SequenceEqual(bytes))
            throw new OptimizerException(new Msg("error.profile_encoding"));
        return doc;
    }

    public bool Contains(string key) => _byKey.ContainsKey(key);

    /// <summary>First occurrence, matching v1.4.0 semantics.</summary>
    public string? Get(string key) =>
        _byKey.TryGetValue(key, out var list) ? Text.Substring(list[0].ValueStart, list[0].ValueLength) : null;

    public IEnumerable<string> GetAll(string key) =>
        _byKey.TryGetValue(key, out var list)
            ? list.Select(e => Text.Substring(e.ValueStart, e.ValueLength))
            : [];

    public IEnumerable<string> Keys => _byKey.Keys;

    /// <summary>Returns the file bytes with changes applied. Unknown keys are ignored, never inserted.</summary>
    public byte[] WithChanges(IReadOnlyDictionary<string, string> changes)
    {
        var sb = new StringBuilder(Text);
        foreach (var e in _entries.Where(e => changes.ContainsKey(e.Key)).OrderByDescending(e => e.ValueStart))
            sb.Remove(e.ValueStart, e.ValueLength).Insert(e.ValueStart, changes[e.Key]);
        return Encode(sb.ToString());
    }

    private void Scan()
    {
        int pos = 0;
        while (pos < Text.Length)
        {
            int end = pos;
            while (end < Text.Length && Text[end] != '\n' && Text[end] != '\r') end++;
            ScanLine(pos, end);
            pos = end;
            if (pos < Text.Length && Text[pos] == '\r') pos++;
            if (pos < Text.Length && Text[pos] == '\n') pos++;
        }
    }

    private void ScanLine(int start, int end)
    {
        int i = start;
        while (i < end && IsBlank(Text[i])) i++;
        int keyStart = i;
        while (i < end && !IsBlank(Text[i])) i++;
        int keyEnd = i;
        if (keyEnd == keyStart || keyEnd == end) return; // blank line or key without a value

        while (i < end && IsBlank(Text[i])) i++;
        int valueEnd = end;
        while (valueEnd > i && IsBlank(Text[valueEnd - 1])) valueEnd--;

        var entry = new Entry(Text[keyStart..keyEnd], i, valueEnd - i);
        _entries.Add(entry);
        if (!_byKey.TryGetValue(entry.Key, out var list)) _byKey[entry.Key] = list = [];
        list.Add(entry);
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';

    private byte[] Encode(string text)
    {
        (Encoding codec, byte[] preamble) = FileEncoding switch
        {
            ProfileEncoding.Utf8 => ((Encoding)new UTF8Encoding(false, true), Array.Empty<byte>()),
            ProfileEncoding.Utf8Bom => (new UTF8Encoding(false, true), new byte[] { 0xEF, 0xBB, 0xBF }),
            ProfileEncoding.Utf16LeBom => (new UnicodeEncoding(false, false, true), new byte[] { 0xFF, 0xFE }),
            ProfileEncoding.Utf16BeBom => (new UnicodeEncoding(true, false, true), new byte[] { 0xFE, 0xFF }),
            ProfileEncoding.Utf32LeBom => (new UTF32Encoding(false, false, true), new byte[] { 0xFF, 0xFE, 0, 0 }),
            ProfileEncoding.Utf32BeBom => (new UTF32Encoding(true, false, true), new byte[] { 0, 0, 0xFE, 0xFF }),
            _ => (Encoding.Latin1, Array.Empty<byte>()),
        };
        var body = codec.GetBytes(text);
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    private static (ProfileEncoding Encoding, int BomLength, Encoding Codec) DetectEncoding(byte[] b)
    {
        if (b is [0xFF, 0xFE, 0, 0, ..]) return (ProfileEncoding.Utf32LeBom, 4, new UTF32Encoding(false, false, true));
        if (b is [0, 0, 0xFE, 0xFF, ..]) return (ProfileEncoding.Utf32BeBom, 4, new UTF32Encoding(true, false, true));
        if (b is [0xEF, 0xBB, 0xBF, ..]) return (ProfileEncoding.Utf8Bom, 3, new UTF8Encoding(false, true));
        if (b is [0xFF, 0xFE, ..]) return (ProfileEncoding.Utf16LeBom, 2, new UnicodeEncoding(false, false, true));
        if (b is [0xFE, 0xFF, ..]) return (ProfileEncoding.Utf16BeBom, 2, new UnicodeEncoding(true, false, true));
        try
        {
            var strict = new UTF8Encoding(false, true);
            _ = strict.GetCharCount(b);
            return (ProfileEncoding.Utf8, 0, strict);
        }
        catch (ArgumentException)
        {
            // Latin-1 maps every byte to one char, so legacy code-page text survives unchanged;
            // all recognized keys and values are ASCII.
            return (ProfileEncoding.SingleByte, 0, Encoding.Latin1);
        }
    }

    private static NewLineStyle DetectNewLine(string t)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < t.Length; i++)
            if (t[i] == '\n')
            {
                if (i > 0 && t[i - 1] == '\r') crlf++;
                else lf++;
            }
        return (crlf, lf) switch
        {
            (0, 0) => NewLineStyle.None,
            (> 0, 0) => NewLineStyle.CrLf,
            (0, > 0) => NewLineStyle.Lf,
            _ => NewLineStyle.Mixed,
        };
    }
}
