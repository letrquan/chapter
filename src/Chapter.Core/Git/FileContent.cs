using System.Text;

namespace Chapter.Core.Git;

/// <summary>How a text file's bytes encode its characters.</summary>
public enum FileEncoding
{
    /// <summary>UTF-8 with no byte-order mark. The default for anything undetectable.</summary>
    Utf8,

    Utf8Bom,
    Utf16Le,
    Utf16Be,
}

/// <summary>Which newline a file uses.</summary>
public enum LineEnding
{
    Lf,
    CrLf,

    /// <summary>Both, which means the file must be written back untouched.</summary>
    Mixed,
}

/// <summary>
/// The representation choices a file makes, separate from its content.
///
/// These exist to be preserved. Reading a file and writing it back is only lossless if the
/// encoding, the byte-order mark and the newline all survive the round trip; get any of
/// them wrong and saving a one-character edit rewrites every line of the file, which shows
/// up in the diff as the user having changed everything.
/// </summary>
public sealed record TextFormat(FileEncoding Encoding, LineEnding LineEnding)
{
    /// <summary>
    /// What a file with nothing to detect gets. LF rather than the platform newline: this
    /// is a git repository, and CRLF is the choice that needs evidence.
    /// </summary>
    public static readonly TextFormat Default = new(FileEncoding.Utf8, LineEnding.Lf);

    /// <summary>
    /// The encoder for this format. Its preamble carries the byte-order mark.
    ///
    /// Fully qualified because <c>Encoding</c> is also the name of this record's own
    /// property, and the two meanings sitting one line apart is exactly the kind of thing
    /// that reads as a bug when it is not.
    /// </summary>
    public System.Text.Encoding ToEncoding() => Encoding switch
    {
        FileEncoding.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        FileEncoding.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        FileEncoding.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    /// <summary>
    /// Encodes text back to the bytes a file should contain, newlines and byte-order mark
    /// included.
    ///
    /// The mark has to be prepended explicitly. <see cref="System.Text.Encoding.GetBytes(string)"/>
    /// never emits one — the flag on <see cref="UTF8Encoding"/> governs
    /// <see cref="System.Text.Encoding.GetPreamble"/> alone — so encoding a file that had a
    /// BOM and writing the result strips it, which git then reports as a change to the
    /// first line of a file nobody edited.
    /// </summary>
    public byte[] Encode(string text)
    {
        var encoding = ToEncoding();
        var body = encoding.GetBytes(ApplyLineEndings(text));
        var preamble = encoding.GetPreamble();

        if (preamble.Length == 0) return body;

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>
    /// Rewrites the text's newlines to match. Mixed is left exactly as it arrived —
    /// a file that already disagrees with itself has no convention to preserve, and
    /// picking one for it would rewrite half the lines.
    /// </summary>
    public string ApplyLineEndings(string text) => LineEnding switch
    {
        LineEnding.CrLf => text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        LineEnding.Lf => text.Replace("\r\n", "\n"),
        _ => text,
    };
}

/// <summary>Decoded file content, or a marker that it is not text at all.</summary>
public sealed record FileContent(string Text, bool IsBinary, int ByteLength)
{
    public static readonly FileContent Empty = new("", false, 0);

    /// <summary>How to write this content back without reformatting the file.</summary>
    public TextFormat Format { get; init; } = TextFormat.Default;

    /// <summary>
    /// Whether writing <see cref="Text"/> back would reproduce the file.
    ///
    /// False in two cases, and both have to block editing rather than merely be noted.
    /// A file that is not valid UTF-8 — Latin-1 or Shift-JIS source, which is ordinary
    /// rather than corrupt — decodes with every invalid sequence replaced by U+FFFD, and
    /// encoding that back writes EF BF BD over bytes the user never touched. A file with
    /// mixed newlines cannot survive the editor, which normalises the model to one EOL, so
    /// saving it rewrites every line that disagreed.
    ///
    /// The text is still returned in both cases: a file with a few stray bytes should be
    /// reviewable. It just must not be saved.
    /// </summary>
    public bool CanRoundTrip { get; init; } = true;

    /// <summary>
    /// Decodes bytes to text, honouring a BOM when present and defaulting to UTF-8.
    /// Binary detection uses git's own heuristic: a NUL byte near the start of the file.
    /// </summary>
    public static FileContent FromBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return Empty;

        // BOM check comes first. UTF-16 text is full of NUL bytes, so running the binary
        // probe ahead of this would classify every UTF-16 source file as binary.
        if (TryDecodeWithBom(bytes, out var bomText, out var bomEncoding, out var bomLossless))
            return Build(bomText, bomEncoding, bytes.Length, bomLossless);

        var probe = Math.Min(bytes.Length, 8000);
        if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
            return new FileContent("", IsBinary: true, bytes.Length) { CanRoundTrip = false };

        var text = Decode(Encoding.UTF8, bytes, 0, bytes.Length, out var lossless);
        return Build(text, FileEncoding.Utf8, bytes.Length, lossless);
    }

    private static FileContent Build(string text, FileEncoding encoding, int byteLength, bool lossless)
    {
        var lineEnding = DetectLineEnding(text);

        return new FileContent(text, IsBinary: false, byteLength)
        {
            Format = new TextFormat(encoding, lineEnding),
            CanRoundTrip = lossless && lineEnding is not LineEnding.Mixed,
        };
    }

    /// <summary>
    /// Decodes, reporting whether anything was substituted.
    ///
    /// Decoded twice on purpose: once strictly to find out, once leniently to produce
    /// something readable. The strict pass is the only way to distinguish a file that
    /// genuinely contains U+FFFD from one where the decoder put it there, and that
    /// distinction is what stands between an edit and silent data loss.
    /// </summary>
    private static string Decode(Encoding encoding, byte[] bytes, int index, int count, out bool lossless)
    {
        try
        {
            var strict = (Encoding)encoding.Clone();
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;

            var text = strict.GetString(bytes, index, count);
            lossless = true;
            return text;
        }
        catch (DecoderFallbackException)
        {
            lossless = false;
            return encoding.GetString(bytes, index, count);
        }
    }

    /// <summary>
    /// Classifies the file's newline by counting both kinds. A lone CR — old Mac line
    /// endings — is not detected on purpose: git has not produced one this century, and
    /// treating a stray CR inside a line as a newline would misreport ordinary files.
    /// </summary>
    internal static LineEnding DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }

        if (crlf > 0 && lf > 0) return LineEnding.Mixed;
        return crlf > 0 ? LineEnding.CrLf : LineEnding.Lf;
    }

    private static bool TryDecodeWithBom(
        byte[] bytes, out string text, out FileEncoding encoding, out bool lossless)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Decode(Encoding.UTF8, bytes, 3, bytes.Length - 3, out lossless);
            encoding = FileEncoding.Utf8Bom;
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Decode(Encoding.Unicode, bytes, 2, bytes.Length - 2, out lossless);
            encoding = FileEncoding.Utf16Le;
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = Decode(Encoding.BigEndianUnicode, bytes, 2, bytes.Length - 2, out lossless);
            encoding = FileEncoding.Utf16Be;
            return true;
        }

        text = "";
        encoding = FileEncoding.Utf8;
        lossless = true;
        return false;
    }
}
