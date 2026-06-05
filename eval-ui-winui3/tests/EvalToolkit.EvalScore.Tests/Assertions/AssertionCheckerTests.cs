using EvalToolkit.EvalScore.Assertions;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Assertions;

public class AssertionCheckerTests
{
    [Fact]
    public void MustContain_substring_case_insensitive_passes()
    {
        var a = new Assertion { Type = AssertionType.MustContain, Value = "Hello" };
        var r = AssertionChecker.EvaluateAssertion(a, "well, HELLO there");
        Assert.True(r.Passed);
        Assert.Equal("✅ Found \"Hello\"", r.Detail);
    }

    [Fact]
    public void MustContain_missing_returns_fail_detail()
    {
        var a = new Assertion { Type = AssertionType.MustContain, Value = "Bar" };
        var r = AssertionChecker.EvaluateAssertion(a, "foo qux");
        Assert.False(r.Passed);
        Assert.Equal("❌ Missing \"Bar\"", r.Detail);
    }

    [Fact]
    public void MustContain_with_wholeWord_does_not_match_inside_token()
    {
        var a = new Assertion { Type = AssertionType.MustContain, Value = "cat", WholeWord = true };
        var r = AssertionChecker.EvaluateAssertion(a, "concatenate");
        Assert.False(r.Passed);
    }

    [Fact]
    public void MustContain_with_wholeWord_matches_at_boundary()
    {
        var a = new Assertion { Type = AssertionType.MustContain, Value = "cat", WholeWord = true };
        var r = AssertionChecker.EvaluateAssertion(a, "the CAT sat");
        Assert.True(r.Passed);
    }

    [Fact]
    public void MustContain_with_wholeWord_escapes_regex_metacharacters()
    {
        var a = new Assertion { Type = AssertionType.MustContain, Value = "a.b", WholeWord = true };
        var r = AssertionChecker.EvaluateAssertion(a, "xxx a.b xxx");
        Assert.True(r.Passed);
        var r2 = AssertionChecker.EvaluateAssertion(a, "xxx aXb xxx");
        Assert.False(r2.Passed);
    }

    [Fact]
    public void MustContainAny_returns_first_match_in_listed_order()
    {
        var a = new Assertion
        {
            Type = AssertionType.MustContainAny,
            Values = new[] { "First", "Second", "Third" },
        };
        var r = AssertionChecker.EvaluateAssertion(a, "we have second AND THIRD");
        Assert.True(r.Passed);
        Assert.Equal("✅ Found \"Second\"", r.Detail);
    }

    [Fact]
    public void MustContainAny_none_returns_quoted_list()
    {
        var a = new Assertion
        {
            Type = AssertionType.MustContainAny,
            Values = new[] { "alpha", "beta" },
        };
        var r = AssertionChecker.EvaluateAssertion(a, "gamma delta");
        Assert.False(r.Passed);
        Assert.Equal("❌ None found: \"alpha\", \"beta\"", r.Detail);
    }

    [Fact]
    public void MustNotContain_absent_passes()
    {
        var a = new Assertion { Type = AssertionType.MustNotContain, Value = "boom" };
        var r = AssertionChecker.EvaluateAssertion(a, "all clear");
        Assert.True(r.Passed);
        Assert.Equal("✅ Correctly absent: \"boom\"", r.Detail);
    }

    [Fact]
    public void MustNotContain_present_fails()
    {
        var a = new Assertion { Type = AssertionType.MustNotContain, Value = "Boom" };
        var r = AssertionChecker.EvaluateAssertion(a, "kaBOOM");
        Assert.False(r.Passed);
        Assert.Equal("❌ Unexpectedly found \"Boom\"", r.Detail);
    }

    [Fact]
    public void EvaluateRowAssertions_returns_empty_when_no_assertions()
    {
        var row = new EvalRow { Prompt = "p", ExpectedAnswer = "e", SourceLocation = "s", ActualAnswer = "a" };
        Assert.Empty(AssertionChecker.EvaluateRowAssertions(row));
    }

    [Fact]
    public void EvaluateRowAssertions_returns_empty_when_actual_answer_empty()
    {
        var row = new EvalRow
        {
            Prompt = "p",
            ExpectedAnswer = "e",
            SourceLocation = "s",
            ActualAnswer = string.Empty,
            Assertions = new[] { new Assertion { Type = AssertionType.MustContain, Value = "x" } },
        };
        Assert.Empty(AssertionChecker.EvaluateRowAssertions(row));
    }

    [Fact]
    public void EvaluateRowAssertions_returns_empty_when_actual_answer_is_error()
    {
        var row = new EvalRow
        {
            Prompt = "p",
            ExpectedAnswer = "e",
            SourceLocation = "s",
            ActualAnswer = "[ERROR: timeout]",
            Assertions = new[] { new Assertion { Type = AssertionType.MustContain, Value = "x" } },
        };
        Assert.Empty(AssertionChecker.EvaluateRowAssertions(row));
    }

    [Fact]
    public void EvaluateAllAssertions_attaches_results_per_row()
    {
        var rows = new List<EvalRow>
        {
            new() { Prompt = "p", ExpectedAnswer = "e", SourceLocation = "s", ActualAnswer = "hello world",
                    Assertions = new[] { new Assertion { Type = AssertionType.MustContain, Value = "world" } } },
            new() { Prompt = "p", ExpectedAnswer = "e", SourceLocation = "s", ActualAnswer = "[ERROR: oops]",
                    Assertions = new[] { new Assertion { Type = AssertionType.MustContain, Value = "x" } } },
        };
        AssertionChecker.EvaluateAllAssertions(rows);
        Assert.NotNull(rows[0].AssertionResults);
        Assert.Single(rows[0].AssertionResults!);
        Assert.True(rows[0].AssertionResults![0].Passed);
        Assert.Empty(rows[1].AssertionResults!);
    }
}
