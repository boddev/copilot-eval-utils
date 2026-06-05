using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// Aggregated scoring outcome. Mirrors the TS <c>ScoringResult</c>
/// interface in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public sealed record ScoringResult
{
    public required int TotalQuestions { get; init; }

    /// <summary>Average score rounded to 1 decimal place. Matches TS
    /// <c>Math.round(sum / total * 10) / 10</c>.</summary>
    public required double AverageScore { get; init; }

    public required double MinScore { get; init; }
    public required double MaxScore { get; init; }
    public required int PassCount { get; init; }
    public required int FailCount { get; init; }
    public required double PassThreshold { get; init; }

    public required int TotalAssertions { get; init; }
    public required int AssertionsPassed { get; init; }
    public required int AssertionsFailed { get; init; }

    /// <summary>
    /// Aggregate a row collection into a <see cref="ScoringResult"/>.
    /// Mirrors TS <c>calculateScoringResult(rows, passThreshold)</c>.
    /// Rows with <c>SimilarityScore == null</c> are skipped from the
    /// score totals (matches the TS filter); assertion totals include
    /// every row that carries assertion results.
    /// </summary>
    public static ScoringResult Calculate(IReadOnlyList<EvalRow> rows, double passThreshold)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var scores = new List<double>(rows.Count);
        foreach (EvalRow row in rows)
        {
            if (row.SimilarityScore.HasValue)
            {
                scores.Add(row.SimilarityScore.Value);
            }
        }

        int total = scores.Count;
        double sum = 0;
        double min = 0;
        double max = 0;
        int passCount = 0;
        if (total > 0)
        {
            min = scores[0];
            max = scores[0];
            foreach (double s in scores)
            {
                sum += s;
                if (s < min)
                {
                    min = s;
                }
                if (s > max)
                {
                    max = s;
                }
                if (s >= passThreshold)
                {
                    passCount++;
                }
            }
        }
        double averageScore = total > 0
            ? Math.Round(sum / total * 10, MidpointRounding.AwayFromZero) / 10
            : 0;
        int failCount = total - passCount;

        int totalAssertions = 0;
        int assertionsPassed = 0;
        int assertionsFailed = 0;
        foreach (EvalRow row in rows)
        {
            if (row.AssertionResults is { Count: > 0 } results)
            {
                totalAssertions += results.Count;
                foreach (AssertionResult ar in results)
                {
                    if (ar.Passed)
                    {
                        assertionsPassed++;
                    }
                    else
                    {
                        assertionsFailed++;
                    }
                }
            }
        }

        return new ScoringResult
        {
            TotalQuestions = total,
            AverageScore = averageScore,
            MinScore = min,
            MaxScore = max,
            PassCount = passCount,
            FailCount = failCount,
            PassThreshold = passThreshold,
            TotalAssertions = totalAssertions,
            AssertionsPassed = assertionsPassed,
            AssertionsFailed = assertionsFailed,
        };
    }
}
