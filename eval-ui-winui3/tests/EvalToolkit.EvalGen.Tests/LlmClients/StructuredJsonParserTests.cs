using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

public sealed class StructuredJsonParserTests
{
    public sealed record Bag(string Name, int Count);

    [Fact]
    public void Parse_DirectJson()
    {
        Bag result = StructuredJsonParser.Parse<Bag>("{\"name\":\"acme\",\"count\":3}");
        Assert.Equal("acme", result.Name);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Parse_StripsAnsiColorEscapes()
    {
        string content = "\u001b[31m{\"name\":\"red\",\"count\":1}\u001b[0m";
        Bag result = StructuredJsonParser.Parse<Bag>(content);
        Assert.Equal("red", result.Name);
    }

    [Fact]
    public void Parse_FencedJsonBlock()
    {
        string content = "Here is your answer:\n```json\n{\"name\":\"fenced\",\"count\":7}\n```\nThanks!";
        Bag result = StructuredJsonParser.Parse<Bag>(content);
        Assert.Equal("fenced", result.Name);
        Assert.Equal(7, result.Count);
    }

    [Fact]
    public void Parse_FencedBlockWithoutLangTag()
    {
        string content = "```\n{\"name\":\"plain\",\"count\":1}\n```";
        Bag result = StructuredJsonParser.Parse<Bag>(content);
        Assert.Equal("plain", result.Name);
    }

    [Fact]
    public void Parse_FencedBlockButInvalid_DoesNotFallBackToBraceExtraction()
    {
        // Per GPT-5.5 review: TS throws if fenced block is invalid, no fallback.
        string content = "```json\nthis is not json\n```\n{\"name\":\"outside\",\"count\":99}";
        Assert.ThrowsAny<Exception>(() => StructuredJsonParser.Parse<Bag>(content));
    }

    [Fact]
    public void Parse_BraceExtraction()
    {
        string content = "Sure! Here's the data: {\"name\":\"brace\",\"count\":42} — let me know if you need more.";
        Bag result = StructuredJsonParser.Parse<Bag>(content);
        Assert.Equal("brace", result.Name);
        Assert.Equal(42, result.Count);
    }

    [Fact]
    public void Parse_NoJsonAtAll_ThrowsTsError()
    {
        Exception ex = Assert.Throws<InvalidOperationException>(() =>
            StructuredJsonParser.Parse<Bag>("plain text answer with no braces"));
        Assert.Equal("LLM response did not contain a JSON object", ex.Message);
    }

    [Fact]
    public void Parse_NullContent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StructuredJsonParser.Parse<Bag>(null!));
    }
}
