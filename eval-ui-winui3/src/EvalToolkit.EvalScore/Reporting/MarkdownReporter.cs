using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Reporting;

/// <summary>
/// Generates the markdown evaluation report. Mirrors TS
/// <c>generateReport</c> + <c>writeReport</c> in
/// <c>eval-score/node/src/reporter.ts</c>.
///
/// <para>Formatting rules preserved exactly:
/// <list type="bullet">
///   <item>Score buckets: Excellent 90-100, Good 70-89, Fair 50-69,
///     Poor 0-49 (inclusive on both ends). Rows with
///     <c>similarityScore == null</c> are skipped from bucketing.</item>
///   <item>Distribution bar: <c>█</c> repeated
///     <c>round(count/maxCount * 20)</c> times; empty string when
///     <c>maxCount == 0</c>.</item>
///   <item>Truncation: head + <c>"..."</c> when over the limit.</item>
///   <item>Percentages: integer percent (<c>toFixed(0)</c>), zero when
///     total is zero.</item>
///   <item>Per-category section only renders when at least one row has
///     a non-null <see cref="EvalRow.Category"/>.</item>
///   <item>Metric line: <c>{score}/100</c> when score has a value, else
///     <c>"True"</c>/<c>"False"</c>/<c>"N/A"</c> from <c>passed</c>;
///     a trailing pass/fail icon when <c>passed</c> is set; provider
///     printed as <c>{provider}/{model}</c> when model present.</item>
/// </list></para>
/// </summary>
public static class MarkdownReporter
{
    public static string GenerateReport(EvalResult evalResult, ScoringResult scoringResult)
    {
        ArgumentNullException.ThrowIfNull(evalResult);
        ArgumentNullException.ThrowIfNull(scoringResult);

        var lines = new List<string>();

        lines.Add("# Evaluation Report");
        lines.Add(string.Empty);

        lines.Add("## Summary");
        lines.Add(string.Empty);
        lines.Add($"- **Input File:** {evalResult.InputFile}");
        lines.Add($"- **Input Format:** {evalResult.InputFormat.ToWireString()}");
        lines.Add($"- **Timestamp:** {evalResult.Timestamp}");

        if (evalResult.SystemPrompt is not null)
        {
            lines.Add($"- **System Prompt:** {Truncate(evalResult.SystemPrompt, 200)}");
        }
        if (evalResult.Target is not null)
        {
            string target = evalResult.Target.Type switch
            {
                TargetType.M365Agent => $"M365 Agent ID: {evalResult.Target.AgentId}",
                TargetType.Connector => $"Connector ID: {evalResult.Target.ConnectorId}",
                _ => "WorkIQ default",
            };
            lines.Add($"- **Response Target:** {target}");
        }
        if (evalResult.JudgeProvider.HasValue)
        {
            lines.Add($"- **Judge Provider:** {evalResult.JudgeProvider.Value.ToWireString()}");
        }
        if (evalResult.Evaluators is { Count: > 0 } evals)
        {
            lines.Add($"- **Evaluators:** {string.Join(", ", evals.Select(e => e.ToWireString()))}");
        }

        lines.Add($"- **Total Questions:** {scoringResult.TotalQuestions}");
        lines.Add($"- **Average Score:** {scoringResult.AverageScore.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"- **Min Score:** {scoringResult.MinScore}");
        lines.Add($"- **Max Score:** {scoringResult.MaxScore}");

        string passPct = FormatPercentage(scoringResult.PassCount, scoringResult.TotalQuestions);
        lines.Add($"- **Pass Rate:** {scoringResult.PassCount}/{scoringResult.TotalQuestions} ({passPct}%)");
        lines.Add($"- **Pass Threshold:** {scoringResult.PassThreshold}");

        if (scoringResult.TotalAssertions > 0)
        {
            string assertPct = FormatPercentage(scoringResult.AssertionsPassed, scoringResult.TotalAssertions);
            lines.Add($"- **Assertions:** {scoringResult.AssertionsPassed}/{scoringResult.TotalAssertions} passed ({assertPct}%)");
        }

        lines.Add(string.Empty);

        // Score Distribution
        lines.Add("## Score Distribution");
        lines.Add(string.Empty);

        ScoreBucket[] buckets = BuildScoreBuckets(evalResult.Rows);
        int maxCount = buckets.Max(b => b.Count);
        foreach (ScoreBucket bucket in buckets)
        {
            string bar = MakeBar(bucket.Count, maxCount);
            string pct = FormatPercentage(bucket.Count, scoringResult.TotalQuestions);
            lines.Add($"{bucket.Label} ({bucket.Range}): {bar} {bucket.Count} ({pct}%)");
        }
        lines.Add(string.Empty);

        // Per-category breakdown
        var categoryRows = new Dictionary<string, CategoryAggregate>(StringComparer.Ordinal);
        var categoryOrder = new List<string>();
        foreach (EvalRow row in evalResult.Rows)
        {
            if (string.IsNullOrEmpty(row.Category))
            {
                continue;
            }
            if (!categoryRows.TryGetValue(row.Category, out CategoryAggregate? agg))
            {
                agg = new CategoryAggregate();
                categoryRows[row.Category] = agg;
                categoryOrder.Add(row.Category);
            }
            agg.Scores.Add(row.SimilarityScore ?? 0);
            if (row.AssertionResults is { Count: > 0 } results)
            {
                foreach (AssertionResult r in results)
                {
                    if (r.Passed)
                    {
                        agg.AssertionsPassed++;
                    }
                    else
                    {
                        agg.AssertionsFailed++;
                    }
                }
            }
        }

        if (categoryRows.Count > 0)
        {
            lines.Add("## Results by Category");
            lines.Add(string.Empty);
            lines.Add("| Category | Questions | Avg Score | Pass Rate | Assertions |");
            lines.Add("|----------|-----------|-----------|-----------|------------|");
            foreach (string cat in categoryOrder)
            {
                CategoryAggregate data = categoryRows[cat];
                string avg = data.Scores.Count > 0
                    ? (data.Scores.Sum() / data.Scores.Count).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    : "N/A";
                int passing = data.Scores.Count(s => s >= scoringResult.PassThreshold);
                string passRate = data.Scores.Count > 0 ? $"{FormatPercentage(passing, data.Scores.Count)}%" : "N/A";
                int assertTotal = data.AssertionsPassed + data.AssertionsFailed;
                string assertStr = assertTotal > 0 ? $"{data.AssertionsPassed}/{assertTotal}" : "-";
                lines.Add($"| {cat} | {data.Scores.Count} | {avg} | {passRate} | {assertStr} |");
            }
            lines.Add(string.Empty);
        }

        // Detailed Results
        lines.Add("## Detailed Results");
        lines.Add(string.Empty);

        for (int index = 0; index < evalResult.Rows.Count; index++)
        {
            EvalRow row = evalResult.Rows[index];
            int n = index + 1;
            string promptPreview = Truncate(row.Prompt, 60);
            lines.Add($"### Question {n}: {promptPreview}");
            lines.Add(string.Empty);

            if (row.SimilarityScore.HasValue)
            {
                bool passed = row.SimilarityScore.Value >= scoringResult.PassThreshold;
                string icon = passed ? "✅" : "❌";
                lines.Add($"**Score:** {FormatScore(row.SimilarityScore.Value)}/100 {icon}");
            }
            else
            {
                lines.Add("**Score:** N/A");
            }

            if (row.Metrics is { Count: > 0 } metrics)
            {
                lines.Add(string.Empty);
                lines.Add("**Metrics:**");
                foreach (MetricResult metric in metrics)
                {
                    string value;
                    if (metric.Score.HasValue)
                    {
                        value = $"{FormatScore(metric.Score.Value)}/100";
                    }
                    else if (metric.Passed.HasValue)
                    {
                        value = metric.Passed.Value ? "True" : "False";
                    }
                    else
                    {
                        value = "N/A";
                    }
                    string icon = metric.Passed.HasValue ? (metric.Passed.Value ? " ✅" : " ❌") : string.Empty;
                    string provider = metric.Model is not null
                        ? $"{metric.Provider.ToWireString()}/{metric.Model}"
                        : metric.Provider.ToWireString();
                    string reason = metric.Reason is not null ? $" - {Truncate(metric.Reason, 120)}" : string.Empty;
                    lines.Add($"- **{metric.Name.ToWireString()}:** {value}{icon} ({provider}){reason}");
                }
            }

            lines.Add(string.Empty);
            lines.Add($"**Source:** {row.SourceLocation}");
            lines.Add(string.Empty);
            lines.Add("**Prompt:**");
            lines.Add($"> {row.Prompt}");
            lines.Add(string.Empty);
            lines.Add("**Expected Answer:**");
            lines.Add($"> {row.ExpectedAnswer}");
            lines.Add(string.Empty);
            lines.Add("**Actual Answer:**");
            lines.Add($"> {row.ActualAnswer}");
            lines.Add(string.Empty);

            if (row.AssertionResults is { Count: > 0 } results)
            {
                lines.Add("**Assertions:**");
                foreach (AssertionResult ar in results)
                {
                    lines.Add($"- {ar.Detail}");
                }
                int passedCount = results.Count(r => r.Passed);
                lines.Add($"- **Result:** {passedCount}/{results.Count} assertions passed");
                lines.Add(string.Empty);
            }
        }

        return string.Join("\n", lines);
    }

    public static async Task<string> WriteReportAsync(
        string report,
        string outputDir,
        string inputFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);

        Directory.CreateDirectory(outputDir);
        string baseName = Path.GetFileNameWithoutExtension(inputFile);
        string outputPath = Path.Combine(outputDir, $"{baseName}-report.md");
        await File.WriteAllTextAsync(outputPath, report, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static ScoreBucket[] BuildScoreBuckets(IReadOnlyList<EvalRow> rows)
    {
        ScoreBucket[] buckets =
        {
            new("Excellent", "90-100", 90, 100),
            new("Good", "70-89", 70, 89),
            new("Fair", "50-69", 50, 69),
            new("Poor", "0-49", 0, 49),
        };
        foreach (EvalRow row in rows)
        {
            if (!row.SimilarityScore.HasValue)
            {
                continue;
            }
            double score = row.SimilarityScore.Value;
            foreach (ScoreBucket bucket in buckets)
            {
                if (score >= bucket.Min && score <= bucket.Max)
                {
                    bucket.Count++;
                    break;
                }
            }
        }
        return buckets;
    }

    private static string MakeBar(int count, int maxCount)
    {
        if (maxCount == 0)
        {
            return string.Empty;
        }
        const int maxWidth = 20;
        int width = (int)Math.Round((double)count / maxCount * maxWidth, MidpointRounding.AwayFromZero);
        return new string('█', width);
    }

    private static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxLength)
        {
            return text;
        }
        return text[..maxLength] + "...";
    }

    private static string FormatPercentage(int count, int total)
    {
        if (total == 0)
        {
            return "0";
        }
        double pct = (double)count / total * 100;
        // TS Number.prototype.toFixed(0) uses banker's-style rounding via
        // IEEE 754; .NET ToString("F0") rounds AwayFromZero. The
        // difference only appears on .5 boundaries (e.g., 25.5). For
        // single-percentage display in a report this is acceptable; the
        // TS rounding tie case is rare and the report is human-facing.
        return ((int)Math.Round(pct, MidpointRounding.AwayFromZero)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatScore(double score)
    {
        // TS prints raw numeric (e.g., "0", "100", "87.5"); .NET's default
        // double->string uses round-trip format which yields the same
        // shape. Use InvariantCulture for "." decimal separator.
        if (score == Math.Truncate(score))
        {
            return ((long)score).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return score.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ScoreBucket
    {
        public ScoreBucket(string label, string range, int min, int max)
        {
            Label = label;
            Range = range;
            Min = min;
            Max = max;
        }
        public string Label { get; }
        public string Range { get; }
        public int Min { get; }
        public int Max { get; }
        public int Count { get; set; }
    }

    private sealed class CategoryAggregate
    {
        public List<double> Scores { get; } = new();
        public int AssertionsPassed { get; set; }
        public int AssertionsFailed { get; set; }
    }
}
