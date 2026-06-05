using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class ValidatorTests
{
    private static DraftedQuestion MakeQ(
        string prompt,
        string sourceLoc,
        QuestionCategory cat = QuestionCategory.SingleRecordLookup,
        params string[] facts) => new()
        {
            Prompt = prompt,
            Category = cat,
            Difficulty = Difficulty.Easy,
            ReferencedFacts = Array.Empty<Fact>(),
            ExpectedAnswer = "answer with " + string.Join(" ", facts.Select(f =>
                f.Contains('=', StringComparison.Ordinal) ? f.Substring(f.IndexOf('=', StringComparison.Ordinal) + 1) : f)),
            SupportingFacts = facts,
            SourceLocation = sourceLoc,
        };

    [Fact]
    public void BuildEvalItems_MapsAssertionsByIndex()
    {
        var qs = new[]
        {
            MakeQ("q1", "x:row 1", QuestionCategory.SingleRecordLookup, "name=Alice"),
            MakeQ("q2", "x:row 2", QuestionCategory.SingleRecordLookup, "name=Bob"),
        };
        var map = new Dictionary<int, IReadOnlyList<Assertion>>
        {
            [0] = new[] { (Assertion)new MustContainAssertion { Value = "Alice" } },
            [1] = new[] { (Assertion)new MustContainAssertion { Value = "Bob" } },
        };
        var items = Validator.BuildEvalItems(qs, map);
        Assert.Equal(2, items.Count);
        Assert.Single(items[0].Assertions);
        Assert.Equal("Alice", ((MustContainAssertion)items[0].Assertions[0]).Value);
    }

    [Fact]
    public void BuildEvalItems_GeneratesStableId()
    {
        var q = MakeQ("identical prompt", "f.csv:row 1");
        var items1 = Validator.BuildEvalItems(new[] { q }, new Dictionary<int, IReadOnlyList<Assertion>>());
        var items2 = Validator.BuildEvalItems(new[] { q }, new Dictionary<int, IReadOnlyList<Assertion>>());
        Assert.Equal(items1[0].Id, items2[0].Id);
        Assert.Equal(12, items1[0].Id.Length);
    }

    [Fact]
    public void BuildEvalItems_IncludesSourceLocationInReferencedRows()
    {
        var q = MakeQ("q", "f.csv:row 1");
        var items = Validator.BuildEvalItems(new[] { q }, new Dictionary<int, IReadOnlyList<Assertion>>());
        Assert.NotNull(items[0].ReferencedRows);
        Assert.Contains("f.csv:row 1", items[0].ReferencedRows!);
    }

    [Fact]
    public void ValidateEvalSet_RemovesDuplicatePrompts()
    {
        var qs = new[]
        {
            MakeQ("What is X?", "f.csv:row 1"),
            MakeQ("what is x", "f.csv:row 2"),
            MakeQ("What is Y?", "f.csv:row 3"),
        };
        var items = Validator.BuildEvalItems(qs, new Dictionary<int, IReadOnlyList<Assertion>>());
        var (validated, result) = Validator.ValidateEvalSet(items, totalRows: 100);
        Assert.Equal(2, validated.Count);
        Assert.Equal(1, result.DuplicatesRemoved);
    }

    [Fact]
    public void ValidateEvalSet_PopulatesCategoryBalance()
    {
        var qs = new[]
        {
            MakeQ("a", "f.csv:row 1", QuestionCategory.SingleRecordLookup),
            MakeQ("b", "f.csv:row 2", QuestionCategory.Comparison),
            MakeQ("c", "f.csv:row 3", QuestionCategory.EdgeCase),
        };
        var items = Validator.BuildEvalItems(qs, new Dictionary<int, IReadOnlyList<Assertion>>());
        var (_, result) = Validator.ValidateEvalSet(items, 50);
        Assert.True(result.CategoryBalance.ContainsKey(QuestionCategory.SingleRecordLookup));
        Assert.Equal(1, result.CategoryBalance[QuestionCategory.SingleRecordLookup]);
        Assert.Equal(1, result.CategoryBalance[QuestionCategory.Comparison]);
        Assert.Equal(1, result.CategoryBalance[QuestionCategory.EdgeCase]);
    }

    [Fact]
    public void ValidateEvalSet_ComputesCoverageScore()
    {
        var qs = Enumerable.Range(1, 10).Select(i =>
            MakeQ($"q{i}", $"f.csv:row {i}")).ToArray();
        var items = Validator.BuildEvalItems(qs, new Dictionary<int, IReadOnlyList<Assertion>>());
        var (_, result) = Validator.ValidateEvalSet(items, totalRows: 100);
        Assert.True(result.CoverageScore > 0);
        Assert.True(result.CoverageScore <= 1);
        Assert.Equal(10, result.UniqueRowsReferenced);
        Assert.Equal(100, result.TotalRows);
    }

    [Fact]
    public void ValidateEvalSet_ZeroTotalRows_CoverageZero()
    {
        var qs = new[] { MakeQ("q", "f.csv:row 1") };
        var items = Validator.BuildEvalItems(qs, new Dictionary<int, IReadOnlyList<Assertion>>());
        var (_, result) = Validator.ValidateEvalSet(items, totalRows: 0);
        Assert.Equal(0, result.CoverageScore);
    }

    [Fact]
    public void ValidateEvalSet_LargeDataset_GeneratesSampledNotExhaustiveFlag()
    {
        var qs = Enumerable.Range(1, 10).Select(i => MakeQ($"q{i}", $"f.csv:row {i}")).ToArray();
        var items = Validator.BuildEvalItems(qs, new Dictionary<int, IReadOnlyList<Assertion>>());
        var (_, result) = Validator.ValidateEvalSet(items, totalRows: 10000);
        Assert.True(result.DatasetSampledNotExhaustive);
        Assert.True(result.RecommendedCountForTarget > 50);
    }

    [Fact]
    public void ValidateEvalSet_PassedTrueOnGoodCoverage()
    {
        var qs = Enumerable.Range(1, 20).Select(i =>
            MakeQ($"q{i}", $"f.csv:row {i}",
                i % 6 == 0 ? QuestionCategory.SingleRecordLookup :
                i % 6 == 1 ? QuestionCategory.AttributeRetrieval :
                i % 6 == 2 ? QuestionCategory.FilteredFind :
                i % 6 == 3 ? QuestionCategory.Temporal :
                i % 6 == 4 ? QuestionCategory.Comparison : QuestionCategory.EdgeCase)).ToArray();
        var items = Validator.BuildEvalItems(qs, new Dictionary<int, IReadOnlyList<Assertion>>());
        var (_, result) = Validator.ValidateEvalSet(items, totalRows: 20);
        Assert.True(result.CoverageScore >= 0.2);
    }
}
