using System.Globalization;
using System.IO.Compression;
using System.Text;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

/// <summary>
/// Slice-3 (sub-slice B) PPTX reader parity tests. Each test pins a
/// behavior that was verified empirically against the TS
/// <c>readDatasetFile</c> at slice-3-B pre-flight. The exact ground
/// truth is quoted in each test's doc comment so the rule can be
/// re-derived without re-probing node. Probe artifacts live in
/// <c>~/.copilot/session-state/.../pptx-probe/</c>.
///
/// Fixtures are constructed in-memory by writing minimal OPC zip
/// packages — same pattern as <see cref="DocxReaderTests"/>.
/// </summary>
public class PptxReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public PptxReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-pptx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        // Best-effort cleanup so opt-in env var doesn't leak across
        // tests if a test was interrupted mid-run.
        Environment.SetEnvironmentVariable(PptxReader.IncludeMasterEnvVar, null);
        if (Directory.Exists(_tmpDir))
        {
            try { Directory.Delete(_tmpDir, recursive: true); }
            catch { /* tolerate locked entries on Windows */ }
        }
        GC.SuppressFinalize(this);
    }

    // ===== Fixture builders =====

    /// <summary>Description of one shape inside a slide.</summary>
    private sealed record Sp
    {
        public string? Ph { get; init; }      // "title" | "ctrTitle" | null
        public string?[]? Paras { get; init; } // null entries → <a:p/>, otherwise <a:p><a:r><a:t>...</a:t></a:r></a:p>
        public string? RawXml { get; init; }  // raw <p:sp>...</p:sp> override
    }

    /// <summary>Description of one slide.</summary>
    private sealed record Sl
    {
        public Sp[]? Shapes { get; init; }
        public string? RawSpTree { get; init; }
        public bool Hidden { get; init; }
        public string? Notes { get; init; }
    }

    private string BuildPptx(string name, params Sl[] slides)
    {
        return BuildPptx(name, slides, master: null, layout: null, skipPresentationRels: false);
    }

    private string BuildPptx(
        string name,
        Sl[] slides,
        string? master = null,
        string? layout = null,
        bool skipPresentationRels = false,
        IReadOnlyList<(string Rid, string Target)>? customPresentationRels = null,
        IReadOnlyList<string>? customSlideFileNames = null)
    {
        string path = Path.Combine(_tmpDir, name);
        if (File.Exists(path)) File.Delete(path);

        bool hasMaster = master is not null;
        var slideFileNames = customSlideFileNames ?? Enumerable.Range(1, slides.Length).Select(i => $"slide{i}.xml").ToList();

        // Content types
        var ct = new StringBuilder();
        ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        ct.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        ct.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>");
        foreach (var fn in slideFileNames)
        {
            ct.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/ppt/slides/{fn}\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        }
        for (int i = 0; i < slides.Length; i++)
        {
            if (slides[i].Notes is not null)
            {
                ct.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/ppt/notesSlides/notesSlide{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml\"/>");
            }
        }
        if (hasMaster)
        {
            ct.Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>");
            ct.Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>");
        }
        ct.Append("</Types>");

        const string rootRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
            "</Relationships>";

        // presentation.xml — sldIdLst order = rId1, rId2, ...
        var pres = new StringBuilder();
        pres.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        pres.Append("<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        pres.Append("<p:sldIdLst>");
        for (int i = 0; i < slides.Length; i++)
        {
            pres.Append(CultureInfo.InvariantCulture, $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 1}\"/>");
        }
        pres.Append("</p:sldIdLst>");
        pres.Append("</p:presentation>");

        // presentation rels — map rId1..N to slideN.xml unless overridden
        string presRels;
        if (customPresentationRels is not null)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            foreach (var (rid, target) in customPresentationRels)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"{rid}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"{target}\"/>");
            }
            sb.Append("</Relationships>");
            presRels = sb.ToString();
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < slides.Length; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/{slideFileNames[i]}\"/>");
            }
            sb.Append("</Relationships>");
            presRels = sb.ToString();
        }

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "[Content_Types].xml", ct.ToString());
            AddEntry(zip, "_rels/.rels", rootRels);
            AddEntry(zip, "ppt/presentation.xml", pres.ToString());
            if (!skipPresentationRels)
            {
                AddEntry(zip, "ppt/_rels/presentation.xml.rels", presRels);
            }

            for (int i = 0; i < slides.Length; i++)
            {
                Sl s = slides[i];
                string slideXml = BuildSlideXml(s);
                AddEntry(zip, "ppt/slides/" + slideFileNames[i], slideXml);

                string notesTarget = s.Notes is not null ? $"../notesSlides/notesSlide{i + 1}.xml" : "";
                string slideRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    (notesTarget.Length > 0
                        ? "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide\" Target=\"" + notesTarget + "\"/>"
                        : "") +
                    "</Relationships>";
                AddEntry(zip, "ppt/slides/_rels/" + slideFileNames[i] + ".rels", slideRels);

                if (s.Notes is not null)
                {
                    string notesXml =
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<p:notes xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                        "<p:cSld><p:spTree>" +
                        "<p:sp><p:txBody><a:p><a:r><a:t>" + EscapeXml(s.Notes) + "</a:t></a:r></a:p></p:txBody></p:sp>" +
                        "</p:spTree></p:cSld></p:notes>";
                    AddEntry(zip, "ppt/notesSlides/notesSlide" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", notesXml);
                }
            }

            if (master is not null)
            {
                AddEntry(zip, "ppt/slideMasters/slideMaster1.xml", master);
                AddEntry(zip, "ppt/slideLayouts/slideLayout1.xml",
                    layout ?? "<?xml version=\"1.0\"?><p:sldLayout xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree/></p:cSld></p:sldLayout>");
            }
        }

        return path;
    }

    private static string BuildSlideXml(Sl slide)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<p:sld");
        if (slide.Hidden) sb.Append(" show=\"0\"");
        sb.Append(" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"");
        sb.Append(" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"");
        sb.Append(" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">");
        sb.Append("<p:cSld><p:spTree>");
        if (slide.RawSpTree is not null)
        {
            sb.Append(slide.RawSpTree);
        }
        else
        {
            foreach (Sp s in slide.Shapes ?? Array.Empty<Sp>())
            {
                if (s.RawXml is not null) { sb.Append(s.RawXml); continue; }
                sb.Append("<p:sp>");
                if (s.Ph is not null)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<p:nvSpPr><p:nvPr><p:ph type=\"{s.Ph}\"/></p:nvPr></p:nvSpPr>");
                }
                sb.Append("<p:txBody>");
                foreach (string? para in s.Paras ?? Array.Empty<string>())
                {
                    if (para is null) sb.Append("<a:p/>");
                    else sb.Append("<a:p><a:r><a:t>" + EscapeXml(para) + "</a:t></a:r></a:p>");
                }
                sb.Append("</p:txBody>");
                sb.Append("</p:sp>");
            }
        }
        sb.Append("</p:spTree></p:cSld></p:sld>");
        return sb.ToString();
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

    private static long N(object? v) =>
        v switch { long l => l, int i => i, _ => Convert.ToInt64(v, CultureInfo.InvariantCulture) };

    // ===== Tests =====

    /// <summary>
    /// Probe <c>simple-2-slide-titled</c>: two slides, each with title
    /// placeholder + body shape. Expect 2 records with correct
    /// slide_number, title, content fields.
    /// </summary>
    [Fact]
    public void Read_SimpleTwoSlideTitled()
    {
        string path = BuildPptx("simple-2-slide-titled.pptx",
            new Sl { Shapes = new[]
            {
                new Sp { Ph = "title", Paras = new[] { "Title A" } },
                new Sp { Paras = new[] { "Body line 1", "Body line 2" } },
            }},
            new Sl { Shapes = new[]
            {
                new Sp { Ph = "title", Paras = new[] { "Title B" } },
                new Sp { Paras = new[] { "Other body" } },
            }});

        var r = new PptxReader().Read(path);
        Assert.Equal(InputFormat.Pptx, r.Format);
        Assert.Equal(2, r.Records.Count);

        Assert.Equal(1L, N(r.Records[0]["slide_number"]));
        Assert.Equal("Title A", r.Records[0]["title"]);
        Assert.Equal("Body line 1\nBody line 2", r.Records[0]["content"]);
        Assert.Equal(string.Empty, r.Records[0]["notes"]);

        Assert.Equal(2L, N(r.Records[1]["slide_number"]));
        Assert.Equal("Title B", r.Records[1]["title"]);
        Assert.Equal("Other body", r.Records[1]["content"]);
    }

    /// <summary>
    /// Probe <c>no-title-placeholder</c>: shapes without a
    /// <c>&lt;p:ph type="title"&gt;</c> fall back to the first
    /// non-empty paragraph in document order.
    /// </summary>
    [Fact]
    public void Read_NoTitlePlaceholder_FirstParagraphBecomesTitle()
    {
        string path = BuildPptx("no-title-placeholder.pptx",
            new Sl { Shapes = new[]
            {
                new Sp { Paras = new[] { "First para becomes title" } },
                new Sp { Paras = new[] { "Body line" } },
            }});

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("First para becomes title", r.Records[0]["title"]);
        Assert.Equal("Body line", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>title-not-first</c>: when a title placeholder shape
    /// appears AFTER a non-placeholder body shape in document order,
    /// the placeholder still wins title selection. The earlier body
    /// shape's paragraph becomes part of body content instead.
    /// </summary>
    [Fact]
    public void Read_TitleNotFirst_PlaceholderWinsOverDocumentOrder()
    {
        string path = BuildPptx("title-not-first.pptx",
            new Sl { Shapes = new[]
            {
                new Sp { Paras = new[] { "Body shape first in document order" } },
                new Sp { Ph = "title", Paras = new[] { "Actual Title (placeholder)" } },
                new Sp { Paras = new[] { "Trailing body" } },
            }});

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("Actual Title (placeholder)", r.Records[0]["title"]);
        Assert.Equal("Body shape first in document order\nTrailing body", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>ctrTitle-variant</c>: <c>type="ctrTitle"</c> is also
    /// recognized as a title placeholder.
    /// </summary>
    [Fact]
    public void Read_CtrTitlePlaceholder_IsRecognizedAsTitle()
    {
        string path = BuildPptx("ctrTitle-variant.pptx",
            new Sl { Shapes = new[]
            {
                new Sp { Paras = new[] { "Decoration shape" } },
                new Sp { Ph = "ctrTitle", Paras = new[] { "Center Title" } },
            }});

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("Center Title", r.Records[0]["title"]);
        Assert.Equal("Decoration shape", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>multiple-title-placeholders</c>: when more than one
    /// title placeholder exists, the FIRST one in document order wins.
    /// </summary>
    [Fact]
    public void Read_MultipleTitlePlaceholders_FirstWins()
    {
        string path = BuildPptx("multi-title.pptx",
            new Sl { Shapes = new[]
            {
                new Sp { Ph = "title", Paras = new[] { "First Title" } },
                new Sp { Ph = "title", Paras = new[] { "Second Title" } },
                new Sp { Paras = new[] { "Body" } },
            }});

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("First Title", r.Records[0]["title"]);
        Assert.Equal("Second Title\nBody", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>with-notes</c>: a notes part referenced via the slide
    /// rels populates the <c>notes</c> field with concatenated +
    /// trimmed paragraph text.
    /// </summary>
    [Fact]
    public void Read_WithNotes_PopulatesNotesField()
    {
        string path = BuildPptx("with-notes.pptx",
            new Sl
            {
                Shapes = new[]
                {
                    new Sp { Ph = "title", Paras = new[] { "Slide 1" } },
                    new Sp { Paras = new[] { "Body" } },
                },
                Notes = "Speaker note line.",
            });

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("Speaker note line.", r.Records[0]["notes"]);
    }

    /// <summary>
    /// Probe <c>hidden-slide-still-counted</c>: <c>&lt;p:sld
    /// show="0"&gt;</c> slides are emitted with their correct
    /// slide_number — hidden flag is not consulted.
    /// </summary>
    [Fact]
    public void Read_HiddenSlide_IsStillIncluded()
    {
        string path = BuildPptx("hidden.pptx",
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Visible 1" } } } },
            new Sl { Hidden = true, Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Hidden 2" } } } },
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Visible 3" } } } });

        var r = new PptxReader().Read(path);
        Assert.Equal(3, r.Records.Count);
        Assert.Equal(1L, N(r.Records[0]["slide_number"]));
        Assert.Equal(2L, N(r.Records[1]["slide_number"]));
        Assert.Equal(3L, N(r.Records[2]["slide_number"]));
        Assert.Equal("Hidden 2", r.Records[1]["title"]);
    }

    /// <summary>
    /// Probe <c>empty-slide-skipped-but-numbered</c>: a slide with no
    /// extractable text is dropped from records, but the slide_number
    /// ordinal still advances — producing a gap. Probe ground truth:
    /// 3-slide deck where slide 2 is empty emits records with
    /// slide_number 1 and 3.
    /// </summary>
    [Fact]
    public void Read_EmptySlide_IsSkippedButOrdinalPreserved()
    {
        string path = BuildPptx("empty-skip.pptx",
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "First" } } } },
            new Sl { Shapes = Array.Empty<Sp>() },  // truly empty: no shapes
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Third (numbered 3)" } } } });

        var r = new PptxReader().Read(path);
        Assert.Equal(2, r.Records.Count);
        Assert.Equal(1L, N(r.Records[0]["slide_number"]));
        Assert.Equal(3L, N(r.Records[1]["slide_number"]));
        Assert.Equal("Third (numbered 3)", r.Records[1]["title"]);
    }

    /// <summary>
    /// Probe <c>shape-with-empty-paragraphs</c>: paragraphs whose
    /// trimmed text is empty (including <c>&lt;a:p/&gt;</c> and
    /// whitespace-only) are dropped before being added as entries.
    /// </summary>
    [Fact]
    public void Read_EmptyParagraphsWithinShape_AreFiltered()
    {
        string path = BuildPptx("shape-empty.pptx",
            new Sl { Shapes = new[]
            {
                new Sp { Ph = "title", Paras = new[] { "T" } },
                new Sp { Paras = new[] { (string?)null, "  ", "real content" } },
            }});

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("T", r.Records[0]["title"]);
        Assert.Equal("real content", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>grouped-shape</c>: <c>&lt;p:grpSp&gt;</c> is walked
    /// transparently and its child <c>&lt;p:sp&gt;</c> elements
    /// contribute paragraphs. Outer title shape supplies the title;
    /// the two grouped child paragraphs become body content.
    /// </summary>
    [Fact]
    public void Read_GroupedShape_ChildrenContributeText()
    {
        string rawSpTree =
            "<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
            "<p:txBody><a:p><a:r><a:t>Group Test</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:grpSp>" +
              "<p:sp><p:txBody><a:p><a:r><a:t>Grouped child 1</a:t></a:r></a:p></p:txBody></p:sp>" +
              "<p:sp><p:txBody><a:p><a:r><a:t>Grouped child 2</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:grpSp>";
        string path = BuildPptx("grouped.pptx",
            new Sl { RawSpTree = rawSpTree });

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("Group Test", r.Records[0]["title"]);
        Assert.Equal("Grouped child 1\nGrouped child 2", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>alternate-content-in-slide</c>: BOTH
    /// <c>&lt;mc:Choice&gt;</c> AND <c>&lt;mc:Fallback&gt;</c>
    /// branches contribute text — this is the OPPOSITE of the DOCX
    /// reader's Fallback-only behavior, and reflects the unstructured
    /// tree walk done by the TS reader. Reimplementations must mirror
    /// this exactly because existing eval datasets encode the
    /// double-extracted content.
    /// </summary>
    [Fact]
    public void Read_AlternateContent_BothBranchesExtracted()
    {
        string rawSpTree =
            "<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
            "<p:txBody><a:p><a:r><a:t>AltCt Title</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<mc:AlternateContent>" +
              "<mc:Choice Requires=\"future\">" +
                "<p:sp><p:txBody><a:p><a:r><a:t>CHOICE text</a:t></a:r></a:p></p:txBody></p:sp>" +
              "</mc:Choice>" +
              "<mc:Fallback>" +
                "<p:sp><p:txBody><a:p><a:r><a:t>FALLBACK text</a:t></a:r></a:p></p:txBody></p:sp>" +
              "</mc:Fallback>" +
            "</mc:AlternateContent>";
        string path = BuildPptx("alt-content.pptx",
            new Sl { RawSpTree = rawSpTree });

        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("AltCt Title", r.Records[0]["title"]);
        Assert.Equal("CHOICE text\nFALLBACK text", r.Records[0]["content"]);
    }

    /// <summary>
    /// Probe <c>master-opt-in</c>: when
    /// <c>EVALGEN_PPTX_INCLUDE_MASTER=true</c>, the reader appends a
    /// trailer record with <c>slide_number=0</c> and
    /// <c>title="(slide master / layout)"</c>, containing the joined
    /// text of every <c>&lt;a:p&gt;</c> across slide master and
    /// layout parts.
    /// </summary>
    [Fact]
    public void Read_MasterOptIn_AppendsTrailerRecord()
    {
        const string master =
            "<?xml version=\"1.0\"?>" +
            "<p:sldMaster xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            "<p:cSld><p:spTree>" +
            "<p:sp><p:txBody><a:p><a:r><a:t>MASTER footer text</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:spTree></p:cSld></p:sldMaster>";
        const string layout =
            "<?xml version=\"1.0\"?>" +
            "<p:sldLayout xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            "<p:cSld><p:spTree>" +
            "<p:sp><p:txBody><a:p><a:r><a:t>LAYOUT text</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:spTree></p:cSld></p:sldLayout>";

        string path = BuildPptx("master-opt.pptx",
            new[] { new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Slide 1" } } } } },
            master: master,
            layout: layout);

        Environment.SetEnvironmentVariable(PptxReader.IncludeMasterEnvVar, "true");
        try
        {
            var r = new PptxReader().Read(path);
            Assert.Equal(2, r.Records.Count);
            Assert.Equal(1L, N(r.Records[0]["slide_number"]));
            Assert.Equal("Slide 1", r.Records[0]["title"]);

            Assert.Equal(0L, N(r.Records[1]["slide_number"]));
            Assert.Equal(PptxReader.MasterLayoutTitle, r.Records[1]["title"]);
            Assert.Equal("MASTER footer text\nLAYOUT text", r.Records[1]["content"]);
            Assert.Equal(string.Empty, r.Records[1]["notes"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PptxReader.IncludeMasterEnvVar, null);
        }
    }

    /// <summary>
    /// Probe <c>master-opt-in</c> negative: without the opt-in env
    /// var, master/layout text is suppressed even when present in the
    /// package.
    /// </summary>
    [Fact]
    public void Read_MasterPresentButOptOutByDefault_NoTrailerRecord()
    {
        const string master =
            "<?xml version=\"1.0\"?>" +
            "<p:sldMaster xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            "<p:cSld><p:spTree>" +
            "<p:sp><p:txBody><a:p><a:r><a:t>MASTER footer text</a:t></a:r></a:p></p:txBody></p:sp>" +
            "</p:spTree></p:cSld></p:sldMaster>";
        string path = BuildPptx("master-default.pptx",
            new[] { new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Slide 1" } } } } },
            master: master);

        // Defensive: ensure env var is unset for this test.
        Environment.SetEnvironmentVariable(PptxReader.IncludeMasterEnvVar, null);
        var r = new PptxReader().Read(path);
        Assert.Single(r.Records);
        Assert.Equal("Slide 1", r.Records[0]["title"]);
    }

    /// <summary>
    /// Probe <c>fallback-numeric-order</c>: a deck WITHOUT
    /// <c>ppt/_rels/presentation.xml.rels</c> still parses by scanning
    /// for <c>ppt/slides/slideN.xml</c> entries and sorting
    /// numerically by N.
    /// </summary>
    [Fact]
    public void Read_NoPresentationRels_FallsBackToNumericFilenameOrder()
    {
        string path = BuildPptx("no-rels.pptx",
            new[]
            {
                new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "First slide (slide1.xml)" } } } },
                new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Second slide (slide2.xml)" } } } },
            },
            skipPresentationRels: true);

        var r = new PptxReader().Read(path);
        Assert.Equal(2, r.Records.Count);
        Assert.Equal("First slide (slide1.xml)", r.Records[0]["title"]);
        Assert.Equal("Second slide (slide2.xml)", r.Records[1]["title"]);
    }

    /// <summary>
    /// Probe <c>reversed-slide-order-via-rels</c>: when
    /// <c>presentation.xml.rels</c> points rId1 → <c>slide2.xml</c> and
    /// rId2 → <c>slide1.xml</c>, the output order follows the rels
    /// resolution — slide_number=1 contains the text from physical
    /// file <c>slide2.xml</c>. Pins the contract that rId-resolved
    /// order takes precedence over filename-numeric order.
    /// </summary>
    [Fact]
    public void Read_RelsResolutionOverridesFilenameOrder()
    {
        // Build slide1.xml with text "Title in slide1.xml file" and
        // slide2.xml with "Title in slide2.xml file", but rels remaps
        // rId1 → slide2.xml.
        var slides = new[]
        {
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Title in slide1.xml file" } } } },
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Title in slide2.xml file" } } } },
        };
        string path = BuildPptx("rels-reversed.pptx",
            slides,
            customPresentationRels: new[]
            {
                ("rId1", "slides/slide2.xml"),
                ("rId2", "slides/slide1.xml"),
            });

        var r = new PptxReader().Read(path);
        Assert.Equal(2, r.Records.Count);
        Assert.Equal(1L, N(r.Records[0]["slide_number"]));
        Assert.Equal("Title in slide2.xml file", r.Records[0]["title"]);
        Assert.Equal(2L, N(r.Records[1]["slide_number"]));
        Assert.Equal("Title in slide1.xml file", r.Records[1]["title"]);
    }

    /// <summary>
    /// Defensive contract: a package with no slides at all throws
    /// <c>"No slides found in PPTX file"</c>.
    /// </summary>
    [Fact]
    public void Read_NoSlidesAtAll_Throws()
    {
        string path = Path.Combine(_tmpDir, "noslides.pptx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
                "</Types>");
            AddEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
                "</Relationships>");
            AddEntry(zip, "ppt/presentation.xml",
                "<?xml version=\"1.0\"?><p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:sldIdLst/></p:presentation>");
        }
        var ex = Assert.Throws<InvalidOperationException>(() => new PptxReader().Read(path));
        Assert.Equal("No slides found in PPTX file", ex.Message);
    }

    /// <summary>
    /// Defensive contract: a deck with slides that all produce empty
    /// text (and no master opt-in) throws
    /// <c>"No text content found in PPTX file"</c>.
    /// </summary>
    [Fact]
    public void Read_AllSlidesEmpty_Throws()
    {
        string path = BuildPptx("all-empty.pptx",
            new Sl { Shapes = Array.Empty<Sp>() },
            new Sl { Shapes = Array.Empty<Sp>() });

        var ex = Assert.Throws<InvalidOperationException>(() => new PptxReader().Read(path));
        Assert.Equal("No text content found in PPTX file", ex.Message);
    }

    /// <summary>
    /// Field order is part of the contract because writers and the
    /// parity harness compare serialized wire output. Verifies
    /// insertion order is exactly: <c>slide_number</c>, <c>title</c>,
    /// <c>content</c>, <c>notes</c>.
    /// </summary>
    [Fact]
    public void Read_FieldOrder_MatchesTsLiteral()
    {
        string path = BuildPptx("field-order.pptx",
            new Sl
            {
                Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "X" } } },
                Notes = "n",
            });

        var r = new PptxReader().Read(path);
        var keys = r.Records[0].Entries.Select(e => e.Key).ToArray();
        Assert.Equal(new[] { "slide_number", "title", "content", "notes" }, keys);
    }

    /// <summary>
    /// Integration with <see cref="DatasetReader.ReadDatasetFile"/>:
    /// <c>.pptx</c> dispatches to <see cref="PptxReader"/> and the
    /// orchestrator stamps <c>_source_file</c>.
    /// </summary>
    [Fact]
    public void Dispatch_PptxExtension_GoesToPptxReader()
    {
        string path = BuildPptx("dispatch.pptx",
            new Sl { Shapes = new[] { new Sp { Ph = "title", Paras = new[] { "Hello" } } } });

        var r = DatasetReader.ReadDatasetFile(path);
        Assert.Equal(InputFormat.Pptx, r.Format);
        Assert.Single(r.Records);
        Assert.Equal("Hello", r.Records[0]["title"]);
        Assert.Equal("dispatch.pptx", r.Records[0]["_source_file"]);
        // _source_file appended after the 4 reader-emitted fields.
        Assert.Equal(
            new[] { "slide_number", "title", "content", "notes", "_source_file" },
            r.Records[0].Entries.Select(e => e.Key).ToArray());
    }

    /// <summary>
    /// <c>ParseBoolEnv</c> matches the TS truthy set:
    /// <c>"true"</c> / <c>"1"</c> / <c>"yes"</c> / <c>"on"</c>
    /// (case-insensitive) → true; everything else → false.
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void ParseBoolEnv_Matches(string? raw, bool expected)
    {
        Assert.Equal(expected, PptxReader.ParseBoolEnv(raw));
    }
}
