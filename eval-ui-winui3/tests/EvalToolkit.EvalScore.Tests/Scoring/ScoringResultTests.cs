using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Scoring;

public class ScoringResultTests
{
    private static EvalRow Row(double? score, params AssertionResult[] results)
    {
        var r = new EvalRow { Prompt = "p", ExpectedAnswer = "e", SourceLocation = "s" };
        r.SimilarityScore = score;
        if (results.Length > 0)
        {
            r.AssertionResults = results.ToList();
        }
        return r;
    }

    private static AssertionResult AR(bool passed)
        => new() { Assertion = new Assertion { Type = AssertionType.MustContain, Value = "x" }, Passed = passed, Detail = "x" };

    [Fact]
    public void Calculate_empty_rows_returns_zero_totals()
    {
        var r = ScoringResult.Calculate(Array.Empty<EvalRow>(), 70);
        Assert.Equal(0, r.TotalQuestions);
        Assert.Equal(0, r.AverageScore);
        Assert.Equal(0, r.PassCount);
        Assert.Equal(0, r.FailCount);
        Assert.Equal(70, r.PassThreshold);
    }

    [Fact]
    public void Calculate_skips_rows_with_null_similarity_score()
    {
        var rows = new[] { Row(null), Row(80), Row(null), Row(60) };
        var r = ScoringResult.Calculate(rows, 70);
        Assert.Equal(2, r.TotalQuestions);
        Assert.Equal(70, r.AverageScore);
    }

    [Fact]
    public void Calculate_pass_fail_split_uses_inclusive_threshold()
    {
        var rows = new[] { Row(70), Row(69), Row(100) };
        var r = ScoringResult.Calculate(rows, 70);
        Assert.Equal(2, r.PassCount);
        Assert.Equal(1, r.FailCount);
    }

    [Fact]
    public void Calculate_rounds_average_to_one_decimal()
    {
        var rows = new[] { Row(80), Row(85), Row(90) };
        var r = ScoringResult.Calculate(rows, 70);
        Assert.Equal(85, r.AverageScore);
    }

    [Fact]
    public void Calculate_min_max_track_extremes()
    {
        var rows = new[] { Row(40), Row(95), Row(60) };
        var r = ScoringResult.Calculate(rows, 70);
        Assert.Equal(40, r.MinScore);
        Assert.Equal(95, r.MaxScore);
    }

    [Fact]
    public void Calculate_aggregates_assertion_totals_across_all_rows()
    {
        var rows = new[]
        {
            Row(80, AR(true), AR(true), AR(false)),
            Row(60, AR(false), AR(true)),
            Row(null, AR(true)),
        };
        var r = ScoringResult.Calculate(rows, 70);
        Assert.Equal(6, r.TotalAssertions);
        Assert.Equal(4, r.AssertionsPassed);
        Assert.Equal(2, r.AssertionsFailed);
    }

    [Fact]
    public void Calculate_threshold_zero_passes_everything_with_score()
    {
        var rows = new[] { Row(0), Row(50), Row(100) };
        var r = ScoringResult.Calculate(rows, 0);
        Assert.Equal(3, r.PassCount);
        Assert.Equal(0, r.FailCount);
    }

    [Fact]
    public void Calculate_null_rows_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ScoringResult.Calculate(null!, 70));
    }
}
