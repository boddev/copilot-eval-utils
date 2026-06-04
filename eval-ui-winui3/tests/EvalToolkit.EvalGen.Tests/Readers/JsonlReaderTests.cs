using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class JsonlReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public JsonlReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-jsonl-" + Guid.NewGuid().ToString("N"));
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

    private string Write(string fileName, string content, bool bom = false)
    {
        string path = Path.Combine(_tmpDir, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(bom));
        return path;
    }

    [Fact]
    public void Read_OneObjectPerLine_ReturnsAllRecordsInOrder()
    {
        string path = Write("a.jsonl", "{\"id\":1}\n{\"id\":2}\n{\"id\":3}\n");
        var result = new JsonlReader().Read(path);
        Assert.Equal(InputFormat.Jsonl, result.Format);
        Assert.Equal(3, result.Records.Count);
        Assert.Equal(1L, result.Records[0]["id"]);
        Assert.Equal(2L, result.Records[1]["id"]);
        Assert.Equal(3L, result.Records[2]["id"]);
    }

    [Fact]
    public void Read_BlankAndWhitespaceLines_AreSkipped()
    {
        string path = Write("blanks.jsonl", "{\"id\":1}\n\n   \n{\"id\":2}\n");
        var result = new JsonlReader().Read(path);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Read_BomOnLine1_IsStripped()
    {
        string path = Write("bom.jsonl", "{\"id\":1}\n{\"id\":2}\n", bom: true);
        var result = new JsonlReader().Read(path);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(1L, result.Records[0]["id"]);
    }

    [Fact]
    public void Read_NoTrailingNewline_StillReadsFinalRecord()
    {
        string path = Write("trail.jsonl", "{\"id\":1}\n{\"id\":2}");
        var result = new JsonlReader().Read(path);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Read_InvalidLine_ThrowsWithFilePathAndLineNumber()
    {
        string path = Write("bad.jsonl", "{\"id\":1}\n{not json}\n{\"id\":3}\n");
        var ex = Assert.Throws<InvalidDataException>(() => new JsonlReader().Read(path));
        Assert.Contains("at line 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CrLfLineEndings_HandledCorrectly()
    {
        string path = Write("crlf.jsonl", "{\"id\":1}\r\n{\"id\":2}\r\n");
        var result = new JsonlReader().Read(path);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Read_EmptyFile_ReturnsNoRecords()
    {
        string path = Write("empty.jsonl", "");
        var result = new JsonlReader().Read(path);
        Assert.Empty(result.Records);
    }
}
