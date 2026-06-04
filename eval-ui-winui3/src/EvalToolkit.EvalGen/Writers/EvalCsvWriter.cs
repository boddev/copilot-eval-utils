using System.Text;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// Writes an EvalScore-compatible CSV with the fixed column set
/// <c>prompt, expected_answer, source_location, actual_answer</c>.
/// Mirrors the TS <c>writeEvalCsv</c> in <c>eval-gen/src/writers.ts</c>
/// which uses <c>csv-stringify/sync</c> with
/// <c>{ header: true, columns: […] }</c>.
///
/// <para><b>Byte-exact contract pinned by the writers-probe</b>
/// (<c>~/.copilot/session-state/.../writers-probe/</c>):</para>
///
/// <list type="bullet">
///   <item><b>Header row.</b> Always written, even when
///     <paramref name="items"/> is empty. Header literal:
///     <c>prompt,expected_answer,source_location,actual_answer</c>.</item>
///   <item><b>Row separator + trailing terminator.</b> Bare <c>\n</c>
///     (0x0A) between rows AND after the final row. No CR. No BOM.
///     Pinned by the <c>csv-empty</c> scenario (53-byte file).</item>
///   <item><b>Minimal RFC-4180 quoting.</b> A field is wrapped in
///     <c>"</c> only when it contains the delimiter <c>,</c>, the
///     quote <c>"</c>, or a CR/LF character. Embedded <c>"</c> doubles
///     to <c>""</c>.</item>
///   <item><b>Embedded CRLF preserved.</b> A field containing <c>\r\n</c>
///     is quoted and the bytes pass through unchanged.</item>
///   <item><b>Control chars pass through</b> (U+0001, U+0007, U+001F):
///     written as-is, NOT quoted, NOT escaped.</item>
///   <item><b>Unicode pass-through.</b> Astral plane (emoji), NBSP,
///     accented letters written as UTF-8 without escaping.</item>
///   <item><b>Extra item fields ignored.</b> Only the four canonical
///     fields are read off each item.</item>
///   <item><b><c>actual_answer</c> is always empty.</b> EvalScore fills
///     it in later.</item>
/// </list>
///
/// <para><b>Output path:</b> resolved via
/// <see cref="Path.GetFullPath(string)"/> (matches Node's
/// <c>path.resolve</c>). Parent directory is created if missing.</para>
/// </summary>
public sealed class EvalCsvWriter
{
    private const char Delimiter = ',';
    private const char Quote = '"';
    private const char RowTerminator = '\n';

    private static readonly string[] s_columns =
    [
        "prompt", "expected_answer", "source_location", "actual_answer",
    ];

    /// <summary>
    /// Write <paramref name="items"/> as an EvalScore-compatible CSV.
    /// Returns the absolute path the file was written to.
    /// </summary>
#pragma warning disable CA1822 // Instance method by design; future-proofs for DI / mocking
    public string Write(IReadOnlyList<GeneratedEvalItem> items, string outputPath)
#pragma warning restore CA1822
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        string absolutePath = Path.GetFullPath(outputPath);

        var sb = new StringBuilder();
        // Header row — emitted verbatim. None of the header tokens
        // contain a trigger character so no quoting is needed.
        sb.Append(string.Join(Delimiter, s_columns));
        sb.Append(RowTerminator);

        foreach (var item in items)
        {
            // Field order MUST match the column order above. Each
            // value is read off the typed property (so any incidental
            // extra fields on an item subclass are ignored — same as
            // TS where csv-stringify follows the explicit `columns`).
            AppendField(sb, item.Prompt);
            sb.Append(Delimiter);
            AppendField(sb, item.ExpectedAnswer);
            sb.Append(Delimiter);
            AppendField(sb, item.SourceLocation);
            sb.Append(Delimiter);
            // actual_answer is always empty — EvalScore fills it in
            // later. No trailing comma; just a row terminator.
            sb.Append(RowTerminator);
        }

        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(absolutePath, sb.ToString(), JsonShape.Utf8NoBom);
        return absolutePath;
    }

    /// <summary>
    /// Append a single CSV cell to <paramref name="sb"/>, applying
    /// minimal RFC-4180 quoting only when the field contains the
    /// delimiter, a double-quote, or a CR/LF.
    /// </summary>
    private static void AppendField(StringBuilder sb, string? raw)
    {
        // null and empty both serialize to the empty string (no
        // quoting needed).
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        bool needsQuoting = false;
        foreach (char c in raw)
        {
            if (c == Delimiter || c == Quote || c == '\n' || c == '\r')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            sb.Append(raw);
            return;
        }

        sb.Append(Quote);
        foreach (char c in raw)
        {
            if (c == Quote)
            {
                sb.Append(Quote);
                sb.Append(Quote);
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append(Quote);
    }
}
