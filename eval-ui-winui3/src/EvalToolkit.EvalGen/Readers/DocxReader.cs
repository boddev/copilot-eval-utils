using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads <c>.docx</c> files to mirror the TS <c>readDocx</c> reader
/// in <c>eval-gen/src/readers/index.ts</c>:
///
/// <code>
/// const buffer = fs.readFileSync(filePath);
/// const { value } = await mammoth.extractRawText({ buffer }) as { value: string };
/// const text = value ?? '';
/// const paragraphs = text.split(/\r?\n/).map(p =&gt; p.trim()).filter(p =&gt; p.length &gt; 0);
/// const records = chunkText(paragraphs);
/// if (records.length === 0) throw new Error('No text content found in DOCX file');
/// </code>
///
/// <para><b>Why DocumentFormat.OpenXml + a hand-written walker rather
/// than a higher-level library?</b> No actively-maintained .NET port
/// of mammoth exists. mammoth's <c>extractRawText</c> is precisely
/// specified — walk the document body, concatenate <c>&lt;w:t&gt;</c>
/// and <c>&lt;w:tab/&gt;</c> text in document order, terminate each
/// <c>&lt;w:p&gt;</c> with <c>\n\n</c>, and silently drop everything
/// else — so the walker is short and trivially auditable. All
/// behaviors below were verified empirically against the
/// <c>mammoth</c> npm package at slice-3 pre-flight (probe artifacts
/// saved in the session-state directory).</para>
///
/// <list type="bullet">
///   <item><b>Body-only.</b> Only paragraphs reachable from
///     <see cref="MainDocumentPart.Document"/>'s <c>Body</c> are
///     extracted. Headers, footers, footnotes, endnotes, and comments
///     are ignored — matching mammoth's behavior.</item>
///   <item><b>Document order via <c>Body.Descendants&lt;Paragraph&gt;()</c></b>
///     to capture paragraphs nested inside tables (each table cell's
///     <c>&lt;w:p&gt;</c> becomes its own entry in source order),
///     <c>&lt;w:sdt&gt;/&lt;w:sdtContent&gt;</c> structured documents,
///     <c>&lt;w:hyperlink&gt;</c> wrappers, etc. — verified via
///     scenario probes named <c>table</c>, <c>sdt</c>,
///     <c>hyperlink</c> in the probe set.</item>
///   <item><b>Per-paragraph concatenation:</b> for each
///     <c>&lt;w:p&gt;</c>, iterate descendants in document order and
///     concatenate every <see cref="Text"/> element's value plus a
///     <c>\t</c> for every <see cref="TabChar"/>. Multi-run paragraphs
///     join run text with <b>no separator</b> — verified probe
///     <c>multi-run</c>: <c>"Hello " + "world" + "."</c> →
///     <c>"Hello world."</c>.</item>
///   <item><b><see cref="Break"/> (<c>&lt;w:br/&gt;</c>) is silently
///     dropped.</b> mammoth's <c>extractRawText</c> emits nothing for
///     line/page breaks — probe <c>br-line</c> verifies the
///     surprising result <c>"line 1line 2"</c> (no separator between
///     pieces sandwiching a <c>&lt;w:br/&gt;</c>). We mirror this
///     exactly. <see cref="ReadDocxKnownDivergences"/> documents the
///     residual.</item>
///   <item><b>Paragraph separator = single <c>\n</c>.</b> mammoth
///     terminates each paragraph with <c>\n\n</c> (two newlines), but
///     the TS reader immediately calls
///     <c>text.split(/\r?\n/).map(p =&gt; p.trim()).filter(p =&gt; p.length &gt; 0)</c>
///     which collapses any run of empty lines down to nothing. We
///     skip the round-trip and emit each paragraph directly into the
///     paragraphs array — equivalent end-state.</item>
///   <item><b>JS-equivalent trim.</b> Trim each paragraph via
///     <see cref="JsCompat.Trim(string)"/> (NOT .NET
///     <see cref="string.Trim()"/>) so U+FEFF / U+0085 behave
///     identically to ECMAScript. Verified probe
///     <c>whitespace-runs</c>: <c>"  leading"</c> → <c>"leading"</c>,
///     <c>"  both  "</c> → <c>"both"</c>.</item>
///   <item><b>Drop empties.</b> After trim, paragraphs whose length
///     drops to zero are filtered out before chunking — matches the
///     TS <c>.filter(p =&gt; p.length &gt; 0)</c>.</item>
///   <item><b>Chunking.</b> Pass the paragraph list to
///     <see cref="TextChunker.Chunk(IEnumerable{string})"/> which is
///     the byte-equivalent C# port of the TS <c>chunkText</c> helper
///     (500-char target, greedy-pack, oversize-paragraph-emits-alone,
///     join-with-<c>\n</c>).</item>
///   <item><b>Empty-content error.</b> Throws
///     <see cref="InvalidOperationException"/> with the exact TS
///     message <c>"No text content found in DOCX file"</c> when
///     chunking yields zero records.</item>
/// </list>
/// </summary>
public sealed class DocxReader : IDatasetReader
{
    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var paragraphs = new List<string>();

        // Open in read-only mode (no editing, no autosave). Using a
        // FileStream with read-only access lets the file co-exist with
        // other readers and avoids the "file is in use" surprise that
        // bites WordprocessingDocument.Open(string, bool) callers when
        // the path is also referenced elsewhere in the session.
        using var fileStream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var doc = WordprocessingDocument.Open(fileStream, isEditable: false);

        Body? body = doc.MainDocumentPart?.Document?.Body;
        if (body is not null)
        {
            // Descendants<Paragraph>() yields paragraphs in document
            // order, including those nested inside tables, sdt,
            // hyperlink wrappers, <w:txbxContent> text boxes, and the
            // Fallback branch of <mc:AlternateContent>. Matches
            // mammoth's tree walk exactly — verified empirically
            // against scenarios `table`, `sdt`, `hyperlink`,
            // `textbox`, `textbox-simple`.
            foreach (Paragraph p in body.Descendants<Paragraph>())
            {
                // Skip the Choice branch of <mc:AlternateContent>:
                // mammoth processes the Fallback branch instead
                // (verified probe `textbox`). OpenXml SDK does NOT
                // resolve AlternateContent by default — it exposes
                // both branches in the tree — so we filter the Choice
                // branch out manually here. (We do NOT use the SDK's
                // MarkupCompatibilityProcessSettings to do this at
                // open time because that would also strip post-2007
                // features from non-AlternateContent contexts.)
                if (p.Ancestors<AlternateContentChoice>().Any())
                {
                    continue;
                }
                // Skip paragraphs nested inside <w:customXml> at block
                // level. mammoth has no handler for w:customXml so it
                // drops the subtree entirely (verified probe
                // `customXml-wraps-run`: customXml content vanishes
                // including any nested paragraphs). Matched here by
                // LocalName so this filter covers both CustomXmlRun
                // (run-level) and CustomXmlBlock (block-level)
                // without depending on the SDK's typed-class layout.
                if (HasCustomXmlAncestor(p))
                {
                    continue;
                }
                string text = ConcatParagraphText(p);
                string trimmed = JsCompat.Trim(text);
                if (trimmed.Length == 0)
                {
                    continue;
                }
                paragraphs.Add(trimmed);
            }
        }

        List<DatasetRow> records = TextChunker.Chunk(paragraphs);
        if (records.Count == 0)
        {
            throw new InvalidOperationException("No text content found in DOCX file");
        }

        return new ReadResult
        {
            Format = InputFormat.Docx,
            Records = records,
        };
    }

    /// <summary>
    /// Concatenate the text of a single paragraph in document order.
    /// Mirrors mammoth's extractRawText paragraph step: pick up every
    /// <see cref="Text"/> value, append <c>\t</c> for every
    /// <see cref="TabChar"/>, ignore everything else (notably
    /// <see cref="Break"/> — see class doc for the rationale).
    ///
    /// <para><b>Three subtle exclusions, all empirically verified
    /// against mammoth probes</b> (artifacts in session-state
    /// <c>docx-probe/probe2.js</c>):</para>
    /// <list type="number">
    ///   <item><b>Nested paragraphs.</b> A paragraph nested inside the
    ///     current one (typically via <c>&lt;w:txbxContent&gt;</c>
    ///     text boxes, or via the Fallback branch of
    ///     <c>&lt;mc:AlternateContent&gt;</c>) is yielded as its OWN
    ///     top-level entry by <c>Body.Descendants&lt;Paragraph&gt;()</c>
    ///     and handled in that loop. To avoid emitting its text
    ///     twice (once flattened into the outer paragraph and again
    ///     as its own paragraph), we skip any element whose nearest
    ///     Paragraph ancestor is NOT the current <c>p</c>. Verified
    ///     probe <c>textbox-simple</c>:
    ///     <c>["before","txbx 1","txbx 2","after"]</c>.</item>
    ///   <item><b>Simple field display text.</b>
    ///     <c>&lt;w:fldSimple&gt;</c> wraps the cached display value
    ///     of a Word field (DATE, PAGE, TOC, etc.). Mammoth DROPS
    ///     that display text in <c>extractRawText</c>. Probe
    ///     <c>fldSimple</c> with display <c>"3/15/2023"</c> in
    ///     <c>"before "</c> + field + <c>" after"</c> →
    ///     <c>"before  after"</c> (two spaces; field text excluded).
    ///     Note this is ASYMMETRIC vs <c>&lt;w:fldChar&gt;</c>-style
    ///     <i>complex</i> fields: those DO emit their display text
    ///     (probe <c>fldChar-complex</c>:
    ///     <c>"before 3/15/2023 after"</c>). The difference: complex
    ///     field display text lives in a plain
    ///     <c>&lt;w:r&gt;/&lt;w:t&gt;</c> between
    ///     <c>&lt;w:fldChar separate&gt;</c> and
    ///     <c>&lt;w:fldChar end&gt;</c> — no special wrapper element
    ///     — so the default walker picks it up correctly without
    ///     extra logic. Only <c>SimpleField</c> needs an explicit
    ///     skip.</item>
    ///   <item><b>AlternateContent Choice.</b>
    ///     <c>&lt;mc:AlternateContent&gt;</c> publishes the same
    ///     content in two forms: a Choice for feature-aware consumers
    ///     and a Fallback for everyone else. Mammoth uses Fallback
    ///     (verified probe <c>textbox</c>:
    ///     <c>["outer","fallback inner"]</c>, NOT <c>["outer",
    ///     "inner"]</c>). The body-level loop already filters Choice
    ///     paragraphs; this guard catches any Choice content nested
    ///     inside a paragraph at run level.</item>
    /// </list>
    /// </summary>
    private static string ConcatParagraphText(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (OpenXmlElement el in p.Descendants())
        {
            if (!ReferenceEquals(el.Ancestors<Paragraph>().FirstOrDefault(), p))
            {
                continue;
            }
            if (el.Ancestors<SimpleField>().Any())
            {
                continue;
            }
            if (el.Ancestors<AlternateContentChoice>().Any())
            {
                continue;
            }
            if (el.Ancestors<MoveFromRun>().Any() ||
                el.Ancestors<MoveToRun>().Any())
            {
                // mammoth has NO handler for <w:moveFrom>/<w:moveTo>
                // (verified by inspecting mammoth's xmlElementReaders
                // map in node_modules/mammoth/lib/docx/body-reader.js),
                // so it falls through to the "unrecognised element was
                // ignored" warning path and emits nothing. The C# walk
                // would otherwise pick up the inner <w:t> normally —
                // explicit filter required for parity.
                //
                // Notes on the asymmetric tracked-changes contract:
                //  * <w:ins> → readChildElements: transparent wrapper,
                //    inner <w:t> flows through normally (NO filter
                //    needed; verified `Read_TrackedInsert_Kept`).
                //  * <w:del> → explicit handler returning empty:
                //    its content is <w:delText> (a distinct class,
                //    DeletedText, NOT Text), which my switch already
                //    drops (verified `Read_TrackedDelete_Dropped`).
                //  * <w:moveFrom>/<w:moveTo> → unhandled, contents
                //    typically <w:t>, must be explicitly filtered
                //    here.
                continue;
            }
            if (HasCustomXmlAncestor(el))
            {
                // mammoth has NO handler for <w:customXml> — drops
                // the subtree (verified probe `customXml-wraps-run`:
                // mammoth output `"before  after"` (two spaces) for
                // a customXml that wraps a normal run with "INSIDE").
                // Without this filter the typed Text descendant path
                // would pick up "INSIDE" — a real (if narrow)
                // over-extraction divergence flagged by Opus-4.8
                // round-2.
                continue;
            }
            switch (el)
            {
                case Text t:
                    // <w:t> content (xml:space="preserve" or default —
                    // OpenXml SDK's Text.Text preserves either way,
                    // verified probes xmlspace-default and
                    // xmlspace-preserve both yield "  leading  ").
                    sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case OpenXmlUnknownElement u
                    when u.NamespaceUri == WordprocessingNamespace
                      && (u.LocalName == "t" || u.LocalName == "tab")
                      && HasUnknownSmartTagAncestor(u):
                    // <w:smartTag> is allowlisted by mammoth
                    // (`xmlElementReaders["w:smartTag"] = readChildElements`)
                    // but the OpenXml SDK parses it as
                    // OpenXmlUnknownElement — AND its descendants
                    // (<w:r>, <w:t>, <w:tab>) inherit unknown status.
                    // The typed `case Text t` branch above misses
                    // them. We restore mammoth's transparent-wrapper
                    // behavior for smartTag (verified probe
                    // `smartTag`: "tagged rest") — but ONLY when the
                    // unknown <w:t>/<w:tab> sits beneath an unknown
                    // <w:smartTag>. Any OTHER unknown w: wrapper
                    // (e.g., <w:fooBar>, custom-XML extensions
                    // unknown to mammoth) is dropped to match
                    // mammoth's allowlist-recursion semantics
                    // (verified probe `unknown-w-wrapper`: mammoth
                    // output `"a  b"` for <w:fooBar><w:r><w:t>X</w:t>
                    // <w:t>Y</w:t></w:r></w:fooBar>).
                    switch (u.LocalName)
                    {
                        case "t":
                            sb.Append(u.InnerText);
                            break;
                        case "tab":
                            sb.Append('\t');
                            break;
                    }
                    break;
                // Break (both plain w:br and w:br type="page"),
                // FieldChar, FootnoteReference, EndnoteReference,
                // CommentReference, FieldCode (w:instrText) etc. are
                // intentionally ignored to match mammoth.extractRawText.
            }
        }
        return sb.ToString();
    }

    /// <summary>True if any ancestor of <paramref name="el"/> is a
    /// WordprocessingML <c>&lt;w:customXml&gt;</c> element (matched by
    /// LocalName + namespace so it covers BOTH OpenXml SDK's typed
    /// classes — <see cref="CustomXmlRun"/> for run-level and
    /// <see cref="CustomXmlBlock"/> for block-level — without
    /// depending on a particular SDK class hierarchy).</summary>
    private static bool HasCustomXmlAncestor(OpenXmlElement el)
    {
        foreach (OpenXmlElement a in el.Ancestors())
        {
            if (a.NamespaceUri == WordprocessingNamespace &&
                a.LocalName == "customXml")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if any UNKNOWN ancestor of <paramref name="el"/>
    /// is a WordprocessingML <c>&lt;w:smartTag&gt;</c>. The "unknown"
    /// qualifier matters because mammoth's smartTag transparency
    /// applies ONLY when smartTag itself is in the parse path (it's
    /// the only mammoth-allowlisted wrapper whose subtree the OpenXml
    /// SDK leaves entirely as OpenXmlUnknownElement). Other unknown
    /// w: wrappers (<c>&lt;w:fooBar&gt;</c>, third-party extensions)
    /// must drop their subtree to match mammoth's allowlist
    /// recursion.</summary>
    private static bool HasUnknownSmartTagAncestor(OpenXmlElement el)
    {
        foreach (OpenXmlElement a in el.Ancestors())
        {
            if (a is OpenXmlUnknownElement &&
                a.NamespaceUri == WordprocessingNamespace &&
                a.LocalName == "smartTag")
            {
                return true;
            }
        }
        return false;
    }

    private const string WordprocessingNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Marker constant referenced by the class xmldoc — keeps the doc
    /// link from going stale if the project moves to a real
    /// divergences registry in the future.
    /// </summary>
    internal const string ReadDocxKnownDivergences =
        "br=dropped (including type=page); fldSimple display=dropped (fldChar complex=kept); " +
        "AlternateContent Choice=dropped (Fallback=used); " +
        "w:ins=transparent (kept); w:del=dropped; w:moveFrom/w:moveTo=dropped; " +
        "w:customXml=dropped (run + block level); " +
        "unknown w: wrappers=dropped (except w:smartTag which is mammoth-allowlisted); " +
        "headers/footers=ignored; comments/footnotes/endnotes=ignored";
}
