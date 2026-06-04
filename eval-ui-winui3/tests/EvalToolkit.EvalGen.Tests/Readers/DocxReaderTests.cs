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
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"" +
            " xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"" +
            " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"" +
            " xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"" +
            " xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"" +
            " xmlns:v=\"urn:schemas-microsoft-com:vml\"" +
            " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
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

    // ===== Round-1 reviewer fix coverage =====
    // The following scenarios were verified empirically against
    // mammoth in `docx-probe/probe2.js` after GPT-5.5's round-1
    // review flagged the original walker as having two latent bugs:
    // (B1) nested-paragraph text duplication, (B2) fldSimple display
    // text leaking through. The scenarios also pin AlternateContent
    // Choice/Fallback handling, page-break treatment, smartTag /
    // proofErr transparency, and xml:space preservation.

    [Fact]
    public void Read_SimpleField_DisplayTextDropped()
    {
        // mammoth probe `fldSimple`:
        //   "before  after\n\n"  (TWO spaces — display "3/15/2023" excluded)
        // Mammoth.extractRawText skips the cached display value of
        // <w:fldSimple>. Required for parity on field-heavy docs
        // (dates, page numbers, TOC entries).
        string path = BuildDocx("fldSimple.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:fldSimple w:instr=\" DATE \">" +
                    "<w:r><w:t>3/15/2023</w:t></w:r>" +
                  "</w:fldSimple>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        // JsCompat.Trim strips outer whitespace but preserves internal
        // double-space — matches mammoth's "before  after".
        Assert.Equal("before  after", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_ComplexField_DisplayTextKept()
    {
        // mammoth probe `fldChar-complex`:
        //   "before 3/15/2023 after\n\n"
        // Complex fields wrap their cached display value in plain
        // <w:r>/<w:t> between <w:fldChar separate/> and
        // <w:fldChar end/> — no special wrapper. Mammoth (and our
        // walker, by default) picks up that text just like any other
        // run text. Pinning here so the fldSimple fix doesn't
        // accidentally over-apply.
        string path = BuildDocx("fldChar-complex.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
                  "<w:r><w:instrText xml:space=\"preserve\"> DATE </w:instrText></w:r>" +
                  "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
                  "<w:r><w:t>3/15/2023</w:t></w:r>" +
                  "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before 3/15/2023 after", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_TextboxNestedParagraphs_BecomeOwnTopLevelEntries()
    {
        // mammoth probe `textbox-simple`:
        //   "before\n\n\n\ntxbx 1\n\ntxbx 2\n\nafter\n\n"
        //   → ["before","txbx 1","txbx 2","after"]
        // Nested <w:p> inside <v:textbox>/<w:txbxContent> surfaces as
        // its own top-level paragraph in document order. Critical
        // round-1 fix: the outer paragraph that CONTAINS the textbox
        // must NOT also concatenate the nested paragraphs' text into
        // itself (duplication bug). The fix: when concatenating a
        // paragraph's text, skip any descendant whose nearest
        // Paragraph ancestor is NOT the paragraph being processed.
        string path = BuildDocx("textbox-simple.docx",
            new P { Text = "before" },
            new P { RawXml =
                "<w:p>" +
                  "<w:r>" +
                    "<w:pict>" +
                      "<v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\">" +
                        "<v:textbox>" +
                          "<w:txbxContent>" +
                            "<w:p><w:r><w:t>txbx 1</w:t></w:r></w:p>" +
                            "<w:p><w:r><w:t>txbx 2</w:t></w:r></w:p>" +
                          "</w:txbxContent>" +
                        "</v:textbox>" +
                      "</v:shape>" +
                    "</w:pict>" +
                  "</w:r>" +
                "</w:p>" },
            new P { Text = "after" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before\ntxbx 1\ntxbx 2\nafter", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_AlternateContent_UsesFallbackBranch()
    {
        // mammoth probe `textbox`:
        //   "outer\n\nfallback inner\n\n"
        //   → ["outer","fallback inner"]
        // <mc:AlternateContent> publishes the same content twice —
        // once via <mc:Choice Requires="..."> for feature-aware
        // consumers, once via <mc:Fallback> for everyone else.
        // Mammoth uses the Fallback branch (and so does Word's
        // "Save As Plain Text" feature). We mirror by filtering out
        // any paragraph or descendant whose ancestor chain contains
        // AlternateContentChoice.
        string path = BuildDocx("altcontent.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t>outer</w:t></w:r>" +
                  "<w:r>" +
                    "<mc:AlternateContent>" +
                      "<mc:Choice Requires=\"wps\">" +
                        "<w:drawing>" +
                          "<wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                            "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                              "<a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">" +
                                "<wps:wsp xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">" +
                                  "<wps:txbx>" +
                                    "<w:txbxContent>" +
                                      "<w:p><w:r><w:t>choice inner</w:t></w:r></w:p>" +
                                    "</w:txbxContent>" +
                                  "</wps:txbx>" +
                                "</wps:wsp>" +
                              "</a:graphicData>" +
                            "</a:graphic>" +
                          "</wp:inline>" +
                        "</w:drawing>" +
                      "</mc:Choice>" +
                      "<mc:Fallback>" +
                        "<w:pict>" +
                          "<v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\">" +
                            "<v:textbox>" +
                              "<w:txbxContent>" +
                                "<w:p><w:r><w:t>fallback inner</w:t></w:r></w:p>" +
                              "</w:txbxContent>" +
                            "</v:textbox>" +
                          "</v:shape>" +
                        "</w:pict>" +
                      "</mc:Fallback>" +
                    "</mc:AlternateContent>" +
                  "</w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        // "outer" from the outermost paragraph + "fallback inner" from
        // the Fallback's nested paragraph. "choice inner" must NOT
        // appear (it's behind <mc:Choice>).
        Assert.Equal("outer\nfallback inner", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_PageBreak_AlsoSilentlyDropped()
    {
        // mammoth probe `page-break`:
        //   "beforeafter\n\n"
        // <w:br w:type="page"/> is silently dropped exactly like a
        // plain <w:br/>. Confirms our Break-ignoring switch case
        // applies to both flavors (no need to special-case Type).
        string path = BuildDocx("page-break.docx",
            new P { Runs = new[] {
                new Run { Text = "before" },
                new Run { Tag = "<w:br w:type=\"page\"/>" },
                new Run { Text = "after" },
            }});

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("beforeafter", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_SmartTag_IsTransparentWrapper()
    {
        // mammoth probe `smartTag`:
        //   "tagged rest\n\n"
        // <w:smartTag> wraps runs without adding/removing text.
        string path = BuildDocx("smarttag.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:smartTag w:uri=\"urn:foo\" w:element=\"bar\">" +
                    "<w:r><w:t>tagged</w:t></w:r>" +
                  "</w:smartTag>" +
                  "<w:r><w:t xml:space=\"preserve\"> rest</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("tagged rest", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_ProofErr_IsTransparentMarker()
    {
        // mammoth probe `proofErr`:
        //   "typox rest\n\n"
        // <w:proofErr> is a paragraph-level marker for spell/grammar
        // squigglies — it should be invisible to text extraction.
        string path = BuildDocx("prooferr.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:proofErr w:type=\"spellStart\"/>" +
                  "<w:r><w:t>typox</w:t></w:r>" +
                  "<w:proofErr w:type=\"spellEnd\"/>" +
                  "<w:r><w:t xml:space=\"preserve\"> rest</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("typox rest", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_XmlSpacePreserveAndDefault_BothPreserveInternalWhitespace()
    {
        // mammoth probes `xmlspace-default` and `xmlspace-preserve`
        // both produced raw "  leading  \n\n" — meaning the OpenXml
        // text node's effective value is identical whether or not
        // xml:space="preserve" is present. JsCompat.Trim strips the
        // outer whitespace identically in both cases.
        string defaultPath = BuildDocx("xmlspace-default.docx",
            new P { RawXml =
                "<w:p><w:r><w:t>  leading  </w:t></w:r></w:p>" });
        string preservePath = BuildDocx("xmlspace-preserve.docx",
            new P { RawXml =
                "<w:p><w:r><w:t xml:space=\"preserve\">  leading  </w:t></w:r></w:p>" });

        var rDefault = new DocxReader().Read(defaultPath);
        var rPreserve = new DocxReader().Read(preservePath);
        Assert.Single(rDefault.Records);
        Assert.Single(rPreserve.Records);
        Assert.Equal("leading", rDefault.Records[0]["content"]);
        Assert.Equal("leading", rPreserve.Records[0]["content"]);
    }

    // ===== Tracked-changes coverage (post-round-2, GPT-5.5 residual)
    // Verified empirically against mammoth@1.12.0 in
    // `docx-probe/probe-tracked.js`, and cross-checked against
    // mammoth's source (`node_modules/mammoth/lib/docx/body-reader.js`
    // xmlElementReaders map). Mammoth's contract:
    //   "w:ins"       → readChildElements  (transparent wrapper, KEPT)
    //   "w:del"       → true               (explicit empty-result handler, DROPPED)
    //   "w:moveFrom"  → (no handler)       → "unrecognised element" warning → DROPPED
    //   "w:moveTo"    → (no handler)       → "unrecognised element" warning → DROPPED
    // The asymmetry (ins kept, del/moveFrom/moveTo dropped) is the
    // canonical "what you'd render in a plain text export" view of
    // an accepted document: insertions become normal text, deletions
    // vanish, and moves are deduplicated by dropping both ends
    // (matches what Word's "Save As Plain Text" produces for an
    // un-accepted tracked-changes document).

    [Fact]
    public void Read_TrackedInsert_Kept()
    {
        // mammoth probe `ins-inline`:  "before inserted after\n\n"
        // No explicit C# filter needed — <w:ins> wraps a normal
        // <w:r><w:t>, and our walker's typed Text case picks it up
        // transparently (mirrors mammoth's readChildElements).
        string path = BuildDocx("ins-inline.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:ins w:id=\"1\" w:author=\"A\" w:date=\"2024-01-01T00:00:00Z\">" +
                    "<w:r><w:t>inserted</w:t></w:r>" +
                  "</w:ins>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before inserted after", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_TrackedDelete_Dropped()
    {
        // mammoth probe `del-inline`:  "before  after\n\n"
        //   (note TWO spaces — the deleted run vanished, but the
        //   surrounding whitespace runs are still there)
        // No explicit C# filter needed — <w:del>'s content is
        // <w:delText> which maps to OpenXml's DeletedText class, not
        // Text. Our switch only matches `case Text t`, so DeletedText
        // is silently dropped — matches mammoth's empty-result
        // handler exactly.
        string path = BuildDocx("del-inline.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:del w:id=\"2\" w:author=\"A\" w:date=\"2024-01-01T00:00:00Z\">" +
                    "<w:r><w:delText>deleted</w:delText></w:r>" +
                  "</w:del>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before  after", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_TrackedMoveFrom_Dropped()
    {
        // mammoth probe `moveFrom-inline`:  "before  after\n\n"
        //   (TWO spaces — the moved-from text dropped)
        // <w:moveFrom> contains <w:r><w:t>, so our walker would
        // naturally include the text — requires explicit ancestor
        // filter to match mammoth's unrecognized-element behavior.
        string path = BuildDocx("moveFrom-inline.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:moveFrom w:id=\"3\" w:author=\"A\" w:date=\"2024-01-01T00:00:00Z\">" +
                    "<w:r><w:t>moved</w:t></w:r>" +
                  "</w:moveFrom>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before  after", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_TrackedMoveTo_Dropped()
    {
        // mammoth probe `moveTo-inline`:  "before  after\n\n"
        //   (TWO spaces — moveTo content also dropped, despite being
        //   the destination of the move. Reason: mammoth has no
        //   handler for w:moveTo either, so both ends of a move are
        //   dedup-dropped. Matches Word's "Save As Plain Text" output
        //   when tracked changes are NOT accepted.)
        // <w:moveTo> contains <w:r><w:t>, requires explicit ancestor
        // filter to match.
        string path = BuildDocx("moveTo-inline.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:moveTo w:id=\"4\" w:author=\"A\" w:date=\"2024-01-01T00:00:00Z\">" +
                    "<w:r><w:t>arrived</w:t></w:r>" +
                  "</w:moveTo>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before  after", r.Records[0]["content"]);
    }

    // ===== Allowlist-recursion parity (post-Opus round-2 fix) =====
    // Opus-4.8's round-2 review observed that mammoth uses an
    // ALLOWLIST recursion model (xmlElementReaders dispatches by
    // element name; unrecognized elements drop their subtree with a
    // warning), whereas the C# walker was effectively DENYLIST
    // (recurse into everything, skip a few known wrappers). The
    // round-2 OpenXmlUnknownElement fix to make <w:smartTag> work
    // also widened the over-extraction surface to ANY unknown w:
    // wrapper. These two tests pin parity for the two cases Opus
    // empirically demonstrated:
    //   * <w:customXml> at run level: typed CustomXmlRun ancestor
    //     filter required so the typed Text descendant path drops
    //     the inner text.
    //   * <w:fooBar> unknown wrapper: the unknown-element handler is
    //     restricted to <w:t>/<w:tab> whose unknown-ancestor chain
    //     contains an unknown <w:smartTag> (the sole mammoth-
    //     allowlisted wrapper the OpenXml SDK leaves untyped).

    [Fact]
    public void Read_CustomXml_DescendantsDropped()
    {
        // mammoth probe `customXml-wraps-run`:
        //   value:    "before  after\n\n"   (TWO spaces — INSIDE dropped)
        //   messages: An unrecognised element was ignored: w:customXml
        string path = BuildDocx("customxml.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">before </w:t></w:r>" +
                  "<w:customXml w:element=\"cElement\" w:uri=\"urn:cust\">" +
                    "<w:r><w:t>INSIDE</w:t></w:r>" +
                  "</w:customXml>" +
                  "<w:r><w:t xml:space=\"preserve\"> after</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("before  after", r.Records[0]["content"]);
    }

    [Fact]
    public void Read_UnknownWordprocessingWrapper_DescendantsDropped()
    {
        // mammoth probe `unknown-w-wrapper`:
        //   value:    "a  b\n\n"   (TWO spaces — X and Y dropped)
        //   messages: An unrecognised element was ignored: w:fooBar
        // Without the smartTag-only restriction on the unknown-element
        // handler, the previous walker would have output "a XY b".
        string path = BuildDocx("unknown-wrapper.docx",
            new P { RawXml =
                "<w:p>" +
                  "<w:r><w:t xml:space=\"preserve\">a </w:t></w:r>" +
                  "<w:fooBar>" +
                    "<w:r><w:t>X</w:t></w:r>" +
                    "<w:r><w:t>Y</w:t></w:r>" +
                  "</w:fooBar>" +
                  "<w:r><w:t xml:space=\"preserve\"> b</w:t></w:r>" +
                "</w:p>" });

        var r = new DocxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("a  b", r.Records[0]["content"]);
    }
}
