using System.Text;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Scoring;

namespace EvalToolkit.EvalScore.Reporting;

/// <summary>
/// Generates the HTML evaluation report. Mirrors TS
/// <c>generateHtmlReport</c> + <c>writeHtmlReport</c> in
/// <c>eval-score/node/src/reporter.ts</c>.
///
/// <para>HTML escaping covers <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>,
/// <c>&quot;</c> — the exact list the TS reporter escapes. Single
/// quotes are NOT escaped (TS doesn't either). The inline CSS preserves
/// the original class colors and table border styling exactly so the
/// report renders identically in WebView2 or any browser.</para>
/// </summary>
public static class HtmlReporter
{
    public static string GenerateHtmlReport(EvalResult evalResult, ScoringResult scoringResult)
    {
        ArgumentNullException.ThrowIfNull(evalResult);
        ArgumentNullException.ThrowIfNull(scoringResult);

        var rowsHtml = new StringBuilder();
        for (int index = 0; index < evalResult.Rows.Count; index++)
        {
            EvalRow row = evalResult.Rows[index];
            string status = row.Status?.ToWireString()
                ?? ((row.SimilarityScore ?? 0) >= scoringResult.PassThreshold ? "pass" : "fail");
            string scoreCell = row.SimilarityScore.HasValue
                ? FormatScore(row.SimilarityScore.Value)
                : string.Empty;
            rowsHtml.Append("<tr><td>");
            rowsHtml.Append(index + 1);
            rowsHtml.Append("</td><td>");
            rowsHtml.Append(EscapeHtml(status));
            rowsHtml.Append("</td><td>");
            rowsHtml.Append(scoreCell);
            rowsHtml.Append("</td><td>");
            rowsHtml.Append(EscapeHtml(Truncate(row.Prompt, 120)));
            rowsHtml.Append("</td><td>");
            rowsHtml.Append(EscapeHtml(Truncate(row.ActualAnswer, 200)));
            rowsHtml.Append("</td></tr>");
            if (index < evalResult.Rows.Count - 1)
            {
                rowsHtml.Append('\n');
            }
        }

        var lines = new[]
        {
            "<!doctype html>",
            "<html lang=\"en\">",
            "<head>",
            "<meta charset=\"utf-8\">",
            "<title>EvalScore Report</title>",
            "<style>body{font-family:Segoe UI,Arial,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:.5rem;vertical-align:top}.pass{color:#107c10}.fail,.error{color:#a4262c}.partial{color:#8a6d00}</style>",
            "</head>",
            "<body>",
            "<h1>Evaluation Report</h1>",
            "<section>",
            $"<p><strong>Input:</strong> {EscapeHtml(evalResult.InputFile)}</p>",
            $"<p><strong>Average score:</strong> {scoringResult.AverageScore.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} / 100</p>",
            $"<p><strong>Pass rate:</strong> {scoringResult.PassCount}/{scoringResult.TotalQuestions}</p>",
            $"<p><strong>Judge:</strong> {EscapeHtml(evalResult.JudgeProvider?.ToWireString() ?? string.Empty)}</p>",
            "</section>",
            "<table>",
            "<thead><tr><th>#</th><th>Status</th><th>Score</th><th>Prompt</th><th>Actual response</th></tr></thead>",
            $"<tbody>{rowsHtml}</tbody>",
            "</table>",
            "</body>",
            "</html>",
        };

        return string.Join("\n", lines);
    }

    public static async Task<string> WriteHtmlReportAsync(
        string html,
        string outputDir,
        string inputFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);

        Directory.CreateDirectory(outputDir);
        string baseName = Path.GetFileNameWithoutExtension(inputFile);
        string outputPath = Path.Combine(outputDir, $"{baseName}-report.html");
        await File.WriteAllTextAsync(outputPath, html, cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    private static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxLength)
        {
            return text;
        }
        return text[..maxLength] + "...";
    }

    private static string EscapeHtml(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string FormatScore(double score)
    {
        if (score == Math.Truncate(score))
        {
            return ((long)score).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return score.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
