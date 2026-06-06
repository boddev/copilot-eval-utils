using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Writers;

namespace EvalToolkit.EvalScore.Tests.Writers;

/// <summary>
/// Slice 26 — engine-level scored CSV writer used by both the CLI
/// (ScoreCommand) and the WinUI service (EvalScoreJobService).
/// Verifies parity with the Node TS reference
/// (<c>eval-score/node/src/writers/csv-writer.ts</c>): same 6-column
/// header order (with <c>metrics</c> as the last column,
/// JSON-serialized), RFC 4180 quoting for embedded commas / quotes /
/// newlines, the <c>{basename}-results.csv</c> naming convention with
/// <c>.evalgen</c> trimmed, and atomic writes (no <c>.tmp</c> leftovers
/// on overwrite).
/// </summary>
public class ResultsCsvWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ResultsCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"results-csv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static EvalRow Row(string prompt, string expected, string source, string actual, double? score)
    {
        var r = new EvalRow
        {
            Prompt = prompt,
            ExpectedAnswer = expected,
            SourceLocation = source,
            ActualAnswer = actual,
        };
        r.SimilarityScore = score;
        return r;
    }

    [Fact]
    public async Task WriteAsync_emits_six_column_header_matching_TS_reference()
    {
        var rows = new[]
        {
            Row("What?", "Yes.", "doc.md#L1", "Yes, indeed.", 92),
            Row("Why?", "Because.", "doc.md#L2", "Because reasons.", 55),
        };
        string sidecar = Path.Combine(_tempDir, "set1.evalgen.json");
        string outPath = await ResultsCsvWriter.WriteAsync(rows, _tempDir, sidecar, CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, "set1-results.csv"), outPath);
        string[] lines = await File.ReadAllLinesAsync(outPath);
        Assert.Equal(
            "prompt,expected_answer,source_location,actual_answer,similarity_score,metrics",
            lines[0]);
        // Two rows after the header; metrics column empty because none set.
        Assert.Equal("What?,Yes.,doc.md#L1,\"Yes, indeed.\",92,", lines[1]);
        Assert.Equal("Why?,Because.,doc.md#L2,Because reasons.,55,", lines[2]);
    }

    [Fact]
    public async Task WriteAsync_RFC4180_quotes_commas_quotes_and_newlines()
    {
        var rows = new[]
        {
            Row("Has, comma", "Has \"quote\"", "loc", "Line1\nLine2", null),
        };
        string sidecar = Path.Combine(_tempDir, "edge.evalgen.json");
        string outPath = await ResultsCsvWriter.WriteAsync(rows, _tempDir, sidecar, CancellationToken.None);

        string content = await File.ReadAllTextAsync(outPath);
        Assert.Contains("\"Has, comma\"", content);
        Assert.Contains("\"Has \"\"quote\"\"\"", content);
        Assert.Contains("\"Line1\nLine2\"", content);
    }

    [Fact]
    public async Task WriteAsync_trims_evalgen_suffix_from_basename()
    {
        var rows = new[] { Row("Q", "E", "S", "A", 100) };
        string sidecar = Path.Combine(_tempDir, "company-faq.evalgen.json");
        string outPath = await ResultsCsvWriter.WriteAsync(rows, _tempDir, sidecar, CancellationToken.None);
        Assert.EndsWith("company-faq-results.csv", outPath);
    }

    [Fact]
    public async Task WriteAsync_overwrites_existing_file_atomically()
    {
        string sidecar = Path.Combine(_tempDir, "set2.evalgen.json");
        string expectedOut = Path.Combine(_tempDir, "set2-results.csv");
        await File.WriteAllTextAsync(expectedOut, "stale content");

        var rows = new[] { Row("New", "Yes", "src", "ok", 80) };
        string outPath = await ResultsCsvWriter.WriteAsync(rows, _tempDir, sidecar, CancellationToken.None);
        Assert.Equal(expectedOut, outPath);

        string content = await File.ReadAllTextAsync(outPath);
        Assert.DoesNotContain("stale content", content);
        Assert.Contains("New,Yes,src,ok,80", content);

        var leftovers = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task WriteAsync_serializes_metrics_as_camelcase_json()
    {
        var row = Row("Q", "E", "src", "A", 85);
        row.Metrics = new List<MetricResult>
        {
            new()
            {
                Name = EvaluatorName.Relevance,
                Score = 90,
                Passed = true,
                Reason = "ok",
                Provider = MetricProvider.Deterministic,
                Scale = MetricScale.ZeroToOneHundred,
            },
        };
        string sidecar = Path.Combine(_tempDir, "m.evalgen.json");
        string outPath = await ResultsCsvWriter.WriteAsync(new[] { row }, _tempDir, sidecar, CancellationToken.None);

        string content = await File.ReadAllTextAsync(outPath);
        // The metrics field is the 6th column and contains JSON. Since
        // JSON has embedded quotes, the field is RFC 4180 quoted with
        // doubled inner quotes. Don't assert the full JSON shape (would
        // be brittle to property ordering); just confirm camelCase keys
        // that match the TS reference (eval-score/node/src/types.ts
        // MetricResult).
        Assert.Contains("\"\"name\"\"", content);
        Assert.Contains("\"\"score\"\"", content);
        Assert.Contains("\"\"passed\"\"", content);
        Assert.Contains("\"\"reason\"\"", content);
    }

    [Fact]
    public async Task WriteAsync_serializes_null_metrics_as_empty_string()
    {
        var row = Row("Q", "E", "src", "A", 50);
        row.Metrics = null;
        string sidecar = Path.Combine(_tempDir, "nm.evalgen.json");
        string outPath = await ResultsCsvWriter.WriteAsync(new[] { row }, _tempDir, sidecar, CancellationToken.None);

        string content = await File.ReadAllTextAsync(outPath);
        // Trailing empty field after the score: ",\n" at end of data row.
        Assert.EndsWith(",\n", content.Replace("\r\n", "\n"));
    }
}
