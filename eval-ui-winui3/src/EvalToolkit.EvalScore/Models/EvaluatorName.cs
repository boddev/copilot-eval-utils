namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Evaluator names supported by EvalScore. Mirrors the TS
/// <c>EvaluatorName</c> union in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public enum EvaluatorName
{
    SemanticSimilarity,
    Similarity,
    Relevance,
    Coherence,
    Groundedness,
    Citations,
    ExactMatch,
    PartialMatch,
    EvalGenAssertions,
}

/// <summary>Wire-string mapping for <see cref="EvaluatorName"/>.</summary>
public static class EvaluatorNames
{
    public static string ToWireString(this EvaluatorName name) => name switch
    {
        EvaluatorName.SemanticSimilarity => "SemanticSimilarity",
        EvaluatorName.Similarity => "Similarity",
        EvaluatorName.Relevance => "Relevance",
        EvaluatorName.Coherence => "Coherence",
        EvaluatorName.Groundedness => "Groundedness",
        EvaluatorName.Citations => "Citations",
        EvaluatorName.ExactMatch => "ExactMatch",
        EvaluatorName.PartialMatch => "PartialMatch",
        EvaluatorName.EvalGenAssertions => "EvalGenAssertions",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public static EvaluatorName FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim() switch
        {
            "SemanticSimilarity" => EvaluatorName.SemanticSimilarity,
            "Similarity" => EvaluatorName.Similarity,
            "Relevance" => EvaluatorName.Relevance,
            "Coherence" => EvaluatorName.Coherence,
            "Groundedness" => EvaluatorName.Groundedness,
            "Citations" => EvaluatorName.Citations,
            "ExactMatch" => EvaluatorName.ExactMatch,
            "PartialMatch" => EvaluatorName.PartialMatch,
            "EvalGenAssertions" => EvaluatorName.EvalGenAssertions,
            _ => throw new NotSupportedException($"Unknown evaluator name: '{value}'"),
        };
    }

    /// <summary>
    /// TS normalization rule: <c>SemanticSimilarity</c> is rendered as
    /// <c>Similarity</c> in prompt labels and metric names (the two
    /// are semantically identical; <c>SemanticSimilarity</c> is the
    /// historic spelling).
    /// </summary>
    public static EvaluatorName Normalize(this EvaluatorName name) =>
        name == EvaluatorName.SemanticSimilarity ? EvaluatorName.Similarity : name;
}

/// <summary>
/// How row-level evaluators combine with file-level defaults. Mirrors
/// the TS <c>EvaluatorsMode</c> union.
/// </summary>
public enum EvaluatorsMode
{
    Extend,
    Replace,
}

public static class EvaluatorsModes
{
    public static string ToWireString(this EvaluatorsMode mode) => mode switch
    {
        EvaluatorsMode.Extend => "extend",
        EvaluatorsMode.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static EvaluatorsMode FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "extend" => EvaluatorsMode.Extend,
            "replace" => EvaluatorsMode.Replace,
            _ => throw new NotSupportedException($"Unknown evaluators mode: '{value}'"),
        };
    }
}

/// <summary>
/// Schema-native item/turn status. Mirrors the TS <c>EvalStatus</c> union.
/// </summary>
public enum EvalStatus
{
    Pass,
    Fail,
    Partial,
    Error,
}

public static class EvalStatuses
{
    public static string ToWireString(this EvalStatus status) => status switch
    {
        EvalStatus.Pass => "pass",
        EvalStatus.Fail => "fail",
        EvalStatus.Partial => "partial",
        EvalStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static EvalStatus FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "pass" => EvalStatus.Pass,
            "fail" => EvalStatus.Fail,
            "partial" => EvalStatus.Partial,
            "error" => EvalStatus.Error,
            _ => throw new NotSupportedException($"Unknown eval status: '{value}'"),
        };
    }
}

/// <summary>Target type for an evaluation. Mirrors TS <c>TargetType</c>.</summary>
public enum TargetType
{
    WorkIq,
    M365Agent,
    Connector,
}

public static class TargetTypes
{
    public static string ToWireString(this TargetType type) => type switch
    {
        TargetType.WorkIq => "workiq",
        TargetType.M365Agent => "m365-agent",
        TargetType.Connector => "connector",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static TargetType FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "workiq" => TargetType.WorkIq,
            "m365-agent" => TargetType.M365Agent,
            "connector" => TargetType.Connector,
            _ => throw new NotSupportedException($"Unknown target type: '{value}'"),
        };
    }
}
