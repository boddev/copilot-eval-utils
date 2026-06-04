using EvalToolkit.WorkIQ;

namespace EvalToolkit.WorkIQ.Tests;

public class WorkIQScoreParserTests
{
    [Theory]
    [InlineData("{\"score\":87,\"reason\":\"good\"}", 87)]
    [InlineData("{\"score\":87.6}", 88)]
    [InlineData("{\"score\":-2}", 0)]
    [InlineData("{\"score\":130}", 100)]
    public void ParseScore_ReadsJsonScoreAndClamps(string text, int expected)
    {
        Assert.Equal(expected, WorkIQScoreParser.ParseScore(text));
    }

    [Theory]
    [InlineData("92", 92)]
    [InlineData("Score: 73/100", 73)]
    [InlineData("I would give it 101 because...", 100)]
    [InlineData("invalid json { score: 44 }", 44)]
    public void ParseScore_FallsBackToFirstInteger(string text, int expected)
    {
        Assert.Equal(expected, WorkIQScoreParser.ParseScore(text));
    }

    [Fact]
    public void ParseScore_ThrowsWhenNoNumberExists()
    {
        WorkIQException exception = Assert.Throws<WorkIQException>(() => WorkIQScoreParser.ParseScore("no score here"));
        Assert.Contains("Could not parse score", exception.Message, StringComparison.Ordinal);
    }
}
