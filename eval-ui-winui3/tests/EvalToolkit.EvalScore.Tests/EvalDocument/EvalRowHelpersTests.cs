using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.EvalDocument;

public class EvalRowHelpersTests
{
    private static EvalRow NewRow(
        string actual = "answer",
        double? similarity = null,
        IList<MetricResult>? metrics = null,
        EvalError? error = null)
    {
        return new EvalRow
        {
            Prompt = "q",
            ExpectedAnswer = "a",
            SourceLocation = "/file",
            ActualAnswer = actual,
            SimilarityScore = similarity,
            Metrics = metrics,
            Error = error,
        };
    }

    private static MetricResult M(EvaluatorName name, bool? passed = null, double? score = null)
        => new()
        {
            Name = name,
            Provider = MetricProvider.WorkIq,
            Scale = MetricScale.ZeroToOneHundred,
            Passed = passed,
            Score = score,
        };

    // ---------- DeriveRowStatus ----------

    [Fact]
    public void DeriveRowStatus_ExplicitError_ReturnsError()
    {
        var row = NewRow(error: new EvalError { Code = EvalErrorCode.AgentRequestFailed, Message = "boom" });
        Assert.Equal(EvalStatus.Error, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_EmptyActualAnswer_ReturnsError()
    {
        var row = NewRow(actual: "");
        Assert.Equal(EvalStatus.Error, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_ErrorPrefix_ReturnsError()
    {
        var row = NewRow(actual: "[ERROR: timeout]");
        Assert.Equal(EvalStatus.Error, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_AllMetricsPass_ReturnsPass()
    {
        var row = NewRow(metrics: new[] { M(EvaluatorName.Relevance, true), M(EvaluatorName.Coherence, true) });
        Assert.Equal(EvalStatus.Pass, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_AllMetricsFail_ReturnsFail()
    {
        var row = NewRow(metrics: new[] { M(EvaluatorName.Relevance, false), M(EvaluatorName.Coherence, false) });
        Assert.Equal(EvalStatus.Fail, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_MixedMetrics_ReturnsPartial()
    {
        var row = NewRow(metrics: new[] { M(EvaluatorName.Relevance, true), M(EvaluatorName.Coherence, false) });
        Assert.Equal(EvalStatus.Partial, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_AllMetricsHaveNullPassed_FallsBackToThreshold()
    {
        // Metrics array exists but every entry has Passed=null → filter
        // skips them, falls back to similarity threshold.
        var row = NewRow(
            similarity: 90,
            metrics: new[] { M(EvaluatorName.Relevance), M(EvaluatorName.Coherence) });
        Assert.Equal(EvalStatus.Pass, EvalRowHelpers.DeriveRowStatus(row, threshold: 70));
    }

    [Fact]
    public void DeriveRowStatus_NoMetrics_SimilarityAboveThreshold_ReturnsPass()
    {
        var row = NewRow(similarity: 75);
        Assert.Equal(EvalStatus.Pass, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_NoMetrics_SimilarityBelowThreshold_ReturnsFail()
    {
        var row = NewRow(similarity: 50);
        Assert.Equal(EvalStatus.Fail, EvalRowHelpers.DeriveRowStatus(row));
    }

    [Fact]
    public void DeriveRowStatus_NoMetrics_NoSimilarity_TreatsAsZero()
    {
        // (similarityScore ?? 0) >= 70 → 0 >= 70 → false → Fail.
        var row = NewRow();
        Assert.Equal(EvalStatus.Fail, EvalRowHelpers.DeriveRowStatus(row));
    }

    // ---------- SetRowError ----------

    [Fact]
    public void SetRowError_MutatesRow()
    {
        var row = NewRow();
        EvalRowHelpers.SetRowError(row, EvalErrorCode.TurnSkipped, "skipped");
        Assert.NotNull(row.Error);
        Assert.Equal(EvalErrorCode.TurnSkipped, row.Error!.Code);
        Assert.Equal("skipped", row.Error.Message);
        Assert.Equal(EvalStatus.Error, row.Status);
    }

    // ---------- NormalizeEvaluatorList ----------

    [Fact]
    public void NormalizeEvaluatorList_CollapsesSemanticToSimilarity()
    {
        var result = EvalRowHelpers.NormalizeEvaluatorList(
            new[] { EvaluatorName.SemanticSimilarity, EvaluatorName.Relevance });
        Assert.Equal(new[] { EvaluatorName.Similarity, EvaluatorName.Relevance }, result);
    }

    [Fact]
    public void NormalizeEvaluatorList_DedupesPreservingOrder()
    {
        var result = EvalRowHelpers.NormalizeEvaluatorList(
            new[] { EvaluatorName.Relevance, EvaluatorName.Coherence, EvaluatorName.Relevance });
        Assert.Equal(new[] { EvaluatorName.Relevance, EvaluatorName.Coherence }, result);
    }

    [Fact]
    public void NormalizeEvaluatorList_EmptyInput_FallsBackToDefaults()
    {
        var result = EvalRowHelpers.NormalizeEvaluatorList(Array.Empty<EvaluatorName>());
        Assert.Equal(EvalRowHelpers.DefaultM365Evaluators, result);
    }

    [Fact]
    public void NormalizeEvaluatorList_SemanticAndSimilarityCollapseToOne()
    {
        var result = EvalRowHelpers.NormalizeEvaluatorList(
            new[] { EvaluatorName.SemanticSimilarity, EvaluatorName.Similarity });
        Assert.Equal(new[] { EvaluatorName.Similarity }, result);
    }

    // ---------- ResolveRowEvaluators ----------

    [Fact]
    public void ResolveRowEvaluators_NoOverrides_UsesRunEvaluators()
    {
        var row = NewRow();
        var run = new[] { EvaluatorName.Relevance };
        Assert.Equal(run, EvalRowHelpers.ResolveRowEvaluators(row, run));
    }

    [Fact]
    public void ResolveRowEvaluators_DocumentDefaultsBeatRunDefaults()
    {
        var row = NewRow();
        row.DocumentDefaultEvaluators = new EvaluatorMap
        {
            [EvaluatorName.Groundedness] = new EvaluatorOptions(),
        };
        var run = new[] { EvaluatorName.Relevance };
        var result = EvalRowHelpers.ResolveRowEvaluators(row, run);
        Assert.Equal(new[] { EvaluatorName.Groundedness }, result);
    }

    [Fact]
    public void ResolveRowEvaluators_ReplaceMode_OverridesBase()
    {
        var row = NewRow();
        row.Evaluators = new EvaluatorMap
        {
            [EvaluatorName.Citations] = new EvaluatorOptions(),
        };
        row.EvaluatorsMode = EvaluatorsMode.Replace;
        var run = new[] { EvaluatorName.Relevance, EvaluatorName.Coherence };
        var result = EvalRowHelpers.ResolveRowEvaluators(row, run);
        Assert.Equal(new[] { EvaluatorName.Citations }, result);
    }

    [Fact]
    public void ResolveRowEvaluators_ExtendMode_AppendsOverrides()
    {
        var row = NewRow();
        row.Evaluators = new EvaluatorMap
        {
            [EvaluatorName.Citations] = new EvaluatorOptions(),
        };
        row.EvaluatorsMode = EvaluatorsMode.Extend;
        var run = new[] { EvaluatorName.Relevance };
        var result = EvalRowHelpers.ResolveRowEvaluators(row, run);
        Assert.Equal(new[] { EvaluatorName.Relevance, EvaluatorName.Citations }, result);
    }

    [Fact]
    public void ResolveRowEvaluators_DefaultModeIsExtend()
    {
        // TS: when evaluatorsMode is undefined and overrides exist,
        // base + overrides via normalizeEvaluatorList — i.e. extend.
        var row = NewRow();
        row.Evaluators = new EvaluatorMap
        {
            [EvaluatorName.Citations] = new EvaluatorOptions(),
        };
        // EvaluatorsMode left null
        var run = new[] { EvaluatorName.Relevance };
        var result = EvalRowHelpers.ResolveRowEvaluators(row, run);
        Assert.Equal(new[] { EvaluatorName.Relevance, EvaluatorName.Citations }, result);
    }
}
