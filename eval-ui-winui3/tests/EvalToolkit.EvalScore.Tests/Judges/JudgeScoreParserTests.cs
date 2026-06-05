using EvalToolkit.EvalScore.Judges;

namespace EvalToolkit.EvalScore.Tests.Judges;

public class JudgeScoreParserTests
{
    [Fact]
    public void JsonHappyPath_ReturnsScoreReasonModel()
    {
        var result = JudgeScoreParser.Parse("{\"score\": 87, \"reason\": \"good\", \"model\": \"gpt-4\"}");
        Assert.Equal(87, result.Score);
        Assert.Equal("good", result.Reason);
        Assert.Equal("gpt-4", result.Model);
    }

    [Fact]
    public void JsonReasonEmpty_PreservesEmptyString()
    {
        // TS: typeof reason === 'string' ? reason : ...; '' is a string.
        var result = JudgeScoreParser.Parse("{\"score\": 50, \"reason\": \"\"}");
        Assert.Equal(50, result.Score);
        Assert.Equal(string.Empty, result.Reason);
    }

    [Fact]
    public void JsonNoReasonButRationale_FallsBackToRationale()
    {
        var result = JudgeScoreParser.Parse("{\"score\": 42, \"rationale\": \"because\"}");
        Assert.Equal(42, result.Score);
        Assert.Equal("because", result.Reason);
    }

    [Fact]
    public void JsonRationaleEmpty_FallsThroughToNull()
    {
        // toOptionalString('') returns undefined in TS.
        var result = JudgeScoreParser.Parse("{\"score\": 42, \"rationale\": \"\"}");
        Assert.Equal(42, result.Score);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void JsonScoreAboveHundred_ClampsTo100()
    {
        var result = JudgeScoreParser.Parse("{\"score\": 150}");
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void JsonScoreNegative_ClampsTo0()
    {
        var result = JudgeScoreParser.Parse("{\"score\": -10}");
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void JsonScoreHalfFraction_RoundsAwayFromZero()
    {
        // TS Math.round(73.5) === 74 (half-up for positives).
        var result = JudgeScoreParser.Parse("{\"score\": 73.5}");
        Assert.Equal(74, result.Score);
    }

    [Fact]
    public void JsonScoreNonNumeric_FallsThroughToRegex()
    {
        // {"score":"high"} — score is not a number; numeric extraction over the
        // full trimmed string finds no digits — should throw with the TS preview message.
        var ex = Assert.Throws<InvalidOperationException>(
            () => JudgeScoreParser.Parse("{\"score\":\"high\"}"));
        Assert.Contains("Could not parse score", ex.Message);
    }

    [Fact]
    public void JsonScoreFloatPlusTrailingGarbage_FallsToRegexAndReturnsIntegerPart()
    {
        // {"score":73.7} trailing — JSON parse may fail because of "trailing"
        // (it's not valid JSON after the object). Falls through to regex; first
        // integer is "73"; the ".7" portion is ignored.
        var result = JudgeScoreParser.Parse("{\"score\":73.7} trailing");
        Assert.Equal(73, result.Score);
    }

    [Fact]
    public void PlainNumericText_ParsesFirstInteger()
    {
        var result = JudgeScoreParser.Parse("Score: 87/100");
        Assert.Equal(87, result.Score);
        Assert.Null(result.Reason);
        Assert.Null(result.Model);
    }

    [Fact]
    public void PlainText_AboveHundred_Clamps()
    {
        var result = JudgeScoreParser.Parse("999");
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void NoDigits_ThrowsWithTruncatedPreview()
    {
        string longText = new string('a', 200);
        var ex = Assert.Throws<InvalidOperationException>(() => JudgeScoreParser.Parse(longText));
        Assert.StartsWith("Could not parse score from judge response: ", ex.Message);
        // TS: trimmed.slice(0, 120) — preview is 120 chars max.
        string preview = ex.Message["Could not parse score from judge response: ".Length..];
        Assert.Equal(120, preview.Length);
    }

    [Fact]
    public void NoDigits_ShortInput_PreviewIsTheFullString()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JudgeScoreParser.Parse("no number here"));
        Assert.Equal("Could not parse score from judge response: no number here", ex.Message);
    }

    [Fact]
    public void EmptyInput_ThrowsWithEmptyPreview()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JudgeScoreParser.Parse("   "));
        Assert.Equal("Could not parse score from judge response: ", ex.Message);
    }

    [Fact]
    public void TrimmedJsonWithLeadingWhitespace_ParsesJson()
    {
        var result = JudgeScoreParser.Parse("   {\"score\": 60}   ");
        Assert.Equal(60, result.Score);
    }

    [Fact]
    public void JsonWithTrailingComma_FallsThroughToRegex()
    {
        // TS JSON.parse is strict — trailing commas fail. The numeric
        // regex still matches the first integer (80) and a reason field
        // is lost because we never enter the JSON branch.
        var result = JudgeScoreParser.Parse("{\"score\":80,\"reason\":\"x\",}");
        Assert.Equal(80, result.Score);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void HugeDigitRun_DoesNotOverflow()
    {
        // 25 nines would overflow int.Parse; parser must double-parse
        // then clamp before narrowing.
        var result = JudgeScoreParser.Parse(new string('9', 25));
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void JsonScoreWithHugeNumber_ClampsTo100()
    {
        var result = JudgeScoreParser.Parse("{\"score\": 9.99e30}");
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void JsonWithMissingScoreKey_FallsThroughToRegex()
    {
        // Object lacks `score` property; falls through; first integer is 1.
        var result = JudgeScoreParser.Parse("{\"x\":1, \"text\": \"score is 42\"}");
        Assert.Equal(1, result.Score);
    }
}
