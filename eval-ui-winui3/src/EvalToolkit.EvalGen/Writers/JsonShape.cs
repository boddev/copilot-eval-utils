using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// JSON-writing primitives shared by <see cref="SidecarJsonWriter"/>
/// and <see cref="M365MultiPromptWriter"/>. Both writers must match
/// Node's <c>JSON.stringify(value, null, 2)</c> + <c>fs.writeFileSync</c>
/// byte-for-byte.
///
/// <list type="bullet">
///   <item><b>2-space indentation</b> via <see cref="JsonWriterOptions.Indented"/>
///     + <c>IndentSize = 2</c>.</item>
///   <item><b>NO trailing newline.</b> Node's writeFileSync does not
///     append one and JSON.stringify doesn't either; verified by the
///     writers-probe.</item>
///   <item><b>Relaxed escaping</b> via
///     <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so
///     <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, NBSP (U+00A0),
///     BOM (U+FEFF), and emoji (astral surrogate pairs) all pass through
///     as literal UTF-8 — matching Node.js behavior verified by the
///     <c>sidecar-unicode</c> probe scenario.</item>
///   <item><b>UTF-8 output without BOM.</b></item>
///   <item><b>ISO-8601 with milliseconds</b> for <c>generated_at</c> —
///     <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> — matching Node's
///     <c>Date.toISOString()</c>.</item>
/// </list>
/// </summary>
internal static class JsonShape
{
    public const int IndentSize = 2;

    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = IndentSize,
        // CRITICAL: Node's JSON.stringify emits LF newlines on every
        // platform. STJ defaults to Environment.NewLine on Windows
        // (CRLF), which would silently break byte-exact parity. Pin
        // to LF unconditionally.
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// UTF-8 encoder with no BOM. Mirrors Node's
    /// <c>writeFileSync(path, text, 'utf-8')</c>.
    /// </summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Serialize via <paramref name="writeBody"/> using the pinned
    /// <see cref="JsonWriterOptions"/>, then post-process the bytes to
    /// match Node's <c>JSON.stringify</c> output byte-for-byte:
    /// lowercase the hex digits in <c>\uXXXX</c> escapes and unescape
    /// any UTF-16 surrogate pairs <c>\uD8XX\uDCXX</c> into their
    /// 4-byte UTF-8 encoding (Node never escapes astral plane chars
    /// when they are valid surrogate pairs).
    /// </summary>
    public static byte[] Serialize(Action<Utf8JsonWriter> writeBody)
    {
        ArgumentNullException.ThrowIfNull(writeBody);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, s_writerOptions))
        {
            writeBody(writer);
        }
        return NormalizeJsonBytesToNodeShape(ms.ToArray());
    }

    /// <summary>
    /// Post-processor that rewrites STJ's JSON byte stream so that it
    /// matches Node's <c>JSON.stringify</c> output byte-for-byte:
    /// <list type="number">
    ///   <item>Surrogate-pair escape sequences <c>\uD8XX\uDCXX</c> are
    ///     collapsed into the equivalent UTF-8 encoding of the astral
    ///     code point.</item>
    ///   <item>Any other <c>\uXXXX</c> escape whose code point is
    ///     <b>not</b> a C0 control (&lt; U+0020) and <b>not</b> an
    ///     unpaired surrogate is collapsed into its literal UTF-8
    ///     bytes (Node passes NBSP, FEFF, etc. through literally; STJ
    ///     escapes them).</item>
    ///   <item>Hex digits of any remaining escape (control chars and
    ///     unpaired surrogates) are lowercased — Node uses lowercase,
    ///     STJ emits uppercase.</item>
    /// </list>
    /// The transform tracks whether we are inside a JSON string literal
    /// (<c>"…"</c>) so escapes in keys (always strings) and string
    /// values are processed. Escapes outside string literals don't
    /// exist in well-formed JSON; we still process defensively.
    /// Backslash runs (<c>\\</c>) are honored to avoid mis-parsing
    /// <c>"\\u0000"</c> as an escape.
    /// </summary>
    private static byte[] NormalizeJsonBytesToNodeShape(byte[] input)
    {
        // Fast path: no \u escapes → return unchanged.
        if (!ContainsUnicodeEscape(input))
        {
            return input;
        }

        var output = new List<byte>(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            byte b = input[i];

            // Handle escaped backslash FIRST to keep "\\u0000" intact.
            if (b == (byte)'\\' && i + 1 < input.Length && input[i + 1] == (byte)'\\')
            {
                output.Add(b);
                output.Add(input[i + 1]);
                i += 2;
                continue;
            }

            if (b == (byte)'\\' && i + 5 < input.Length && input[i + 1] == (byte)'u'
                && TryParseHex4(input, i + 2, out int high))
            {
                // Surrogate pair: collapse to UTF-8 astral code point.
                if (high is >= 0xD800 and <= 0xDBFF
                    && i + 11 < input.Length
                    && input[i + 6] == (byte)'\\' && input[i + 7] == (byte)'u'
                    && TryParseHex4(input, i + 8, out int low)
                    && low is >= 0xDC00 and <= 0xDFFF)
                {
                    int codePoint = 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00);
                    AppendUtf8(output, codePoint);
                    i += 12;
                    continue;
                }

                // BMP character that Node would NOT escape (≥ U+0020
                // and not an unpaired surrogate): write literal UTF-8.
                bool isControl = high < 0x20;
                bool isLoneSurrogate = high is >= 0xD800 and <= 0xDFFF;
                if (!isControl && !isLoneSurrogate)
                {
                    AppendUtf8(output, high);
                    i += 6;
                    continue;
                }

                // Control or lone surrogate: keep escape, lowercase hex.
                // NOTE: The lone-surrogate branch is effectively dead
                // code under normal use — STJ's encoder substitutes
                // U+FFFD for unpaired surrogates BEFORE the bytes
                // reach this normalizer, so the JSON stream never
                // actually contains a lone `\uD8XX` or `\uDCXX` escape.
                // Kept for defensive completeness; Opus-4.8 review N1.
                output.Add((byte)'\\');
                output.Add((byte)'u');
                output.Add(ToLowerHex(input[i + 2]));
                output.Add(ToLowerHex(input[i + 3]));
                output.Add(ToLowerHex(input[i + 4]));
                output.Add(ToLowerHex(input[i + 5]));
                i += 6;
                continue;
            }

            output.Add(b);
            i++;
        }
        return output.ToArray();
    }

    private static bool ContainsUnicodeEscape(byte[] bytes)
    {
        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\\' && bytes[i + 1] == (byte)'u')
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseHex4(byte[] src, int offset, out int value)
    {
        value = 0;
        for (int k = 0; k < 4; k++)
        {
            byte c = src[offset + k];
            int d = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => 10 + c - (byte)'a',
                >= (byte)'A' and <= (byte)'F' => 10 + c - (byte)'A',
                _ => -1,
            };
            if (d < 0)
            {
                value = 0;
                return false;
            }
            value = (value << 4) | d;
        }
        return true;
    }

    private static byte ToLowerHex(byte c) =>
        c is >= (byte)'A' and <= (byte)'F'
            ? (byte)(c - (byte)'A' + (byte)'a')
            : c;

    private static void AppendUtf8(List<byte> dst, int codePoint)
    {
        // Standard UTF-8 encoding for any non-surrogate code point.
        if (codePoint <= 0x7F)
        {
            dst.Add((byte)codePoint);
        }
        else if (codePoint <= 0x7FF)
        {
            dst.Add((byte)(0xC0 | (codePoint >> 6)));
            dst.Add((byte)(0x80 | (codePoint & 0x3F)));
        }
        else if (codePoint <= 0xFFFF)
        {
            dst.Add((byte)(0xE0 | (codePoint >> 12)));
            dst.Add((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
            dst.Add((byte)(0x80 | (codePoint & 0x3F)));
        }
        else
        {
            dst.Add((byte)(0xF0 | (codePoint >> 18)));
            dst.Add((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
            dst.Add((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
            dst.Add((byte)(0x80 | (codePoint & 0x3F)));
        }
    }

    /// <summary>
    /// Write <paramref name="payload"/> to <paramref name="absolutePath"/>
    /// using UTF-8 with no BOM. Creates the parent directory if needed.
    /// </summary>
    public static void WriteToFile(string absolutePath, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        ArgumentNullException.ThrowIfNull(payload);

        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(absolutePath, payload);
    }

    /// <summary>
    /// Format a UTC instant as Node's <c>Date.toISOString()</c> would:
    /// <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>.
    /// </summary>
    public static string ToIso8601Millis(DateTimeOffset instant)
    {
        return instant.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }
}
