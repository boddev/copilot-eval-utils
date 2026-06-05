using EvalToolkit.EvalScore.EvalSet;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.EvalSet;

#pragma warning disable CA1711
[CollectionDefinition("EvalSetLoader", DisableParallelization = true)]
public class EvalSetLoaderCollection { }
#pragma warning restore CA1711

[Collection("EvalSetLoader")]
public class EvalSetLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Action<string>? _originalWarning;

    public EvalSetLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"evalset-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalWarning = EvalSetLoader.OnVersionWarning;
    }

    public void Dispose()
    {
        EvalSetLoader.OnVersionWarning = _originalWarning;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Missing_file_throws_FileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(
            () => EvalSetLoader.Load(Path.Combine(_tempDir, "missing.json")));
    }

    [Fact]
    public void Invalid_json_throws_InvalidOperationException()
    {
        string p = Write("bad.json", "{not json");
        var ex = Assert.Throws<InvalidOperationException>(() => EvalSetLoader.Load(p));
        Assert.Contains("Invalid JSON", ex.Message);
    }

    [Fact]
    public void Missing_items_array_throws_InvalidOperationException()
    {
        string p = Write("noitems.json", "{\"version\":\"1.0\"}");
        var ex = Assert.Throws<InvalidOperationException>(() => EvalSetLoader.Load(p));
        Assert.Contains("items", ex.Message);
    }

    [Fact]
    public void Loads_items_and_maps_fields()
    {
        string p = Write("set.json", """
        {
          "version": "1.0",
          "description": "Test set",
          "source_file": "doc.pdf",
          "generated_at": "2025-01-01T00:00:00Z",
          "items": [
            {
              "id": "q1",
              "prompt": "What is X?",
              "expected_answer": "X is Y",
              "source_location": "doc.pdf",
              "category": "factual",
              "difficulty": "easy",
              "grounding_confidence": "high",
              "assertions": [
                {"type": "must_contain", "value": "Y"}
              ]
            }
          ],
          "metadata": {
            "model": "gpt-4",
            "evalgen_version": "0.5.0"
          }
        }
        """);
        var result = EvalSetLoader.Load(p);
        Assert.Single(result.Rows);
        EvalRow row = result.Rows[0];
        // TS evalset-loader does NOT set row.id from item.id (it sets a
        // distinct (row as any)._id field that's never read again). Verify
        // C# matches: row.Id remains null for EvalSet-loaded rows.
        Assert.Null(row.Id);
        Assert.Equal("What is X?", row.Prompt);
        Assert.Equal("X is Y", row.ExpectedAnswer);
        Assert.Equal("doc.pdf", row.SourceLocation);
        Assert.Equal(string.Empty, row.ActualAnswer);
        Assert.Equal("factual", row.Category);
        Assert.Equal("easy", row.Difficulty);
        Assert.Equal("high", row.GroundingConfidence);
        Assert.NotNull(row.Assertions);
        Assert.Single(row.Assertions!);
        Assert.Equal(AssertionType.MustContain, row.Assertions![0].Type);
        Assert.Equal("Y", row.Assertions![0].Value);

        Assert.Equal("Test set", result.Metadata["description"]);
        Assert.Equal("doc.pdf", result.Metadata["source_file"]);
        Assert.Equal("gpt-4", result.Metadata["model"]);
        Assert.Equal("0.5.0", result.Metadata["evalgen_version"]);
    }

    [Fact]
    public void Version_mismatch_invokes_warning_sink()
    {
        var warnings = new List<string>();
        EvalSetLoader.OnVersionWarning = warnings.Add;
        string p = Write("v2.json", "{\"version\":\"2.0\",\"items\":[]}");
        EvalSetLoader.Load(p);
        Assert.Single(warnings);
        Assert.Contains("2.0", warnings[0]);
    }

    [Fact]
    public void Version_1_does_not_warn()
    {
        var warnings = new List<string>();
        EvalSetLoader.OnVersionWarning = warnings.Add;
        string p = Write("v1.json", "{\"version\":\"1.0\",\"items\":[]}");
        EvalSetLoader.Load(p);
        Assert.Empty(warnings);
    }
}
