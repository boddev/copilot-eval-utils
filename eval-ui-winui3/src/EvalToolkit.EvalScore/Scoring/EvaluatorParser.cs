using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// CLI-friendly comma-separated evaluator parser. Mirrors TS
/// <c>parseEvaluators</c> in <c>eval-score/node/src/scorer.ts</c>.
///
/// <para>Accepted tokens (case-insensitive): every <see cref="EvaluatorName"/>
/// wire string, plus aliases <c>semantic</c> and <c>semanticsimilarity</c>
/// both mapping to <see cref="EvaluatorName.Similarity"/>, plus the
/// literal <c>all</c> which expands to the full nine-evaluator set in
/// canonical TS order.</para>
/// </summary>
public static class EvaluatorParser
{
    private static readonly EvaluatorName[] s_allEvaluators =
    {
        EvaluatorName.Similarity,
        EvaluatorName.SemanticSimilarity,
        EvaluatorName.Relevance,
        EvaluatorName.Coherence,
        EvaluatorName.Groundedness,
        EvaluatorName.Citations,
        EvaluatorName.ExactMatch,
        EvaluatorName.PartialMatch,
        EvaluatorName.EvalGenAssertions,
    };

    /// <summary>Parse a comma-separated evaluator list. Empty/null
    /// returns the M365 default set. Throws
    /// <see cref="NotSupportedException"/> for unknown tokens.</summary>
    public static IReadOnlyList<EvaluatorName> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EvalRowHelpers.DefaultM365Evaluators;
        }

        string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return EvalRowHelpers.DefaultM365Evaluators;
        }

        var names = new List<EvaluatorName>();
        foreach (string raw in parts)
        {
            string lower = raw.ToLowerInvariant();
            if (lower == "all")
            {
                return s_allEvaluators;
            }
            EvaluatorName? resolved = ResolveAlias(lower);
            if (resolved is null)
            {
                throw new NotSupportedException(
                    $"Unsupported evaluator \"{raw}\". Supported evaluators: " +
                    $"{string.Join(", ", s_allEvaluators.Select(n => n.ToWireString()))}, all");
            }
            EvaluatorName name = resolved.Value;
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }
        return names.Count > 0 ? names : EvalRowHelpers.DefaultM365Evaluators;
    }

    private static EvaluatorName? ResolveAlias(string lower)
    {
        if (lower is "semantic" or "semanticsimilarity")
        {
            return EvaluatorName.Similarity;
        }
        foreach (EvaluatorName n in s_allEvaluators)
        {
            if (string.Equals(n.ToWireString(), lower, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }
        return null;
    }
}
