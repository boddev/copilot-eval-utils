using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Scoring rubrics. Mirrors <c>RUBRICS</c> and <c>RUBRIC_VERSION</c>
/// in <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>Both <see cref="EvaluatorName.Similarity"/> and
/// <see cref="EvaluatorName.SemanticSimilarity"/> are included so a
/// lookup against either yields the identical rubric text (TS uses
/// the same string for both). Other rubricless evaluators
/// (<see cref="EvaluatorName.Citations"/>,
/// <see cref="EvaluatorName.ExactMatch"/>,
/// <see cref="EvaluatorName.PartialMatch"/>,
/// <see cref="EvaluatorName.EvalGenAssertions"/>) fall back to the
/// Similarity rubric in <see cref="ScoringPromptBuilder"/> while
/// keeping their own evaluator label.</para>
/// </summary>
public static class Rubrics
{
    public const string RubricVersion = "evalscore-m365-rubrics-v1";

    public static IReadOnlyDictionary<EvaluatorName, string> Map { get; } =
        new Dictionary<EvaluatorName, string>
        {
            [EvaluatorName.Relevance] =
                "Measure whether the response directly addresses the user query and includes the important points needed to answer it. Penalize off-topic, incomplete, or insufficient answers.",
            [EvaluatorName.Coherence] =
                "Measure whether the response is logically organized, internally consistent, fluent, and easy to follow. Penalize contradictions, confusing structure, or unreadable wording.",
            [EvaluatorName.Groundedness] =
                "Measure whether claims in the response are supported by the provided context/source or expected answer. Penalize unsupported claims, hallucinations, or missing source support.",
            [EvaluatorName.Similarity] =
                "Measure semantic alignment between the actual response and the ground-truth response for the prompt. Wording can differ, but meaning and important facts should match.",
            [EvaluatorName.SemanticSimilarity] =
                "Measure semantic alignment between the actual response and the ground-truth response for the prompt. Wording can differ, but meaning and important facts should match.",
        };
}
