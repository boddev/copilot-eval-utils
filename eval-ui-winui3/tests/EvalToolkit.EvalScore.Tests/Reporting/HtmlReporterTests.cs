using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Reporting;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Tests.Reporting;

public class HtmlReporterTests : IDisposable
{
    private readonly string _tempDir;

    public HtmlReporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"html-reporter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static EvalRow Row(double? score, string prompt = "Q?", string actual = "A")
    {
        var r = new EvalRow { Prompt = prompt, ExpectedAnswer = "E", SourceLocation = "S", ActualAnswer = actual };
        r.SimilarityScore = score;
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
    public void Includes_doctype_and_table()
    {
        var rows = new[] { Row(80) };
        var sr = ScoringResult.Calculate(rows, 70);
        string html = HtmlReporter.GenerateHtmlReport(MakeResult(rows), sr);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<table>", html);
    }

    [Fact]
    public void Escapes_html_in_prompt_and_answer()
    {
        var rows = new[] { Row(80, prompt: "<script>alert('x')</script>", actual: "a & b <p>") };
        var sr = ScoringResult.Calculate(rows, 70);
        string html = HtmlReporter.GenerateHtmlReport(MakeResult(rows), sr);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void Does_not_escape_single_quotes()
    {
        var rows = new[] { Row(80, prompt: "it's working") };
        var sr = ScoringResult.Calculate(rows, 70);
        string html = HtmlReporter.GenerateHtmlReport(MakeResult(rows), sr);
        Assert.Contains("it's working", html);
        Assert.DoesNotContain("it&#39;s", html);
        Assert.DoesNotContain("it&apos;s", html);
    }

    [Fact]
    public void Status_falls_back_to_pass_when_score_meets_threshold()
    {
        var rows = new[] { Row(75), Row(60) };
        var sr = ScoringResult.Calculate(rows, 70);
        string html = HtmlReporter.GenerateHtmlReport(MakeResult(rows), sr);
        Assert.Contains("<td>pass</td>", html);
        Assert.Contains("<td>fail</td>", html);
    }

    [Fact]
    public async Task WriteHtmlReportAsync_writes_basename_dash_report_html()
    {
        var rows = new[] { Row(80) };
        var sr = ScoringResult.Calculate(rows, 70);
        string html = HtmlReporter.GenerateHtmlReport(MakeResult(rows), sr);
        string path = await HtmlReporter.WriteHtmlReportAsync(html, _tempDir, "myinput.csv");
        Assert.Equal(Path.Combine(_tempDir, "myinput-report.html"), path);
        Assert.True(File.Exists(path));
    }
}
