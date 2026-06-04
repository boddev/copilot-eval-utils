using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class TextChunkerTests
{
    [Fact]
    public void Chunk_SkipsEmptyAndWhitespaceOnlyParagraphs()
    {
        var rows = TextChunker.Chunk(new[] { "", "  \t  ", "hello" });
        Assert.Single(rows);
        Assert.Equal("hello", rows[0]["content"]);
    }

    [Fact]
    public void Chunk_GreedilyPacksUntilExceedingTarget()
    {
        string p = new string('x', 200);
        var rows = TextChunker.Chunk(new[] { p, p, p, p });
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["chunk_number"]);
        Assert.Equal(2, rows[1]["chunk_number"]);
    }

    [Fact]
    public void Chunk_OversizedSingleParagraph_EmittedAsOwnChunk()
    {
        string big = new string('y', 1000);
        var rows = TextChunker.Chunk(new[] { big });
        Assert.Single(rows);
        Assert.Equal(big, rows[0]["content"]);
    }

    [Fact]
    public void Chunk_WordCount_MatchesJsSplitWhitespace()
    {
        var rows = TextChunker.Chunk(new[] { "one  two\tthree\n\nfour" });
        Assert.Equal(4, rows[0]["word_count"]);
    }

    [Fact]
    public void Chunk_ChunkNumber_OneBasedAndContiguous()
    {
        string p = new string('z', 300);
        var rows = TextChunker.Chunk(new[] { p, p, p, p, p });
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i + 1, rows[i]["chunk_number"]);
        }
    }

    [Fact]
    public void Chunk_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TextChunker.Chunk(Array.Empty<string>()));
    }

    [Fact]
    public void Chunk_WordCount_BomCountsAsWhitespace_MatchingJs()
    {
        // Verified divergence (round 5): U+FEFF (BOM) is whitespace in
        // JS regex \s but a word character in .NET's default \s. The
        // explicit JS-equivalent class fixes this so "a\uFEFFb" → 2 words
        // (matching JS) rather than 1 (the .NET default).
        var rows = TextChunker.Chunk(new[] { "a\uFEFFb" });
        Assert.Equal(2, rows[0]["word_count"]);
    }

    [Fact]
    public void Chunk_WordCount_NelCountsAsWordChar_MatchingJs()
    {
        // Mirror of the BOM case: U+0085 (NEL) is whitespace in .NET's
        // default \s but a word character in JS \s. The explicit class
        // excludes NEL so "a\u0085b" → 1 word (matching JS, where the
        // two letters glue together) rather than 2 (the .NET default).
        var rows = TextChunker.Chunk(new[] { "a\u0085b" });
        Assert.Equal(1, rows[0]["word_count"]);
    }
}
