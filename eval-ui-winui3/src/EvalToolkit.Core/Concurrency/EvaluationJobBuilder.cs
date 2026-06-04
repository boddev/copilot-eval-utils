namespace EvalToolkit.Core.Concurrency;

/// <summary>
/// Group rows into <see cref="EvaluationJob{TRow}"/>s the same way the
/// TS <c>buildEvaluationJobs(rows)</c> helper does in
/// <c>eval-score/node/src/evaluator.ts</c>:
///
/// <list type="bullet">
///   <item>A row with <c>turnIndex === undefined</c> becomes its own
///     single-row job, preserving original order.</item>
///   <item>A row with a <c>turnIndex</c> joins every other unconsumed
///     row sharing the same thread key
///     (<c>row.threadId ?? row.id ?? String(row.itemIndex ?? i)</c>),
///     then those rows are sorted by <c>turnIndex</c> ascending and
///     consumed as one job.</item>
///   <item>The traversal is left-to-right so the order of resulting
///     jobs is stable: jobs appear in the order their first member
///     appears in the input.</item>
/// </list>
///
/// Generic over <typeparamref name="TRow"/> with a caller-supplied
/// <see cref="RowKey"/> selector so the engine port can plug in its
/// concrete <c>EvalRow</c> without dragging the type into Core.
/// </summary>
public static class EvaluationJobBuilder
{
    public static IReadOnlyList<EvaluationJob<TRow>> Build<TRow>(
        IReadOnlyList<TRow> rows,
        Func<TRow, int, RowKey> keySelector)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keySelector);

        var keys = new RowKey[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            keys[i] = keySelector(rows[i], i);
        }

        var jobs = new List<EvaluationJob<TRow>>();
        var consumed = new bool[rows.Count];

        for (int i = 0; i < rows.Count; i++)
        {
            if (consumed[i])
            {
                continue;
            }

            RowKey k = keys[i];
            if (!k.TurnIndex.HasValue)
            {
                consumed[i] = true;
                jobs.Add(new EvaluationJob<TRow>
                {
                    RowIndices = new[] { i },
                    ThreadKey = ResolveThreadKey(k, i),
                });
                continue;
            }

            string thread = ResolveThreadKey(k, i);

            // Collect every unconsumed row in the same thread.
            var members = new List<(int Index, int Turn)>();
            for (int j = i; j < rows.Count; j++)
            {
                if (consumed[j])
                {
                    continue;
                }
                RowKey kj = keys[j];
                if (!kj.TurnIndex.HasValue)
                {
                    continue;
                }
                if (ResolveThreadKey(kj, j) == thread)
                {
                    members.Add((j, kj.TurnIndex.Value));
                }
            }

            // Sort by turnIndex (TS uses `?? 0`, but we filtered to has-value
            // above so the coalesce is moot). Stable sort isn't required —
            // ties on turnIndex within the same thread are ill-defined and
            // TS leaves them in member order, which List<T>.Sort with the
            // OrderBy linq below also preserves only by accident. Use
            // OrderBy for explicit stability.
            int[] orderedIndices = members
                .OrderBy(m => m.Turn)
                .Select(m => m.Index)
                .ToArray();

            foreach (int idx in orderedIndices)
            {
                consumed[idx] = true;
            }

            jobs.Add(new EvaluationJob<TRow>
            {
                RowIndices = orderedIndices,
                ThreadKey = thread,
            });
        }

        return jobs;
    }

    /// <summary>
    /// Resolve the thread key the same way TS does:
    /// <c>row.threadId ?? row.id ?? String(row.itemIndex ?? rowIndex)</c>.
    /// </summary>
    private static string ResolveThreadKey(RowKey k, int rowIndex)
    {
        if (!string.IsNullOrEmpty(k.ThreadId))
        {
            return k.ThreadId!;
        }
        if (!string.IsNullOrEmpty(k.ItemId))
        {
            return k.ItemId!;
        }
        int fallback = k.ItemIndex ?? rowIndex;
        return fallback.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
