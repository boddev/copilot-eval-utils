using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Reporting;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Reporting;

public class MarkdownReporterTests : IDisposable
{
    private readonly string _tempDir;

    public MarkdownReporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"md-reporter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static EvalRow Row(double? score, string prompt = "Q?", string actual = "A", string? category = null)
    {
        var r = new EvalRow { Prompt = prompt, ExpectedAnswer = "E", SourceLocation = "S", ActualAnswer = actual };
        r.SimilarityScore = score;
        r.Category = category;
        return r;
    }

    private static EvalResult MakeResult(IReadOnlyList<EvalRow> rows)
        => new()
        {
            Rows = rows,
            InputFile = "test.csv",
            InputFormat = InputFormat.Csv,
            Timestamp = "2025-01-01T00:00:00Z",
        };

    [Fact]
    public void Includes_summary_and_distribution()
    {
        var rows = new[] { Row(95), Row(75), Row(50), Row(30) };
        var sr = ScoringResult.Calculate(rows, 70);
        string report = MarkdownReporter.GenerateReport(MakeResult(rows), sr);
        Assert.Contains("# Evaluation Report", report);
        Assert.Contains("**Total Questions:** 4", report);
        Assert.Contains("**Average Score:**", report);
        Assert.Contains("Excellent", report);
        Assert.Contains("Good", report);
        Assert.Contains("Fair", report);
        Assert.Contains("Poor", report);
    }

    [Fact]
    public void Includes_pass_rate_and_threshold()
    {
        var rows = new[] { Row(80), Row(40) };
        var sr = ScoringResult.Calculate(rows, 70);
        string report = MarkdownReporter.GenerateReport(MakeResult(rows), sr);
        Assert.Contains("**Pass Rate:** 1/2 (50%)", report);
        Assert.Contains("**Pass Threshold:** 70", report);
    }

    [Fact]
    public void Renders_per_category_section_only_when_categories_present()
    {
        var rowsWithoutCat = new[] { Row(80), Row(40) };
        string r1 = MarkdownReporter.GenerateReport(MakeResult(rowsWithoutCat), ScoringResult.Calculate(rowsWithoutCat, 70));
        Assert.DoesNotContain("## Results by Category", r1);

        var rowsWithCat = new[] { Row(80, category: "factual"), Row(40, category: "factual") };
        string r2 = MarkdownReporter.GenerateReport(MakeResult(rowsWithCat), ScoringResult.Calculate(rowsWithCat, 70));
        Assert.Contains("## Results by Category", r2);
        Assert.Contains("factual", r2);
    }

    [Fact]
    public void Formats_integer_score_without_decimal()
    {
        var rows = new[] { Row(85) };
        var sr = ScoringResult.Calculate(rows, 70);
        string report = MarkdownReporter.GenerateReport(MakeResult(rows), sr);
        Assert.Contains("**Score:** 85/100", report);
        Assert.DoesNotContain("**Score:** 85.0/100", report);
    }

    [Fact]
    public void Renders_NA_for_rows_without_score()
    {
        var rows = new[] { Row(null) };
        var sr = ScoringResult.Calculate(rows, 70);
        string report = MarkdownReporter.GenerateReport(MakeResult(rows), sr);
        Assert.Contains("**Score:** N/A", report);
    }

    [Fact]
    public async Task WriteReportAsync_writes_to_basename_dash_report_md()
    {
        var rows = new[] { Row(75) };
        var sr = ScoringResult.Calculate(rows, 70);
        string content = MarkdownReporter.GenerateReport(MakeResult(rows), sr);
        string path = await MarkdownReporter.WriteReportAsync(content, _tempDir, "myinput.csv");
        Assert.Equal(Path.Combine(_tempDir, "myinput-report.md"), path);
        Assert.True(File.Exists(path));
        Assert.Contains("# Evaluation Report", File.ReadAllText(path));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => MarkdownReporter.GenerateReport(null!, ScoringResult.Calculate(Array.Empty<EvalRow>(), 70)));
        Assert.Throws<ArgumentNullException>(() => MarkdownReporter.GenerateReport(MakeResult(Array.Empty<EvalRow>()), null!));
    }
}
