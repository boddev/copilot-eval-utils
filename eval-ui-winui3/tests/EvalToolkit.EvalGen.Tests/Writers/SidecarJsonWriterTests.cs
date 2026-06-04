using EvalToolkit.Core;
using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.EvalGen.Tests.Writers;

/// <summary>
/// Byte-exact parity tests for <see cref="SidecarJsonWriter"/> driven
/// by the <c>sidecar-*</c> scenarios in the writers probe. Verifies
/// (a) 2-space indented JSON output with no trailing newline,
/// (b) the field-order contract for both the document and each item,
/// (c) the metadata block's <c>"unknown"</c> model fallback and
/// optional-field elision, (d) the path-rewrite to
/// <c>.evalgen.json</c>, and (e) Node-equivalent unicode + control
/// char escape behavior via
/// <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>.
/// </summary>
public sealed class SidecarJsonWriterTests
{
    private static SidecarJsonWriter NewWriter() =>
        new(new FixedClock(WritersTestFixtures.PinnedNow));

    [Fact]
    public void Basic_NoOptions_MatchesProbeExactly()
    {
        var items = new[] { WritersTestFixtures.BaseItem() };
        AssertMatches("sidecar-basic", items, "Test description", "suppliers.csv", "sidecar-basic.csv");
    }

    [Fact]
    public void FullMetadata_AllOptionalFieldsPresent_MatchesProbeExactly()
    {
        var items = new[] { WritersTestFixtures.RichItem() };
        var opts = new SidecarJsonOptions
        {
            // Field order in SidecarJsonOptions does not affect output
            // — the writer always emits the fixed key order in the JSON.
            Warnings = ["warn1", "warn2 with \"quote\""],
            Model = "gpt-4o-2024-11-20",
            AvoidanceEvalsets = ["eval-a.json", "eval-b.json"],
            AvoidanceItemsCompared = 42,
            CrossRunDuplicatesRemoved = 3,
            CrossRunAssertionOverlaps = 7,
        };
        AssertMatches("sidecar-full-metadata", items, "Full options", "src.csv",
            "sidecar-full-metadata.csv", opts);
    }

    [Fact]
    public void ReferencedRows_IncludedWhenPresent_MatchesProbeExactly()
    {
        var items = new[] { WritersTestFixtures.BaseItem() with { ReferencedRows = ["r1", "r2"] } };
        AssertMatches("sidecar-referenced-rows", items, "desc", "src.csv",
            "sidecar-referenced-rows.csv");
    }

    [Fact]
    public void Unicode_ControlCharsAndNbspAndBom_PassthroughLiterally()
    {
        // This scenario pins the relaxed JSON encoder's behavior:
        // - U+0001/0007/001F → escaped as \u0001 / \u0007 / \u001f
        //   (System.Text.Json + UnsafeRelaxedJsonEscaping matches Node
        //   exactly for these). Note the LOWERCASE hex letters in \u001f
        //   — Node and STJ both emit lowercase.
        // - NBSP (U+00A0) and BOM (U+FEFF) → literal UTF-8 bytes.
        // - Astral emoji 🚀 → literal UTF-8 (4 bytes), NOT a
        //   \uXXXX\uXXXX surrogate pair.
        var items = new[]
        {
            WritersTestFixtures.BaseItem() with
            {
                Prompt = "control \u0001 \u0007 \u001f rocket 🚀",
                ExpectedAnswer = "quote \" backslash \\ tab\there",
                SupportingFacts = ["nbsp\u00a0here", "feff\ufeffhere"],
            },
        };
        AssertMatches("sidecar-unicode", items, "Unicode test", "src.csv",
            "sidecar-unicode.csv");
    }

    [Theory]
    // (scenarioName, inputFileName, expectedOutputFileName)
    [InlineData("sidecar-rewrite-csv",        "rewrite-rewrite-csv.csv",         "rewrite-rewrite-csv.evalgen.json")]
    [InlineData("sidecar-rewrite-json",       "rewrite-rewrite-json.json",       "rewrite-rewrite-json.evalgen.json")]
    [InlineData("sidecar-rewrite-xlsx",       "rewrite-rewrite-xlsx.xlsx",       "rewrite-rewrite-xlsx.evalgen.json")]
    [InlineData("sidecar-rewrite-csv-upper",  "rewrite-rewrite-csv-upper.CSV",   "rewrite-rewrite-csv-upper.evalgen.json")]
    [InlineData("sidecar-rewrite-json-upper", "rewrite-rewrite-json-upper.JSON", "rewrite-rewrite-json-upper.evalgen.json")]
    [InlineData("sidecar-rewrite-txt-noop",   "rewrite-rewrite-txt-noop.txt",    "rewrite-rewrite-txt-noop.txt")]
    [InlineData("sidecar-rewrite-noext-noop", "rewrite-rewrite-noext-noop",      "rewrite-rewrite-noext-noop")]
    public void PathRewrite_MatchesProbeBytesAndOutputPath(
        string scenarioName, string inputFileName, string expectedOutputFileName)
    {
        var items = new[] { WritersTestFixtures.BaseItem() };

        string dir = Path.Combine(
            Path.GetTempPath(),
            "EvalToolkit.EvalGen.Tests.Writers",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string inputPath = Path.Combine(dir, inputFileName);
        string expectedPath = Path.GetFullPath(Path.Combine(dir, expectedOutputFileName));

        string written = NewWriter().Write(items, "Path rewrite", "src.csv", inputPath);
        Assert.Equal(expectedPath, written);

        var probe = WritersProbeData.Get(scenarioName);
        Assert.Equal(probe.Text, WritersTestUtil.ReadAllUtf8(written));
        byte[] bytes = File.ReadAllBytes(written);
        Assert.Equal(probe.ByteLength, bytes.Length);
    }

    private static void AssertMatches(
        string scenarioName,
        IReadOnlyList<GeneratedEvalItem> items,
        string description,
        string sourceFile,
        string outputFileName,
        SidecarJsonOptions? options = null)
    {
        string outPath = WritersTestUtil.TempPath(outputFileName);
        string written = NewWriter().Write(items, description, sourceFile, outPath, options);

        var probe = WritersProbeData.Get(scenarioName);
        Assert.Equal(probe.Text, WritersTestUtil.ReadAllUtf8(written));
        byte[] bytes = File.ReadAllBytes(written);
        Assert.Equal(probe.ByteLength, bytes.Length);
        Assert.False(probe.Bom);
        Assert.False(probe.EndsWithNewline,
            "Sidecar JSON intentionally has no trailing newline (matches JSON.stringify + fs.writeFileSync).");
    }
}
