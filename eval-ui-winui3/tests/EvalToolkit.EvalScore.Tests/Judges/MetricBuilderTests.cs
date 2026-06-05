using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

public class MetricBuilderTests
{
    private sealed class FakeJudge : IJudge
    {
        public JudgeProvider Provider { get; init; }
        public string? Model { get; init; }
        public Task<JudgeScore> ScoreAsync(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, CancellationToken cancellationToken = default)
            => Task.FromResult(new JudgeScore(0));
    }

    [Fact]
    public void NullThreshold_PassedIsNull()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq, Model = "m" };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50), judge, threshold: null);
        Assert.Null(metric.Passed);
    }

    [Theory]
    [InlineData(50.0, 50.0, true)]
    [InlineData(49.0, 50.0, false)]
    [InlineData(100.0, 50.0, true)]
    [InlineData(0.0, 50.0, false)]
    public void ThresholdComparison_UsesGreaterThanOrEqual(double scoreValue, double threshold, bool expected)
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore((int)scoreValue), judge, threshold);
        Assert.Equal(expected, metric.Passed);
    }

    [Fact]
    public void SemanticSimilarityEvaluator_NormalizesName()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(80), judge, threshold: null, evaluator: EvaluatorName.SemanticSimilarity);
        Assert.Equal(EvaluatorName.Similarity, metric.Name);
    }

    [Fact]
    public void ScoreModel_TakesPrecedenceOverJudgeModel()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.AzureOpenAi, Model = "judge-default" };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50, Model: "score-override"), judge, threshold: null);
        Assert.Equal("score-override", metric.Model);
    }

    [Fact]
    public void NullScoreModel_FallsBackToJudgeModel()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.AzureOpenAi, Model = "judge-model" };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50), judge, threshold: null);
        Assert.Equal("judge-model", metric.Model);
    }

    [Fact]
    public void Provider_IsWidenedFromJudgeProvider()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.GitHubCopilot };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(0), judge);
        Assert.Equal(MetricProvider.GitHubCopilot, metric.Provider);
    }

    [Fact]
    public void Scale_IsAlwaysZeroToOneHundred()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50), judge);
        Assert.Equal(MetricScale.ZeroToOneHundred, metric.Scale);
    }

    [Fact]
    public void RubricVersion_IsStamped()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50), judge);
        Assert.Equal(Rubrics.RubricVersion, metric.RubricVersion);
    }

    [Fact]
    public void Threshold_IsCarriedThrough()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50), judge, threshold: 70.0);
        Assert.Equal(70.0, metric.Threshold);
    }

    [Fact]
    public void Reason_IsCarriedThrough()
    {
        var judge = new FakeJudge { Provider = JudgeProvider.WorkIq };
        var metric = MetricBuilder.MetricFromJudge(new JudgeScore(50, "great"), judge);
        Assert.Equal("great", metric.Reason);
    }
}
