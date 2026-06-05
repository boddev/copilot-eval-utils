using EvalToolkit.Core.Concurrency;
using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Evaluator;

/// <summary>
/// Response generation pass: sends every row's prompt through a WorkIQ
/// client and records <see cref="EvalRow.ActualAnswer"/>,
/// <see cref="EvalRow.Citations"/>, <see cref="EvalRow.ResponseMetadata"/>,
/// and <see cref="EvalRow.ConversationId"/>. Mirrors TS
/// <c>evaluatePrompts</c> in <c>eval-score/node/src/evaluator.ts</c>.
///
/// <para>Behavior:
/// <list type="bullet">
///   <item>Rows that already have an <see cref="EvalRow.ActualAnswer"/>
///     are skipped (resumability — TS does the same; the completed
///     counter is seeded from pre-populated rows).</item>
///   <item>Rows are grouped into <see cref="EvaluationJob{TRow}"/>s via
///     <see cref="EvaluationJobBuilder"/> (single-turn vs thread).</item>
///   <item>Within a thread, rows run sequentially and the
///     <see cref="WorkIQAskOptions.ConversationId"/> is threaded forward
///     unless <see cref="EvalRow.ConversationChaining"/> is explicitly
///     false (in which case the chain is broken for that row).</item>
///   <item>Across threads, the <see cref="JobScheduler{TRow}"/> spawns up
///     to <c>min(concurrency, jobs)</c> workers (concurrency clamped to
///     <see cref="ThrottleGate.HardCap"/>). A global
///     <see cref="ThrottleGate"/> is also threaded through every WorkIQ
///     call so the worker-pool cap and the global LLM cap are both
///     enforced (matches the two-part TS contract).</item>
///   <item>Per-worker <see cref="EvaluateOptions.DelayMs"/> fires between
///     consecutive rows (default 500 ms). Implemented by the scheduler.</item>
///   <item>Errors from the WorkIQ client are captured per-row as
///     <c>"[ERROR: {message}]"</c> in <see cref="EvalRow.ActualAnswer"/>
///     and a <see cref="EvalErrorCode.AgentRequestFailed"/> on the row;
///     the pass continues with the next row.</item>
/// </list></para>
/// </summary>
public static class ResponseEvaluator
{
    public static async Task<IList<EvalRow>> EvaluatePromptsAsync(
        IList<EvalRow> rows,
        IWorkIQClient client,
        EvaluateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(client);
        options ??= new EvaluateOptions();

        int total = rows.Count;
        if (total == 0)
        {
            return rows;
        }

        IReadOnlyList<EvaluationJob<EvalRow>> jobs = EvaluationJobBuilder.Build(
            (IReadOnlyList<EvalRow>)rows,
            (row, idx) => new RowKey(row.TurnIndex, row.ThreadId, row.Id, row.ItemIndex));

        int requestedConcurrency = options.Concurrency ?? 1;
        if (requestedConcurrency < 1)
        {
            requestedConcurrency = 1;
        }
        if (requestedConcurrency > total)
        {
            requestedConcurrency = total;
        }
        if (requestedConcurrency > ThrottleGate.HardCap)
        {
            requestedConcurrency = ThrottleGate.HardCap;
        }

        int delayMs = options.DelayMs ?? JobSchedulerDefaults.DefaultDelayMs;
        if (delayMs < 0)
        {
            delayMs = 0;
        }

        int completed = 0;
        foreach (EvalRow row in rows)
        {
            if (!string.IsNullOrEmpty(row.ActualAnswer))
            {
                completed++;
            }
        }

        using var gate = new ThrottleGate(requestedConcurrency);
        var scheduler = new JobScheduler<EvalRow>(rows.ToArray(), jobs);
        await scheduler.RunAsync(
            async (rowIndex, inheritedConversationId, ct) =>
            {
                EvalRow row = rows[rowIndex];
                if (!string.IsNullOrEmpty(row.ActualAnswer))
                {
                    // Resumed row — pass through the existing conversation
                    // id (or the inherited one if the row didn't capture
                    // one) to keep the thread chain consistent.
                    return row.ConversationId ?? inheritedConversationId;
                }

                string? effectiveInherited = row.ConversationChaining == false ? null : inheritedConversationId;
                string fullPrompt = PromptBuilder.BuildPrompt(
                    row.Prompt,
                    options.SystemPrompt,
                    options.ConnectorId,
                    options.ConnectorPromptHint);

                try
                {
                    WorkIQResponse response = await gate.RunAsync(
                        () => client.AskWithMetadataAsync(
                            fullPrompt,
                            new WorkIQAskOptions(options.TenantId, options.AgentId, row.ConversationId ?? effectiveInherited),
                            ct),
                        ct).ConfigureAwait(false);
                    row.ActualAnswer = response.Text.Trim();
                    row.Citations = response.Citations;
                    row.ResponseMetadata = response.Raw;
                    if (!string.IsNullOrEmpty(response.ConversationId))
                    {
                        row.ConversationId = response.ConversationId;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    row.ActualAnswer = $"[ERROR: {message}]";
                    EvalRowHelpers.SetRowError(row, EvalErrorCode.AgentRequestFailed, message);
                }

                int doneNow = Interlocked.Increment(ref completed);
                options.OnProgress?.Invoke(doneNow, total, row.Prompt);
                if (options.OnRowCompleteAsync is not null)
                {
                    await options.OnRowCompleteAsync(rows.ToArray(), row, rowIndex, ct).ConfigureAwait(false);
                }

                if (row.ConversationChaining == false)
                {
                    return null;
                }
                return row.ConversationId ?? effectiveInherited;
            },
            new JobSchedulerOptions
            {
                Concurrency = requestedConcurrency,
                DelayMs = delayMs,
            },
            cancellationToken).ConfigureAwait(false);

        return rows;
    }
}
