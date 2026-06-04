using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.EvalGen.Tests.Writers;

/// <summary>
/// Byte-exact parity tests for <see cref="ReviewMarkdownWriter"/>
/// driven by the <c>review-review-*</c> scenarios in the writers probe.
/// Verifies (a) the <c>.csv|.xlsx|.json</c> path rewrite to
/// <c>-review.md</c> (leading dash, NOT a dotted suffix), (b) the
/// pass-through of unmatched extensions (overwrite-source behavior),
/// and (c) verbatim content writing with no added trailing newline.
/// </summary>
public sealed class ReviewMarkdownWriterTests
{
    [Theory]
    [InlineData("review-review-csv",        "review-csv.csv",         "Hello world",                       "review-csv-review.md")]
    [InlineData("review-review-json",       "review-json.json",       "# Heading\n\n- bullet 1\n- bullet 2\n", "review-json-review.md")]
    [InlineData("review-review-xlsx",       "review-xlsx.xlsx",       "no trailing newline",               "review-xlsx-review.md")]
    [InlineData("review-review-csv-upper",  "review-csv-upper.CSV",   "mixed case ext",                    "review-csv-upper-review.md")]
    [InlineData("review-review-txt-noop",   "review-txt-noop.txt",    "unaffected extension",              "review-txt-noop.txt")]
    [InlineData("review-review-noext-noop", "review-noext-noop",      "no extension at all",               "review-noext-noop")]
    public void Write_PathRewriteAndContent_MatchesProbeExactly(
        string scenarioName, string inputFileName, string body, string expectedOutputFileName)
    {
        var probe = WritersProbeData.Get(scenarioName);
        Assert.Equal(body.Length, probe.BodyLength);

        string dir = Path.Combine(
            Path.GetTempPath(),
            "EvalToolkit.EvalGen.Tests.Writers",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string outputPath = Path.Combine(dir, inputFileName);
        string expectedPath = Path.GetFullPath(Path.Combine(dir, expectedOutputFileName));

        var writer = new ReviewMarkdownWriter();
        string written = writer.Write(body, outputPath);

        Assert.Equal(expectedPath, written);
        Assert.Equal(probe.Text, WritersTestUtil.ReadAllUtf8(written));

        byte[] bytes = File.ReadAllBytes(written);
        Assert.Equal(probe.ByteLength, bytes.Length);
        Assert.False(probe.Bom);
    }
}
