using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// Optional arguments for <see cref="SidecarJsonWriter"/>. Mirrors the
/// optional second-argument bag in the TS
/// <c>writeSidecarJson(..., options?)</c>. Every field is optional;
/// when omitted, the writer either emits a fallback (<c>"unknown"</c>
/// for model) or drops the field entirely (matching JS-object property
/// shape).
/// </summary>
public sealed record SidecarJsonOptions
{
    public IReadOnlyList<string>? Warnings { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string>? AvoidanceEvalsets { get; init; }
    public int? AvoidanceItemsCompared { get; init; }
    public int? CrossRunDuplicatesRemoved { get; init; }
    public int? CrossRunAssertionOverlaps { get; init; }
}

/// <summary>
/// Writes the rich sidecar JSON document (<c>.evalgen.json</c>). Mirrors
/// the TS <c>writeSidecarJson</c> in <c>eval-gen/src/writers.ts</c>.
///
/// <para><b>Byte-exact contract pinned by the writers-probe:</b></para>
/// <list type="bullet">
///   <item><b>Path rewrite.</b> <c>outputPath</c> has its trailing
///     <c>.csv|.xlsx|.json</c> extension (case-insensitive) replaced
///     with <c>.evalgen.json</c>. Non-matching extensions are
///     unchanged (the writer will then overwrite the source file at
///     that exact path — matches TS exactly so the byte-on-disk
///     outcome is identical, though it is rarely desired).</item>
///   <item><b>JSON shape.</b> 2-space indented, no trailing newline,
///     UTF-8 without BOM, relaxed escaping (NBSP, BOM, emoji
///     literal). See <see cref="JsonShape"/>.</item>
///   <item><b>Field order:</b> exactly as the TS object literal:
///     <c>version, generated_at, description, source_file, item_count,
///     items, warnings, metadata</c>. The <c>metadata</c> block writes
///     <c>model</c> first (default <c>"unknown"</c>), then
///     <c>evalgen_version</c> (pinned literal <c>"1.0.0"</c>), then
///     the four optional cross-run fields if provided.</item>
///   <item><b>Per-item field order:</b> <c>id, prompt, expected_answer,
///     source_location, assertions, category, difficulty,
///     supporting_facts, grounding_confidence</c>, then
///     <c>referenced_rows</c> if present (matches TS object literal
///     plus the optional <c>referenced_rows</c> from the original
///     <c>GeneratedEvalItem</c>).</item>
///   <item><b>Assertion shape:</b> handled by
///     <see cref="AssertionJsonConverter"/> (already wired on
///     <see cref="Assertion"/>).</item>
///   <item><b>Generated_at format:</b>
///     <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> via <see cref="JsonShape.ToIso8601Millis"/>.
///     Uses the injected <see cref="IClock"/> for testability.</item>
///   <item><b>Optional fields:</b> a field whose corresponding
///     options value is null is OMITTED from the JSON (the TS object
///     literal contains <c>undefined</c> which <c>JSON.stringify</c>
///     drops). Lists are emitted even when empty (matches TS).</item>
/// </list>
/// </summary>
public sealed class SidecarJsonWriter
{
    private readonly IClock _clock;

    public SidecarJsonWriter() : this(SystemClock.Instance) { }

    public SidecarJsonWriter(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Write the sidecar JSON. Returns the absolute path written.
    /// </summary>
    public string Write(
        IReadOnlyList<GeneratedEvalItem> items,
        string description,
        string sourceFile,
        string outputPath,
        SidecarJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        string rewritten = PathRewrite.RewriteExtension(outputPath, ".evalgen.json");
        string absolutePath = Path.GetFullPath(rewritten);
        string generatedAt = JsonShape.ToIso8601Millis(_clock.UtcNow);

        byte[] payload = JsonShape.Serialize(w => WriteRoot(
            w, items, description, sourceFile, generatedAt, options));
        JsonShape.WriteToFile(absolutePath, payload);
        return absolutePath;
    }

    private static void WriteRoot(
        Utf8JsonWriter w,
        IReadOnlyList<GeneratedEvalItem> items,
        string description,
        string sourceFile,
        string generatedAt,
        SidecarJsonOptions? options)
    {
        w.WriteStartObject();
        // Field order is locked by the TS object literal in
        // eval-gen/src/writers.ts lines 50-66.
        w.WriteString("version", "1.0");
        w.WriteString("generated_at", generatedAt);
        w.WriteString("description", description);
        w.WriteString("source_file", sourceFile);
        w.WriteNumber("item_count", items.Count);

        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var item in items)
        {
            WriteGeneratedEvalItem(w, item);
        }
        w.WriteEndArray();

        // Optional `warnings` array. The TS writer passes `warnings`
        // through verbatim, including writing an empty array if one
        // was supplied. We omit only when null/undefined (matches TS
        // `options?.warnings` evaluating to undefined → dropped).
        if (options?.Warnings is not null)
        {
            w.WritePropertyName("warnings");
            w.WriteStartArray();
            foreach (string s in options.Warnings)
            {
                w.WriteStringValue(s);
            }
            w.WriteEndArray();
        }

        // The TS object literal at lines 58-65 ALWAYS emits the
        // `metadata` block (with `model` defaulting to "unknown" and
        // `evalgen_version` fixed). Optional cross-run fields are
        // included only when defined.
        w.WritePropertyName("metadata");
        w.WriteStartObject();
        w.WriteString("model", options?.Model ?? "unknown");
        w.WriteString("evalgen_version", CoreInfo.WireEvalgenVersion);
        if (options?.AvoidanceEvalsets is not null)
        {
            w.WritePropertyName("avoidance_evalsets");
            w.WriteStartArray();
            foreach (string s in options.AvoidanceEvalsets)
            {
                w.WriteStringValue(s);
            }
            w.WriteEndArray();
        }
        if (options?.AvoidanceItemsCompared is int aic)
        {
            w.WriteNumber("avoidance_items_compared", aic);
        }
        if (options?.CrossRunDuplicatesRemoved is int crd)
        {
            w.WriteNumber("cross_run_duplicates_removed", crd);
        }
        if (options?.CrossRunAssertionOverlaps is int crao)
        {
            w.WriteNumber("cross_run_assertion_overlaps", crao);
        }
        w.WriteEndObject();

        w.WriteEndObject();
    }

    internal static void WriteGeneratedEvalItem(Utf8JsonWriter w, GeneratedEvalItem item)
    {
        w.WriteStartObject();
        // Field order locked by TS GeneratedEvalItem interface in
        // eval-gen/src/types.ts. The TS object literal that constructs
        // these items follows the same order in the orchestrator, so
        // this is the canonical insertion order JSON.stringify sees.
        w.WriteString("id", item.Id);
        w.WriteString("prompt", item.Prompt);
        w.WriteString("expected_answer", item.ExpectedAnswer);
        w.WriteString("source_location", item.SourceLocation);

        w.WritePropertyName("assertions");
        w.WriteStartArray();
        foreach (var a in item.Assertions)
        {
            // Reuse the polymorphic Assertion converter wired on the
            // Assertion record itself — guarantees the wire shape is
            // identical to TS regardless of which writer emits it.
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

        w.WriteEndObject();
    }
}
