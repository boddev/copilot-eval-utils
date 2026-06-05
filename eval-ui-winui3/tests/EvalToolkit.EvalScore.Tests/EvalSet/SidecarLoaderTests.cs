using EvalToolkit.EvalScore.EvalSet;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.EvalSet;

public class SidecarLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public SidecarLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sidecar-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    private static EvalRow Row(string prompt) => new()
    {
        Prompt = prompt,
        ExpectedAnswer = "e",
        SourceLocation = "s",
    };

    [Fact]
    public void Missing_file_throws_FileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(
            () => SidecarLoader.LoadAssertions(new[] { Row("p") }.ToList(), Path.Combine(_tempDir, "missing.json")));
    }

    [Fact]
    public void Invalid_json_throws_InvalidOperationException()
    {
        string p = Write("bad.json", "not json");
        Assert.Throws<InvalidOperationException>(
            () => SidecarLoader.LoadAssertions(new[] { Row("p") }.ToList(), p));
    }

    [Fact]
    public void Missing_items_throws()
    {
        string p = Write("empty.json", "{}");
        Assert.Throws<InvalidOperationException>(
            () => SidecarLoader.LoadAssertions(new[] { Row("p") }.ToList(), p));
    }

    [Fact]
    public void Matches_assertions_to_rows_by_normalized_prompt()
    {
        string p = Write("sidecar.json", """
        {
          "items": [
            {
              "prompt": "  What is X?  ",
              "assertions": [{"type": "must_contain", "value": "Y"}]
            },
            {
              "prompt": "WHAT IS Z?",
              "assertions": [{"type": "must_not_contain", "value": "W"}]
            }
          ]
        }
        """);
        var rows = new[] { Row("what is x?"), Row("what is z?"), Row("orphan") }.ToList();
        SidecarLoader.LoadAssertions(rows, p);
        Assert.NotNull(rows[0].Assertions);
        Assert.Single(rows[0].Assertions!);
        Assert.Equal(AssertionType.MustContain, rows[0].Assertions![0].Type);
        Assert.NotNull(rows[1].Assertions);
        Assert.Equal(AssertionType.MustNotContain, rows[1].Assertions![0].Type);
        Assert.Null(rows[2].Assertions);
    }

    [Fact]
    public void Items_with_empty_prompt_or_no_assertions_are_skipped()
    {
        string p = Write("sparse.json", """
        {
          "items": [
            {"prompt": "", "assertions": [{"type": "must_contain", "value": "Y"}]},
            {"prompt": "skip me", "assertions": []},
            {"prompt": "match", "assertions": [{"type": "must_contain", "value": "ok"}]}
          ]
        }
        """);
        var rows = new[] { Row("match"), Row("skip me") }.ToList();
        SidecarLoader.LoadAssertions(rows, p);
        Assert.NotNull(rows[0].Assertions);
        Assert.Null(rows[1].Assertions);
    }
}
