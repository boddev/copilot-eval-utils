using EvalToolkit.Core;
using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.EvalGen.Tests.Writers;

/// <summary>
/// Byte-exact parity tests for <see cref="EvalCsvWriter"/> driven by
/// the <c>csv-*</c> scenarios in the writers probe. Each test
/// reproduces the JS input fixture, calls the C# writer, and asserts
/// the produced bytes equal the recorded <see cref="WritersProbeScenario.Text"/>.
///
/// Header is always emitted (even for empty input → 53-byte file).
/// Minimal quoting; embedded CRLF preserved; control chars and
/// unicode pass through unchanged. See <see cref="EvalCsvWriter"/>
/// xmldoc for the full contract.
/// </summary>
public sealed class EvalCsvWriterTests
{
    [Fact]
    public void CsvBasic_MatchesProbeExactly()
    {
        var items = new List<GeneratedEvalItem> { WritersTestFixtures.BaseItem() };
        AssertCsvMatchesProbe("csv-basic", items);
    }

    [Fact]
    public void CsvTwoItems_MatchesProbeExactly()
    {
        var items = new List<GeneratedEvalItem>
        {
            WritersTestFixtures.BaseItem(),
            WritersTestFixtures.SecondItem(),
        };
        AssertCsvMatchesProbe("csv-two-items", items);
    }

    [Fact]
    public void CsvEdges_QuotingAndUnicodeAndControlChars_MatchProbeExactly()
    {
        AssertCsvMatchesProbe("csv-edges", WritersTestFixtures.CsvEdgeItems());
    }

    [Fact]
    public void CsvEmpty_WritesHeaderOnly_MatchesProbeExactly()
    {
        AssertCsvMatchesProbe("csv-empty", []);
        // Pin the precise 53-byte header-only file shape.
        Assert.Equal(53, WritersProbeData.Get("csv-empty").ByteLength);
    }

    [Fact]
    public void CsvActualAnswerColumn_AlwaysEmpty_RegardlessOfItemShape()
    {
        // The TS writer reads only the explicit columns set; if a future
        // item subclass adds an `actual_answer` field it MUST NOT leak
        // into the row. This test mirrors the JS `csv-actual-ignored`
        // scenario which writes a normal baseItem (since C# doesn't
        // permit dynamic extra props on the record). The bytes are
        // identical to `csv-basic` because the typed record has no
        // such field — pin that explicitly.
        var items = new List<GeneratedEvalItem> { WritersTestFixtures.BaseItem() };
        AssertCsvMatchesProbe("csv-actual-ignored", items);
    }

    private static void AssertCsvMatchesProbe(
        string scenarioName, IReadOnlyList<GeneratedEvalItem> items)
    {
        var probe = WritersProbeData.Get(scenarioName);
        var writer = new EvalCsvWriter();
        string outPath = WritersTestUtil.TempPath($"{scenarioName}.csv");

        string written = writer.Write(items, outPath);
        Assert.Equal(Path.GetFullPath(outPath), written);

        string actualText = WritersTestUtil.ReadAllUtf8(written);
        Assert.Equal(probe.Text, actualText);

        byte[] actualBytes = File.ReadAllBytes(written);
        Assert.Equal(probe.ByteLength, actualBytes.Length);
        // BOM check — TS writes plain UTF-8 with no BOM.
        Assert.False(probe.Bom);
        Assert.False(StartsWithUtf8Bom(actualBytes));
    }

    private static bool StartsWithUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
