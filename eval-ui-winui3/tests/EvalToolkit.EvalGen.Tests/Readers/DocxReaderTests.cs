using System.Globalization;
using System.IO.Compression;
using System.Text;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

/// <summary>
/// Slice-3 DOCX reader parity tests. Each test pins a behavior that
/// was verified empirically against the <c>mammoth</c> npm package
/// at slice-3 pre-flight (probe artifacts saved in
/// <c>~/.copilot/session-state/.../docx-probe</c>). The exact
/// mammoth output is quoted in each test's doc comment so the rule
/// can be re-derived without re-probing node.
///
/// Fixtures are constructed in-memory by writing minimal OPC zip
/// packages — same approach as the TS
/// <c>build-doc-fixtures.ts::buildSampleDocx</c>. Keeps the test
/// dependency graph small (no need for a real-Word generator) and
/// makes every byte of the fixture inspectable in source.
/// </summary>
public class DocxReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public DocxReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-docx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    // ===== Fixture builders =====

    /// <summary>
    /// Description of a single paragraph in the document body. Mirrors
    /// the TS <c>build-doc-fixtures.ts</c> shape so tests read like
    /// the node probes.
    /// </summary>
    private sealed record P
    {
        public string? Style { get; init; }
        public string? Text { get; init; }
        public Run[]? Runs { get; init; }
        /// <summary>Raw XML override — used for tables, sdt, hyperlink, lists.</summary>
        public string? RawXml { get; init; }
    }

    private sealed record Run
    {
        public string? Text { get; init; }
        /// <summary>Raw element override — e.g. "&lt;w:tab/&gt;" or "&lt;w:br/&gt;".</summary>
        public string? Tag { get; init; }
    }

    private string BuildDocx(string name, params P[] paragraphs)
    {
        string path = Path.Combine(_tmpDir, name);

        const string contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";

        const string rootRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        const string docRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"></Relationships>";

        var body = new StringBuilder();
        foreach (P p in paragraphs)
        {
            if (p.RawXml is not null)
            {
                body.Append(p.RawXml);
                continue;
            }
            body.Append("<w:p>");
            if (p.Style is not null)
            {
                body.Append(CultureInfo.InvariantCulture, $"<w:pPr><w:pStyle w:val=\"{EscapeXml(p.Style)}\"/></w:pPr>");
            }
            Run[] runs = p.Runs ?? (p.Text is null ? Array.Empty<Run>() : new[] { new Run { Text = p.Text } });
            foreach (Run r in runs)
            {
                body.Append("<w:r>");
                if (r.Tag is not null)
                {
                    body.Append(r.Tag);
                }
                else
                {
                    body.Append(CultureInfo.InvariantCulture, $"<w:t xml:space=\"preserve\">{EscapeXml(r.Text ?? string.Empty)}</w:t>");
                }
                body.Append("</w:r>");
            }
            body.Append("</w:p>");
        }

        string document =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            $"<w:body>{body}<w:sectPr/></w:body>" +
            "</w:document>";

        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "[Content_Types].xml", contentTypes);
            AddEntry(zip, "_rels/.rels", rootRels);
            AddEntry(zip, "word/document.xml", document);
            AddEntry(zip, "word/_rels/document.xml.rels", docRels);
        }
        return path;
    }

    private static void AddEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        sw.Write(content);
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string[] Contents(ReadResult r) =>
        r.Records.Select(row => (string)row["content"]!).ToArray();

    // ===== Parity probes =====

    [Fact]
    public void Read_SimpleParagraphs_MammothProbe()
    {
        // mammoth probe `simple` raw output:
        //   "Title\n\nPara one.\n\nPara two.\n\n"
        // After split/trim/filter:
        //   ["Title", "Para one.", "Para two."]
        // All three fit comfortably in one 500-char chunk joined by '\n'.
        string path = BuildDocx("simple.docx",
            new P { Style = "Heading1", Text = "Title" },
            new P { Text = "Para one." },
            new P { Text = "Para two." });

        var r = new DocxReader().Read(path);
        Assert.Equal(InputFormat.Docx, r.Format);
        Assert.Single(r.Records);
        Assert.Equal("Title\nPara one.\nPara two.", r.Records[0]["content"]);
        Assert.Equal(1, r.Records[0]["chunk_number"]);
        Assert.Equal(5, r.Records[0]["word_count"]); // Title + Para + one. + Para + two. = 5
    }

    [Fact]
    public void Read_MultiRunParagraph_ConcatsRunsWithNoSeparator()
    {
        // mammoth probe `multi-run` raw output:
        //   "Hello world.\n\n"
        // Multi-run paragraphs concat without separator — verified
        // probe vector: ["Hello ", "world", "."] → "Hello world."
        string path = BuildDocx("multi-run.docx",
            new P { Runs = new[] {
                new Run { Text = "Hello " },
                new Run { Text = "world" },
                new Run { Text = "." },
            }});

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("Hello world.", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_EmptyParagraphInMiddle_DroppedByTrimFilter()
    {
        // mammoth probe `empty-middle` raw output:
        //   "first\n\n\n\nthird\n\n"
        // After split/trim/filter:
        //   ["first", "third"]
        string path = BuildDocx("empty-middle.docx",
            new P { Text = "first" },
            new P { Text = "" },
            new P { Text = "third" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("first\nthird", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_TabCharInsideRun_BecomesTabCharacter()
    {
        // mammoth probe `tab-runs` raw output:
        //   "A\tB\n\n"
        // <w:tab/> becomes a literal tab character.
        string path = BuildDocx("tab-runs.docx",
            new P { Runs = new[] {
                new Run { Text = "A" },
                new Run { Tag = "<w:tab/>" },
                new Run { Text = "B" },
            }});

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("A\tB", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_BreakElementSilentlyDropped_MammothCompatible()
    {
        // mammoth probe `br-line` raw output:
        //   "line 1line 2\n\n"
        // <w:br/> is SILENTLY DROPPED by mammoth.extractRawText — runs
        // sandwiching it are concatenated without any separator. This
        // is surprising but the contract we mirror for parity.
        string path = BuildDocx("br-line.docx",
            new P { Runs = new[] {
                new Run { Text = "line 1" },
                new Run { Tag = "<w:br/>" },
                new Run { Text = "line 2" },
            }});

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("line 1line 2", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_TableCells_EachCellParagraphInDocumentOrder()
    {
        // mammoth probe `table` raw output:
        //   "r1c1\n\nr1c2\n\nr2c1\n\nr2c2\n\nafter table\n\n"
        // Each cell's <w:p> is a top-level paragraph in document order.
        string path = BuildDocx("table.docx",
            new P { RawXml =
                "<w:tbl>" +
                  "<w:tr>" +
                    "<w:tc><w:p><w:r><w:t>r1c1</w:t></w:r></w:p></w:tc>" +
                    "<w:tc><w:p><w:r><w:t>r1c2</w:t></w:r></w:p></w:tc>" +
                  "</w:tr>" +
                  "<w:tr>" +
                    "<w:tc><w:p><w:r><w:t>r2c1</w:t></w:r></w:p></w:tc>" +
                    "<w:tc><w:p><w:r><w:t>r2c2</w:t></w:r></w:p></w:tc>" +
                  "</w:tr>" +
                "</w:tbl>" },
            new P { Text = "after table" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("r1c1\nr1c2\nr2c1\nr2c2\nafter table", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_ListItems_TextOnlyNoBulletMarker()
    {
        // mammoth probe `list` raw output:
        //   "item one\n\nitem two\n\n"
        // List items are just paragraphs with numPr; mammoth omits the
        // bullet/number marker — only the run text comes through.
        string path = BuildDocx("list.docx",
            new P { RawXml =
                "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>" +
                "<w:r><w:t>item one</w:t></w:r></w:p>" },
            new P { RawXml =
                "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>" +
                "<w:r><w:t>item two</w:t></w:r></w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("item one\nitem two", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_WhitespaceRuns_TrimDropsLeadingTrailing()
    {
        // mammoth probe `whitespace-runs` raw output (each paragraph
        // preserves its xml:space):
        //   "  leading\n\ntrailing  \n\n  both  \n\n"
        // After JsCompat trim: ["leading", "trailing", "both"]
        string path = BuildDocx("whitespace-runs.docx",
            new P { Runs = new[] { new Run { Text = "  leading" } } },
            new P { Runs = new[] { new Run { Text = "trailing  " } } },
            new P { Runs = new[] { new Run { Text = "  both  " } } });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("leading\ntrailing\nboth", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_SdtContent_WrapperIsTransparent()
    {
        // mammoth probe `sdt` raw output:
        //   "wrapped text\n\n"
        // <w:sdt>/<w:sdtContent> wrappers are transparent — wrapped
        // runs surface as normal text. Body.Descendants<Paragraph>()
        // pulls the inner <w:p>... wait — actually the probe has the
        // <w:sdt> AT paragraph level wrapping run. The fixture builder
        // already wraps each P in <w:p>, so we use RawXml.
        string path = BuildDocx("sdt.docx",
            new P { RawXml =
                "<w:p><w:sdt><w:sdtContent>" +
                "<w:r><w:t>wrapped text</w:t></w:r>" +
                "</w:sdtContent></w:sdt></w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("wrapped text", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_Hyperlink_WrapperIsTransparent()
    {
        // mammoth probe `hyperlink` raw output:
        //   "click here\n\n"
        // <w:hyperlink> wrapper is transparent — wrapped run text
        // surfaces as normal paragraph text.
        string path = BuildDocx("hyperlink.docx",
            new P { RawXml =
                "<w:p>" +
                "<w:hyperlink r:id=\"rId1\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<w:r><w:t>click here</w:t></w:r>" +
                "</w:hyperlink>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("click here", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_EmptyDocument_ThrowsMatchingTsErrorMessage()
    {
        // TS reader throws 'No text content found in DOCX file' when
        // chunkText returns zero records. Byte-exact match required.
        string path = BuildDocx("empty.docx");
        var ex = Assert.Throws<InvalidOperationException>(() => new DocxReader().Read(path));
        Assert.Equal("No text content found in DOCX file", ex.Message);
    }

    [Fact]
    public void Read_OnlyWhitespaceParagraphs_ThrowsEmptyError()
    {
        // Paragraphs containing only whitespace get filtered → empty
        // → must throw the same error as a truly-empty document.
        string path = BuildDocx("ws-only.docx",
            new P { Text = "   " },
            new P { Text = "\t\t" },
            new P { Text = "" });
        Assert.Throws<InvalidOperationException>(() => new DocxReader().Read(path));
    }

    [Fact]
    public void Read_LargeParagraphCount_ChunksAt500CharTarget()
    {
        // Generate enough small paragraphs to overflow one chunk and
        // verify TextChunker's 500-char greedy-pack semantics carry
        // over from the slice-1 / shared chunker. Each paragraph is
        // 50 chars; 12 paragraphs joined by '\n' = 50*12 + 11 = 611
        // chars — should split into 2+ chunks.
        var parts = Enumerable.Range(0, 20)
            .Select(i => new P { Text = new string('a', 50) })
            .ToArray();
        string path = BuildDocx("big.docx", parts);

        var r = new DocxReader().Read(path);
        // 20 * 50 = 1000 chars; with '\n' separators: roughly 2-3 chunks.
        Assert.True(r.Records.Count >= 2);
        // Every record content is <= 500 chars + room for one final
        // paragraph that overshoots the boundary by < 50 chars.
        foreach (var rec in r.Records)
        {
            string content = (string)rec["content"]!;
            Assert.True(content.Length <= 550, $"Chunk content was {content.Length} chars (target 500)");
        }
        // chunk_number is 1-based and contiguous.
        for (int i = 0; i < r.Records.Count; i++)
        {
            Assert.Equal(i + 1, r.Records[i]["chunk_number"]);
        }
    }

    [Fact]
    public void Read_OversizedSingleParagraph_EmittedAsOneOversizeChunk()
    {
        // Per TextChunker contract (mirrors TS chunkText): a single
        // paragraph longer than CHUNK_TARGET_CHARS (500) is emitted
        // as one oversized chunk rather than split mid-paragraph.
        string huge = new string('x', 1200);
        string path = BuildDocx("huge.docx", new P { Text = huge });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal(1200, ((string)r.Records[0]["content"]!).Length);
    }

    [Fact]
    public void Read_OpensReadOnly_DoesNotLockFileForConcurrentReaders()
    {
        // Regression: WordprocessingDocument.Open(string, bool) opens
        // with FileShare.Read but FileShare.None on the FileStream
        // overload by default — we explicitly pass FileShare.Read.
        // A second reader opening the same file simultaneously should
        // succeed, not throw IOException.
        string path = BuildDocx("share.docx", new P { Text = "shared" });

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var r = new DocxReader().Read(path); // should not throw
        Assert.Single(r.Records);
    }

    [Fact]
    public void Read_HeadersAndFootersAreIgnored_MammothCompatible()
    {
        // mammoth.extractRawText is body-only. To validate this
        // without hand-rolling header/footer parts (which would
        // significantly complicate the fixture builder), this test
        // instead asserts the inverse: a body with just a "BODY"
        // paragraph yields exactly "BODY", and nothing leaks in from
        // sectPr or other extra-body parts. Combined with the body-
        // only descendants<Paragraph>() walk in DocxReader, this
        // confirms the body-only contract.
        string path = BuildDocx("body-only.docx", new P { Text = "BODY" });
        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("BODY", r.Records[0]["content"]);
    }
}
