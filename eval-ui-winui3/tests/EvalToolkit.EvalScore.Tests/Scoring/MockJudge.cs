using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Scoring;

/// <summary>Programmable mock judge.</summary>
internal sealed class MockJudge : IJudge
{
    public JudgeProvider Provider { get; init; } = JudgeProvider.WorkIq;
    public string? Model { get; init; } = "mock-model";
    public Func<EvalRow, EvaluatorName, JudgeScore>? Scorer { get; init; }
    public Func<EvalRow, EvaluatorName, Exception>? ErrorThrower { get; init; }
    public int CallCount { get; private set; }
    public List<(EvalRow Row, EvaluatorName Evaluator)> Calls { get; } = new();

    public Task<JudgeScore> ScoreAsync(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Calls.Add((row, evaluator));
        if (ErrorThrower is not null)
        {
            throw ErrorThrower(row, evaluator);
        }
        if (Scorer is not null)
        {
            return Task.FromResult(Scorer(row, evaluator));
        }
        return Task.FromResult(new JudgeScore(80, "ok", Model));
    }
}
