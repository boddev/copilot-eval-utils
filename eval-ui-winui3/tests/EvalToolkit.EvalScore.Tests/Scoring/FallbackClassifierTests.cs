using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Scoring;

public class FallbackClassifierTests
{
    [Theory]
    [InlineData("Request timed out after 30s")]
    [InlineData("Connection TIMEOUT")]
    [InlineData("Could not parse score from judge response")]
    [InlineData("ask_work_iq tool failed")]
    [InlineData("MCP server reset")]
    [InlineData("HTTP 429 too many requests")]
    [InlineData("Rate limit exceeded")]
    [InlineData("Service throttled")]
    [InlineData("Temporarily unavailable")]
    [InlineData("Bad gateway (502)")]
    [InlineData("503 service unavailable")]
    [InlineData("504 gateway timeout")]
    public void Returns_true_for_eligible_substrings(string message)
    {
        Assert.True(FallbackClassifier.IsEligible(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData("File not found")]
    [InlineData("Invalid argument")]
    [InlineData("Permission denied")]
    public void Returns_false_for_non_eligible_messages(string message)
    {
        Assert.False(FallbackClassifier.IsEligible(new InvalidOperationException(message)));
    }

    [Fact]
    public void Returns_false_for_null()
    {
        Assert.False(FallbackClassifier.IsEligible(null));
    }

    [Fact]
    public void Returns_false_for_empty_message()
    {
        Assert.False(FallbackClassifier.IsEligible(new InvalidOperationException(string.Empty)));
    }

    [Fact]
    public void Case_insensitive_match()
    {
        Assert.True(FallbackClassifier.IsEligible(new InvalidOperationException("THROTTLING")));
        Assert.True(FallbackClassifier.IsEligible(new InvalidOperationException("Could Not Parse Score")));
    }
}
