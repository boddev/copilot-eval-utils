using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.EvalDocument;

/// <summary>
/// Helpers operating on <see cref="EvalRow"/> for evaluator resolution
/// and status derivation. Mirrors the top of
/// <c>eval-score/node/src/eval-document.ts</c>.
/// </summary>
public static class EvalRowHelpers
{
    /// <summary>TS <c>DEFAULT_M365_EVALUATORS</c>.</summary>
    public static IReadOnlyList<EvaluatorName> DefaultM365Evaluators { get; } =
        new[] { EvaluatorName.Relevance, EvaluatorName.Coherence };

    /// <summary>
    /// Resolve the effective evaluator list for a row. Mirrors TS
    /// <c>resolveRowEvaluators</c>: row-level defaults win over run-level
    /// defaults; row-level overrides either replace or extend the base.
    /// </summary>
    public static IReadOnlyList<EvaluatorName> ResolveRowEvaluators(
        EvalRow row,
        IReadOnlyList<EvaluatorName> runEvaluators)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(runEvaluators);

        IReadOnlyList<EvaluatorName> defaults = EvaluatorMapNames(row.DocumentDefaultEvaluators);
        IReadOnlyList<EvaluatorName> baseList = defaults.Count > 0 ? defaults : runEvaluators;
        IReadOnlyList<EvaluatorName> overrides = EvaluatorMapNames(row.Evaluators);

        if (overrides.Count == 0) return NormalizeEvaluatorList(baseList);
        if (row.EvaluatorsMode == EvaluatorsMode.Replace) return NormalizeEvaluatorList(overrides);
        return NormalizeEvaluatorList(baseList.Concat(overrides));
    }

    /// <summary>
    /// Derive status for a row. Mirrors TS <c>deriveRowStatus</c>.
    /// Error precedence: explicit <see cref="EvalRow.Error"/>, missing
    /// answer, or <c>[ERROR:</c> prefix → <see cref="EvalStatus.Error"/>.
    /// Otherwise aggregate <see cref="MetricResult.Passed"/> values when
    /// any are non-null, else fall back to similarity threshold.
    /// </summary>
    public static EvalStatus DeriveRowStatus(EvalRow row, int threshold = 70)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Error is not null ||
            string.IsNullOrEmpty(row.ActualAnswer) ||
            row.ActualAnswer.StartsWith("[ERROR:", StringComparison.Ordinal))
        {
            return EvalStatus.Error;
        }

        if (row.Metrics is { Count: > 0 } metrics)
        {
            var statuses = metrics
                .Where(m => m.Passed.HasValue)
                .Select(m => m.Passed!.Value)
                .ToList();

            if (statuses.Count > 0)
            {
                int passed = statuses.Count(s => s);
                if (passed == statuses.Count) return EvalStatus.Pass;
                if (passed == 0) return EvalStatus.Fail;
                return EvalStatus.Partial;
            }
        }

        return (row.SimilarityScore ?? 0) >= threshold ? EvalStatus.Pass : EvalStatus.Fail;
    }

    /// <summary>
    /// Set an error on a row and mark its status as Error. Mirrors TS
    /// <c>setRowError</c> mutation semantics.
    /// </summary>
    public static void SetRowError(EvalRow row, EvalErrorCode code, string message)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(message);

        row.Error = new EvalError { Code = code, Message = message };
        row.Status = EvalStatus.Error;
    }

    /// <summary>
    /// Collapse <c>SemanticSimilarity</c> → <c>Similarity</c>, dedupe
    /// preserving order, fall back to <see cref="DefaultM365Evaluators"/>
    /// when the resulting list is empty. Mirrors TS
    /// <c>normalizeEvaluatorList</c>.
    /// </summary>
    public static IReadOnlyList<EvaluatorName> NormalizeEvaluatorList(IEnumerable<EvaluatorName> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var result = new List<EvaluatorName>();
        foreach (var n in names)
        {
            var normalized = n.Normalize();
            if (!result.Contains(normalized)) result.Add(normalized);
        }

        return result.Count > 0 ? result : DefaultM365Evaluators;
    }

    /// <summary>
    /// Return the evaluator names declared by an <see cref="EvaluatorMap"/>.
    /// Mirrors TS <c>evaluatorMapNames</c> — preserves insertion order.
    /// </summary>
    public static IReadOnlyList<EvaluatorName> EvaluatorMapNames(EvaluatorMap? map)
    {
        if (map is null || map.Count == 0) return Array.Empty<EvaluatorName>();
        return map.Keys.ToList();
    }
}
