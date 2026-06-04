using System.Text.RegularExpressions;
using EvalToolkit.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads <c>.pdf</c> files to mirror the TS <c>readPdf</c> reader in
/// <c>eval-gen/src/readers/index.ts</c>:
///
/// <code>
/// const buffer = fs.readFileSync(filePath);
/// const parser = new PDFParse({ data: buffer });
/// const result = await parser.getText();
/// const cleaned = (result.text ?? '')
///   .replace(/[\x00-\x08\x0B\x0C\x0E-\x1F]/g, '')
///   .replace(/\r\n?/g, '\n');
/// const paragraphs = cleaned
///   .split(/\n{1,}/)
///   .map(p =&gt; p.replace(/[ \t]+/g, ' ').trim())
///   .filter(p =&gt; p.length &gt; 0);
/// const records = chunkText(paragraphs);
/// if (records.length === 0) throw new Error('No text content found in PDF file');
/// </code>
///
/// <para><b>Parity policy: SEMANTIC ONLY for extraction; BYTE-EXACT
/// for post-processing.</b> The TS reader uses <c>pdf-parse v2</c>
/// which wraps the pdf.js text-extraction stack. The C# port uses
/// <c>UglyToad.PdfPig</c>'s <see cref="ContentOrderTextExtractor"/>.
/// The two libraries' glyph ordering and line reconstruction
/// heuristics differ enough that the raw extracted page text is NOT
/// byte-equivalent — this is the test-fragility flagged at Phase 0
/// and acknowledged in Section 11 of the plan (PDF is the only
/// extractor with semantic-only parity).</para>
///
/// <para>The deterministic post-processing pipeline that runs AFTER
/// extraction (control-char stripping, CR/CRLF normalization,
/// <c>\n{1,}</c> paragraph splitting, <c>[ \t]+</c>→single-space
/// collapse, <see cref="JsCompat.Trim"/>, empty-paragraph filter,
/// and <see cref="TextChunker.Chunk"/>) IS byte-exact. It's factored
/// out into <see cref="PostProcessText"/> so the post-processing
/// behavior can be probed independently of extraction via the
/// 35-scenario probe matrix at
/// <c>~/.copilot/session-state/.../pdf-probe/post-process-results.json</c>.</para>
///
/// <list type="bullet">
///   <item><b>Control characters stripped.</b> The regex
///     <c>[\x00-\x08\x0B\x0C\x0E-\x1F]</c> removes the C0 controls
///     EXCEPT tab (0x09), LF (0x0A), and CR (0x0D). DEL (0x7F) and
///     the C1 controls (0x80-0x9F) are PRESERVED — pdf-parse-style
///     leakage of, e.g., a soft hyphen (U+00AD) or NBSP (U+00A0) is
///     left intact for downstream tooling to handle. Probes
///     <c>strip-control-chars-low</c>, <c>preserve-tab-newline-cr</c>,
///     <c>strip-vt-ff</c>, <c>strip-shift-out-and-up</c>,
///     <c>preserve-del</c>, <c>preserve-c1-controls</c>.</item>
///   <item><b>Line endings normalized.</b> <c>\r\n</c> → <c>\n</c>
///     and lone <c>\r</c> → <c>\n</c>. Probes <c>crlf-to-lf</c>,
///     <c>cr-only-to-lf</c>, <c>mixed-line-endings</c>.</item>
///   <item><b>Paragraph split on <c>\n{1,}</c>.</b> Runs of newlines
///     collapse to a single split, so consecutive blank lines yield
///     exactly one paragraph break. Leading and trailing newlines
///     produce empty paragraphs which are later filtered. Probes
///     <c>split-single-newline</c>,
///     <c>split-multiple-newlines</c>, <c>leading-newlines</c>,
///     <c>trailing-newlines</c>, <c>only-newlines</c>.</item>
///   <item><b>Per-paragraph collapse + trim.</b> Each paragraph has
///     runs of spaces and tabs (<c>[ \t]+</c>) collapsed to a single
///     space, then <see cref="JsCompat.Trim"/> strips leading and
///     trailing JS-whitespace. NBSP (U+00A0) is NOT collapsed; soft
///     hyphen (U+00AD), word joiner (U+2060), and similar zero-width
///     characters are NOT touched. Probes <c>collapse-spaces</c>,
///     <c>collapse-tabs</c>, <c>collapse-mixed</c>,
///     <c>nbsp-preserved</c>, <c>soft-hyphen-preserved</c>,
///     <c>word-joiner-preserved</c>.</item>
///   <item><b>Trim parity edges.</b> Trim uses
///     <see cref="JsCompat.Trim"/> so U+FEFF is stripped (matches
///     JS) and U+0085 is NOT stripped (also matches JS). Probes
///     <c>feff-only</c>, <c>nel-only</c>, <c>feff-leading</c>,
///     <c>nel-leading</c>, <c>feff-trailing</c>,
///     <c>nel-trailing</c>.</item>
///   <item><b>Empty after collapse + trim filtered out.</b>
///     Whitespace-only and zero-length paragraphs are dropped.
///     Probes <c>empty-paragraph-between-good</c>,
///     <c>all-empty-after-trim</c>, <c>inner-spaces-only</c>.</item>
///   <item><b>Chunking via <see cref="TextChunker.Chunk"/>.</b>
///     Greedy pack with 500-char target; a paragraph longer than
///     target still gets emitted in its own chunk. Probes
///     <c>three-medium-paragraphs</c>,
///     <c>one-huge-paragraph-no-split</c>.</item>
/// </list>
///
/// <para><b>Error contract:</b> throws
/// <see cref="InvalidOperationException"/> with the byte-exact TS
/// message <c>"No text content found in PDF file"</c> when the
/// post-processing pipeline yields zero records (empty PDF or PDF
/// whose extracted text is all whitespace / control characters).
/// Mirrors TS exactly.</para>
/// </summary>
public sealed class PdfReader : IDatasetReader
{
    private const string ErrorNoText = "No text content found in PDF file";

    // Compiled once, shared across all PDF reads. Source-of-truth
    // equivalents are inline-documented above. All three regexes are
    // ASCII-only so culture/Unicode mode differences are not a parity
    // risk; the only Unicode-sensitive step is the JsCompat.Trim call.
    private static readonly Regex s_stripControlChars = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_normalizeCr = new(
        @"\r\n?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_paragraphSplit = new(
        @"\n{1,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_spaceTabRun = new(
        @"[ \t]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Read and parse a <c>.pdf</c> file into chunked records.
    /// Returns a <see cref="ReadResult"/> with format
    /// <see cref="InputFormat.Pdf"/>.
    /// </summary>
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);

        string rawText = ExtractRawText(absolutePath);
        var records = PostProcessText(rawText);
        if (records.Count == 0)
        {
            throw new InvalidOperationException(ErrorNoText);
        }
        return new ReadResult { Records = records, Format = InputFormat.Pdf };
    }

    /// <summary>
    /// Extract raw text from every page using PdfPig's
    /// <see cref="ContentOrderTextExtractor"/> — the rough analog of
    /// what pdf.js (and therefore pdf-parse v2) produces. Pages are
    /// joined with a single <c>\n</c> to match pdf-parse v2's
    /// per-page join. NB: this step is the source of the
    /// PDF-specific semantic-vs-byte-exact parity gap; tests that
    /// pin specific output strings should pre-compute the rawText
    /// directly and call <see cref="PostProcessText"/>, not load a
    /// real PDF.
    /// </summary>
    private static string ExtractRawText(string absolutePath)
    {
        using var document = PdfDocument.Open(absolutePath);
        var perPage = new List<string>();
        foreach (var page in document.GetPages())
        {
            string text = ContentOrderTextExtractor.GetText(page);
            perPage.Add(text);
        }
        return string.Join("\n", perPage);
    }

    /// <summary>
    /// Byte-exact port of the post-extraction pipeline in TS
    /// <c>readPdf</c>. Exposed <c>internal</c> so tests can pin all
    /// 35 ground-truth scenarios from the probe matrix at
    /// <c>~/.copilot/session-state/.../pdf-probe/post-process-results.json</c>
    /// without needing a real PDF.
    /// </summary>
    internal static List<DatasetRow> PostProcessText(string? rawText)
    {
        string cleaned = s_stripControlChars.Replace(rawText ?? string.Empty, string.Empty);
        cleaned = s_normalizeCr.Replace(cleaned, "\n");

        // Splitting on `\n{1,}` collapses runs of newlines into a
        // single split — equivalent to the TS regex `/\n{1,}/`. .NET
        // Regex.Split returns the trailing empty string when the
        // input ends with a match, matching JS behavior here.
        string[] rawParas = s_paragraphSplit.Split(cleaned);

        var paragraphs = new List<string>(rawParas.Length);
        foreach (string p in rawParas)
        {
            string collapsed = s_spaceTabRun.Replace(p, " ");
            string trimmed = JsCompat.Trim(collapsed);
            if (trimmed.Length > 0)
            {
                paragraphs.Add(trimmed);
            }
        }
        return TextChunker.Chunk(paragraphs);
    }
}
