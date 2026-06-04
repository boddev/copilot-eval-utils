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
}
