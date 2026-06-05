namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Structured error for a failed evaluation row/turn. Mirrors the TS
/// <c>EvalError</c> interface in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public sealed record EvalError
{
    public required EvalErrorCode Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>Mirrors the TS <c>EvalError.code</c> union.</summary>
public enum EvalErrorCode
{
    AgentRequestFailed,
    TurnSkipped,
    EvaluatorsFailed,
}

public static class EvalErrorCodes
{
    public static string ToWireString(this EvalErrorCode code) => code switch
    {
        EvalErrorCode.AgentRequestFailed => "agentRequestFailed",
        EvalErrorCode.TurnSkipped => "turnSkipped",
        EvalErrorCode.EvaluatorsFailed => "evaluatorsFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    public static EvalErrorCode FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim() switch
        {
            "agentRequestFailed" => EvalErrorCode.AgentRequestFailed,
            "turnSkipped" => EvalErrorCode.TurnSkipped,
            "evaluatorsFailed" => EvalErrorCode.EvaluatorsFailed,
            _ => throw new NotSupportedException($"Unknown eval error code: '{value}'"),
        };
    }
}

/// <summary>
/// Evaluation target. Mirrors the TS <c>EvaluationTarget</c> interface.
/// </summary>
public sealed record EvaluationTarget
{
    public required TargetType Type { get; init; }
    public string? AgentId { get; init; }
    public string? ConnectorId { get; init; }
}
