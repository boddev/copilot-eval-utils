namespace EvalToolkit.Core.Concurrency;

/// <summary>
/// Per-worker delay applied between consecutive rows. Matches the
/// <c>DELAY_MS = 500</c> constant in TS <c>evaluator.ts</c> /
/// <c>scorer.ts</c>. Used by the scheduler unless overridden via
/// <see cref="JobSchedulerOptions.DelayMs"/>.
/// </summary>
public static class JobSchedulerDefaults
{
    public const int DefaultDelayMs = 500;
}

/// <summary>
/// Knobs the engine port can tune per call. All optional; unset means
/// "match the TS defaults". <see cref="DelayMs"/> is clamped to
/// non-negative.
/// </summary>
public sealed record JobSchedulerOptions
{
    /// <summary>
    /// Concurrency (caller-requested). The scheduler clamps it to
    /// <c>[1, <see cref="ThrottleGate.HardCap"/>]</c> and additionally
    /// to the total job count (so a 3-job run with concurrency 5 only
    /// spawns 3 workers — matches TS <c>Math.min(concurrency, total||1)</c>).
    /// </summary>
    public int? Concurrency { get; init; }

    /// <summary>Per-worker delay between rows, milliseconds. Default 500.</summary>
    public int? DelayMs { get; init; }
}

/// <summary>
/// Worker-loop runner. Mirrors the TS
/// <c>await Promise.all(Array.from({length: concurrency}, () =&gt; worker()))</c>
/// pattern in <c>evaluator.ts</c>:
///
/// <list type="number">
///   <item>Spawn <c>min(concurrency, jobs.Count)</c> workers.</item>
///   <item>Each worker pulls the next un-claimed job from a shared
///     index (<see cref="Interlocked.Increment(ref int)"/>) and
///     processes its rows sequentially.</item>
///   <item>A per-worker delay (<see cref="JobSchedulerOptions.DelayMs"/>)
///     fires between rows of a multi-turn job and between jobs.
///     Within a single-row job there's nothing to delay.</item>
/// </list>
///
/// Conversation chaining (`conversationChaining === false` opts out)
/// is the caller's responsibility — the scheduler threads a
/// <c>string?</c> conversationId between row callbacks but does not
/// inspect it. The caller decides whether to propagate or reset based
/// on the row's chain flag, exactly as TS does.
///
/// <para><b>Two-part concurrency contract</b> (per Opus-4.8 round-4):
/// TS runs <b>both</b> caps simultaneously — the worker pool
/// (<c>concurrency</c>) AND a global <see cref="ThrottleGate"/>
/// (capped at <see cref="ThrottleGate.HardCap"/>) wrapping every
/// client call inside the row callback. The scheduler enforces only
/// the worker-pool cap. The engine glue (evalscore-engine-port) is
/// responsible for instantiating a <see cref="ThrottleGate"/> from
/// <c>EVALSCORE_MAX_CONCURRENCY</c> and wrapping every LLM/WorkIQ
/// invocation inside <c>processRow</c> with
/// <see cref="ThrottleGate.RunAsync{T}(Func{Task{T}}, CancellationToken)"/>.
/// Forgetting this drops the global cap silently — a row callback that
/// only does local work will run at <c>concurrency</c>, but as soon as
/// it makes a gated call it is correctly throttled.</para>
/// </summary>
public sealed class JobScheduler<TRow>
{
    private readonly IReadOnlyList<TRow> _rows;
    private readonly IReadOnlyList<EvaluationJob<TRow>> _jobs;

    public JobScheduler(IReadOnlyList<TRow> rows, IReadOnlyList<EvaluationJob<TRow>> jobs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(jobs);
        _rows = rows;
        _jobs = jobs;
    }

    /// <summary>
    /// Process every job. <paramref name="processRow"/> is invoked once
    /// per row with the inherited conversationId (null on the first
    /// row of a job) and must return the conversationId to chain into
    /// the next row of the same job (or null to break the chain).
    /// </summary>
    /// <param name="processRow">
    /// Per-row callback: <c>(rowIndex, inheritedConversationId, ct) =&gt; nextConversationId</c>.
    /// </param>
    public async Task RunAsync(
        Func<int, string?, CancellationToken, Task<string?>> processRow,
        JobSchedulerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processRow);
        _ = _rows;

        if (_jobs.Count == 0)
        {
            return;
        }

        int desired = options?.Concurrency ?? 1;
        if (desired < 1)
        {
            desired = 1;
        }
        if (desired > ThrottleGate.HardCap)
        {
            desired = ThrottleGate.HardCap;
        }
        if (desired > _jobs.Count)
        {
            desired = _jobs.Count;
        }

        int delayMs = options?.DelayMs ?? JobSchedulerDefaults.DefaultDelayMs;
        if (delayMs < 0)
        {
            delayMs = 0;
        }

        int nextJob = -1;
        var workers = new Task[desired];
        for (int w = 0; w < desired; w++)
        {
            workers[w] = Task.Run(() => WorkerLoopAsync(processRow, delayMs, () => Interlocked.Increment(ref nextJob), cancellationToken), cancellationToken);
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(
        Func<int, string?, CancellationToken, Task<string?>> processRow,
        int delayMs,
        Func<int> nextIndex,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int idx = nextIndex();
            if (idx >= _jobs.Count)
            {
                return;
            }

            EvaluationJob<TRow> job = _jobs[idx];
            string? conversationId = null;

            for (int i = 0; i < job.RowIndices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rowIndex = job.RowIndices[i];
                conversationId = await processRow(rowIndex, conversationId, cancellationToken).ConfigureAwait(false);

                bool moreRowsInJob = i < job.RowIndices.Count - 1;
                if (delayMs > 0 && moreRowsInJob)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            // Inter-job delay (matches TS where the per-row delay fires
            // unless this is the last row in the worker's queue —
            // approximated here by always delaying between jobs except
            // when there are no more jobs at all).
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
