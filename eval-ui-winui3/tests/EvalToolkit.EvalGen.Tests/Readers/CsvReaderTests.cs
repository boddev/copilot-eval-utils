using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class CsvReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public CsvReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-csv-" + Guid.NewGuid().ToString("N"));
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

    private string WriteCsv(string fileName, string content)
    {
        string path = Path.Combine(_tmpDir, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    private string WriteCsvWithBom(string fileName, string content)
    {
        string path = Path.Combine(_tmpDir, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(true));
        return path;
    }

    [Fact]
    public void Read_BasicCsv_ReturnsHeaderKeyedRecords()
    {
        string path = WriteCsv("basic.csv", "id,name,age\n1,Alice,30\n2,Bob,25\n");
        var result = new CsvReader().Read(path);

        Assert.Equal(InputFormat.Csv, result.Format);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("1", result.Records[0]["id"]);
        Assert.Equal("Alice", result.Records[0]["name"]);
        Assert.Equal("30", result.Records[0]["age"]);
        Assert.Equal("Bob", result.Records[1]["name"]);
    }

    [Fact]
    public void Read_NumericCells_StayStringTyped()
    {
        string path = WriteCsv("nums.csv", "x,y\n42,3.14\n");
        var result = new CsvReader().Read(path);
        Assert.IsType<string>(result.Records[0]["x"]);
        Assert.IsType<string>(result.Records[0]["y"]);
        Assert.Equal("42", result.Records[0]["x"]);
        Assert.Equal("3.14", result.Records[0]["y"]);
    }

    [Fact]
    public void Read_TrimsWhitespaceAroundCells()
    {
        string path = WriteCsv("trim.csv", "a,b\n  hello  ,  world  \n");
        var result = new CsvReader().Read(path);
        Assert.Equal("hello", result.Records[0]["a"]);
        Assert.Equal("world", result.Records[0]["b"]);
    }

    [Fact]
    public void Read_SkipsBlankLines()
    {
        string path = WriteCsv("blanks.csv", "x\n1\n\n\n2\n");
        var result = new CsvReader().Read(path);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("1", result.Records[0]["x"]);
        Assert.Equal("2", result.Records[1]["x"]);
    }

    [Fact]
    public void Read_StripsUtf8Bom()
    {
        string path = WriteCsvWithBom("bom.csv", "id,name\n1,first\n");
        var result = new CsvReader().Read(path);
        Assert.Equal("id", result.Records[0].Entries[0].Key);
    }

    [Fact]
    public void Read_QuotedFieldsWithEmbeddedDelimitersAndNewlines()
    {
        string path = WriteCsv("quoted.csv", "id,note\n1,\"hello, world\"\n2,\"line1\nline2\"\n");
        var result = new CsvReader().Read(path);
        Assert.Equal("hello, world", result.Records[0]["note"]);
        Assert.Equal("line1\nline2", result.Records[1]["note"]);
    }

    [Fact]
    public void Read_EscapedDoubleQuotesInQuotedField()
    {
        string path = WriteCsv("escq.csv", "id,note\n1,\"she said \"\"hi\"\"\"\n");
        var result = new CsvReader().Read(path);
        Assert.Equal("she said \"hi\"", result.Records[0]["note"]);
    }

    [Fact]
    public void Read_DuplicateHeadersGetUnderscoreSuffix()
    {
        string path = WriteCsv("dup.csv", "foo,foo,foo\na,b,c\n");
        var result = new CsvReader().Read(path);
        Assert.Equal(new[] { "foo", "foo_1", "foo_2" },
                     result.Records[0].Entries.Select(e => e.Key).ToArray());
        Assert.Equal("a", result.Records[0]["foo"]);
        Assert.Equal("b", result.Records[0]["foo_1"]);
        Assert.Equal("c", result.Records[0]["foo_2"]);
    }

    [Fact]
    public void Read_TsvFile_UsesTabDelimiter()
    {
        string path = WriteCsv("data.tsv", "id\tname\n1\tAlice\n2\tBob\n");
        var result = new CsvReader().Read(path);
        Assert.Equal(InputFormat.Csv, result.Format);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("Alice", result.Records[0]["name"]);
    }

    [Fact]
    public void Read_PreservesFieldInsertionOrder()
    {
        string path = WriteCsv("order.csv", "c,a,b\n1,2,3\n");
        string[] keys = new CsvReader().Read(path).Records[0].Entries
            .Select(e => e.Key).ToArray();
        Assert.Equal(new[] { "c", "a", "b" }, keys);
    }

    [Fact]
    public void Read_EmptyFile_ReturnsNoRecords()
    {
        string path = WriteCsv("empty.csv", "");
        var result = new CsvReader().Read(path);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Read_HeaderOnlyFile_ReturnsNoRecords()
    {
        string path = WriteCsv("hdr.csv", "id,name\n");
        var result = new CsvReader().Read(path);
        Assert.Empty(result.Records);
    }
}
