using EvalToolkit.Core;
using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.EvalGen.Tests.Writers;

/// <summary>
/// Byte-exact parity tests for <see cref="M365MultiPromptWriter"/>
/// driven by the <c>m365-*</c> scenarios in the writers probe. Covers:
/// chunking (full + partial), <c>promptsPerThread</c> clamping
/// (negative, fractional, &lt;2, &gt;20), the deterministic SHA-256
/// thread-id (order-sensitive, id-or-prompt|source_location fallback,
/// 12 lowercase hex chars), context-line construction (empty supporting
/// facts AND empty source location → field omitted), categories
/// joining (dedupe + "mixed categories" fallback), and the metadata
/// block's field order.
/// </summary>
public sealed class M365MultiPromptWriterTests
{
    private static M365MultiPromptWriter NewWriter() =>
        new(new FixedClock(WritersTestFixtures.PinnedNow));

    [Fact]
    public void TwoItems_Ppt2_WithModel_MatchesProbeExactly()
    {
        var items = new[] { WritersTestFixtures.BaseItem(), WritersTestFixtures.SecondItem() };
        AssertMatches("m365-two-items", items, "Test description", "suppliers.csv", "m365-two.json",
            new M365MultiPromptOptions { PromptsPerThread = 2, Model = "test-model" });
    }

    [Fact]
    public void OneItem_Ppt2_ProducesOneTurnGroup()
    {
        var items = new[] { WritersTestFixtures.BaseItem() };
        AssertMatches("m365-one-item-ppt2", items, "desc", "src.csv", "m365-one.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void ThreeItems_Ppt2_ChunksAs_2Plus1()
    {
        var items = new[]
        {
            WritersTestFixtures.BaseItem(),
            WritersTestFixtures.SecondItem(),
            WritersTestFixtures.RichItem(),
        };
        AssertMatches("m365-three-items-ppt2", items, "desc", "src.csv", "m365-three.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void FiveItems_Ppt3_ChunksAs_3Plus2()
    {
        var items = new[]
        {
            WritersTestFixtures.BaseItem(),
            WritersTestFixtures.SecondItem(),
            WritersTestFixtures.RichItem(),
            WritersTestFixtures.BaseItem() with { Id = "item-4" },
            WritersTestFixtures.BaseItem() with { Id = "item-5" },
        };
        AssertMatches("m365-five-items-ppt3", items, "desc", "src.csv", "m365-five-ppt3.json",
            new M365MultiPromptOptions { PromptsPerThread = 3 });
    }

    [Theory]
    [InlineData("m365-ppt-1-clamp-to-2",  1)]
    [InlineData("m365-ppt-0-clamp-to-2",  0)]
    [InlineData("m365-ppt-neg-clamp-to-2", -5)]
    [InlineData("m365-ppt-21-clamp-to-20", 21)]
    public void PromptsPerThread_IntegerClamping_MatchesProbe(string scenario, int ppt)
    {
        var items = new[] { WritersTestFixtures.BaseItem(), WritersTestFixtures.SecondItem() };
        AssertMatches(scenario, items, "desc", "src.csv", scenario + ".json",
            new M365MultiPromptOptions { PromptsPerThread = ppt });
    }

    // Fractional clamping (2.7→2, 3.5→3) is covered by ClampPromptsPerThread
    // unit tests + the same probe-driven flow but uses doubles. Round-tripping
    // 2.7/3.5 through `int` would discard the fraction before the writer sees
    // it; the writer exposes ClampPromptsPerThread as internal so we test it
    // directly here:
    [Theory]
    [InlineData(2.7, 2)]
    [InlineData(3.5, 3)]
    [InlineData(1.0, 2)]    // clamped up to minimum
    [InlineData(0.5, 2)]    // truncated to 0, clamped to 2
    [InlineData(-5.0, 2)]   // clamped to minimum
    [InlineData(21.0, 20)]  // clamped to maximum
    [InlineData(20.9, 20)]  // truncated to 20
    [InlineData(2.0, 2)]
    [InlineData(20.0, 20)]
    public void ClampPromptsPerThread_MatchesJsBehavior(double raw, int expected)
    {
        Assert.Equal(expected, M365MultiPromptWriter.ClampPromptsPerThread(raw));
    }

    [Fact]
    public void HashStability_SameItems_ProducesSameThreadId()
    {
        var probe1 = WritersProbeData.Get("m365-hash-stable-1");
        var probe2 = WritersProbeData.Get("m365-hash-stable-2");
        // Different files but identical content (the probe wrote them
        // to two different paths to prove the hash is content-derived).
        Assert.Equal(probe1.Text, probe2.Text);

        var items = new[] { WritersTestFixtures.BaseItem(), WritersTestFixtures.SecondItem() };
        AssertMatches("m365-hash-stable-1", items, "desc", "src.csv", "m365-hash-stable-1.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
        AssertMatches("m365-hash-stable-2", items, "desc", "src.csv", "m365-hash-stable-2.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void HashOrder_SwappingItems_ChangesHash()
    {
        // The probe captured both orderings; the texts MUST differ (the
        // thread_id is derived from the joined ids in encounter order).
        var probeAB = WritersProbeData.Get("m365-hash-order-AB");
        var probeBA = WritersProbeData.Get("m365-hash-order-BA");
        Assert.NotEqual(probeAB.Text, probeBA.Text);

        var itemsAB = new[] { WritersTestFixtures.BaseItem(), WritersTestFixtures.SecondItem() };
        var itemsBA = new[] { WritersTestFixtures.SecondItem(), WritersTestFixtures.BaseItem() };
        AssertMatches("m365-hash-order-AB", itemsAB, "desc", "src.csv", "m365-hash-order-AB.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
        AssertMatches("m365-hash-order-BA", itemsBA, "desc", "src.csv", "m365-hash-order-BA.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void Hash_EmptyId_FallsBackToPromptPipeSourceLocation()
    {
        var items = new[]
        {
            WritersTestFixtures.BaseItem() with { Id = "" },
            WritersTestFixtures.SecondItem() with { Id = "" },
        };
        AssertMatches("m365-hash-fallback-empty-id", items, "desc", "src.csv",
            "m365-hash-fallback.json", new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void Context_BothSourceLocationAndSupportingFactsEmpty_FieldOmitted()
    {
        // Pin: empty string source_location is FALSY (JS `?` test);
        // empty supporting_facts means no lines; result → no context
        // field at all (not an empty string).
        var items = new[]
        {
            WritersTestFixtures.BaseItem() with { SourceLocation = "", SupportingFacts = [] },
        };
        AssertMatches("m365-context-omitted", items, "desc", "src.csv", "m365-context-omitted.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void ReferencedRows_IncludedInTurnExtension()
    {
        var items = new[] { WritersTestFixtures.BaseItem() with { ReferencedRows = ["r1", "r2"] } };
        AssertMatches("m365-referenced-rows", items, "desc", "src.csv", "m365-referenced-rows.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void Categories_Mixed_AreDedupedAndCommaJoined()
    {
        var items = new[]
        {
            WritersTestFixtures.BaseItem() with { Category = QuestionCategory.SingleRecordLookup },
            // The TS fixture's "aggregation" maps to AttributeRetrieval
            // in the C# wire mapping — but the JS fixture used the
            // literal "aggregation" string which isn't in the enum.
            // Re-derive the expected text from the actual enum value
            // to keep the test honest:
            WritersTestFixtures.BaseItem() with { Id = "item-b", Category = QuestionCategory.AttributeRetrieval },
        };

        // The probe captured "single_record_lookup, aggregation" but
        // the C# enum has no "aggregation" — so we DO NOT byte-compare
        // this scenario directly. Instead pin the behavioral contract:
        // category de-duplication and comma joining.
        string outPath = WritersTestUtil.TempPath("m365-mixed-categories.json");
        string written = NewWriter().Write(items, "desc", "src.csv", outPath,
            new M365MultiPromptOptions { PromptsPerThread = 2 });
        string text = WritersTestUtil.ReadAllUtf8(written);
        Assert.Contains(
            "Synthetic multi-prompt evaluator group (single_record_lookup, attribute_retrieval).",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void Categories_Same_Twice_DedupesToOne()
    {
        var items = new[]
        {
            WritersTestFixtures.BaseItem() with { Category = QuestionCategory.SingleRecordLookup },
            WritersTestFixtures.BaseItem() with { Id = "item-b", Category = QuestionCategory.SingleRecordLookup },
        };
        AssertMatches("m365-same-category", items, "desc", "src.csv", "m365-same-category.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    [Fact]
    public void Minimal_NoWarningsNoModel_DefaultsAreEmittedAsExpected()
    {
        var items = new[] { WritersTestFixtures.BaseItem(), WritersTestFixtures.SecondItem() };
        AssertMatches("m365-no-warnings-no-model", items, "desc", "src.csv", "m365-minimal.json",
            new M365MultiPromptOptions { PromptsPerThread = 2 });
    }

    private static void AssertMatches(
        string scenarioName,
        IReadOnlyList<GeneratedEvalItem> items,
        string description,
        string sourceFile,
        string outputFileName,
        M365MultiPromptOptions options)
    {
        string outPath = WritersTestUtil.TempPath(outputFileName);
        string written = NewWriter().Write(items, description, sourceFile, outPath, options);

        var probe = WritersProbeData.Get(scenarioName);
        Assert.Equal(probe.Text, WritersTestUtil.ReadAllUtf8(written));
        byte[] bytes = File.ReadAllBytes(written);
        Assert.Equal(probe.ByteLength, bytes.Length);
        Assert.False(probe.Bom);
    }
}
