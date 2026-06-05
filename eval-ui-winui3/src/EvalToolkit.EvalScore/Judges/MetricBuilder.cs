using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Builds a <see cref="MetricResult"/> from a <see cref="JudgeScore"/>.
/// Mirrors TS <c>metricFromJudge</c> in
/// <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>Model fallback: <c>score.Model</c> wins over
/// <c>judge.Model</c> (matches TS <c>score.model ?? judge.model</c>).
/// Pass/fail: <c>null</c> when no threshold (TS <c>undefined</c>);
/// otherwise <c>score &gt;= threshold</c>.</para>
/// </summary>
public static class MetricBuilder
{
    public static MetricResult MetricFromJudge(
        JudgeScore score,
        IJudge judge,
        double? threshold = null,
        EvaluatorName evaluator = EvaluatorName.Similarity)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(judge);

        return new MetricResult
        {
            Name = evaluator.Normalize(),
            Score = score.Score,
            Passed = threshold is null ? null : score.Score >= threshold.Value,
            Reason = score.Reason,
            Provider = MetricProviders.FromJudge(judge.Provider),
            Model = score.Model ?? judge.Model,
            Scale = MetricScale.ZeroToOneHundred,
            RubricVersion = Rubrics.RubricVersion,
            Threshold = threshold,
        };
    }
}
