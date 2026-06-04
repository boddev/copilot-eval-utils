using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.EvalGen.Tests.Writers;

/// <summary>
/// Path-rewrite tests for <see cref="PathRewrite.RewriteExtension"/>.
/// Pinned by the <c>sidecar-rewrite-*</c> and <c>review-review-*</c>
/// scenarios in the writers probe: the regex
/// <c>\.(csv|xlsx|json)$</c> is case-insensitive; unmatched extensions
/// and no-extension paths return the input UNCHANGED (the TS writers
/// then overwrite that source file at that exact path).
/// </summary>
public sealed class PathRewriteTests
{
    [Theory]
    [InlineData("out.csv", ".evalgen.json", "out.evalgen.json")]
    [InlineData("out.json", ".evalgen.json", "out.evalgen.json")]
    [InlineData("out.xlsx", ".evalgen.json", "out.evalgen.json")]
    [InlineData("out.CSV", ".evalgen.json", "out.evalgen.json")]
    [InlineData("out.JSON", ".evalgen.json", "out.evalgen.json")]
    [InlineData("out.XLSX", ".evalgen.json", "out.evalgen.json")]
    [InlineData("out.txt", ".evalgen.json", "out.txt")]
    [InlineData("out", ".evalgen.json", "out")]
    [InlineData("out.csv", "-review.md", "out-review.md")]
    [InlineData("out.JSON", "-review.md", "out-review.md")]
    [InlineData("out.txt", "-review.md", "out.txt")]
    [InlineData("nested/dir/out.csv", "-review.md", "nested/dir/out-review.md")]
    [InlineData("path.with.dots.json", "-review.md", "path.with.dots-review.md")]
    public void RewriteExtension_AppliesExpectedTransform(string input, string suffix, string expected)
    {
        string actual = PathRewrite.RewriteExtension(input, suffix);
        Assert.Equal(expected, actual);
    }
}
