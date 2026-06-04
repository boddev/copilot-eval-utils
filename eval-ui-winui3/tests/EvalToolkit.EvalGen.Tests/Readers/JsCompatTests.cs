using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

/// <summary>
/// Pins <see cref="JsCompat.Trim"/> and
/// <see cref="JsCompat.WhitespaceRun"/> against ECMAScript semantics
/// rather than .NET's <see cref="string.Trim"/>. Round-6 reviewers
/// flagged that the previous TextChunker / TextFileReader uses of
/// <c>.NET .Trim()</c> diverged from JS on U+FEFF and U+0085.
/// </summary>
public class JsCompatTests
{
    [Theory]
    // U+FEFF (BOM / ZWNBSP) — JS trims; .NET .Trim() leaves it alone.
    [InlineData("\uFEFFhello\uFEFF", "hello")]
    // U+0085 (NEL) — JS does NOT trim; .NET .Trim() does. The expected
    // result here is the input unchanged, which is what JS gives.
    [InlineData("\u0085hello\u0085", "\u0085hello\u0085")]
    // Plain ASCII / Unicode whitespace cases that both runtimes agree on.
    [InlineData("  hello  ", "hello")]
    [InlineData("\t\nhello\r\n", "hello")]
    [InlineData("\u00a0hello\u00a0", "hello")] // NBSP
    [InlineData("\u2028hello\u2029", "hello")] // line/paragraph sep
    [InlineData("\u3000hello\u3000", "hello")] // ideographic space
    [InlineData("", "")]
    [InlineData("noWS", "noWS")]
    public void Trim_MatchesEcmascript(string input, string expected)
    {
        Assert.Equal(expected, JsCompat.Trim(input));
    }

    [Fact]
    public void Trim_MixedJsAndDotNetWhitespace_PreservesNel()
    {
        // The input has BOM (JS=ws), NEL (JS=not-ws), then text. JS
        // would only strip the leading BOM, leaving "\u0085hello".
        Assert.Equal("\u0085hello", JsCompat.Trim("\uFEFF\u0085hello"));
    }

    [Fact]
    public void Trim_NelInside_NotTouched()
    {
        // U+0085 appearing inside a word stays inside both for JS and
        // .NET; the divergence is only at the edges.
        Assert.Equal("a\u0085b", JsCompat.Trim("  a\u0085b  "));
    }

    [Fact]
    public void WhitespaceRun_MatchesEcmascriptClass()
    {
        // Sanity-check that Split via WhitespaceRun honors the same set:
        // BOM splits, NEL does NOT.
        string[] withBom = JsCompat.WhitespaceRun.Split("a\uFEFFb");
        Assert.Equal(2, withBom.Length);

        string[] withNel = JsCompat.WhitespaceRun.Split("a\u0085b");
        Assert.Single(withNel);
    }
}
