using System.Globalization;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalScore.EvalDocument;

/// <summary>
/// Build the canonical M365 eval document from a sequence of
/// <see cref="EvalRow"/>s. Mirrors the TS <c>rowsToEvalDocument</c> in
/// <c>eval-score/node/src/eval-document.ts</c>.
///
/// <para>The output dictionaries already have TS-<c>undefined</c>-equivalent
/// keys dropped, so System.Text.Json writes them out exactly the same
/// way <c>JSON.stringify</c> would.</para>
/// </summary>
public static class EvalDocumentBuilder
{
    /// <summary>Locked schema version literal.</summary>
    public const string SchemaVersion = "1.4.0";

    /// <summary>Locked metadata.cliVersion literal.</summary>
    public const string CliVersion = "eval-score";

    /// <summary>Locked canonical-score-scale literal in metadata extensions.</summary>
    public const string CanonicalScoreScale = "0-100";

    public sealed record BuildOptions
    {
        /// <summary>Inbound document metadata to merge with synthesized fields.</summary>
        public IDictionary<string, object?>? Metadata { get; init; }
        public EvaluatorMap? DefaultEvaluators { get; init; }

        /// <summary>Pass threshold for similarity scoring fallback (TS default 70).</summary>
        public int Threshold { get; init; } = 70;

        public string? InputFile { get; init; }
        public EvaluationTarget? Target { get; init; }
        public JudgeProvider? JudgeProvider { get; init; }
        public IReadOnlyList<EvaluatorName>? RunEvaluators { get; init; }
    }

    public static Models.EvalDocument RowsToEvalDocument(
        IReadOnlyList<EvalRow> rows,
        BuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var opt = options ?? new BuildOptions();

        var metadata = BuildMetadata(opt);
        var defaultEvaluators = EvaluatorMapToDict(
            opt.DefaultEvaluators ?? (rows.Count > 0 ? rows[0].DocumentDefaultEvaluators : null));

        var items = BuildDocumentItems(rows, opt.Threshold);

        return new Models.EvalDocument(
            SchemaVersion: SchemaVersion,
            Metadata: metadata,
            DefaultEvaluators: defaultEvaluators,
            Items: items);
    }

    // ---------------------- metadata ----------------------

    private static Dictionary<string, object?> BuildMetadata(BuildOptions opt)
    {
        // TS spreads inbound metadata first, then overlays evaluatedAt /
        // cliVersion / extensions. Preserve insertion order so the JSON
        // output keeps consumers from cargo-culting key order.
        var metadata = new Dictionary<string, object?>();

        if (opt.Metadata is not null)
        {
            foreach (var kv in opt.Metadata)
            {
                // TS spread preserves explicit null/undefined fields. The
                // builder treats null as undefined throughout, so drop here
                // and let downstream re-insert if needed.
                if (kv.Value is null) continue;
                metadata[kv.Key] = kv.Value;
            }
        }

        // TS: `new Date().toISOString()` → millis precision, UTC, Z suffix.
        metadata["evaluatedAt"] = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);

        metadata["cliVersion"] = CliVersion;

        // TS preserves sibling extensions keys and overlays evalscore.
        Dictionary<string, object?> extensions;
        if (opt.Metadata?.TryGetValue("extensions", out var rawExt) == true
            && rawExt is IDictionary<string, object?> existing)
        {
            extensions = new Dictionary<string, object?>(existing);
        }
        else
        {
            extensions = new Dictionary<string, object?>();
        }

        var evalscore = new Dictionary<string, object?>();
        AddIfNotNull(evalscore, "inputFile", opt.InputFile);
        AddIfNotNull(evalscore, "target", TargetToDict(opt.Target));
        AddIfNotNull(evalscore, "judgeProvider", opt.JudgeProvider?.ToWireString());
        AddIfNotNull(evalscore, "evaluators", opt.RunEvaluators?.Select(e => (object)e.ToWireString()).ToList());
        evalscore["canonicalScoreScale"] = CanonicalScoreScale;

        extensions["evalscore"] = evalscore;
        metadata["extensions"] = extensions;

        return metadata;
    }

    // ---------------------- grouping ----------------------

    private static List<IDictionary<string, object?>> BuildDocumentItems(
        IReadOnlyList<EvalRow> rows,
        int threshold)
    {
        // Preserve insertion order. .NET 9+ `OrderedDictionary<TKey,
        // TValue>` is the modern generic equivalent of TS's iteration
        // over a Map that stores values in insertion order.
        var ordered = new OrderedDictionary<string, List<EvalRow>>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string groupKey = row.TurnIndex.HasValue
                ? $"thread:{row.ThreadId ?? row.Id ?? row.ItemIndex?.ToString(CultureInfo.InvariantCulture) ?? row.Prompt}"
                : $"single:{row.ItemIndex?.ToString(CultureInfo.InvariantCulture) ?? i.ToString(CultureInfo.InvariantCulture)}:{row.Id ?? row.Prompt}";

            if (!ordered.TryGetValue(groupKey, out var bucket))
            {
                bucket = new List<EvalRow>();
                ordered[groupKey] = bucket;
            }

            bucket.Add(row);
        }

        var items = new List<IDictionary<string, object?>>(ordered.Count);
        foreach (var entry in ordered)
        {
            var group = entry.Value;
            var first = group[0];

            // TS thread detection: first row has turnIndex OR (multi-row
            // group with any turnIndex).
            bool isThread = first.TurnIndex.HasValue
                || (group.Count > 1 && group.Any(r => r.TurnIndex.HasValue));

            if (isThread)
            {
                items.Add(BuildThreadItem(group, threshold));
            }
            else
            {
                items.Add(BuildSingleTurnItem(first, threshold));
            }
        }

        return items;
    }

    private static Dictionary<string, object?> BuildThreadItem(
        List<EvalRow> group,
        int threshold)
    {
        var first = group[0];
        // OrderBy is stable in .NET; matches TS's stable sort.
        var sortedTurns = group
            .OrderBy(r => r.TurnIndex ?? 0)
            .Select(r => BuildSingleTurnItem(r, threshold))
            .ToList();

        var statuses = sortedTurns
            .Select(t => t.TryGetValue("status", out var s) && s is string str
                ? EvalStatuses.FromWireString(str)
                : EvalStatus.Fail)
            .ToList();

        var summary = new Dictionary<string, object?>
        {
            ["turns_total"] = sortedTurns.Count,
            ["turns_passed"] = statuses.Count(s => s == EvalStatus.Pass),
            ["turns_failed"] = statuses.Count(s => s == EvalStatus.Fail),
            ["turns_partial"] = statuses.Count(s => s == EvalStatus.Partial),
            ["turns_errored"] = statuses.Count(s => s == EvalStatus.Error),
            ["overall_status"] = SummarizeStatuses(statuses).ToWireString(),
        };

        var item = new Dictionary<string, object?>();
        AddIfNotNull(item, "name", first.ThreadName ?? first.Id);
        AddIfNotNull(item, "description", first.ThreadDescription);
        AddIfNotNull(item, "conversation_id", first.ConversationId);
        item["turns"] = sortedTurns;
        item["summary"] = summary;

        var evalscoreExt = new Dictionary<string, object?>();
        AddIfNotNull(evalscoreExt, "item_id", first.Id);
        var extensions = new Dictionary<string, object?>
        {
            ["evalscore"] = evalscoreExt,
        };
        item["extensions"] = extensions;

        return item;
    }

    private static Dictionary<string, object?> BuildSingleTurnItem(EvalRow row, int threshold)
    {
        var status = row.Status ?? EvalRowHelpers.DeriveRowStatus(row, threshold);

        var turn = new Dictionary<string, object?>
        {
            ["prompt"] = row.Prompt,
        };

        // TS: `row.expectedAnswer || undefined` — empty string is omitted.
        AddIfNotNullOrEmpty(turn, "expected_response", row.ExpectedAnswer);
        AddIfNotNullOrEmpty(turn, "response", row.ActualAnswer);
        // TS: `row.context || row.sourceLocation || undefined`.
        string? contextOrLocation = !string.IsNullOrEmpty(row.Context)
            ? row.Context
            : (!string.IsNullOrEmpty(row.SourceLocation) ? row.SourceLocation : null);
        AddIfNotNull(turn, "context", contextOrLocation);

        AddIfNotNull(turn, "evaluators", EvaluatorMapToDict(row.Evaluators));
        AddIfNotNull(turn, "evaluators_mode", row.EvaluatorsMode?.ToWireString());

        var citations = NormalizeCitations(row.Citations);
        AddIfNotNull(turn, "citations", citations);

        var scores = MetricsToScores(row.Metrics, threshold);
        AddIfNotNull(turn, "scores", scores);

        turn["status"] = status.ToWireString();

        AddIfNotNull(turn, "error", ErrorToDict(row.Error));

        var evalscoreExt = new Dictionary<string, object?>();
        AddIfNotNull(evalscoreExt, "item_id", row.Id);
        AddIfNotNull(evalscoreExt, "item_index", row.ItemIndex);
        AddIfNotNull(evalscoreExt, "turn_index", row.TurnIndex);
        AddIfNotNullOrEmpty(evalscoreExt, "source_location", row.SourceLocation);
        AddIfNotNull(evalscoreExt, "canonical_score_0_100", row.SimilarityScore);
        AddIfNotNull(evalscoreExt, "response_metadata", row.ResponseMetadata);
        AddIfNotNull(evalscoreExt, "assertions", row.Assertions?.Select(AssertionToDict).ToList());
        AddIfNotNull(evalscoreExt, "assertion_results", row.AssertionResults?.Select(AssertionResultToDict).ToList());

        var extensions = new Dictionary<string, object?>
        {
            ["evalscore"] = evalscoreExt,
        };
        turn["extensions"] = extensions;

        return turn;
    }

    // ---------------------- score mapping ----------------------

    // TS-keyed score field names (eval-document.ts SCORE_KEY).
    private static readonly Dictionary<EvaluatorName, string> ScoreKey = new()
    {
        [EvaluatorName.SemanticSimilarity] = "similarity",
        [EvaluatorName.Similarity] = "similarity",
        [EvaluatorName.Relevance] = "relevance",
        [EvaluatorName.Coherence] = "coherence",
        [EvaluatorName.Groundedness] = "groundedness",
        [EvaluatorName.Citations] = "citations",
        [EvaluatorName.ExactMatch] = "exactMatch",
        [EvaluatorName.PartialMatch] = "partialMatch",
    };

    private static Dictionary<string, object?>? MetricsToScores(
        IList<MetricResult>? metrics,
        int threshold)
    {
        if (metrics is null || metrics.Count == 0) return null;

        var scores = new Dictionary<string, object?>();
        foreach (var metric in metrics)
        {
            if (!ScoreKey.TryGetValue(metric.Name, out var key)) continue;

            if (metric.Name == EvaluatorName.ExactMatch)
            {
                var em = new Dictionary<string, object?>
                {
                    ["match"] = metric.Passed == true,
                    ["result"] = metric.Passed == true ? "pass" : "fail",
                };
                AddIfNotNull(em, "reason", metric.Reason);
                AddIfNotNull(em, "score_0_100", metric.Score);
                scores[key] = em;
                continue;
            }

            if (metric.Name == EvaluatorName.PartialMatch)
            {
                double partial = (metric.Score ?? 0) / 100.0;
                var pm = new Dictionary<string, object?>
                {
                    ["score"] = partial,
                    ["result"] = metric.Passed == true ? "pass" : "fail",
                    ["threshold"] = threshold / 100.0,
                };
                AddIfNotNull(pm, "reason", metric.Reason);
                AddIfNotNull(pm, "score_0_100", metric.Score);
                scores[key] = pm;
                continue;
            }

            if (metric.Name == EvaluatorName.Citations)
            {
                var cm = new Dictionary<string, object?>
                {
                    ["count"] = metric.Passed == true ? 1 : 0,
                    ["result"] = metric.Passed == true ? "pass" : "fail",
                    ["threshold"] = 1,
                };
                AddIfNotNull(cm, "reason", metric.Reason);
                AddIfNotNull(cm, "score_0_100", metric.Score);
                scores[key] = cm;
                continue;
            }

            // Standard scaled metric (Similarity / Relevance / Coherence / Groundedness).
            var s = new Dictionary<string, object?>
            {
                ["score"] = ToM365FivePointScore(metric.Score ?? 0),
                ["result"] = metric.Passed == true ? "pass" : "fail",
                ["threshold"] = ToM365FivePointScore(metric.Threshold ?? threshold),
                ["provider"] = metric.Provider.ToWireString(),
            };
            AddIfNotNull(s, "reason", metric.Reason);
            AddIfNotNull(s, "score_0_100", metric.Score);
            AddIfNotNull(s, "model", metric.Model);
            AddIfNotNull(s, "rubricVersion", metric.RubricVersion);
            scores[key] = s;
        }

        return scores.Count > 0 ? scores : null;
    }

    /// <summary>
    /// TS <c>toM365FivePointScore</c>:
    /// <c>s &lt;= 0 ? 1 : Math.round(clamp(s/20, 1, 5) * 10) / 10</c>.
    /// Uses <see cref="MidpointRounding.AwayFromZero"/> to match JS
    /// <c>Math.round</c> half-up semantics for positives.
    /// </summary>
    public static double ToM365FivePointScore(double score0To100)
    {
        if (score0To100 <= 0) return 1;
        double scaled = Math.Max(1.0, Math.Min(5.0, score0To100 / 20.0));
        return Math.Round(scaled * 10.0, MidpointRounding.AwayFromZero) / 10.0;
    }

    // ---------------------- citations / status helpers ----------------------

    private static List<IDictionary<string, object?>>? NormalizeCitations(
        IReadOnlyList<Citation>? citations)
    {
        if (citations is null || citations.Count == 0) return null;
        var list = new List<IDictionary<string, object?>>(citations.Count);
        for (int i = 0; i < citations.Count; i++)
        {
            var c = citations[i];
            var dict = new Dictionary<string, object?>
            {
                ["index"] = i + 1,
            };
            AddIfNotNull(dict, "text", c.Title);
            // TS: `citation.url ?? citation.sourceLocation` — url wins, even if empty string.
            AddIfNotNull(dict, "source", c.Url ?? c.SourceLocation);
            AddIfNotNull(dict, "raw", c.Raw);
            list.Add(dict);
        }
        return list;
    }

    /// <summary>
    /// TS <c>summarizeStatuses</c>: any error → error; all pass → pass;
    /// all fail → fail; else partial.
    /// </summary>
    public static EvalStatus SummarizeStatuses(IReadOnlyList<EvalStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Any(s => s == EvalStatus.Error)) return EvalStatus.Error;
        if (statuses.All(s => s == EvalStatus.Pass)) return EvalStatus.Pass;
        if (statuses.All(s => s == EvalStatus.Fail)) return EvalStatus.Fail;
        return EvalStatus.Partial;
    }

    // ---------------------- conversion helpers ----------------------

    private static Dictionary<string, object?>? EvaluatorMapToDict(EvaluatorMap? map)
    {
        if (map is null || map.Count == 0) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var kv in map)
        {
            var inner = new Dictionary<string, object?>();
            AddIfNotNull(inner, "threshold", kv.Value.Threshold);
            AddIfNotNull(inner, "citation_format", kv.Value.CitationFormat);
            AddIfNotNull(inner, "case_sensitive", kv.Value.CaseSensitive);
            if (kv.Value.Options is { Count: > 0 } opts)
            {
                inner["options"] = new Dictionary<string, object?>(opts);
            }
            dict[kv.Key.ToWireString()] = inner;
        }
        return dict;
    }

    private static Dictionary<string, object?>? TargetToDict(EvaluationTarget? target)
    {
        if (target is null) return null;
        var dict = new Dictionary<string, object?>
        {
            ["type"] = target.Type.ToWireString(),
        };
        AddIfNotNull(dict, "agentId", target.AgentId);
        AddIfNotNull(dict, "connectorId", target.ConnectorId);
        return dict;
    }

    private static Dictionary<string, object?>? ErrorToDict(EvalError? error)
    {
        if (error is null) return null;
        return new Dictionary<string, object?>
        {
            ["code"] = error.Code.ToWireString(),
            ["message"] = error.Message,
        };
    }

    private static Dictionary<string, object?> AssertionToDict(Assertion a)
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"] = a.Type.ToWireString(),
        };
        AddIfNotNull(dict, "value", a.Value);
        AddIfNotNull(dict, "values", a.Values);
        AddIfNotNull(dict, "wholeWord", a.WholeWord);
        return dict;
    }

    private static Dictionary<string, object?> AssertionResultToDict(AssertionResult ar)
    {
        return new Dictionary<string, object?>
        {
            ["assertion"] = AssertionToDict(ar.Assertion),
            ["passed"] = ar.Passed,
            ["detail"] = ar.Detail,
        };
    }

    // ---------------------- compact helpers ----------------------

    /// <summary>
    /// Add to dictionary only when value is non-null. Matches TS
    /// <c>compactObject</c> which deletes <c>undefined</c> keys; in C#
    /// the equivalent is "don't insert the null in the first place".
    /// </summary>
    private static void AddIfNotNull<T>(Dictionary<string, object?> dict, string key, T? value)
    {
        if (value is null) return;
        dict[key] = value;
    }

    /// <summary>
    /// Add to dictionary only when string is non-null and non-empty.
    /// Matches the per-field TS <c>field || undefined</c> pattern used
    /// for <c>expected_response</c>, <c>response</c>, <c>source_location</c>,
    /// and <c>context</c>.
    /// </summary>
    private static void AddIfNotNullOrEmpty(Dictionary<string, object?> dict, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        dict[key] = value;
    }
}
