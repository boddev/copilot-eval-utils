namespace EvalToolkit.Core.Concurrency;

/// <summary>
/// A unit of work the scheduler processes sequentially. Rows belonging
/// to the same multi-turn thread share one job so they execute in
/// <see cref="RowKey.TurnIndex"/> order and can chain a
/// <c>conversationId</c> through the thread (TS
/// <c>processJob</c> / <c>buildEvaluationJobs</c> contract in
/// <c>eval-score/node/src/evaluator.ts</c>).
/// </summary>
/// <typeparam name="TRow">Caller's row type.</typeparam>
public sealed record EvaluationJob<TRow>
{
    /// <summary>Indices into the original row list, in execution order.</summary>
    public required IReadOnlyList<int> RowIndices { get; init; }

    /// <summary>Identifier used to group rows. Useful for diagnostics.</summary>
    public required string ThreadKey { get; init; }

    /// <summary>True when this job spans more than one turn.</summary>
    public bool IsMultiTurn => RowIndices.Count > 1;
}

/// <summary>
/// Per-row inputs the builder needs to group threaded rows. Caller
/// supplies a <see cref="EvaluationJobBuilder"/>-compatible selector
/// that maps each row to one of these.
/// </summary>
public readonly record struct RowKey(
    int? TurnIndex,
    string? ThreadId,
    string? ItemId,
    int? ItemIndex);
