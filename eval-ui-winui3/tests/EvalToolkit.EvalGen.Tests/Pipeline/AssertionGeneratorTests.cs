using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class AssertionGeneratorTests
{
    private static DraftedQuestion MakeQ(string answer, params string[] facts) => new()
    {
        Prompt = "p",
        Category = QuestionCategory.SingleRecordLookup,
        Difficulty = Difficulty.Easy,
        ReferencedFacts = Array.Empty<Fact>(),
        ExpectedAnswer = answer,
        SupportingFacts = facts,
        SourceLocation = "x:row 1",
    };

    [Fact]
    public void GenerateAssertions_EmitsMustContainForFactValuesInAnswer()
    {
        var q = MakeQ("Alice lives in Seattle", "name=Alice", "city=Seattle");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Equal(2, assertions.Count);
        Assert.All(assertions, a => Assert.IsType<MustContainAssertion>(a));
        var values = assertions.OfType<MustContainAssertion>().Select(a => a.Value).ToList();
        Assert.Contains("Alice", values);
        Assert.Contains("Seattle", values);
    }

    [Fact]
    public void GenerateAssertions_SkipsValuesNotInAnswer()
    {
        var q = MakeQ("Alice", "name=Alice", "city=Seattle");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Single(assertions);
        Assert.Equal("Alice", ((MustContainAssertion)assertions[0]).Value);
    }

    [Fact]
    public void GenerateAssertions_SkipsShortValues()
    {
        var q = MakeQ("ab is a thing", "x=a");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Empty(assertions);
    }

    [Fact]
    public void GenerateAssertions_SkipsLongValues()
    {
        var longVal = new string('x', 100);
        var q = MakeQ(longVal, $"x={longVal}");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Empty(assertions);
    }

    [Fact]
    public void GenerateAssertions_UsesWholeWordForShortAlphaTokens()
    {
        var q = MakeQ("the cat sat", "what=cat");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Single(assertions);
        Assert.True(((MustContainAssertion)assertions[0]).WholeWord);
    }

    [Fact]
    public void GenerateAssertions_NoWholeWordForLongerTokens()
    {
        var q = MakeQ("Alice Smith", "name=Alice");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.False(((MustContainAssertion)assertions[0]).WholeWord);
    }

    [Fact]
    public void GenerateAssertions_DeduplicatesByValue()
    {
        var q = MakeQ("Alice Alice Alice", "a=Alice", "b=Alice", "c=Alice");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Single(assertions);
    }

    [Fact]
    public void GenerateAssertions_CapsAtFive()
    {
        var facts = Enumerable.Range(0, 10).Select(i => $"f{i}=value{i}").ToArray();
        var answer = string.Join(" ", Enumerable.Range(0, 10).Select(i => $"value{i}"));
        var q = MakeQ(answer, facts);
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Equal(5, assertions.Count);
    }

    [Fact]
    public void GenerateAssertions_CaseInsensitiveMatchOnAnswer()
    {
        var q = MakeQ("alice in wonderland", "name=Alice");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Single(assertions);
    }

    [Fact]
    public void GenerateAssertions_TrimsQuotesFromFactValue()
    {
        var q = MakeQ("Alice Smith", "name=\"Alice\"");
        var assertions = AssertionGenerator.GenerateAssertions(q);
        Assert.Equal("Alice", ((MustContainAssertion)assertions[0]).Value);
    }

    [Fact]
    public void GenerateAllAssertions_KeyedByIndex()
    {
        var qs = new[]
        {
            MakeQ("Alice", "name=Alice"),
            MakeQ("Bob", "name=Bob"),
        };
        var map = AssertionGenerator.GenerateAllAssertions(qs);
        Assert.Equal(2, map.Count);
        Assert.Single(map[0]);
        Assert.Single(map[1]);
        Assert.Equal("Alice", ((MustContainAssertion)map[0][0]).Value);
        Assert.Equal("Bob", ((MustContainAssertion)map[1][0]).Value);
    }
}
