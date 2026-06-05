namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Per-evaluator configuration. Mirrors the TS <c>EvaluatorOptions</c>
/// interface in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public sealed record EvaluatorOptions
{
    public double? Threshold { get; init; }
    public string? CitationFormat { get; init; }
    public bool? CaseSensitive { get; init; }

    /// <summary>Free-form per-evaluator options bag (TS <c>options?: Record&lt;string, unknown&gt;</c>).</summary>
    public IReadOnlyDictionary<string, object?>? Options { get; init; }
}

/// <summary>
/// Map of evaluator name → options. The TS shape excludes
/// <c>SemanticSimilarity</c> and <c>EvalGenAssertions</c> from the
/// configurable keys; that exclusion is enforced at the document
/// schema level rather than in this record type.
/// </summary>
public sealed class EvaluatorMap : Dictionary<EvaluatorName, EvaluatorOptions>
{
    public EvaluatorMap()
    {
    }

    public EvaluatorMap(IDictionary<EvaluatorName, EvaluatorOptions> source)
        : base(source)
    {
    }
}
