using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Scoring;

public class DeterministicEvaluatorsTests
{
    private static EvalRow Row(string actual, string expected, string source = "", params Assertion[] assertions)
    {
        var r = new EvalRow { Prompt = "p", ExpectedAnswer = expected, SourceLocation = source, ActualAnswer = actual };
        if (assertions.Length > 0)
        {
            r.Assertions = assertions;
        }
        return r;
    }

    [Fact]
    public void ExactMatch_passes_after_normalization()
    {
        var r = Row("  Hello   World  ", "hello world");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.ExactMatch }, 70);
        Assert.Single(ms);
        Assert.Equal(100, ms[0].Score);
        Assert.True(ms[0].Passed);
        Assert.Equal(MetricProvider.Deterministic, ms[0].Provider);
    }

    [Fact]
    public void ExactMatch_fails_on_substantive_difference()
    {
        var r = Row("hello there", "hello world");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.ExactMatch }, 70);
        Assert.Equal(0, ms[0].Score);
        Assert.False(ms[0].Passed);
    }

    [Fact]
    public void PartialMatch_passes_when_actual_contains_expected()
    {
        var r = Row("the answer is 42 actually", "answer is 42");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.PartialMatch }, 70);
        Assert.True(ms[0].Passed);
        Assert.Equal(100, ms[0].Score);
    }

    [Fact]
    public void PartialMatch_passes_when_expected_contains_actual()
    {
        var r = Row("42", "the answer is 42");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.PartialMatch }, 70);
        Assert.True(ms[0].Passed);
    }

    [Fact]
    public void PartialMatch_fails_when_expected_is_empty()
    {
        var r = Row("anything", "");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.PartialMatch }, 70);
        Assert.False(ms[0].Passed);
    }

    [Fact]
    public void Citations_passes_when_row_has_citations_array()
    {
        var r = Row("text without source", "expected", "loc");
        r.Citations = new[] { new EvalToolkit.WorkIQ.Citation("title", "url") };
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.Citations }, 70);
        Assert.True(ms[0].Passed);
    }

    [Fact]
    public void Citations_passes_when_actual_contains_source_location_case_insensitive()
    {
        var r = Row("see the README.md for details", "expected", "README.md");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.Citations }, 70);
        Assert.True(ms[0].Passed);
    }

    [Fact]
    public void Citations_fails_when_no_citations_or_source_text()
    {
        var r = Row("just a free-text answer", "expected", "secret-file.docx");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.Citations }, 70);
        Assert.False(ms[0].Passed);
        Assert.Equal(0, ms[0].Score);
    }

    [Fact]
    public void EvalGenAssertions_computes_pass_ratio_and_mutates_row()
    {
        var r = Row(
            "hello world banana",
            "expected",
            assertions:
            new[]
            {
                new Assertion { Type = AssertionType.MustContain, Value = "hello" },
                new Assertion { Type = AssertionType.MustContain, Value = "missing" },
                new Assertion { Type = AssertionType.MustContain, Value = "banana" },
            });
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.EvalGenAssertions }, 70);
        Assert.Single(ms);
        Assert.Equal(67, ms[0].Score);
        Assert.False(ms[0].Passed);
        Assert.Equal("2/3 assertions passed.", ms[0].Reason);
        Assert.NotNull(r.AssertionResults);
        Assert.Equal(3, r.AssertionResults!.Count);
    }

    [Fact]
    public void EvalGenAssertions_all_pass_marks_passed_true()
    {
        var r = Row(
            "alpha beta",
            "expected",
            assertions:
            new[]
            {
                new Assertion { Type = AssertionType.MustContain, Value = "alpha" },
                new Assertion { Type = AssertionType.MustContain, Value = "beta" },
            });
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.EvalGenAssertions }, 70);
        Assert.True(ms[0].Passed);
        Assert.Equal(100, ms[0].Score);
    }

    [Fact]
    public void EvalGenAssertions_skipped_when_row_has_no_assertions()
    {
        var r = Row("hello", "expected");
        var ms = DeterministicEvaluators.Evaluate(r, new[] { EvaluatorName.EvalGenAssertions }, 70);
        Assert.Empty(ms);
    }

    [Fact]
    public void Multiple_evaluators_emit_in_declared_input_order()
    {
        var r = Row("hello", "hello");
        var ms = DeterministicEvaluators.Evaluate(
            r,
            new[] { EvaluatorName.PartialMatch, EvaluatorName.ExactMatch, EvaluatorName.Citations },
            70);
        // Implementation iterates fixed order: ExactMatch, PartialMatch, Citations
        Assert.Equal(EvaluatorName.ExactMatch, ms[0].Name);
        Assert.Equal(EvaluatorName.PartialMatch, ms[1].Name);
        Assert.Equal(EvaluatorName.Citations, ms[2].Name);
    }

    [Fact]
    public void Normalize_collapses_whitespace_and_lowercases()
    {
        Assert.Equal("hello world", DeterministicEvaluators.Normalize("  HELLO\t \n WORLD  "));
        Assert.Equal(string.Empty, DeterministicEvaluators.Normalize(null));
        Assert.Equal(string.Empty, DeterministicEvaluators.Normalize(string.Empty));
    }
}
