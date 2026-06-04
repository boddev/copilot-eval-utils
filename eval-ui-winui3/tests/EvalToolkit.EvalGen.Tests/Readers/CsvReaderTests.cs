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
    public void Read_BomIsRetainedOnFirstHeader_MatchingCsvParse()
    {
        // Verified ground truth (round 5):
        //   parse('\uFEFFid,name\n1,foo', {columns:true, trim:true,
        //         skip_empty_lines:true})
        //   → [{ "\uFEFFid": "1", "name": "foo" }]
        // csv-parse does NOT strip the BOM; the first header keeps it
        // attached. The previous reader scrubbed it, which broke parity
        // for every Excel "CSV UTF-8" file.
        string path = WriteCsvWithBom("bom.csv", "id,name\n1,first\n");
        var result = new CsvReader().Read(path);
        Assert.Equal("\uFEFFid", result.Records[0].Entries[0].Key);
        Assert.Equal("name", result.Records[0].Entries[1].Key);
        Assert.Equal("1", result.Records[0]["\uFEFFid"]);
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
    public void Read_DuplicateHeaders_CollapseAndLastValueWins()
    {
        // Verified ground truth (round 5):
        //   parse('foo,foo,foo\na,b,c', {columns:true,...}) → [{"foo":"c"}]
        // csv-parse collapses duplicate header names into a single key
        // at the first occurrence's position; the surviving value is
        // the rightmost column's value.
        string path = WriteCsv("dup.csv", "foo,foo,foo\na,b,c\n");
        var result = new CsvReader().Read(path);
        Assert.Single(result.Records[0].Entries);
        Assert.Equal("foo", result.Records[0].Entries[0].Key);
        Assert.Equal("c", result.Records[0]["foo"]);
    }

    [Fact]
    public void Read_DuplicateHeaders_AbA_KeepsBothInFirstPositions()
    {
        // Verified ground truth: a,b,a / 1,2,3 → {a:"3", b:"2"} (two
        // keys, "a" at its FIRST position with the value of the THIRD
        // column).
        string path = WriteCsv("dup2.csv", "a,b,a\n1,2,3\n");
        var result = new CsvReader().Read(path);
        Assert.Equal(2, result.Records[0].Entries.Count);
        Assert.Equal(new[] { "a", "b" },
                     result.Records[0].Entries.Select(e => e.Key).ToArray());
        Assert.Equal("3", result.Records[0]["a"]);
        Assert.Equal("2", result.Records[0]["b"]);
    }

    [Fact]
    public void Read_EmptyHeaderName_IsPreservedAsEmptyStringKey()
    {
        // Verified ground truth: a,,b / 1,2,3 → {a:"1", "":"2", b:"3"}.
        // csv-parse keeps the empty string as a valid header name.
        string path = WriteCsv("emphdr.csv", "a,,b\n1,2,3\n");
        var result = new CsvReader().Read(path);
        Assert.Equal(3, result.Records[0].Entries.Count);
        Assert.Equal(new[] { "a", "", "b" },
                     result.Records[0].Entries.Select(e => e.Key).ToArray());
        Assert.Equal("1", result.Records[0]["a"]);
        Assert.Equal("2", result.Records[0][""]);
        Assert.Equal("3", result.Records[0]["b"]);
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

    [Fact]
    public void Read_RaggedRow_Short_Throws_MatchingCsvParse()
    {
        // Verified ground truth (round 6): csv-parse(columns:true) throws
        //   CSV_RECORD_INCONSISTENT_COLUMNS
        //   "Invalid Record Length: columns length is 3, got 2 on line 2"
        // when a data row has fewer fields than the header row. We
        // cannot silently null-pad without diverging from TS.
        string path = WriteCsv("ragged-short.csv", "a,b,c\n1,2\n");
        var ex = Assert.Throws<InvalidDataException>(
            () => new CsvReader().Read(path));
        Assert.Contains("Invalid Record Length", ex.Message);
        Assert.Contains("columns length is 3", ex.Message);
        Assert.Contains("got 2", ex.Message);
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Read_RaggedRow_Long_Throws_MatchingCsvParse()
    {
        // Mirror of the short case: a data row with MORE fields than the
        // header also throws — csv-parse refuses to silently truncate.
        string path = WriteCsv("ragged-long.csv", "a,b\n1,2,3\n");
        var ex = Assert.Throws<InvalidDataException>(
            () => new CsvReader().Read(path));
        Assert.Contains("Invalid Record Length", ex.Message);
        Assert.Contains("columns length is 2", ex.Message);
        Assert.Contains("got 3", ex.Message);
    }

    [Fact]
    public void Read_TrailingComma_IsRealEmptyTrailingCell_NotRagged()
    {
        // Verified ground truth: a row "1,2,\n" against header "a,b,c"
        // parses as {a:"1", b:"2", c:""} — the trailing comma yields a
        // genuine empty cell, not a missing one. This is the boundary
        // case for the ragged-row throw.
        string path = WriteCsv("trailing.csv", "a,b,c\n1,2,\n");
        var result = new CsvReader().Read(path);
        Assert.Single(result.Records);
        Assert.Equal("1", result.Records[0]["a"]);
        Assert.Equal("2", result.Records[0]["b"]);
        Assert.Equal("", result.Records[0]["c"]);
    }
}
