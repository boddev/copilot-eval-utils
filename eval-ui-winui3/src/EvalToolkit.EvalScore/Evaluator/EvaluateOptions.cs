using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Evaluator;

/// <summary>
/// Per-call configuration for <see cref="ResponseEvaluator"/>. Mirrors
/// the TS <c>EvaluateOptions</c> interface in
/// <c>eval-score/node/src/evaluator.ts</c>.
///
/// <para>Defaults:
/// <list type="bullet">
///   <item><see cref="ConnectorPromptHint"/>: <c>false</c> default in TS
///     for the evaluator caller (the buildPrompt helper itself
///     defaults to <c>true</c>, but the evaluator passes
///     <c>options?.connectorPromptHint ?? false</c>).</item>
///   <item><see cref="Concurrency"/>: 1.</item>
///   <item><see cref="DelayMs"/>: 500.</item>
/// </list></para>
/// </summary>
public sealed record EvaluateOptions
{
    public string? SystemPrompt { get; init; }
    public string? ConnectorId { get; init; }
    public bool ConnectorPromptHint { get; init; }
    public string? TenantId { get; init; }
    public string? AgentId { get; init; }

    /// <summary>Caller-requested concurrency. Clamped to
    /// <c>[1, ThrottleGate.HardCap]</c> and additionally to the total
    /// job count. Default 1.</summary>
    public int? Concurrency { get; init; }

    /// <summary>Per-worker delay between rows in milliseconds. Default 500.</summary>
    public int? DelayMs { get; init; }

    /// <summary>Optional progress callback: completed/total/currentPrompt.</summary>
    public Action<int, int, string>? OnProgress { get; init; }

    /// <summary>Optional per-row callback after each row completes
    /// (synchronous or async). Used by the engine to write the
    /// checkpoint between rows.</summary>
    public Func<IReadOnlyList<EvalRow>, EvalRow, int, CancellationToken, Task>? OnRowCompleteAsync { get; init; }
}
