using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class DatasetReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public DatasetReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-ds-" + Guid.NewGuid().ToString("N"));
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

    private string Write(string relPath, string content)
    {
        string path = Path.Combine(_tmpDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ReadDatasetFile_SingleFile_StampsSourceFile()
    {
        string a = Write("a.csv", "id,name\n1,A\n2,B\n");
        var result = DatasetReader.ReadDatasetFile(a);
        Assert.Equal(InputFormat.Csv, result.Format);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("a.csv", result.Records[0][DatasetReader.SourceFileField]);
        Assert.Equal(new[] { "a.csv" }, result.SourceFiles.ToArray());
    }

    [Fact]
    public void ReadDatasetFile_CommaSeparated_MergesInOrder()
    {
        string a = Write("a.csv", "id,name\n1,A\n");
        string b = Write("b.csv", "id,name\n2,B\n");
        var result = DatasetReader.ReadDatasetFile($"{a},{b}");
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("a.csv", result.Records[0][DatasetReader.SourceFileField]);
        Assert.Equal("b.csv", result.Records[1][DatasetReader.SourceFileField]);
        Assert.Equal(new[] { "a.csv", "b.csv" }, result.SourceFiles.ToArray());
    }

    [Fact]
    public void ReadDatasetFile_Directory_DiscoversAndSortsOrdinally()
    {
        Write("dir/c.csv", "id,name\n3,C\n");
        Write("dir/a.csv", "id,name\n1,A\n");
        Write("dir/b.csv", "id,name\n2,B\n");
        var result = DatasetReader.ReadDatasetFile(Path.Combine(_tmpDir, "dir"));
        Assert.Equal(new[] { "a.csv", "b.csv", "c.csv" }, result.SourceFiles.ToArray());
    }

    [Fact]
    public void ReadDatasetFile_Directory_RecursiveByDefault()
    {
        Write("d/top.csv", "id\n1\n");
        Write("d/sub/nested.csv", "id\n2\n");
        var result = DatasetReader.ReadDatasetFile(Path.Combine(_tmpDir, "d"));
        Assert.Equal(2, result.SourceFiles.Count);
    }

    [Fact]
    public void ReadDatasetFile_Directory_NonRecursive_OnlyTopLevel()
    {
        Write("d/top.csv", "id\n1\n");
        Write("d/sub/nested.csv", "id\n2\n");
        var result = DatasetReader.ReadDatasetFile(
            Path.Combine(_tmpDir, "d"),
            new ReadDatasetOptions { Recursive = false });
        Assert.Equal(new[] { "top.csv" }, result.SourceFiles.ToArray());
    }

    [Fact]
    public void ReadDatasetFile_Directory_ExtensionAllowList_FiltersOthers()
    {
        Write("d/data.csv", "id\n1\n");
        Write("d/data.json", "[{\"id\":2}]");
        Write("d/data.md", "hello");
        var result = DatasetReader.ReadDatasetFile(
            Path.Combine(_tmpDir, "d"),
            new ReadDatasetOptions { Extensions = new[] { "json" } });
        Assert.Equal(new[] { "data.json" }, result.SourceFiles.ToArray());
    }

    [Fact]
    public void ReadDatasetFile_Directory_ExtensionAllowList_HonorsDotPrefix()
    {
        Write("d/data.csv", "id\n1\n");
        Write("d/data.json", "[{\"id\":2}]");
        var result = DatasetReader.ReadDatasetFile(
            Path.Combine(_tmpDir, "d"),
            new ReadDatasetOptions { Extensions = new[] { ".csv" } });
        Assert.Equal(new[] { "data.csv" }, result.SourceFiles.ToArray());
    }

    [Fact]
    public void ReadDatasetFile_MissingPath_ThrowsFileNotFound()
    {
        string bad = Path.Combine(_tmpDir, "does-not-exist.csv");
        Assert.Throws<FileNotFoundException>(() => DatasetReader.ReadDatasetFile(bad));
    }

    [Fact]
    public void ReadDatasetFile_EmptyDirectory_ThrowsNoSupportedFilesError()
    {
        string emptyDir = Path.Combine(_tmpDir, "edir");
        Directory.CreateDirectory(emptyDir);
        var ex = Assert.Throws<InvalidOperationException>(() => DatasetReader.ReadDatasetFile(emptyDir));
        Assert.Contains("No supported files found in directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDatasetFile_LastFormatWins()
    {
        string a = Write("first.csv", "id\n1\n");
        string b = Write("second.jsonl", "{\"id\":2}\n");
        var result = DatasetReader.ReadDatasetFile($"{a},{b}");
        Assert.Equal(InputFormat.Jsonl, result.Format);
    }

    [Fact]
    public void ReadDatasetFile_UnsupportedFormat_ThrowsNotSupported()
    {
        string path = Write("data.xml", "<root/>");
        Assert.Throws<NotSupportedException>(() => DatasetReader.ReadDatasetFile(path));
    }

    [Fact]
    public void ReadDatasetFile_Slice3PlusFormat_ThrowsClearMessage()
    {
        // Slice 2 ported .xlsx; .docx/.pdf/.pptx remain deferred to
        // slice 3. Update this test when those slices land.
        string path = Write("data.docx", "");
        var ex = Assert.Throws<NotSupportedException>(() => DatasetReader.ReadDatasetFile(path));
        Assert.Contains("not yet ported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDatasetFile_XlsxFile_DispatchesToXlsxReader()
    {
        // Slice-2 dispatch coverage: an .xlsx path goes through
        // DatasetReader → XlsxReader → ReadResult with the same
        // _source_file stamping the other readers do.
        string path = Path.Combine(_tmpDir, "book.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "id";
            ws.Cell(1, 2).Value = "name";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = "A";
            wb.SaveAs(path);
        }
        var result = DatasetReader.ReadDatasetFile(path);
        Assert.Equal(InputFormat.Xlsx, result.Format);
        Assert.Single(result.Records);
        Assert.Equal(1L, result.Records[0]["id"]);
        Assert.Equal("A", result.Records[0]["name"]);
        Assert.Equal("book.xlsx", result.Records[0][DatasetReader.SourceFileField]);
    }
}
