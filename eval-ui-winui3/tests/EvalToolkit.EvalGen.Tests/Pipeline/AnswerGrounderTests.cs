using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class AnswerGrounderTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in pairs) d[p.Key] = p.Value;
        return d;
    }

    private static DraftedQuestion MakeQ(
        string sourceLoc,
        string answer,
        params string[] facts)
        => new()
        {
            Prompt = "p",
            Category = QuestionCategory.SingleRecordLookup,
            Difficulty = Difficulty.Easy,
            ReferencedFacts = Array.Empty<Fact>(),
            ExpectedAnswer = answer,
            SupportingFacts = facts,
            SourceLocation = sourceLoc,
        };

    [Fact]
    public void GroundAnswer_VerifiesFactsMatchingRecord()
    {
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("name", "Alice"), ("age", "30")),
            (IReadOnlyDictionary<string, object?>)Row(("name", "Bob"), ("age", "25")),
        };
        var q = MakeQ("data.csv:row 1", "Alice is 30", "name=Alice", "age=30");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        Assert.Equal(2, grounded.SupportingFacts.Count);
        Assert.Contains("name=Alice", grounded.SupportingFacts);
        Assert.Contains("age=30", grounded.SupportingFacts);
    }

    [Fact]
    public void GroundAnswer_NoMatch_PreservesOriginalFacts()
    {
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("name", "Alice")),
        };
        var q = MakeQ("data.csv:row 1", "ans", "name=Bob", "missing=field");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        Assert.Equal(q.SupportingFacts, grounded.SupportingFacts);
    }

    [Fact]
    public void GroundAnswer_TrimsSurroundingQuotes()
    {
        var records = new[] { (IReadOnlyDictionary<string, object?>)Row(("name", "Alice")) };
        var q = MakeQ("data.csv:row 1", "ans", "name=\"Alice\"");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        Assert.Contains("name=Alice", grounded.SupportingFacts);
    }

    [Fact]
    public void GroundAnswer_CaseInsensitiveOnTrimmedValue()
    {
        var records = new[] { (IReadOnlyDictionary<string, object?>)Row(("status", "Active")) };
        var q = MakeQ("data.csv:row 1", "ans", "status=\"active\"");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        Assert.Contains("status=Active", grounded.SupportingFacts);
    }

    [Fact]
    public void GroundAnswer_OutOfRangeRow_ReturnsOriginal()
    {
        var records = new[] { (IReadOnlyDictionary<string, object?>)Row(("name", "A")) };
        var q = MakeQ("data.csv:row 99", "ans", "name=A");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        Assert.Same(q, grounded);
    }

    [Fact]
    public void GroundAnswer_NoRowRefInSourceLocation_ReturnsOriginal()
    {
        var records = new[] { (IReadOnlyDictionary<string, object?>)Row(("name", "A")) };
        var q = MakeQ("data.csv", "ans", "name=A");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        Assert.Same(q, grounded);
    }

    [Fact]
    public void GroundAnswer_NormalizesSourceLocationWithFileName()
    {
        var records = new[] { (IReadOnlyDictionary<string, object?>)Row(("name", "Alice")) };
        var q = MakeQ("other.csv:row 1", "ans", "name=Alice");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "canonical.csv");
        Assert.Equal("canonical.csv:row 1", grounded.SourceLocation);
    }

    [Fact]
    public void ComputeGroundingConfidence_HighWhenAllFactsInAnswer()
    {
        var q = MakeQ("x:row 1", "Alice is 30 years old",
            "name=Alice", "age=30");
        Assert.Equal(GroundingConfidence.High, AnswerGrounder.ComputeGroundingConfidence(q));
    }

    [Fact]
    public void ComputeGroundingConfidence_MediumWhenHalfMatch()
    {
        var q = new DraftedQuestion
        {
            Prompt = "p",
            Category = QuestionCategory.SingleRecordLookup,
            Difficulty = Difficulty.Easy,
            ReferencedFacts = Array.Empty<Fact>(),
            ExpectedAnswer = "Alice in NYC",
            SupportingFacts = new[] { "name=Alice", "age=30", "city=NYC", "zip=10001" },
            SourceLocation = "x:row 1",
        };
        Assert.Equal(GroundingConfidence.Medium, AnswerGrounder.ComputeGroundingConfidence(q));
    }

    [Fact]
    public void ComputeGroundingConfidence_LowWhenFewMatch()
    {
        var q = MakeQ("x:row 1", "some unrelated text",
            "name=Alice", "age=30");
        Assert.Equal(GroundingConfidence.Low, AnswerGrounder.ComputeGroundingConfidence(q));
    }

    [Fact]
    public void ComputeGroundingConfidence_NoFacts_Low()
    {
        var q = MakeQ("x:row 1", "text");
        Assert.Equal(GroundingConfidence.Low, AnswerGrounder.ComputeGroundingConfidence(q));
    }

    [Fact]
    public void GroundAllAnswers_HandlesMultiple()
    {
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("name", "Alice")),
            (IReadOnlyDictionary<string, object?>)Row(("name", "Bob")),
        };
        var qs = new[]
        {
            MakeQ("data.csv:row 1", "Alice", "name=Alice"),
            MakeQ("data.csv:row 2", "Bob", "name=Bob"),
        };
        var grounded = AnswerGrounder.GroundAllAnswers(qs, records, "data.csv");
        Assert.Equal(2, grounded.Count);
        Assert.Contains("name=Alice", grounded[0].SupportingFacts);
        Assert.Contains("name=Bob", grounded[1].SupportingFacts);
    }

    [Fact]
    public void GroundAnswer_LoneQuoteFact_DoesNotThrow()
    {
        // Round-2 fix: TrimSurroundingQuotes("\"") used to throw ArgumentOutOfRange
        // because Substring(1, -1). TS regex /^"|"$/g returns empty string.
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("name", "Alice")),
        };
        var q = MakeQ("data.csv:row 1", "Alice", "\"", "name=Alice");
        var grounded = AnswerGrounder.GroundAnswer(q, records, "data.csv");
        // Lone quote becomes empty after trim and won't match — just verify no throw.
        Assert.NotNull(grounded);
        Assert.Contains("name=Alice", grounded.SupportingFacts);
    }
}
