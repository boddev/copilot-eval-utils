namespace EvalToolkit.Cli.Tests;

/// <summary>
/// Tests for <see cref="EvalToolkit.Cli.OptionHelpers"/>. Mirrors the TS-side
/// behaviour of <c>splitCsvOption</c>, <c>parsePositiveInt</c>, <c>parseNonNegativeInt</c>,
/// the <c>--count</c> clamp, multi-prompt resolution, and the trailing-extension
/// rewrite used to derive sidecar and review paths.
/// </summary>
public sealed class OptionHelpersTests
{
    [Fact]
    public void SplitCsv_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(OptionHelpers.SplitCsv(null));
        Assert.Null(OptionHelpers.SplitCsv(""));
        Assert.Null(OptionHelpers.SplitCsv("   "));
    }

    [Fact]
    public void SplitCsv_TrimsAndDropsEmpty()
    {
        var result = OptionHelpers.SplitCsv(" a , b ,, c ");
        Assert.NotNull(result);
        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void SplitCsv_AllEmpty_ReturnsNull()
    {
        Assert.Null(OptionHelpers.SplitCsv(",,, ,"));
    }

    [Theory]
    [InlineData("5", 1, 5)]
    [InlineData("0", 7, 7)]
    [InlineData("-3", 7, 7)]
    [InlineData("abc", 9, 9)]
    [InlineData(null, 4, 4)]
    public void ParsePositiveInt_FallbackWhenInvalid(string? input, int fallback, int expected)
    {
        Assert.Equal(expected, OptionHelpers.ParsePositiveInt(input, fallback));
    }

    [Theory]
    [InlineData("0", 7, 0)]
    [InlineData("3", 1, 3)]
    [InlineData("-1", 5, 5)]
    [InlineData("xyz", 2, 2)]
    public void ParseNonNegativeInt_FallbackWhenInvalid(string? input, int fallback, int expected)
    {
        Assert.Equal(expected, OptionHelpers.ParseNonNegativeInt(input, fallback));
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(30, 30)]
    [InlineData(50, 50)]
    [InlineData(75, 50)]
    public void ClampGenerateCount_ClampsTo10To50(int requested, int expected)
    {
        Assert.Equal(expected, OptionHelpers.ClampGenerateCount(requested));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, 5, true)]
    [InlineData(true, 5, true)]
    public void IsMultiPromptEnabled_TrueWhenFlagOrTurns(bool flag, int? turns, bool expected)
    {
        Assert.Equal(expected, OptionHelpers.IsMultiPromptEnabled(flag, turns));
    }

    [Fact]
    public void ResolveMultiPromptTurns_NotEnabled_ReturnsNull()
    {
        Assert.Null(OptionHelpers.ResolveMultiPromptTurns(null, false));
        Assert.Null(OptionHelpers.ResolveMultiPromptTurns(10, false));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [InlineData(20, 20)]
    [InlineData(50, 20)]
    public void ResolveMultiPromptTurns_ClampedWhenEnabled(int? turns, int expected)
    {
        Assert.Equal(expected, OptionHelpers.ResolveMultiPromptTurns(turns, true));
    }

    [Theory]
    [InlineData("./out/eval.csv", "./out/eval-multi-prompt.json")]
    [InlineData("./out/eval.xlsx", "./out/eval-multi-prompt.json")]
    [InlineData("./out/eval.json", "./out/eval-multi-prompt.json")]
    [InlineData("./OUT/EVAL.CSV", "./OUT/EVAL-multi-prompt.json")]
    [InlineData("./out/eval", "./out/eval-multi-prompt.json")]
    [InlineData("./out/eval.txt", "./out/eval.txt-multi-prompt.json")]
    public void DeriveMultiPromptOutputPath_ReplacesOrAppends(string input, string expected)
    {
        Assert.Equal(expected, OptionHelpers.DeriveMultiPromptOutputPath(input));
    }

    [Theory]
    [InlineData("./out/eval.csv", ".evalgen.json", "./out/eval.evalgen.json")]
    [InlineData("./out/eval.csv", "-review.md", "./out/eval-review.md")]
    [InlineData("./OUT/EVAL.CSV", "-review.md", "./OUT/EVAL-review.md")]
    [InlineData("./out/eval.txt", "-review.md", "./out/eval.txt")]
    public void RewriteOutputExtension_ReplacesKnownExtensions(string input, string suffix, string expected)
    {
        Assert.Equal(expected, OptionHelpers.RewriteOutputExtension(input, suffix));
    }
}
