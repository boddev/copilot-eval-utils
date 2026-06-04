using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class TextFileReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public TextFileReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-txt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private string Write(string fileName, string content)
    {
        string path = Path.Combine(_tmpDir, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void Read_ShortText_ProducesSingleChunk()
    {
        string path = Write("a.txt", "hello world");
        var result = new TextFileReader().Read(path);
        Assert.Equal(InputFormat.Txt, result.Format);
        Assert.Single(result.Records);
        Assert.Equal(1, result.Records[0]["chunk_number"]);
        Assert.Equal("hello world", result.Records[0]["content"]);
        Assert.Equal(2, result.Records[0]["word_count"]);
    }

    [Fact]
    public void Read_EmptyFile_ThrowsWithTsMatchingMessage()
    {
        string path = Write("empty.txt", "");
        var ex = Assert.Throws<InvalidDataException>(() => new TextFileReader().Read(path));
        Assert.Contains("Text file is empty:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WhitespaceOnlyFile_AlsoEmptyError()
    {
        string path = Write("ws.txt", "   \n\n  \t  \n");
        Assert.Throws<InvalidDataException>(() => new TextFileReader().Read(path));
    }

    [Fact]
    public void Read_ParagraphsSeparatedByBlankLines_SplitToSections()
    {
        string path = Write("p.txt", "para one\n\npara two\n\npara three");
        var result = new TextFileReader().Read(path);
        Assert.Single(result.Records);
        string content = (string)result.Records[0]["content"]!;
        Assert.Contains("para one", content, StringComparison.Ordinal);
        Assert.Contains("para two", content, StringComparison.Ordinal);
        Assert.Contains("para three", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MarkdownHeadings_SplitOnHeadingBoundary()
    {
        string content = "intro paragraph\n# Heading One\nbody one\n## Heading Two\nbody two";
        string path = Write("md.md", content);
        var result = new TextFileReader().Read(path);
        Assert.Single(result.Records);
        string chunkContent = (string)result.Records[0]["content"]!;
        Assert.Contains("# Heading One", chunkContent, StringComparison.Ordinal);
        Assert.Contains("## Heading Two", chunkContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CrLfNormalized_SameAsLf()
    {
        string crlf = "para one\r\n\r\npara two";
        string path = Write("crlf.txt", crlf);
        var result = new TextFileReader().Read(path);
        Assert.Single(result.Records);
        string c = (string)result.Records[0]["content"]!;
        Assert.Contains("para one", c, StringComparison.Ordinal);
        Assert.Contains("para two", c, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_LongInput_SplitsAcrossMultipleChunks()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            if (i > 0) sb.Append("\n\n");
            sb.Append(new string('x', 200));
        }
        string path = Write("long.txt", sb.ToString());
        var result = new TextFileReader().Read(path);
        Assert.True(result.Records.Count >= 2);
        for (int i = 0; i < result.Records.Count; i++)
        {
            Assert.Equal(i + 1, result.Records[i]["chunk_number"]);
        }
    }

    [Fact]
    public void Read_MdFile_DetectedAsTxtFormat()
    {
        string path = Write("doc.md", "# Title\n\nBody");
        var result = new TextFileReader().Read(path);
        Assert.Equal(InputFormat.Txt, result.Format);
    }
}
