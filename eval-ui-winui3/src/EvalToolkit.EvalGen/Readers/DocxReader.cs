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
            // order, including those nested inside tables, sdt, etc.
            // Matches mammoth's tree walk exactly for parity probes.
            foreach (Paragraph p in body.Descendants<Paragraph>())
            {
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
    /// </summary>
    private static string ConcatParagraphText(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (OpenXmlElement el in p.Descendants())
        {
            switch (el)
            {
                case Text t:
                    // <w:t xml:space="preserve"> content. xml:space is
                    // handled by the OpenXml SDK; t.Text reflects the
                    // preserved characters.
                    sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                // Break, FieldChar, FootnoteReference, EndnoteReference,
                // CommentReference, etc. are intentionally ignored to
                // match mammoth.extractRawText.
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Marker constant referenced by the class xmldoc — keeps the doc
    /// link from going stale if the project moves to a real
    /// divergences registry in the future.
    /// </summary>
    internal const string ReadDocxKnownDivergences =
        "br=dropped; headers/footers=ignored; comments/footnotes/endnotes=ignored";
}
