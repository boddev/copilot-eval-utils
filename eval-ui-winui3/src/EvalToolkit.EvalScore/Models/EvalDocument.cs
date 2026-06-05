using System.Text.Json.Serialization;

namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Top-level eval document shell. Mirrors the TS <c>EvalDocument</c>
/// interface in <c>eval-score/node/src/types.ts</c>.
///
/// <para>The <see cref="Items"/> are intentionally loose
/// (<c>IDictionary&lt;string, object?&gt;</c>) because the TS shape is
/// heterogeneous: single-turn items, multi-turn threads, and the
/// per-evaluator score objects all coexist with conditional fields.
/// <see cref="EvalDocumentBuilder"/> produces these dictionaries with
/// <c>null</c> keys already dropped — they correspond to TS
/// <c>undefined</c>, which <c>JSON.stringify</c> omits.</para>
/// </summary>
public sealed record EvalDocument(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IDictionary<string, object?>? Metadata,
    [property: JsonPropertyName("default_evaluators"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IDictionary<string, object?>? DefaultEvaluators,
    [property: JsonPropertyName("items")] IReadOnlyList<IDictionary<string, object?>> Items);
