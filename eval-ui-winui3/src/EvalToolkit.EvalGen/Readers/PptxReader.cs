using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Reads <c>.pptx</c> files to mirror the TS <c>readPptx</c> reader in
/// <c>eval-gen/src/readers/index.ts</c>. Emits one record per non-empty
/// slide with the shape:
///
/// <code>
/// { slide_number: int, title: string, content: string, notes: string }
/// </code>
///
/// <para>The TS implementation walks the parsed XML tree using
/// fast-xml-parser with <c>removeNSPrefix: true</c> and
/// <c>preserveOrder: true</c>, so we mirror the same local-name-only,
/// document-order walk using <see cref="XDocument"/> directly. We do
/// not use <c>DocumentFormat.OpenXml</c> typed parts because some
/// PPTX-specific behaviors documented below diverge sharply from the
/// typed SDK's semantics — notably <c>&lt;mc:AlternateContent&gt;</c>
/// inside a slide, where the TS walker extracts BOTH the
/// <c>&lt;mc:Choice&gt;</c> and <c>&lt;mc:Fallback&gt;</c> branches
/// (the opposite of the DOCX reader's mammoth-faithful Fallback-only
/// behavior).</para>
///
/// <para><b>Empirically verified behaviors (probe artifacts in
/// <c>~/.copilot/session-state/.../pptx-probe/</c>):</b></para>
/// <list type="bullet">
///   <item><b>Slide order</b> = order of <c>&lt;p:sldId&gt;</c> entries
///     in <c>ppt/presentation.xml</c>'s <c>&lt;p:sldIdLst&gt;</c>,
///     resolved via <c>ppt/_rels/presentation.xml.rels</c>. Probe
///     <c>reversed-slide-order-via-rels</c>: pointing rId1 →
///     <c>slide2.xml</c> in the rels file flips the output order. If
///     <c>presentation.xml</c> or its rels is missing/empty,
///     <b>fall back to scanning entry names for
///     <c>ppt/slides/slideN.xml</c> and sorting by numeric N</b> —
///     probe <c>fallback-numeric-order</c>.</item>
///   <item><b>Canonical slide_number = 1-based position in the
///     resolved order</b> (includes hidden slides). Never derived from
///     the file-name digits. Probe <c>reversed-slide-order-via-rels</c>
///     confirms slide_number = 1 maps to physical file
///     <c>slide2.xml</c> when rels points rId1 there.</item>
///   <item><b>Hidden slides (<c>&lt;p:sld show="0"&gt;</c>) are still
///     included.</b> Probe <c>hidden-slide-still-counted</c> emits
///     three records (1, 2, 3) for a 3-slide deck where the middle
///     slide is hidden.</item>
///   <item><b>Empty slides (no recoverable <c>&lt;a:t&gt;</c> text)
///     are dropped, but the ordinal index still advances.</b> Probe
///     <c>empty-slide-skipped-but-numbered</c>: a 3-slide deck where
///     slide 2 is empty produces records with slide_number 1 and 3 —
///     2 is missing, not renumbered. This matches the TS
///     <c>continue</c> on <c>!title &amp;&amp; body.length === 0</c>.</item>
///   <item><b>Title selection</b> = first paragraph belonging to a
///     shape with <c>&lt;p:nvSpPr&gt;/&lt;p:nvPr&gt;/&lt;p:ph
///     type="title"&gt;</c> or <c>type="ctrTitle"</c>. If no such
///     placeholder exists, the first non-empty paragraph in document
///     order wins. Probes: <c>title-not-first</c> (placeholder shape
///     appears AFTER a body shape — placeholder still wins),
///     <c>ctrTitle-variant</c> (centered-title also recognized),
///     <c>multiple-title-placeholders</c> (first placeholder wins
///     ties), <c>no-title-placeholder</c> (first paragraph in document
///     order falls through).</item>
///   <item><b>Body = all entries except the selected title entry, in
///     document order, joined with <c>\n</c>.</b> Probe
///     <c>simple-2-slide-titled</c>: shape with paragraphs
///     <c>["Body line 1", "Body line 2"]</c> emits
///     <c>content="Body line 1\nBody line 2"</c>.</item>
///   <item><b>Grouped shapes (<c>&lt;p:grpSp&gt;</c>) are walked
///     transparently.</b> The tree walker recurses through any
///     unknown key (including <c>grpSp</c>) and the child
///     <c>&lt;p:sp&gt;</c> elements contribute paragraphs in the
///     usual way — no double-counting because <c>grpSp</c> itself is
///     not a paragraph-emitting key. Probe <c>grouped-shape</c>.</item>
///   <item><b><c>&lt;mc:AlternateContent&gt;</c> inside a slide:
///     BOTH <c>&lt;mc:Choice&gt;</c> AND <c>&lt;mc:Fallback&gt;</c>
///     branches contribute text.</b> This is the opposite of the DOCX
///     reader's Fallback-only behavior because the PPTX walker is
///     unaware of MarkupCompatibility semantics — it just recurses
///     through every key. Probe <c>alternate-content-in-slide</c>:
///     <c>content="CHOICE text\nFALLBACK text"</c>. Reimplementations
///     must mirror this (even though it produces "double" text)
///     because the existing eval datasets already encode this
///     content.</item>
///   <item><b>Empty paragraphs (<c>&lt;a:p/&gt;</c> and runs whose
///     concatenated text trims to empty) are filtered.</b> Per-shape
///     paragraph collection drops any paragraph whose trimmed text is
///     empty before it ever becomes an entry. Probe
///     <c>shape-with-empty-paragraphs</c>: only <c>"real content"</c>
///     survives a <c>[null, "  ", "real content"]</c> list.</item>
///   <item><b>Title and body texts are emitted UNTRIMMED.</b> Only the
///     filtering predicate (<c>text.Trim().Length &gt; 0</c>) is
///     trimmed via <see cref="JsCompat.Trim"/> (NOT .NET
///     <see cref="string.Trim()"/>) — the recorded text is the raw
///     concatenation of run contents. The TS reader uses
///     <c>parseTagValue: false, trimValues: false</c> on the parser
///     for this reason. This is important when the slide author has
///     intentional leading/trailing whitespace.</item>
///   <item><b>Speaker notes</b> = concatenated text of every
///     <c>&lt;a:p&gt;</c> in the slide's notes part
///     (<c>../notesSlides/notesSlideN.xml</c> via the slide's
///     <c>.rels</c> relationship of type ending in <c>/notesSlide</c>),
///     joined with <c>\n</c> then trimmed. No notes part → empty
///     string. Probe <c>with-notes</c> emits
///     <c>notes="Speaker note line."</c>.</item>
///   <item><b>Master / layout text is suppressed by default.</b> When
///     <c>EVALGEN_PPTX_INCLUDE_MASTER=true</c> is set, the reader
///     appends ONE extra record with
///     <c>{ slide_number = 0, title = "(slide master / layout)",
///     content = &lt;all master + layout paragraphs joined&gt;,
///     notes = "" }</c>. Probe <c>master-opt-in</c>.</item>
///   <item><b>Empty result</b> (zero records after slide + master
///     processing) throws <see cref="InvalidOperationException"/>
///     with message <c>"No text content found in PPTX file"</c> —
///     byte-exact match to TS.</item>
///   <item><b>No slides at all</b> (presentation has no parseable
///     sldIdLst entries and no <c>ppt/slides/slideN.xml</c> files)
///     throws with message <c>"No slides found in PPTX file"</c>.</item>
/// </list>
///
/// <para><b>Why ZipArchive + XDocument instead of
/// <c>DocumentFormat.OpenXml.Packaging.PresentationDocument</c>?</b>
/// The TS reader does an unstructured XML tree walk that happens to
/// extract text from constructs the typed SDK would either reject
/// (malformed parts) or pre-resolve (AlternateContent collapses to a
/// single branch). Mirroring the TS behavior precisely is easier when
/// we own the walk. The SDK is also unnecessary here because we never
/// need to write back, and the parts we read are all small text XML.</para>
/// </summary>
public sealed class PptxReader : IDatasetReader
{
    private const string PresentationPartPath = "ppt/presentation.xml";
    private const string PresentationRelsPath = "ppt/_rels/presentation.xml.rels";

    private const string OpcRelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string PresentationmlNamespace =
        "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string OfficeRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NotesSlideRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide";

    private static readonly Regex s_slidePartName = new(
        @"^ppt/slides/slide(\d+)\.xml$",
        RegexOptions.Compiled);
    private static readonly Regex s_masterPartName = new(
        @"^ppt/slideMasters/slideMaster\d+\.xml$",
        RegexOptions.Compiled);
    private static readonly Regex s_layoutPartName = new(
        @"^ppt/slideLayouts/slideLayout\d+\.xml$",
        RegexOptions.Compiled);

    /// <summary>
    /// Field key used to mark the master/layout opt-in record.
    /// Matches TS literal <c>"(slide master / layout)"</c>.
    /// </summary>
    public const string MasterLayoutTitle = "(slide master / layout)";

    /// <summary>
    /// Sentinel <c>slide_number</c> value for the master/layout opt-in
    /// record. Matches TS literal <c>0</c>.
    /// </summary>
    public const int MasterLayoutSlideNumber = 0;

    /// <summary>
    /// Environment variable that opts in to master/layout extraction.
    /// Same name and parsing rules as the TS reader
    /// (<see cref="ParseBoolEnv"/>).
    /// </summary>
    public const string IncludeMasterEnvVar = "EVALGEN_PPTX_INCLUDE_MASTER";

    public ReadResult Read(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        using var zip = ZipFile.OpenRead(absolutePath);

        // Build a name → entry index. Same idea as the TS Map.
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            // OPC entry names use forward slashes. ZipArchive preserves
            // them verbatim; no normalization needed.
            entries[entry.FullName] = entry;
        }

        var slidePartPaths = ResolveSlideOrder(entries);
        if (slidePartPaths.Count == 0)
        {
            throw new InvalidOperationException("No slides found in PPTX file");
        }

        var records = new List<DatasetRow>();

        for (int i = 0; i < slidePartPaths.Count; i++)
        {
            string slidePath = slidePartPaths[i];
            if (!entries.TryGetValue(slidePath, out var slideEntry))
            {
                // Listed in sldIdLst but the part file is missing — skip
                // silently, matching TS's `if (!buf) continue`.
                continue;
            }

            var slideDoc = LoadXml(slideEntry);
            var (title, body) = ExtractSlideText(slideDoc);

            // TS: `if (!title && body.length === 0) continue;`
            if (title.Length == 0 && body.Count == 0)
            {
                continue;
            }

            string notes = ReadSlideNotes(entries, slidePath);

            var row = new DatasetRow(capacity: 4);
            // Field order must match the TS record literal exactly:
            // slide_number, title, content, notes.
            row.Set("slide_number", (long)(i + 1));
            row.Set("title", title);
            row.Set("content", string.Join("\n", body));
            row.Set("notes", notes);
            records.Add(row);
        }

        if (ShouldIncludeMaster())
        {
            var masterParas = new List<string>();
            // Use ordinal iteration order over the entry list so the
            // appended text is deterministic across runs. JS
            // Map-iteration is insertion-order, which for a zip is the
            // central-directory order; ZipArchive preserves that too.
            foreach (var entry in zip.Entries)
            {
                if (s_masterPartName.IsMatch(entry.FullName) ||
                    s_layoutPartName.IsMatch(entry.FullName))
                {
                    var doc = LoadXml(entry);
                    masterParas.AddRange(ExtractDrawingMlParagraphs(doc));
                }
            }
            string masterText = JsCompat.Trim(string.Join("\n", masterParas));
            if (masterText.Length > 0)
            {
                var row = new DatasetRow(capacity: 4);
                row.Set("slide_number", (long)MasterLayoutSlideNumber);
                row.Set("title", MasterLayoutTitle);
                row.Set("content", masterText);
                row.Set("notes", string.Empty);
                records.Add(row);
            }
        }

        if (records.Count == 0)
        {
            throw new InvalidOperationException("No text content found in PPTX file");
        }

        return new ReadResult
        {
            Records = records,
            Format = InputFormat.Pptx,
        };
    }

    // ===== Slide order =====

    /// <summary>
    /// Resolve the ordered list of slide part paths via OPC
    /// relationships, with a numeric-filename fallback for malformed
    /// or minimal decks. Mirrors TS <c>resolveSlideOrder</c>.
    /// </summary>
    private static List<string> ResolveSlideOrder(
        Dictionary<string, ZipArchiveEntry> entries)
    {
        if (entries.TryGetValue(PresentationPartPath, out var presentationEntry) &&
            entries.TryGetValue(PresentationRelsPath, out var relsEntry))
        {
            var ridOrder = ExtractSlideRidOrder(LoadXml(presentationEntry));
            if (ridOrder.Count > 0)
            {
                var ridToTarget = ExtractRelationshipMap(LoadXml(relsEntry));
                var resolved = new List<string>(ridOrder.Count);
                foreach (string rid in ridOrder)
                {
                    if (!ridToTarget.TryGetValue(rid, out string? target))
                    {
                        continue;
                    }
                    resolved.Add(ResolveRelTarget("ppt", target));
                }
                if (resolved.Count > 0)
                {
                    return resolved;
                }
            }
        }

        // Fallback: numeric scan over entry names.
        var byNumber = new List<(string Path, int Num)>();
        foreach (var name in entries.Keys)
        {
            var m = s_slidePartName.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int num))
            {
                byNumber.Add((name, num));
            }
        }
        byNumber.Sort((a, b) => a.Num.CompareTo(b.Num));
        return byNumber.Select(x => x.Path).ToList();
    }

    /// <summary>
    /// Extract the ordered rIds from <c>&lt;p:sldIdLst&gt;/&lt;p:sldId
    /// r:id="rIdN"/&gt;</c>. We match by namespace+local-name pair
    /// rather than the <c>r:id</c> string because the TS reader
    /// intentionally keeps namespace prefixes here
    /// (<c>removeNSPrefix: false</c>) — confirms r:id is the
    /// relationships-namespace attribute and not collision with the
    /// presentation-namespace <c>id</c> attribute.
    /// </summary>
    private static List<string> ExtractSlideRidOrder(XDocument presentationDoc)
    {
        var rids = new List<string>();
        XName sldIdName = XName.Get("sldId", PresentationmlNamespace);
        XName rIdAttr = XName.Get("id", OfficeRelationshipsNamespace);
        foreach (var sldId in presentationDoc.Descendants(sldIdName))
        {
            string? rid = (string?)sldId.Attribute(rIdAttr);
            if (rid is not null && rid.StartsWith("rId", StringComparison.Ordinal))
            {
                rids.Add(rid);
            }
        }
        return rids;
    }

    /// <summary>
    /// Parse an OPC <c>.rels</c> document into <c>Map&lt;Id, Target&gt;</c>.
    /// </summary>
    private static Dictionary<string, string> ExtractRelationshipMap(XDocument relsDoc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        XName relName = XName.Get("Relationship", OpcRelationshipsNamespace);
        foreach (var rel in relsDoc.Descendants(relName))
        {
            string? id = (string?)rel.Attribute("Id");
            string? target = (string?)rel.Attribute("Target");
            if (id is not null && target is not null)
            {
                map[id] = target;
            }
        }
        return map;
    }

    /// <summary>
    /// Resolve an OPC relationship <c>Target</c> against the directory
    /// of the source part. Handles absolute-package paths and
    /// dot-segment-bearing relative paths. Mirrors TS
    /// <c>resolveRelTarget</c>.
    /// </summary>
    private static string ResolveRelTarget(string sourceDir, string target)
    {
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }
        var parts = sourceDir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (seg.Length > 0 && seg != ".")
            {
                parts.Add(seg);
            }
        }
        return string.Join('/', parts);
    }

    // ===== Slide text extraction =====

    /// <summary>
    /// Extract canonical (title, body) from a slide XML document.
    /// Title selection prefers shapes with <c>&lt;p:ph
    /// type="title"&gt;</c> or <c>type="ctrTitle"</c>; falls back to
    /// the first non-empty paragraph in document order.
    /// </summary>
    private static (string Title, IReadOnlyList<string> Body) ExtractSlideText(XDocument slideDoc)
    {
        var entries = CollectSlideParagraphEntries(slideDoc);
        if (entries.Count == 0)
        {
            return (string.Empty, Array.Empty<string>());
        }

        int titleIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsTitlePlaceholder)
            {
                titleIndex = i;
                break;
            }
        }
        if (titleIndex < 0)
        {
            titleIndex = 0;
        }

        string title = entries[titleIndex].Text;
        var body = new List<string>(entries.Count - 1);
        for (int i = 0; i < entries.Count; i++)
        {
            if (i != titleIndex)
            {
                body.Add(entries[i].Text);
            }
        }
        return (title, body);
    }

    private readonly record struct PptxParagraphEntry(string Text, bool IsTitlePlaceholder);

    /// <summary>
    /// Tree-walk equivalent of TS
    /// <c>collectPptxSlideParagraphEntries</c>. We treat any element
    /// whose local name is <c>sp</c> as a shape boundary (collect all
    /// descendant <c>p</c> texts together, all marked with the same
    /// <c>isTitlePlaceholder</c> flag derived from the shape's
    /// non-visual properties), any local-name <c>p</c> outside a shape
    /// as a free paragraph, and otherwise recurse through children.
    ///
    /// <para><b>Why match by local-name rather than namespace?</b> The
    /// TS reader uses <c>removeNSPrefix: true</c> in the slide parser,
    /// so it collapses <c>&lt;p:sp&gt;</c>, <c>&lt;a:sp&gt;</c>, and
    /// any other prefix to the bare key <c>sp</c>. We do the same with
    /// <c>XName.LocalName</c> comparison.</para>
    /// </summary>
    private static List<PptxParagraphEntry> CollectSlideParagraphEntries(XDocument slideDoc)
    {
        var entries = new List<PptxParagraphEntry>();
        if (slideDoc.Root is null)
        {
            return entries;
        }
        Visit(slideDoc.Root, entries);
        return entries;
    }

    private static void Visit(XElement node, List<PptxParagraphEntry> entries)
    {
        string local = node.Name.LocalName;
        if (local == "sp")
        {
            bool isTitle = IsTitlePlaceholderShape(node);
            foreach (string paraText in CollectShapeParagraphs(node))
            {
                entries.Add(new PptxParagraphEntry(paraText, isTitle));
            }
            return;
        }
        if (local == "p")
        {
            string text = CollectTextLeaves(node, leafLocalName: "t");
            if (JsCompat.Trim(text).Length > 0)
            {
                entries.Add(new PptxParagraphEntry(text, false));
            }
            return;
        }
        foreach (var child in node.Elements())
        {
            Visit(child, entries);
        }
    }

    /// <summary>
    /// Walk a shape's descendants and return one string per non-empty
    /// <c>&lt;a:p&gt;</c> (local name <c>p</c>). Empty / whitespace-only
    /// paragraphs are dropped — matches TS
    /// <c>if (text.trim().length &gt; 0) paragraphs.push(text);</c>.
    /// </summary>
    private static List<string> CollectShapeParagraphs(XElement shape)
    {
        var paragraphs = new List<string>();
        foreach (var p in shape.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            string text = CollectTextLeaves(p, leafLocalName: "t");
            if (JsCompat.Trim(text).Length > 0)
            {
                paragraphs.Add(text);
            }
        }
        return paragraphs;
    }

    /// <summary>
    /// Determine whether a shape's non-visual properties advertise a
    /// title or center-title placeholder. Walks
    /// <c>nvSpPr</c> → <c>nvPr</c> → <c>ph</c> by local name only,
    /// matching the TS <c>walkForTag</c> + <c>walkForElementAttrs</c>
    /// chain.
    /// </summary>
    private static bool IsTitlePlaceholderShape(XElement shape)
    {
        foreach (var nvSpPr in shape.Descendants().Where(e => e.Name.LocalName == "nvSpPr"))
        {
            foreach (var nvPr in nvSpPr.Descendants().Where(e => e.Name.LocalName == "nvPr"))
            {
                foreach (var ph in nvPr.Descendants().Where(e => e.Name.LocalName == "ph"))
                {
                    string? type = (string?)ph.Attribute("type");
                    if (type == "title" || type == "ctrTitle")
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Concatenate the text content of every descendant element whose
    /// local name is <paramref name="leafLocalName"/> (typically
    /// <c>"t"</c>) in document order. Walks ALL descendants — including
    /// those nested under unknown wrappers, <c>mc:AlternateContent</c>
    /// (both <c>mc:Choice</c> AND <c>mc:Fallback</c>), and grouped
    /// shapes — matching the TS <c>collectText</c> traversal which
    /// recurses unconditionally.
    /// </summary>
    private static string CollectTextLeaves(XElement root, string leafLocalName)
    {
        var sb = new StringBuilder();
        foreach (var leaf in root.Descendants().Where(e => e.Name.LocalName == leafLocalName))
        {
            sb.Append(leaf.Value);
        }
        return sb.ToString();
    }

    // ===== Notes =====

    /// <summary>
    /// Locate the notes part for a given slide via its <c>.rels</c> and
    /// return the concatenated, trimmed text of every paragraph in it.
    /// Empty when the slide has no notes part.
    /// </summary>
    private static string ReadSlideNotes(
        Dictionary<string, ZipArchiveEntry> entries,
        string slidePath)
    {
        int lastSlash = slidePath.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return string.Empty;
        }
        string dir = slidePath.Substring(0, lastSlash);
        string file = slidePath.Substring(lastSlash + 1);
        string relsPath = $"{dir}/_rels/{file}.rels";
        if (!entries.TryGetValue(relsPath, out var relsEntry))
        {
            return string.Empty;
        }

        var relsDoc = LoadXml(relsEntry);
        string? notesTarget = null;
        XName relName = XName.Get("Relationship", OpcRelationshipsNamespace);
        foreach (var rel in relsDoc.Descendants(relName))
        {
            string? type = (string?)rel.Attribute("Type");
            string? target = (string?)rel.Attribute("Target");
            if (type is not null && target is not null &&
                type.EndsWith("/notesSlide", StringComparison.Ordinal))
            {
                notesTarget = target;
                // TS picks the last matching relationship (it overwrites
                // in the loop with no break), so do the same — don't
                // break here.
            }
        }
        if (notesTarget is null)
        {
            return string.Empty;
        }
        string notesPath = ResolveRelTarget(dir, notesTarget);
        if (!entries.TryGetValue(notesPath, out var notesEntry))
        {
            return string.Empty;
        }
        var notesDoc = LoadXml(notesEntry);
        var paras = ExtractDrawingMlParagraphs(notesDoc);
        return JsCompat.Trim(string.Join("\n", paras));
    }

    // ===== DrawingML paragraphs (used by notes + master/layout) =====

    /// <summary>
    /// Mirror of TS <c>extractDrawingMlParagraphs</c>: return one string
    /// per non-empty <c>&lt;a:p&gt;</c> (local name <c>p</c>), with all
    /// <c>&lt;a:t&gt;</c> (local name <c>t</c>) leaves concatenated in
    /// document order. Used for notes and for the master/layout
    /// opt-in record.
    /// </summary>
    private static List<string> ExtractDrawingMlParagraphs(XDocument doc)
    {
        var paragraphs = new List<string>();
        if (doc.Root is null)
        {
            return paragraphs;
        }
        foreach (var p in doc.Root.DescendantsAndSelf().Where(e => e.Name.LocalName == "p"))
        {
            string text = CollectTextLeaves(p, leafLocalName: "t");
            if (JsCompat.Trim(text).Length > 0)
            {
                paragraphs.Add(text);
            }
        }
        return paragraphs;
    }

    // ===== Misc =====

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        // Preserve whitespace so author-intended leading/trailing spaces
        // in <a:t> values survive. The TS parser uses
        // `trimValues: false` for the same reason.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static bool ShouldIncludeMaster()
    {
        string? raw = Environment.GetEnvironmentVariable(IncludeMasterEnvVar);
        return ParseBoolEnv(raw);
    }

    /// <summary>
    /// Truthy-string parser matching the TS <c>parseBoolEnv</c> helper:
    /// case-insensitive accepts of <c>"true"</c>, <c>"1"</c>,
    /// <c>"yes"</c>, <c>"on"</c>. Anything else (including null /
    /// empty / whitespace) → false.
    /// </summary>
    internal static bool ParseBoolEnv(string? raw)
    {
        // Mirror TS `if (!value) return false;` — JS `!value` is true ONLY for
        // null/undefined/empty-string, NOT for whitespace-only strings.
        // .NET `string.IsNullOrWhiteSpace` would over-reject here because
        // .NET classifies U+0085 (NEL) as whitespace while ECMAScript does
        // not — using `IsNullOrEmpty` preserves JS truthiness semantics.
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }
        // Use JsCompat.Trim so whitespace classification matches TS exactly
        // (e.g. trims U+FEFF / does NOT trim U+0085).
        return JsCompat.Trim(raw).ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            _ => false,
        };
    }
}
