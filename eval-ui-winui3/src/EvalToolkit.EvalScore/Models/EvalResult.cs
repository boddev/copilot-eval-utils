namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Complete evaluation result. Mirrors the TS <c>EvalResult</c>
/// interface in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public sealed record EvalResult
{
    public required IReadOnlyList<EvalRow> Rows { get; init; }
    public required string InputFile { get; init; }
    public required InputFormat InputFormat { get; init; }

    /// <summary>ISO 8601 timestamp when the evaluation was performed.</summary>
    public required string Timestamp { get; init; }

    public string? SystemPrompt { get; init; }
    public EvaluationTarget? Target { get; init; }
    public JudgeProvider? JudgeProvider { get; init; }
    public IReadOnlyList<EvaluatorName>? Evaluators { get; init; }
    public IDictionary<string, object?>? Metadata { get; init; }
    public EvaluatorMap? DefaultEvaluators { get; init; }
}
