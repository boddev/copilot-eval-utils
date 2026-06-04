using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

/// <summary>
/// Slice-3 (sub-slice C) PDF reader parity tests.
///
/// <para>The bulk of these tests pin the byte-exact post-processing
/// pipeline (control-char strip → CR normalize → split on
/// <c>\n{1,}</c> → collapse <c>[ \t]+</c> → <see cref="JsCompat.Trim"/>
/// → empty filter → <see cref="TextChunker.Chunk"/>) against the
/// 35-scenario probe matrix captured at slice-3-C pre-flight. The
/// probe (and its TS ground-truth output) is embedded as
/// <c>Readers/PdfPostProcessResults.json</c> from
/// <c>~/.copilot/session-state/.../pdf-probe/post-process-results.json</c>.</para>
///
/// <para>PdfPig extraction itself (<see cref="PdfReader.Read"/>) is
/// semantic-parity-only against pdf-parse v2, so it is not pinned
/// byte-exact here; instead a handful of small self-built PDFs sanity
/// check end-to-end behavior (dispatch, empty-PDF error, etc.).</para>
/// </summary>
public class PdfReaderTests
{
    // ----- Ground truth loading -----

    private sealed record ScenarioGroundTruth(
        string Name,
        string Input,
        IReadOnlyList<string> Paragraphs,
        IReadOnlyList<RecordExpect> Records);

    private sealed record RecordExpect(int ChunkNumber, string Content, int WordCount);

    private static readonly Dictionary<string, ScenarioGroundTruth> s_groundTruth =
        LoadGroundTruth();

    private static Dictionary<string, ScenarioGroundTruth> LoadGroundTruth()
    {
        // Embedded resource name = default-namespace + path (with '.'
        // separators). The default namespace for the tests project is
        // EvalToolkit.EvalGen.Tests.
        const string resourceName =
            "EvalToolkit.EvalGen.Tests.Readers.PdfPostProcessResults.json";
        Assembly asm = typeof(PdfReaderTests).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded ground-truth resource '{resourceName}' was not " +
                "found. Available resources: " +
                string.Join(", ", asm.GetManifestResourceNames()));
        }
        using var doc = JsonDocument.Parse(stream);

        var result = new Dictionary<string, ScenarioGroundTruth>(
            StringComparer.Ordinal);
        foreach (JsonProperty scenario in doc.RootElement.EnumerateObject())
        {
            string name = scenario.Name;
            // raw_codepoints is an array of "U+XXXX" strings; rebuild
            // the input verbatim so we don't have to maintain a parallel
            // copy of the scenario inputs. This keeps a single source
            // of truth (the captured probe JSON) and means
            // codepoint-level edge cases (FEFF, NEL, control chars,
            // C1 controls, NBSP, soft-hyphen, word-joiner) survive the
            // round trip without JSON-string-escape ambiguity.
            string input = ReconstructInputFromCodepoints(
                scenario.Value.GetProperty("raw_codepoints"));

            var paragraphs = new List<string>();
            foreach (JsonElement p in scenario.Value.GetProperty("paragraphs").EnumerateArray())
            {
                paragraphs.Add(p.GetString() ?? string.Empty);
            }

            var records = new List<RecordExpect>();
            foreach (JsonElement r in scenario.Value.GetProperty("records").EnumerateArray())
            {
                records.Add(new RecordExpect(
                    r.GetProperty("chunk_number").GetInt32(),
                    r.GetProperty("content").GetString() ?? string.Empty,
                    r.GetProperty("word_count").GetInt32()));
            }

            result[name] = new ScenarioGroundTruth(name, input, paragraphs, records);
        }
        return result;
    }

    private static string ReconstructInputFromCodepoints(JsonElement codepoints)
    {
        var sb = new StringBuilder();
        foreach (JsonElement cp in codepoints.EnumerateArray())
        {
            string hex = cp.GetString() ?? throw new InvalidOperationException(
                "raw_codepoints entry was null");
            if (!hex.StartsWith("U+", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"raw_codepoints entry '{hex}' is not in 'U+XXXX' form");
            }
            int value = int.Parse(
                hex.AsSpan(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            sb.Append(char.ConvertFromUtf32(value));
        }
        return sb.ToString();
    }

    public static IEnumerable<object[]> AllScenarios()
    {
        // MemberData feeds Theory; loading from the embedded resource
        // here keeps the 35 scenarios fully data-driven.
        foreach (string name in s_groundTruth.Keys)
        {
            yield return new object[] { name };
        }
    }

    // ----- Post-processing byte-exact parity (35 scenarios) -----

    /// <summary>
    /// Drive every scenario in the probe matrix through
    /// <see cref="PdfReader.PostProcessText"/> and assert the chunked
    /// records match the captured TS ground truth byte-for-byte.
    /// This covers control-char stripping, CR normalization,
    /// <c>\n{1,}</c> paragraph splitting, <c>[ \t]+</c> collapse,
    /// JS-compatible trim semantics (FEFF stripped, NEL preserved),
    /// empty-paragraph filtering, and chunking interactions — see
    /// scenario names in <c>post-process-probe.js</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void PostProcessText_MatchesTsGroundTruth(string scenarioName)
    {
        ScenarioGroundTruth gt = s_groundTruth[scenarioName];

        var records = PdfReader.PostProcessText(gt.Input);

        Assert.Equal(gt.Records.Count, records.Count);
        for (int i = 0; i < gt.Records.Count; i++)
        {
            RecordExpect want = gt.Records[i];
            DatasetRow got = records[i];

            // Records are constructed in TextChunker as a 3-key
            // DatasetRow with insertion-ordered keys:
            // chunk_number, content, word_count.
            Assert.Equal(3, got.Count);
            Assert.Equal(want.ChunkNumber, Convert.ToInt32(got["chunk_number"], CultureInfo.InvariantCulture));
            Assert.Equal(want.Content, (string?)got["content"]);
            Assert.Equal(want.WordCount, Convert.ToInt32(got["word_count"], CultureInfo.InvariantCulture));
        }
    }

    // ----- Null / empty input edges (Read contract) -----

    /// <summary>
    /// <see cref="PdfReader.PostProcessText"/> tolerates a null raw
    /// text input the same way the TS reader does
    /// (<c>(result.text ?? '')</c>): empty input → no records.
    /// </summary>
    [Fact]
    public void PostProcessText_NullInput_ReturnsEmpty()
    {
        var records = PdfReader.PostProcessText(null);
        Assert.Empty(records);
    }

    /// <summary>
    /// Empty string input → no records (matches the
    /// <c>empty-input</c> probe scenario but pinned explicitly here
    /// because TS would have thrown
    /// <c>"No text content found in PDF file"</c> at the
    /// <see cref="PdfReader.Read(string)"/> layer for this case).
    /// </summary>
    [Fact]
    public void PostProcessText_EmptyInput_ReturnsEmpty()
    {
        var records = PdfReader.PostProcessText(string.Empty);
        Assert.Empty(records);
    }

    // ----- Read contract: argument validation -----

    /// <summary>
    /// <see cref="PdfReader.Read"/> rejects a null absolutePath with
    /// <see cref="ArgumentException"/> — matches the slice-3 reader
    /// contract pinned in <see cref="DocxReader"/> /
    /// <see cref="PptxReader"/>.
    /// </summary>
    [Fact]
    public void Read_NullPath_Throws()
    {
        var reader = new PdfReader();
        Assert.Throws<ArgumentNullException>(() => reader.Read(null!));
    }

    [Fact]
    public void Read_EmptyPath_Throws()
    {
        var reader = new PdfReader();
        Assert.Throws<ArgumentException>(() => reader.Read(string.Empty));
    }

    // ----- Dispatch wiring -----

    /// <summary>
    /// Verify <c>.pdf</c> dispatch lands on
    /// <see cref="PdfReader"/> rather than the previous
    /// <c>NotSupportedException</c>. We don't need a real PDF here —
    /// we just need to observe that DatasetReader is routing PDF
    /// extensions through the new reader, which it will demonstrate
    /// by failing inside PdfPig rather than at the dispatcher.
    /// </summary>
    [Fact]
    public void Dispatch_PdfExtension_RoutesToPdfReader()
    {
        string tmp = Path.Combine(
            Path.GetTempPath(),
            "evaltoolkit-pdf-dispatch-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(tmp, "not actually a pdf");
        try
        {
            // PdfPig should reject the bogus file with its own
            // exception type — the key signal is that the call does
            // NOT throw NotSupportedException any longer.
            Exception ex = Assert.ThrowsAny<Exception>(
                () => DatasetReader.ReadSingleFile(tmp));
            Assert.IsNotType<NotSupportedException>(ex);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    // ----- Real PDF semantic sanity (self-built fixture) -----

    /// <summary>
    /// Builds a minimal valid single-page PDF with PdfPig's
    /// <see cref="UglyToad.PdfPig.Writer.PdfDocumentBuilder"/>,
    /// reads it back through <see cref="PdfReader"/>, and asserts a
    /// recognizable substring of the original text survives. This is
    /// the semantic-only parity contract: we do NOT compare to TS
    /// pdf-parse output byte-for-byte, only that text round-trips.
    /// </summary>
    [Fact]
    public void Read_SelfBuiltPdf_ExtractsRecognizableText()
    {
        string tmp = Path.Combine(
            Path.GetTempPath(),
            "evaltoolkit-pdf-sanity-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            byte[] pdfBytes = BuildSinglePagePdfWithText("Hello PdfPig world");
            File.WriteAllBytes(tmp, pdfBytes);

            var result = new PdfReader().Read(tmp);

            Assert.NotEmpty(result.Records);
            string allText = string.Join(
                "\n",
                result.Records.Select(r => (string?)r["content"] ?? string.Empty));
            // The exact spacing / line breaks coming out of PdfPig are
            // not specified — we only require the tokens survive.
            Assert.Contains("Hello", allText, StringComparison.Ordinal);
            Assert.Contains("PdfPig", allText, StringComparison.Ordinal);
            Assert.Contains("world", allText, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// An "empty" PDF — a single page with no drawn text — should
    /// surface the TS error string verbatim:
    /// <c>"No text content found in PDF file"</c>. Pinned because
    /// downstream tooling (EvalGen orchestration) matches on this
    /// exact message.
    /// </summary>
    [Fact]
    public void Read_EmptyPdf_ThrowsWithTsMessage()
    {
        string tmp = Path.Combine(
            Path.GetTempPath(),
            "evaltoolkit-pdf-empty-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            byte[] pdfBytes = BuildSinglePagePdfWithText(string.Empty);
            File.WriteAllBytes(tmp, pdfBytes);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new PdfReader().Read(tmp));
            Assert.Equal("No text content found in PDF file", ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    // ----- Helpers -----

    /// <summary>
    /// Build a minimal PDF (single A4 page, single text run if
    /// <paramref name="text"/> is non-empty) using PdfPig's writer.
    /// Returns the raw PDF bytes — fixture only, not under test.
    /// </summary>
    private static byte[] BuildSinglePagePdfWithText(string text)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(
            UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        if (!string.IsNullOrEmpty(text))
        {
            page.AddText(
                text,
                12,
                new UglyToad.PdfPig.Core.PdfPoint(50, 750),
                font);
        }
        return builder.Build();
    }
}
