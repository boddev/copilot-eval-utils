using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class ReviewerTests
{
    private static GeneratedEvalItem MakeItem(
        string prompt = "What?",
        string ans = "answer",
        string sourceLoc = "f.csv:row 1",
        QuestionCategory cat = QuestionCategory.SingleRecordLookup,
        GroundingConfidence conf = GroundingConfidence.High,
        params Assertion[] assertions)
        => new()
        {
            Id = "abc123",
            Prompt = prompt,
            ExpectedAnswer = ans,
            SourceLocation = sourceLoc,
            Assertions = assertions,
            Category = cat,
            Difficulty = Difficulty.Easy,
            SupportingFacts = new[] { "name=Alice" },
            GroundingConfidence = conf,
        };

    private static ValidationResult MakeValidation(int total = 1, double coverage = 0.5) => new()
    {
        Passed = true,
        TotalItems = total,
        DuplicatesRemoved = 0,
        CategoryBalance = new Dictionary<QuestionCategory, int> { [QuestionCategory.SingleRecordLookup] = total },
        CoverageScore = coverage,
        Issues = Array.Empty<string>(),
        UniqueRowsReferenced = total,
        TotalRows = (int)(total / Math.Max(coverage, 0.01)),
    };

    private static readonly DateTimeOffset FixedNow =
        new(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void FormatReview_RendersHeaderSection()
    {
        var items = new[] { MakeItem() };
        var md = Reviewer.FormatReview(items, MakeValidation(), "test set", "data.csv", FixedNow);
        Assert.StartsWith("# EvalGen Review", md);
        Assert.Contains("**Source:** data.csv", md);
        Assert.Contains("**Description:** test set", md);
        Assert.Contains("**Total questions:** 1", md);
        Assert.Contains("2025-01-15T10:30:00.000Z", md);
    }

    [Fact]
    public void FormatReview_RendersCategoryTable()
    {
        var items = new[] { MakeItem() };
        var md = Reviewer.FormatReview(items, MakeValidation(), "d", "f", FixedNow);
        Assert.Contains("## Category Distribution", md);
        Assert.Contains("| Category | Count |", md);
        Assert.Contains("| single_record_lookup | 1 |", md);
    }

    [Fact]
    public void FormatReview_RendersIssuesWhenPresent()
    {
        var v = MakeValidation() with { Issues = new[] { "issue A", "issue B" } };
        var md = Reviewer.FormatReview(new[] { MakeItem() }, v, "d", "f", FixedNow);
        Assert.Contains("## Validation Notes", md);
        Assert.Contains("- ⚠️ issue A", md);
        Assert.Contains("- ⚠️ issue B", md);
    }

    [Fact]
    public void FormatReview_OmitsIssuesSectionWhenEmpty()
    {
        var md = Reviewer.FormatReview(new[] { MakeItem() }, MakeValidation(), "d", "f", FixedNow);
        Assert.DoesNotContain("## Validation Notes", md);
    }

    [Fact]
    public void FormatReview_RendersQuestionDetails()
    {
        var items = new[]
        {
            MakeItem(prompt: "What is X?", ans: "X is here",
                assertions: new Assertion[] { new MustContainAssertion { Value = "X" } }),
        };
        var md = Reviewer.FormatReview(items, MakeValidation(), "d", "f", FixedNow);
        Assert.Contains("### Q1: What is X?", md);
        Assert.Contains("**Category:** single_record_lookup", md);
        Assert.Contains("**Difficulty:** easy", md);
        Assert.Contains("**Confidence:** high", md);
        Assert.Contains("**Source:** f.csv:row 1", md);
        Assert.Contains("**Expected Answer:** X is here", md);
        Assert.Contains("Must contain: \"X\"", md);
    }

    [Fact]
    public void FormatReview_RendersMustContainAny()
    {
        var items = new[]
        {
            MakeItem(assertions: new Assertion[] { new MustContainAnyAssertion { Values = new[] { "A", "B" } } }),
        };
        var md = Reviewer.FormatReview(items, MakeValidation(), "d", "f", FixedNow);
        Assert.Contains("Must contain any:", md);
        Assert.Contains("\"A\"", md);
        Assert.Contains("\"B\"", md);
    }

    [Fact]
    public void FormatReview_RendersMustNotContain()
    {
        var items = new[]
        {
            MakeItem(assertions: new Assertion[] { new MustNotContainAssertion { Value = "BAD" } }),
        };
        var md = Reviewer.FormatReview(items, MakeValidation(), "d", "f", FixedNow);
        Assert.Contains("Must NOT contain: \"BAD\"", md);
    }

    [Fact]
    public void FormatReview_CoverageInfoWithUniqueRows()
    {
        var v = MakeValidation() with { CoverageScore = 0.5, UniqueRowsReferenced = 5, TotalRows = 10 };
        var md = Reviewer.FormatReview(new[] { MakeItem() }, v, "d", "f", FixedNow);
        Assert.Contains("**Coverage:** 50%", md);
        Assert.Contains("5 of 10 source rows tested", md);
    }

    [Fact]
    public void FormatReview_SampledNotExhaustiveNote()
    {
        var v = MakeValidation() with
        {
            CoverageScore = 0.01,
            UniqueRowsReferenced = 10,
            TotalRows = 10000,
            DatasetSampledNotExhaustive = true,
        };
        var md = Reviewer.FormatReview(new[] { MakeItem() }, v, "d", "f", FixedNow);
        Assert.Contains("Representative sample", md);
    }

    [Fact]
    public void FormatReview_NoDoubleTrailingNewline()
    {
        // TS reviewer pushes '' after each item then lines.join('\n'),
        // producing one trailing '\n'. C# mirrors that — exactly one.
        var md = Reviewer.FormatReview(new[] { MakeItem() }, MakeValidation(), "d", "f", FixedNow);
        Assert.True(md.EndsWith('\n'));
        Assert.False(md.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatReview_CoveragePct_UsesAwayFromZeroRounding()
    {
        // Round-2 fix: TS Math.round(.5) rounds upward; .NET default Math.Round
        // is banker's. 0.245 * 100 = 24.5 → TS prints 25, banker prints 24.
        var v = MakeValidation() with { CoverageScore = 0.245, UniqueRowsReferenced = 49, TotalRows = 200 };
        var md = Reviewer.FormatReview(new[] { MakeItem() }, v, "d", "f", FixedNow);
        Assert.Contains("**Coverage:** 25%", md);
        Assert.DoesNotContain("**Coverage:** 24%", md);
    }
}
