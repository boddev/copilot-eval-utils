using EvalToolkit.Core.Concurrency;
using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// Scoring pass: dispatches LLM evaluators to a judge (with optional
/// fallback) and computes deterministic metrics, mutating every row's
/// <see cref="EvalRow.Metrics"/>, <see cref="EvalRow.SimilarityScore"/>,
/// <see cref="EvalRow.Status"/>, and (on error) <see cref="EvalRow.Error"/>.
/// Mirrors TS <c>scoreAnswers</c> in <c>eval-score/node/src/scorer.ts</c>.
///
/// <para>Behavior:
/// <list type="bullet">
///   <item>Rows that already have a <see cref="EvalRow.SimilarityScore"/>
///     are skipped (resumability).</item>
///   <item>Rows with no actual answer or an <c>[ERROR:</c>-prefixed
///     actual answer get a zero-score primary metric and the appropriate
///     <see cref="EvalErrorCode"/> attached, without calling any judge.</item>
///   <item>For each remaining row, every LLM evaluator (filtered via
///     <see cref="IsLlmEvaluator(EvaluatorName)"/>) runs through the
///     judge inside a <see cref="ThrottleGate"/>; on fallback-eligible
///     failure the secondary judge takes over with a "Fallback from
///     {primary} due to: {message}." reason prefix preserved.</item>
///   <item>Deterministic metrics (ExactMatch, PartialMatch, Citations,
///     EvalGenAssertions) are appended after LLM metrics.</item>
///   <item>Primary metric is <see cref="EvaluatorName.Similarity"/> if
///     present, else <see cref="EvaluatorName.SemanticSimilarity"/>, else
///     the first metric. The primary metric's score becomes
///     <see cref="EvalRow.SimilarityScore"/>.</item>
///   <item>A try/catch around the entire row's scoring records a single
///     zero-score metric on uncaught exceptions (matches TS warning path).</item>
///   <item>Concurrency is enforced both at the worker-pool level
///     (<see cref="JobScheduler{TRow}"/>) AND inside the row via
///     <see cref="ThrottleGate"/> — the two-part contract from the
///     concurrency-port slice.</item>
/// </list></para>
///
/// <para><b>Scoring builds single-row jobs</b> (no thread grouping),
/// so JobScheduler workers pull rows in original-row FIFO order to
/// match TS <c>scoreAnswers</c>'s <c>nextIndex++</c> worker pattern.
/// Thread grouping in <c>EvaluationJobBuilder</c> is for the evaluator
/// (where conversation chaining matters), not the scorer.</para>
/// </summary>
public static class Scorer
{
    public static async Task<IList<EvalRow>> ScoreAnswersAsync(
        IList<EvalRow> rows,
        IWorkIQClient? client,
        ScoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        options ??= new ScoreOptions();

        int total = rows.Count;
        if (total == 0)
        {
            return rows;
        }

        JudgeProvider primaryProvider = options.JudgeProvider ?? JudgeProvider.WorkIq;
        IJudge judge = options.Judge ?? JudgeFactory.Create(primaryProvider, client, options.TenantId, options.JudgeAgentId);
        IJudge? fallbackJudge = options.FallbackJudge ?? FallbackJudgeBuilder.Build(
            judge, client, options.TenantId, options.FallbackJudgeProvider, options.DisableFallbackJudge);

        IReadOnlyList<EvaluatorName> evaluators = options.Evaluators ?? EvalRowHelpers.DefaultM365Evaluators;

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
            if (row.SimilarityScore.HasValue)
            {
                completed++;
            }
        }

        // TS scoreAnswers processes rows in original index order via a single
        // shared FIFO counter (nextIndex++). It does NOT group by thread.
        // Build single-row jobs so the JobScheduler preserves original row
        // order; thread grouping in EvaluationJobBuilder is for the evaluator
        // (where conversation chaining matters), not the scorer.
        IReadOnlyList<EvaluationJob<EvalRow>> jobs = EvaluationJobBuilder.Build(
            (IReadOnlyList<EvalRow>)rows,
            (row, idx) => new RowKey(TurnIndex: null, ThreadId: row.ThreadId, ItemId: row.Id, ItemIndex: row.ItemIndex));

        using var gate = new ThrottleGate(requestedConcurrency);
        var scheduler = new JobScheduler<EvalRow>(rows.ToArray(), jobs);
        await scheduler.RunAsync(
            async (rowIndex, _, ct) =>
            {
                EvalRow row = rows[rowIndex];
                // TS returns early for pre-scored rows BEFORE incrementing
                // `completed` or firing onProgress/onRowComplete. The initial
                // seed above already counted these rows.
                if (row.SimilarityScore.HasValue)
                {
                    return null;
                }
                await ProcessRowAsync(row, rowIndex, rows, judge, fallbackJudge, evaluators, options, gate, ct).ConfigureAwait(false);
                int doneNow = Interlocked.Increment(ref completed);
                options.OnProgress?.Invoke(doneNow, total);
                if (options.OnRowCompleteAsync is not null)
                {
                    await options.OnRowCompleteAsync(rows.ToArray(), row, rowIndex, ct).ConfigureAwait(false);
                }
                return null; // scorer never threads conversation ids
            },
            new JobSchedulerOptions
            {
                Concurrency = requestedConcurrency,
                DelayMs = delayMs,
            },
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    private static async Task ProcessRowAsync(
        EvalRow row,
        int rowIndex,
        IList<EvalRow> allRows,
        IJudge judge,
        IJudge? fallbackJudge,
        IReadOnlyList<EvaluatorName> defaultEvaluators,
        ScoreOptions options,
        ThrottleGate gate,
        CancellationToken cancellationToken)
    {
        _ = rowIndex;
        _ = allRows;
        // Pre-scored short-circuit is enforced by the scheduler callback so
        // skipped rows don't fire onProgress/onRowComplete (TS scoreAnswers
        // returns before increment/callbacks at scorer.ts line 45-47).

        IReadOnlyList<EvaluatorName> effectiveEvaluators = EvalRowHelpers.ResolveRowEvaluators(row, defaultEvaluators);

        if (string.IsNullOrEmpty(row.ActualAnswer) || row.ActualAnswer.StartsWith("[ERROR:", StringComparison.Ordinal))
        {
            row.SimilarityScore = 0;
            EvaluatorName primary = FirstLlmEvaluator(effectiveEvaluators) ?? EvaluatorName.Similarity;
            var metric = new MetricResult
            {
                Name = primary,
                Score = 0,
                Passed = false,
                Reason = string.IsNullOrEmpty(row.ActualAnswer)
                    ? "Actual answer is empty."
                    : "Actual answer contains an error.",
                Provider = MetricProviders.FromJudge(judge.Provider),
                Model = judge.Model,
                Scale = MetricScale.ZeroToOneHundred,
                Threshold = options.Threshold,
            };
            row.Metrics = MergeMetrics(row.Metrics, new[] { metric });
            EvalRowHelpers.SetRowError(
                row,
                string.IsNullOrEmpty(row.ActualAnswer) ? EvalErrorCode.TurnSkipped : EvalErrorCode.AgentRequestFailed,
                string.IsNullOrEmpty(row.ActualAnswer) ? "Actual answer is empty." : row.ActualAnswer);
            return;
        }

        try
        {
            var metrics = new List<MetricResult>();
            foreach (EvaluatorName evaluator in effectiveEvaluators.Where(IsLlmEvaluator))
            {
                (JudgeScore scored, IJudge winningJudge) = await gate.RunAsync(
                    () => ScoreWithFallbackAsync(judge, fallbackJudge, row, evaluator, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                metrics.Add(MetricBuilder.MetricFromJudge(scored, winningJudge, options.Threshold, evaluator));
            }
            metrics.AddRange(DeterministicEvaluators.Evaluate(row, effectiveEvaluators, options.Threshold));

            MetricResult? primaryMetric =
                metrics.FirstOrDefault(m => m.Name == EvaluatorName.Similarity)
                ?? metrics.FirstOrDefault(m => m.Name == EvaluatorName.SemanticSimilarity)
                ?? metrics.FirstOrDefault();

            row.Metrics = MergeMetrics(row.Metrics, metrics);
            if (primaryMetric is not null)
            {
                row.SimilarityScore = primaryMetric.Score ?? 0;
                row.Status = EvalRowHelpers.DeriveRowStatus(row, (int)Math.Round(options.Threshold ?? 70));
            }
            else
            {
                row.SimilarityScore = null;
                row.Status = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            row.SimilarityScore = 0;
            IReadOnlyList<EvaluatorName> resolved = EvalRowHelpers.ResolveRowEvaluators(row, defaultEvaluators);
            EvaluatorName primary = FirstLlmEvaluator(resolved) ?? EvaluatorName.Similarity;
            var fallbackMetric = new MetricResult
            {
                Name = primary,
                Score = 0,
                Passed = false,
                Reason = message,
                Provider = MetricProviders.FromJudge(judge.Provider),
                Model = judge.Model,
                Scale = MetricScale.ZeroToOneHundred,
                Threshold = options.Threshold,
            };
            row.Metrics = MergeMetrics(row.Metrics, new[] { fallbackMetric });
            row.Status = EvalRowHelpers.DeriveRowStatus(row, (int)Math.Round(options.Threshold ?? 70));
        }
    }

    private static async Task<(JudgeScore Score, IJudge Judge)> ScoreWithFallbackAsync(
        IJudge primary,
        IJudge? fallback,
        EvalRow row,
        EvaluatorName evaluator,
        CancellationToken cancellationToken)
    {
        try
        {
            JudgeScore primaryScore = await primary.ScoreAsync(row, evaluator, cancellationToken).ConfigureAwait(false);
            return (primaryScore, primary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (fallback is null || !FallbackClassifier.IsEligible(ex))
            {
                throw;
            }
            JudgeScore fbScore = await fallback.ScoreAsync(row, evaluator, cancellationToken).ConfigureAwait(false);
            string prefix = $"Fallback from {primary.Provider.ToWireString()} due to: {ex.Message}.";
            string combinedReason = string.IsNullOrEmpty(fbScore.Reason)
                ? prefix
                : $"{prefix} {fbScore.Reason}";
            var wrapped = new JudgeScore(fbScore.Score, combinedReason, fbScore.Model);
            return (wrapped, fallback);
        }
    }

    private static List<MetricResult> MergeMetrics(IList<MetricResult>? existing, IReadOnlyList<MetricResult> incoming)
    {
        // TS: merge by evaluator name, last-write-wins, ordered by
        // (existing-then-incoming) insertion order.
        var merged = new Dictionary<EvaluatorName, MetricResult>();
        var order = new List<EvaluatorName>();
        if (existing is not null)
        {
            foreach (MetricResult m in existing)
            {
                if (!merged.ContainsKey(m.Name))
                {
                    order.Add(m.Name);
                }
                merged[m.Name] = m;
            }
        }
        foreach (MetricResult m in incoming)
        {
            if (!merged.ContainsKey(m.Name))
            {
                order.Add(m.Name);
            }
            merged[m.Name] = m;
        }
        var result = new List<MetricResult>(order.Count);
        foreach (EvaluatorName key in order)
        {
            result.Add(merged[key]);
        }
        return result;
    }

    private static bool IsLlmEvaluator(EvaluatorName evaluator) => evaluator is
        EvaluatorName.Similarity or
        EvaluatorName.SemanticSimilarity or
        EvaluatorName.Relevance or
        EvaluatorName.Coherence or
        EvaluatorName.Groundedness;

    private static EvaluatorName? FirstLlmEvaluator(IReadOnlyList<EvaluatorName> evaluators)
    {
        foreach (EvaluatorName e in evaluators)
        {
            if (IsLlmEvaluator(e))
            {
                return e;
            }
        }
        return null;
    }
}
