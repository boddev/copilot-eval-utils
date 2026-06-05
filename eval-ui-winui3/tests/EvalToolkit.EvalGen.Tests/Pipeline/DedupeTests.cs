using System.Text;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class DedupeTests : IDisposable
{
    private readonly string _tmpDir;

    public DedupeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-dedupe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("Hello, World!", "hello world")]
    [InlineData("  Lots   of    spaces  ", "lots of spaces")]
    [InlineData("UPPER123case", "upper123case")]
    [InlineData("!@#$%^&*()", "")]
    [InlineData("", "")]
    public void NormalizePrompt_NormalizesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, Dedupe.NormalizePrompt(input));
    }

    [Fact]
    public void IsNearDuplicatePrompt_ExactMatch_True()
    {
        Assert.True(Dedupe.IsNearDuplicatePrompt("Hello world", "hello world!"));
    }

    [Fact]
    public void IsNearDuplicatePrompt_ContainmentOver80Percent_True()
    {
        var a = "what is the capital of france";
        var b = "what is the capital of france please";
        Assert.True(Dedupe.IsNearDuplicatePrompt(a, b));
    }

    [Fact]
    public void IsNearDuplicatePrompt_NoContainment_False()
    {
        Assert.False(Dedupe.IsNearDuplicatePrompt("apples are red", "bananas are yellow"));
    }

    [Fact]
    public void IsNearDuplicatePrompt_ContainmentBelowThreshold_False()
    {
        var a = "what";
        var b = "what is the capital of france and what other facts";
        Assert.False(Dedupe.IsNearDuplicatePrompt(a, b));
    }

    [Fact]
    public void IsNearDuplicatePrompt_EmptyInputs_False()
    {
        Assert.False(Dedupe.IsNearDuplicatePrompt("", "anything"));
        Assert.False(Dedupe.IsNearDuplicatePrompt("anything", ""));
    }

    [Fact]
    public void NormalizeAssertion_MustContain_IncludesWholeWordFlag()
    {
        var a = Dedupe.NormalizeAssertion(new MustContainAssertion { Value = "Alice", WholeWord = true });
        Assert.Equal("must_contain:alice:whole", a);
        var b = Dedupe.NormalizeAssertion(new MustContainAssertion { Value = "Alice", WholeWord = false });
        Assert.Equal("must_contain:alice:partial", b);
    }

    [Fact]
    public void NormalizeAssertion_MustContainAny_SortsValues()
    {
        var a = Dedupe.NormalizeAssertion(new MustContainAnyAssertion { Values = new[] { "Beta", "Alpha", "Gamma" } });
        Assert.Equal("must_contain_any:alpha|beta|gamma", a);
    }

    [Fact]
    public void AssertionSignature_OrderIndependent()
    {
        var a1 = new MustContainAssertion { Value = "x" };
        var a2 = new MustNotContainAssertion { Value = "y" };
        var s1 = Dedupe.AssertionSignature(new Assertion[] { a1, a2 });
        var s2 = Dedupe.AssertionSignature(new Assertion[] { a2, a1 });
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void LoadAvoidanceSet_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Same(Dedupe.AvoidanceSet.Empty, Dedupe.LoadAvoidanceSet(null));
        Assert.Same(Dedupe.AvoidanceSet.Empty, Dedupe.LoadAvoidanceSet(Array.Empty<string>()));
    }

    [Fact]
    public void LoadAvoidanceSet_PathNotFound_Throws()
    {
        var bogus = Path.Combine(_tmpDir, "missing.evalgen.json");
        Assert.Throws<InvalidOperationException>(() =>
            Dedupe.LoadAvoidanceSet(new[] { bogus }));
    }

    [Fact]
    public void LoadAvoidanceSet_ReadsValidSidecar()
    {
        var json = """
        {
          "source_file": "src.csv",
          "items": [
            {
              "prompt": "What is X?",
              "source_location": "src.csv:row 1",
              "assertions": [{ "type": "must_contain", "value": "X", "wholeWord": false }]
            }
          ]
        }
        """;
        var path = Path.Combine(_tmpDir, "prior.evalgen.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        var set = Dedupe.LoadAvoidanceSet(new[] { path });
        Assert.Single(set.Files);
        Assert.Empty(set.Warnings);
    }

    [Fact]
    public void LoadAvoidanceSet_InvalidJson_AddsWarning()
    {
        var path = Path.Combine(_tmpDir, "bad.evalgen.json");
        File.WriteAllText(path, "{ not json");
        var set = Dedupe.LoadAvoidanceSet(new[] { path });
        Assert.Empty(set.Files);
        Assert.Single(set.Warnings);
    }

    [Fact]
    public void LoadAvoidanceSet_MissingItemsArray_AddsWarning()
    {
        var path = Path.Combine(_tmpDir, "noitems.evalgen.json");
        File.WriteAllText(path, "{}");
        var set = Dedupe.LoadAvoidanceSet(new[] { path });
        Assert.Single(set.Warnings);
    }

    [Fact]
    public void LoadAvoidanceSet_DiscoversNestedSidecars()
    {
        var sub = Path.Combine(_tmpDir, "nested");
        Directory.CreateDirectory(sub);
        var p = Path.Combine(sub, "x.evalgen.json");
        File.WriteAllText(p, """{"items":[{"prompt":"q","source_location":"a:row 1","assertions":[]}]}""");
        var set = Dedupe.LoadAvoidanceSet(new[] { _tmpDir });
        Assert.Single(set.Files);
    }

    [Fact]
    public void LoadAvoidanceSet_RespectsExclude()
    {
        var p = Path.Combine(_tmpDir, "x.evalgen.json");
        File.WriteAllText(p, """{"items":[{"prompt":"q","source_location":"a:row 1","assertions":[]}]}""");
        var set = Dedupe.LoadAvoidanceSet(new[] { _tmpDir }, excludePaths: new[] { p });
        Assert.Empty(set.Files);
    }

    private static GeneratedEvalItem MakeItem(string prompt, string sourceLoc, params Assertion[] assertions) => new()
    {
        Id = "x",
        Prompt = prompt,
        ExpectedAnswer = "ans",
        SourceLocation = sourceLoc,
        Assertions = assertions,
        Category = QuestionCategory.SingleRecordLookup,
        Difficulty = Difficulty.Easy,
        SupportingFacts = Array.Empty<string>(),
        GroundingConfidence = GroundingConfidence.High,
    };

    [Fact]
    public void FilterAgainstAvoidance_RemovesExactPromptDuplicates()
    {
        var json = """{"source_file":"src.csv","items":[{"prompt":"What is X?","source_location":"src.csv:row 1","assertions":[]}]}""";
        var path = Path.Combine(_tmpDir, "prior.evalgen.json");
        File.WriteAllText(path, json);
        var avoid = Dedupe.LoadAvoidanceSet(new[] { path });

        var items = new[] { MakeItem("What is X?", "src.csv:row 5") };
        var result = Dedupe.FilterAgainstAvoidance(items, avoid, "src.csv");
        Assert.Empty(result.Items);
        Assert.Equal(1, result.DuplicatePromptCount);
        Assert.Equal(1, result.RemovedCount);
    }

    [Fact]
    public void FilterAgainstAvoidance_RemovesSourceLocationDuplicates()
    {
        var json = """{"source_file":"src.csv","items":[{"prompt":"prior q","source_location":"src.csv:row 1","assertions":[]}]}""";
        var path = Path.Combine(_tmpDir, "prior.evalgen.json");
        File.WriteAllText(path, json);
        var avoid = Dedupe.LoadAvoidanceSet(new[] { path });

        var items = new[] { MakeItem("totally different", "src.csv:row 1") };
        var result = Dedupe.FilterAgainstAvoidance(items, avoid, "src.csv");
        Assert.Empty(result.Items);
        Assert.Equal(1, result.DuplicateSourceLocationCount);
    }

    [Fact]
    public void FilterAgainstAvoidance_KeepsAssertionOverlap_AddsWarning()
    {
        var json = """{"source_file":"src.csv","items":[{"prompt":"prior q","source_location":"src.csv:row 99","assertions":[{"type":"must_contain","value":"X"}]}]}""";
        var path = Path.Combine(_tmpDir, "prior.evalgen.json");
        File.WriteAllText(path, json);
        var avoid = Dedupe.LoadAvoidanceSet(new[] { path });

        var items = new[] { MakeItem("new q", "src.csv:row 1", new MustContainAssertion { Value = "X" }) };
        var result = Dedupe.FilterAgainstAvoidance(items, avoid, "src.csv");
        Assert.Single(result.Items);
        Assert.Equal(1, result.AssertionOverlapCount);
        Assert.Contains(result.Warnings, w => w.Contains("assertion signature"));
    }

    [Fact]
    public void FilterAgainstAvoidance_IgnoresDifferentSourceFile()
    {
        var json = """{"source_file":"other.csv","items":[{"prompt":"What is X?","source_location":"other.csv:row 1","assertions":[]}]}""";
        var path = Path.Combine(_tmpDir, "prior.evalgen.json");
        File.WriteAllText(path, json);
        var avoid = Dedupe.LoadAvoidanceSet(new[] { path });

        var items = new[] { MakeItem("What is X?", "src.csv:row 5") };
        var result = Dedupe.FilterAgainstAvoidance(items, avoid, "src.csv");
        Assert.Single(result.Items);
    }
}
