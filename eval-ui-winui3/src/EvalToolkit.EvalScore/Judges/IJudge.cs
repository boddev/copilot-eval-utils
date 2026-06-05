using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Common surface for all judge providers. Mirrors the TS <c>Judge</c>
/// interface in <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>Implementations may set <see cref="Model"/> to a label that
/// describes the underlying model; <see cref="MetricBuilder"/> uses it
/// as a fallback when the per-call <see cref="JudgeScore.Model"/> is
/// null.</para>
/// </summary>
public interface IJudge
{
    JudgeProvider Provider { get; }
    string? Model { get; }
    Task<JudgeScore> ScoreAsync(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, CancellationToken cancellationToken = default);
}
