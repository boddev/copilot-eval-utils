using System.Text.RegularExpressions;
using EvalToolkit.EvalScore.Assertions;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// Deterministic metric evaluators (ExactMatch, PartialMatch, Citations,
/// EvalGenAssertions). Mirrors the <c>evaluateDeterministicMetrics</c>
/// helper in <c>eval-score/node/src/scorer.ts</c>.
///
/// <para>These do not call any LLM/judge — they compute scores purely
/// from row state.</para>
/// </summary>
public static class DeterministicEvaluators
{
    private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Compute every deterministic metric requested in
    /// <paramref name="evaluators"/>. Side-effect: when
    /// <see cref="EvaluatorName.EvalGenAssertions"/> is requested and the
    /// row carries assertions, the row's
    /// <see cref="EvalRow.AssertionResults"/> is mutated to the freshly
    /// computed list (matches TS behavior).
    /// </summary>
    public static List<MetricResult> Evaluate(EvalRow row, IReadOnlyList<EvaluatorName> evaluators, double? threshold)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(evaluators);

        var metrics = new List<MetricResult>();
        string actual = Normalize(row.ActualAnswer);
        string expected = Normalize(row.ExpectedAnswer);

        if (evaluators.Contains(EvaluatorName.ExactMatch))
        {
            bool passed = string.Equals(actual, expected, StringComparison.Ordinal);
            metrics.Add(new MetricResult
            {
                Name = EvaluatorName.ExactMatch,
                Score = passed ? 100 : 0,
                Passed = passed,
                Reason = passed
                    ? "Actual answer exactly matches expected answer after normalization."
                    : "Actual answer does not exactly match expected answer.",
                Provider = MetricProvider.Deterministic,
                Scale = MetricScale.ZeroToOneHundred,
                Threshold = threshold,
            });
        }

        if (evaluators.Contains(EvaluatorName.PartialMatch))
        {
            bool passed = expected.Length > 0 &&
                (actual.Contains(expected, StringComparison.Ordinal) ||
                 expected.Contains(actual, StringComparison.Ordinal));
            metrics.Add(new MetricResult
            {
                Name = EvaluatorName.PartialMatch,
                Score = passed ? 100 : 0,
                Passed = passed,
                Reason = passed
                    ? "Actual and expected answers partially overlap."
                    : "Actual and expected answers do not partially overlap.",
                Provider = MetricProvider.Deterministic,
                Scale = MetricScale.ZeroToOneHundred,
                Threshold = threshold,
            });
        }

        if (evaluators.Contains(EvaluatorName.Citations))
        {
            // TS: hasCitation = Boolean(row.citations?.length) ||
            //                   (row.sourceLocation && actualAnswer.toLowerCase().includes(row.sourceLocation.toLowerCase()))
            bool hasCitation = (row.Citations is { Count: > 0 }) ||
                (!string.IsNullOrEmpty(row.SourceLocation) &&
                    (row.ActualAnswer ?? string.Empty)
                        .Contains(row.SourceLocation, StringComparison.OrdinalIgnoreCase));
            metrics.Add(new MetricResult
            {
                Name = EvaluatorName.Citations,
                Score = hasCitation ? 100 : 0,
                Passed = hasCitation,
                Reason = hasCitation
                    ? "At least one citation/source reference was detected."
                    : "No citation/source reference was detected.",
                Provider = MetricProvider.Deterministic,
                Scale = MetricScale.ZeroToOneHundred,
                Threshold = threshold,
            });
        }

        if (evaluators.Contains(EvaluatorName.EvalGenAssertions) &&
            row.Assertions is { Count: > 0 })
        {
            List<AssertionResult> results = AssertionChecker.EvaluateRowAssertions(row);
            row.AssertionResults = results;
            int passedCount = results.Count(r => r.Passed);
            int score = results.Count > 0
                ? (int)Math.Round((double)passedCount / results.Count * 100, MidpointRounding.AwayFromZero)
                : 0;
            metrics.Add(new MetricResult
            {
                Name = EvaluatorName.EvalGenAssertions,
                Score = score,
                Passed = results.Count > 0 && passedCount == results.Count,
                Reason = $"{passedCount}/{results.Count} assertions passed.",
                Provider = MetricProvider.Deterministic,
                Scale = MetricScale.ZeroToOneHundred,
                Threshold = threshold,
            });
        }

        return metrics;
    }

    /// <summary>
    /// TS <c>normalize</c>: trim, collapse whitespace runs to single
    /// spaces, lower-case using invariant culture.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        string trimmed = value.Trim();
        string collapsed = s_whitespace.Replace(trimmed, " ");
        return collapsed.ToLowerInvariant();
    }
}
