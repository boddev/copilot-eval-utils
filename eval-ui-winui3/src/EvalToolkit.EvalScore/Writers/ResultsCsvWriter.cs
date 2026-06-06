using System.Globalization;
using System.Text;
using System.Text.Json;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Writers;

/// <summary>
/// Writes a 6-column scored results CSV next to the markdown report.
/// Columns: <c>prompt,expected_answer,source_location,actual_answer,similarity_score,metrics</c>.
/// UTF-8 NO-BOM, RFC-4180 minimal quoting, LF line endings — matches
/// the Node TS writer at <c>eval-score/node/src/writers/csv-writer.ts</c>
/// (verified column-for-column). The <c>metrics</c> column is JSON
/// of <see cref="EvalRow.Metrics"/> when present, empty string otherwise.
///
/// <para>Both the CLI (ScoreCommand) and the WinUI Step 5 panel call
/// <see cref="WriteAsync"/> so the user-visible artifact set is stable
/// across the two product lines.</para>
/// </summary>
public static class ResultsCsvWriter
{
    // camelCase keys to match the Node TS writer's JSON output for the
    // `metrics` column. TS shape (eval-score/node/src/types.ts):
    //   { name: 'Relevance', score: 90, passed: true, reason: '...',
    //     provider: 'workiq', model: '...', scale: '0-100',
    //     rubricVersion: '...', threshold: 70 }
    // Enums are PascalCase / kebab-case / '0-100' on the wire — default
    // Text.Json enum handling would emit integers, so we project to a
    // Dictionary using the existing wire-string helpers.
    private static readonly JsonSerializerOptions s_metricsJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Write scored <paramref name="rows"/> to
    /// <c>{outputDir}/{basename}-results.csv</c> where basename is
    /// derived from <paramref name="inputFile"/> (matching
    /// <see cref="Reporting.MarkdownReporter.WriteReportAsync"/>).
    /// Atomic via <c>.tmp</c> + <c>File.Move(overwrite:true)</c>.
    /// </summary>
    /// <returns>Absolute path of the written file.</returns>
    public static async Task<string> WriteAsync(
        IReadOnlyList<EvalRow> rows,
        string outputDir,
        string inputFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);

        Directory.CreateDirectory(outputDir);
        string baseName = Path.GetFileNameWithoutExtension(inputFile);
        // EvalGen sidecars end with .evalgen.json; the basename above
        // returns "foo.evalgen". Trim the .evalgen suffix so the output
        // matches the report name ("foo-report.md", "foo-results.csv").
        if (baseName.EndsWith(".evalgen", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^".evalgen".Length];
        }
        string outputPath = Path.Combine(outputDir, $"{baseName}-results.csv");
        string tempPath = outputPath + ".tmp";

        var sb = new StringBuilder(rows.Count * 320);
        sb.Append("prompt,expected_answer,source_location,actual_answer,similarity_score,metrics\n");
        foreach (var r in rows)
        {
            AppendField(sb, r.Prompt); sb.Append(',');
            AppendField(sb, r.ExpectedAnswer); sb.Append(',');
            AppendField(sb, r.SourceLocation); sb.Append(',');
            AppendField(sb, r.ActualAnswer); sb.Append(',');
            AppendField(sb, r.SimilarityScore?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty);
            sb.Append(',');
            AppendField(sb, SerializeMetrics(r.Metrics));
            sb.Append('\n');
        }
        try
        {
            await File.WriteAllTextAsync(
                tempPath, sb.ToString(), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, outputPath, overwrite: true);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best-effort cleanup */ }
            throw;
        }
    }

    private static string SerializeMetrics(IList<MetricResult>? metrics)
    {
        if (metrics is null || metrics.Count == 0) return string.Empty;
        try
        {
            var projected = new List<Dictionary<string, object?>>(metrics.Count);
            foreach (var m in metrics)
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = m.Name.ToWireString(),
                    ["provider"] = m.Provider.ToWireString(),
                    ["scale"] = m.Scale.ToWireString(),
                };
                if (m.Score is { } score) dict["score"] = score;
                if (m.Passed is { } passed) dict["passed"] = passed;
                if (!string.IsNullOrEmpty(m.Reason)) dict["reason"] = m.Reason;
                if (!string.IsNullOrEmpty(m.Model)) dict["model"] = m.Model;
                if (!string.IsNullOrEmpty(m.RubricVersion)) dict["rubricVersion"] = m.RubricVersion;
                if (m.Threshold is { } th) dict["threshold"] = th;
                projected.Add(dict);
            }
            return JsonSerializer.Serialize(projected, s_metricsJson);
        }
        catch
        {
            // Never let a metrics-serialization issue corrupt the whole CSV.
            return string.Empty;
        }
    }

    private static void AppendField(StringBuilder sb, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        bool quote = false;
        foreach (char c in raw)
        {
            if (c == ',' || c == '"' || c == '\n' || c == '\r')
            {
                quote = true;
                break;
            }
        }
        if (!quote)
        {
            sb.Append(raw);
            return;
        }
        sb.Append('"');
        foreach (char c in raw)
        {
            if (c == '"') sb.Append('"');
            sb.Append(c);
        }
        sb.Append('"');
    }
}
