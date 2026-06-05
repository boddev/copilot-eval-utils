using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Builds judge prompts. Mirrors <c>buildScoringPrompt</c> in
/// <c>eval-score/node/src/judge-providers.ts</c> line for line.
///
/// <para>Normalization rule (per round-1 review): only
/// <see cref="EvaluatorName.SemanticSimilarity"/> is renamed to
/// <see cref="EvaluatorName.Similarity"/> in the prompt label.
/// Rubricless evaluators (Citations, ExactMatch, PartialMatch,
/// EvalGenAssertions) keep their own label but fall back to the
/// Similarity rubric text — matching the TS
/// <c>RUBRICS[normalizedEvaluator] ?? RUBRICS.Similarity</c>
/// expression.</para>
/// </summary>
public static class ScoringPromptBuilder
{
    public static string Build(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, bool jsonResponse = false)
    {
        ArgumentNullException.ThrowIfNull(row);

        EvaluatorName normalized = evaluator.Normalize();
        string label = normalized.ToWireString();
        string rubric = Rubrics.Map.TryGetValue(normalized, out string? r)
            ? r
            : Rubrics.Map[EvaluatorName.Similarity];

        string responseInstruction = jsonResponse
            ? "Respond with strict JSON: {\"score\": number, \"reason\": \"short rationale\"}."
            : "Respond with ONLY a single number between 0 and 100, nothing else.";

        string contextOrSource = row.Context ?? row.SourceLocation ?? string.Empty;

        // TS uses \n explicitly via Array.join('\n') — must not use Environment.NewLine.
        return string.Join('\n', new[]
        {
            $"Evaluate the response using the {label} rubric.",
            rubric,
            "Use a 0 to 100 scale where 0 is unusable and 100 is excellent for this rubric.",
            responseInstruction,
            string.Empty,
            $"Prompt: {row.Prompt}",
            string.Empty,
            $"Expected or Ground-Truth Response: {row.ExpectedAnswer}",
            string.Empty,
            $"Context / Source: {contextOrSource}",
            string.Empty,
            $"Actual Answer: {row.ActualAnswer}",
        });
    }
}
