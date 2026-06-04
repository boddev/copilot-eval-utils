using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// Required arguments for <see cref="M365MultiPromptWriter"/>.
/// Mirrors the third-argument options bag of the TS
/// <c>writeM365MultiPromptJson</c>. <see cref="PromptsPerThread"/> is
/// REQUIRED (matches TS); the rest are optional.
/// </summary>
public sealed record M365MultiPromptOptions
{
    public required int PromptsPerThread { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
    public string? Model { get; init; }
}

/// <summary>
/// Writes an m365/evalscore-compatible JSON document whose items each
/// contain multiple prompt turns. Mirrors the TS
/// <c>writeM365MultiPromptJson</c> in <c>eval-gen/src/writers.ts</c>.
///
/// <para><b>Byte-exact contract pinned by the writers-probe:</b></para>
/// <list type="bullet">
///   <item><b>Path rewrite.</b> NONE — output path is passed straight
///     through to <see cref="Path.GetFullPath(string)"/>. (Differs from
///     the sidecar writer which rewrites; the m365 doc is written to
///     the literal user-specified path.)</item>
///   <item><b>schemaVersion</b>: literal <c>"1.4.0"</c>. Camel-case
///     property name, NOT snake_case — TS uses the same literal
///     <c>schemaVersion: '1.4.0'</c>.</item>
///   <item><b>Prompts-per-thread clamping.</b> <c>Math.max(2, Math.min(20,
///     Math.trunc(promptsPerThread)))</c>. Negative, fractional, and
///     out-of-range values are clamped/truncated (pinned by
///     <c>m365-ppt-*</c> probe scenarios). The CLAMPED value is what
///     appears in <c>metadata.prompts_per_thread</c> AND drives the
///     chunking — not the raw input.</item>
///   <item><b>Chunking.</b> Sequential, fixed-size chunks of size
///     <c>promptsPerThread</c>; the last chunk is shorter if items
///     don't divide evenly. Empty input → empty <c>items</c> array.</item>
///   <item><b>Thread id.</b>
///     <c>evalgen-multi-prompt-{index+1}-{sha256_12char_hex}</c>.
///     SHA-256 of <c>items.map(i => i.id || `${i.prompt}|${i.source_location}`).join('|')</c>
///     truncated to the FIRST 12 hex characters (lowercase). Pinned by
///     <c>m365-hash-*</c> probe scenarios — including the hash
///     stability across runs, the hash-changes-on-order property, and
///     the empty-id fallback to <c>prompt|source_location</c>.</item>
///   <item><b>Item description / categories.</b>
///     <c>Synthetic multi-prompt evaluator group ({categories}).
///     Prompts are evaluated independently by default.</c> where
///     <c>categories</c> = comma-joined deduplicated category wire
///     strings preserving first-seen order, or the literal
///     <c>"mixed categories"</c> when the deduped list is empty
///     (TS <c>.join(', ') || 'mixed categories'</c>).</item>
///   <item><b>Per-turn context.</b> Lines joined with <c>\n</c>:
///     first an optional <c>"Source: …"</c> line when
///     <c>source_location</c> is truthy (i.e. non-null AND non-empty);
///     then each truthy entry of <c>supporting_facts</c>. If the
///     resulting list is empty, the <c>context</c> field is OMITTED
///     entirely from the turn (matches TS <c>return parts.length &gt; 0
///     ? parts.join('\n') : undefined</c>).</item>
///   <item><b>extensions.evalgen on the item:</b> <c>synthetic_thread:
///     true, conversation_chaining: false, grouping: "sequential_chunk",
///     prompts_per_thread, thread_id</c>.</item>
///   <item><b>extensions.evalgen on each turn:</b> <c>item_id,
///     source_location, assertions, category, difficulty,
///     supporting_facts, grounding_confidence, referenced_rows
///     (optional), synthetic_thread: true, conversation_chaining: false</c>.
///     Field order matches the TS object literal.</item>
///   <item><b>metadata block.</b> Fixed: <c>source: "eval-gen"</c>,
///     then <c>description, source_file, generated_at,
///     evalgen_version: "1.0.0", model, multi_prompt: true,
///     prompts_per_thread, grouping: "sequential_chunk"</c>, then
///     <c>warnings</c> if provided.</item>
/// </list>
/// </summary>
public sealed class M365MultiPromptWriter
{
    private const int MinPromptsPerThread = 2;
    private const int MaxPromptsPerThread = 20;

    private readonly IClock _clock;

    public M365MultiPromptWriter() : this(SystemClock.Instance) { }

    public M365MultiPromptWriter(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Write the multi-prompt JSON document. Returns the absolute path
    /// the file was written to.
    /// </summary>
    public string Write(
        IReadOnlyList<GeneratedEvalItem> items,
        string description,
        string sourceFile,
        string outputPath,
        M365MultiPromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        int promptsPerThread = ClampPromptsPerThread(options.PromptsPerThread);
        string absolutePath = Path.GetFullPath(outputPath);
        string generatedAt = JsonShape.ToIso8601Millis(_clock.UtcNow);
        string model = options.Model ?? "unknown";

        byte[] payload = JsonShape.Serialize(w => WriteRoot(
            w, items, description, sourceFile, generatedAt, options, model, promptsPerThread));
        JsonShape.WriteToFile(absolutePath, payload);
        return absolutePath;
    }

    /// <summary>
    /// JS <c>Math.max(2, Math.min(20, Math.trunc(x)))</c>. <c>Math.trunc</c>
    /// rounds toward zero (so 2.7 → 2 and -1 → -1, then clamp).
    /// </summary>
    internal static int ClampPromptsPerThread(double raw)
    {
        int truncated = (int)Math.Truncate(raw);
        return Math.Max(MinPromptsPerThread, Math.Min(MaxPromptsPerThread, truncated));
    }

    private static void WriteRoot(
        Utf8JsonWriter w,
        IReadOnlyList<GeneratedEvalItem> items,
        string description,
        string sourceFile,
        string generatedAt,
        M365MultiPromptOptions options,
        string model,
        int promptsPerThread)
    {
        w.WriteStartObject();

        // schemaVersion: CAMEL CASE per TS literal. Not snake_case.
        w.WriteString("schemaVersion", "1.4.0");

        // metadata block — field order locked by TS object literal.
        w.WritePropertyName("metadata");
        w.WriteStartObject();
        w.WriteString("source", "eval-gen");
        w.WriteString("description", description);
        w.WriteString("source_file", sourceFile);
        w.WriteString("generated_at", generatedAt);
        w.WriteString("evalgen_version", CoreInfo.WireEvalgenVersion);
        w.WriteString("model", model);
        w.WriteBoolean("multi_prompt", true);
        w.WriteNumber("prompts_per_thread", promptsPerThread);
        w.WriteString("grouping", "sequential_chunk");
        if (options.Warnings is not null)
        {
            w.WritePropertyName("warnings");
            w.WriteStartArray();
            foreach (string s in options.Warnings)
            {
                w.WriteStringValue(s);
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();

        // items array
        w.WritePropertyName("items");
        w.WriteStartArray();
        int groupIndex = 0;
        foreach (var group in ChunkItems(items, promptsPerThread))
        {
            WriteMultiPromptItem(w, group, groupIndex++, promptsPerThread);
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }

    /// <summary>
    /// Yield sequential fixed-size chunks. Matches TS
    /// <c>for (let i = 0; i &lt; items.length; i += promptsPerThread)
    /// chunks.push(items.slice(i, i + promptsPerThread))</c>.
    /// </summary>
    private static IEnumerable<IReadOnlyList<GeneratedEvalItem>> ChunkItems(
        IReadOnlyList<GeneratedEvalItem> items, int chunkSize)
    {
        for (int i = 0; i < items.Count; i += chunkSize)
        {
            int end = Math.Min(i + chunkSize, items.Count);
            var chunk = new List<GeneratedEvalItem>(end - i);
            for (int j = i; j < end; j++)
            {
                chunk.Add(items[j]);
            }
            yield return chunk;
        }
    }

    private static void WriteMultiPromptItem(
        Utf8JsonWriter w,
        IReadOnlyList<GeneratedEvalItem> group,
        int index,
        int promptsPerThread)
    {
        string threadId = string.Create(
            CultureInfo.InvariantCulture,
            $"evalgen-multi-prompt-{index + 1}-{StableGroupHash(group)}");

        w.WriteStartObject();
        // Field order locked by TS buildMultiPromptItem return literal
        // (eval-gen/src/writers.ts:131-164):
        //   name, description, turns, extensions
        w.WriteString("name", string.Create(
            CultureInfo.InvariantCulture, $"EvalGen multi-prompt evaluator {index + 1}"));
        w.WriteString("description", BuildItemDescription(group));

        w.WritePropertyName("turns");
        w.WriteStartArray();
        foreach (var item in group)
        {
            WriteTurn(w, item);
        }
        w.WriteEndArray();

        // item-level extensions.evalgen
        w.WritePropertyName("extensions");
        w.WriteStartObject();
        w.WritePropertyName("evalgen");
        w.WriteStartObject();
        w.WriteBoolean("synthetic_thread", true);
        w.WriteBoolean("conversation_chaining", false);
        w.WriteString("grouping", "sequential_chunk");
        w.WriteNumber("prompts_per_thread", promptsPerThread);
        w.WriteString("thread_id", threadId);
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private static void WriteTurn(Utf8JsonWriter w, GeneratedEvalItem item)
    {
        w.WriteStartObject();
        // Field order locked by TS turn object literal at writers.ts:136-154:
        //   prompt, expected_response, context (optional), extensions
        w.WriteString("prompt", item.Prompt);
        w.WriteString("expected_response", item.ExpectedAnswer);

        string? context = BuildTurnContext(item);
        if (context is not null)
        {
            w.WriteString("context", context);
        }

        w.WritePropertyName("extensions");
        w.WriteStartObject();
        w.WritePropertyName("evalgen");
        w.WriteStartObject();
        // Field order: item_id, source_location, assertions, category,
        // difficulty, supporting_facts, grounding_confidence,
        // referenced_rows (if present), synthetic_thread,
        // conversation_chaining.
        w.WriteString("item_id", item.Id);
        w.WriteString("source_location", item.SourceLocation);
        w.WritePropertyName("assertions");
        w.WriteStartArray();
        foreach (var a in item.Assertions)
        {
            JsonSerializer.Serialize(w, a);
        }
        w.WriteEndArray();
        w.WriteString("category", item.Category.ToWireString());
        w.WriteString("difficulty", item.Difficulty.ToWireString());
        w.WritePropertyName("supporting_facts");
        w.WriteStartArray();
        foreach (string s in item.SupportingFacts)
        {
            w.WriteStringValue(s);
        }
        w.WriteEndArray();
        w.WriteString("grounding_confidence", item.GroundingConfidence.ToWireString());
        if (item.ReferencedRows is not null)
        {
            w.WritePropertyName("referenced_rows");
            w.WriteStartArray();
            foreach (string s in item.ReferencedRows)
            {
                w.WriteStringValue(s);
            }
            w.WriteEndArray();
        }
        w.WriteBoolean("synthetic_thread", true);
        w.WriteBoolean("conversation_chaining", false);
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteEndObject();
    }

    /// <summary>
    /// Build the item description string per TS:
    /// <c>"Synthetic multi-prompt evaluator group ({categories}). Prompts are evaluated independently by default."</c>
    /// where <c>categories</c> is the comma+space-joined deduplicated
    /// list of category wire strings (insertion-order-preserving), or
    /// the literal <c>"mixed categories"</c> when empty.
    /// </summary>
    private static string BuildItemDescription(IReadOnlyList<GeneratedEvalItem> group)
    {
        // Array.from(new Set(...)) preserves insertion order. We
        // replicate by tracking seen entries explicitly rather than
        // sorting (which would diverge from TS).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(group.Count);
        foreach (var item in group)
        {
            string wire = item.Category.ToWireString();
            if (seen.Add(wire))
            {
                ordered.Add(wire);
            }
        }
        string categories = ordered.Count > 0
            ? string.Join(", ", ordered)
            : "mixed categories";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Synthetic multi-prompt evaluator group ({categories}). Prompts are evaluated independently by default.");
    }

    /// <summary>
    /// Build the <c>context</c> string for a single turn. Matches TS
    /// <c>buildTurnContext</c> in <c>eval-gen/src/writers.ts:174-180</c>:
    /// <list type="bullet">
    ///   <item>Optional first line <c>"Source: {source_location}"</c>
    ///     when <see cref="GeneratedEvalItem.SourceLocation"/> is
    ///     truthy (non-null AND non-empty).</item>
    ///   <item>Then each truthy entry of
    ///     <see cref="GeneratedEvalItem.SupportingFacts"/>.</item>
    ///   <item>Joined with <c>\n</c>. Returns null if the resulting
    ///     list is empty so the caller can omit the field.</item>
    /// </list>
    /// </summary>
    private static string? BuildTurnContext(GeneratedEvalItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(item.SourceLocation))
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"Source: {item.SourceLocation}"));
        }
        foreach (string s in item.SupportingFacts)
        {
            // JS `.filter(Boolean)` drops null/undefined/empty-string;
            // .NET strings can't be null in a typed list of string, but
            // we still drop empties to match TS.
            if (!string.IsNullOrEmpty(s))
            {
                parts.Add(s);
            }
        }
        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    /// <summary>
    /// SHA-256 of <c>items.map(i => i.id || `${i.prompt}|${i.source_location}`).join('|')</c>
    /// truncated to the first 12 lowercase hex characters. Matches TS
    /// <c>stableGroupHash</c> exactly.
    /// </summary>
    internal static string StableGroupHash(IReadOnlyList<GeneratedEvalItem> group)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < group.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('|');
            }
            var item = group[i];
            if (!string.IsNullOrEmpty(item.Id))
            {
                sb.Append(item.Id);
            }
            else
            {
                // TS fallback: `${item.prompt}|${item.source_location}`
                sb.Append(item.Prompt);
                sb.Append('|');
                sb.Append(item.SourceLocation);
            }
        }
        byte[] hash = SHA256.HashData(JsonShape.Utf8NoBom.GetBytes(sb.ToString()));
        // 6 bytes → 12 hex chars; lowercase to match Node's
        // crypto.createHash('sha256').digest('hex').
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
