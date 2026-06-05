using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// A single evaluation row. Mirrors the TS <c>EvalRow</c> interface in
/// <c>eval-score/node/src/types.ts</c>.
///
/// <para>Mutable per-field semantics: the TS code mutates rows during
/// evaluation (sets <see cref="ActualAnswer"/>, then
/// <see cref="SimilarityScore"/>, then <see cref="Metrics"/>, etc.).
/// We model it as a mutable class with public setters so the engine
/// can update fields in place without churn from <c>with</c>
/// expressions on every step.</para>
/// </summary>
public sealed class EvalRow
{
    /// <summary>The question/prompt to send to the response provider.</summary>
    public required string Prompt { get; set; }

    /// <summary>The known-correct answer (ground truth).</summary>
    public required string ExpectedAnswer { get; set; }

    /// <summary>Where in Microsoft 365 the answer is found.</summary>
    public required string SourceLocation { get; set; }

    /// <summary>
    /// The response provider's reply. Populated after querying. Empty
    /// string until then. Error rows store <c>[ERROR: ...]</c>.
    /// </summary>
    public string ActualAnswer { get; set; } = string.Empty;

    /// <summary>Canonical semantic similarity score (0-100).</summary>
    public double? SimilarityScore { get; set; }

    /// <summary>Per-evaluator metric results.</summary>
    public IList<MetricResult>? Metrics { get; set; }

    /// <summary>Citations returned by the response provider, when available.</summary>
    public IReadOnlyList<Citation>? Citations { get; set; }

    /// <summary>Raw provider metadata (used by advanced evaluators).</summary>
    public object? ResponseMetadata { get; set; }

    /// <summary>Multi-turn conversation context id from the response provider.</summary>
    public string? ConversationId { get; set; }

    /// <summary>Assertions to check against <see cref="ActualAnswer"/> (from EvalGen sidecar).</summary>
    public IReadOnlyList<Assertion>? Assertions { get; set; }

    /// <summary>Assertion check results.</summary>
    public IList<AssertionResult>? AssertionResults { get; set; }

    /// <summary>Stable m365 eval document item id.</summary>
    public string? Id { get; set; }

    /// <summary>Original item index for preserving document order in schema output.</summary>
    public int? ItemIndex { get; set; }

    /// <summary>Zero-based turn index for rows that belong to a multi-turn thread.</summary>
    public int? TurnIndex { get; set; }

    /// <summary>Multi-turn thread identifier/name used to preserve conversation boundaries.</summary>
    public string? ThreadId { get; set; }

    /// <summary>When false, preserve thread ordering but don't pass provider conversation context.</summary>
    public bool? ConversationChaining { get; set; }

    public string? ThreadName { get; set; }
    public string? ThreadDescription { get; set; }

    /// <summary>Additional context used by groundedness-style evaluators.</summary>
    public string? Context { get; set; }

    /// <summary>File-level default evaluators copied onto rows during read.</summary>
    public EvaluatorMap? DocumentDefaultEvaluators { get; set; }

    /// <summary>Per-item/per-turn evaluator overrides from m365 eval documents.</summary>
    public EvaluatorMap? Evaluators { get; set; }

    /// <summary>How row-level evaluators combine with defaults.</summary>
    public EvaluatorsMode? EvaluatorsMode { get; set; }

    /// <summary>Derived item/turn status after scoring.</summary>
    public EvalStatus? Status { get; set; }

    /// <summary>Structured item/turn error when response generation or scoring fails.</summary>
    public EvalError? Error { get; set; }
}
