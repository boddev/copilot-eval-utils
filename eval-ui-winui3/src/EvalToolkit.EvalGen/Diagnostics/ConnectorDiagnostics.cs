using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Diagnostics;

/// <summary>
/// Port of <c>eval-gen/src/connector-diagnostics.ts</c>. Loads a connector
/// schema, analyses how well a generated eval set matches what the connector
/// will actually index, and emits both a structured <see cref="DiagnosticReport"/>
/// and a markdown rendering of it.
/// </summary>
public static class ConnectorDiagnostics
{
    private static readonly Regex AggregationPattern =
        new(@"\b(how many|count|total|average|sum|all)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BroadPattern =
        new(@"\b(list all|show all|every|summarize all|everything)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Load a connector schema from a JSON file on disk. Mirrors TS
    /// <c>loadConnectorSchema</c>: throws if the file is missing or if the
    /// <c>contentFields</c> array is absent / malformed.
    /// </summary>
    public static ConnectorSchema LoadSchema(string schemaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPath);
        var absPath = Path.GetFullPath(schemaPath);
        if (!File.Exists(absPath))
        {
            throw new FileNotFoundException($"Connector schema file not found: {absPath}", absPath);
        }

        var content = File.ReadAllText(absPath);
        var schema = JsonSerializer.Deserialize(content, ConnectorSchemaJsonContext.Default.ConnectorSchema)
            ?? throw new InvalidDataException("Connector schema file is empty or null");

        if (schema.ContentFields is null)
        {
            throw new InvalidDataException("Connector schema must have a \"contentFields\" array");
        }

        return schema;
    }

    /// <summary>
    /// Analyse which dataset columns are vs aren't indexed in the connector.
    /// Mirrors TS <c>analyzeFieldCoverage</c>: lowercases on both sides,
    /// percentage rounded with TS Math.round semantics.
    /// </summary>
    public static FieldCoverageReport AnalyzeFieldCoverage(DatasetProfile profile, ConnectorSchema schema)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(schema);

        var datasetFields = profile.Columns.Select(c => c.Name.ToLowerInvariant()).ToList();
        var indexedSet = new HashSet<string>(
            (schema.ContentFields ?? new List<string>()).Select(f => f.ToLowerInvariant()),
            StringComparer.Ordinal);

        var indexedFields = datasetFields.Where(f => indexedSet.Contains(f)).ToList();
        var unindexedFields = datasetFields.Where(f => !indexedSet.Contains(f)).ToList();

        var total = datasetFields.Count;
        var coveragePercentage = total > 0
            ? (int)Math.Round((double)indexedFields.Count / total * 100, MidpointRounding.AwayFromZero)
            : 0;

        return new FieldCoverageReport
        {
            IndexedFields = indexedFields,
            UnindexedFields = unindexedFields,
            QuestionsTargetingUnindexed = 0,
            CoveragePercentage = coveragePercentage,
        };
    }

    /// <summary>
    /// Run full connector diagnostics on a generated eval set. Mirrors TS
    /// <c>runDiagnostics</c>: per-item issues, aggregation warnings, severity
    /// classification, and the summary string used by the CLI.
    /// </summary>
    public static DiagnosticReport RunDiagnostics(
        IReadOnlyList<GeneratedEvalItem> items,
        DatasetProfile profile,
        ConnectorSchema schema)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(schema);

        var fieldCoverage = AnalyzeFieldCoverage(profile, schema);
        var unindexedSet = new HashSet<string>(fieldCoverage.UnindexedFields, StringComparer.Ordinal);

        var itemDiagnostics = items.Select(item => DiagnoseItem(item, schema, unindexedSet)).ToList();

        var questionsTargetingUnindexed = itemDiagnostics.Count(d =>
            d.Issues.Any(i => i.Contains("unindexed", StringComparison.Ordinal)));
        fieldCoverage = fieldCoverage with { QuestionsTargetingUnindexed = questionsTargetingUnindexed };

        var aggregationWarnings = itemDiagnostics
            .Where(d => d.Issues.Any(i => i.Contains("Aggregation", StringComparison.Ordinal)))
            .Select(d => d.Prompt)
            .ToList();

        var errorCount = itemDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnCount = itemDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        var okCount = itemDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Ok);

        var connectorName = string.IsNullOrEmpty(schema.Name) ? "Unknown" : schema.Name;
        var totalFields = fieldCoverage.IndexedFields.Count + fieldCoverage.UnindexedFields.Count;
        var inv = CultureInfo.InvariantCulture;

        var summaryLines = new List<string>
        {
            $"Connector: {connectorName}",
            string.Format(inv, "Field coverage: {0}% ({1}/{2} fields indexed)",
                fieldCoverage.CoveragePercentage, fieldCoverage.IndexedFields.Count, totalFields),
            string.Format(inv, "Questions: {0} ok, {1} warnings, {2} errors", okCount, warnCount, errorCount),
            fieldCoverage.QuestionsTargetingUnindexed > 0
                ? string.Format(inv, "⚠️ {0} question(s) target unindexed fields", fieldCoverage.QuestionsTargetingUnindexed)
                : "✅ All questions target indexed fields",
        };
        if (aggregationWarnings.Count > 0)
        {
            summaryLines.Add(string.Format(inv, "⚠️ {0} aggregation question(s) without summary items", aggregationWarnings.Count));
        }
        var summary = string.Join("\n", summaryLines);

        return new DiagnosticReport
        {
            ConnectorName = connectorName,
            TotalItems = items.Count,
            ItemDiagnostics = itemDiagnostics,
            FieldCoverage = fieldCoverage,
            AggregationWarnings = aggregationWarnings,
            Summary = summary,
        };
    }

    /// <summary>
    /// Format a diagnostic report as markdown. Mirrors TS
    /// <c>formatDiagnosticReport</c> exactly: same headers, same emoji,
    /// same trailing-summary separator block.
    /// </summary>
    public static string FormatDiagnosticReport(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.Append("# Connector Diagnostics Report\n\n");
        sb.AppendFormat(inv, "**Connector:** {0}\n", report.ConnectorName);
        sb.AppendFormat(inv, "**Questions analyzed:** {0}\n\n", report.TotalItems);

        sb.Append("## Field Coverage\n\n");
        sb.AppendFormat(inv, "Coverage: {0}%\n\n", report.FieldCoverage.CoveragePercentage);

        if (report.FieldCoverage.IndexedFields.Count > 0)
        {
            sb.Append("**Indexed (searchable by Copilot):**\n");
            foreach (var f in report.FieldCoverage.IndexedFields)
            {
                sb.AppendFormat(inv, "- ✅ {0}\n", f);
            }
            sb.Append('\n');
        }

        if (report.FieldCoverage.UnindexedFields.Count > 0)
        {
            sb.Append("**Not indexed (Copilot cannot search these):**\n");
            foreach (var f in report.FieldCoverage.UnindexedFields)
            {
                sb.AppendFormat(inv, "- ❌ {0}\n", f);
            }
            sb.Append('\n');
        }

        var issues = report.ItemDiagnostics.Where(d => d.Severity != DiagnosticSeverity.Ok).ToList();
        if (issues.Count > 0)
        {
            sb.Append("## Issues Found\n\n");
            foreach (var item in issues)
            {
                var icon = item.Severity == DiagnosticSeverity.Error ? "🔴" : "🟡";
                sb.AppendFormat(inv, "### {0} {1}\n", icon, item.Prompt);
                foreach (var issue in item.Issues)
                {
                    sb.AppendFormat(inv, "- {0}\n", issue);
                }
                sb.Append('\n');
            }
        }

        if (report.AggregationWarnings.Count > 0)
        {
            sb.Append("## Aggregation Warnings\n\n");
            sb.Append("These questions may produce unreliable results because the connector does not ingest summary items:\n\n");
            foreach (var q in report.AggregationWarnings)
            {
                sb.AppendFormat(inv, "- {0}\n", q);
            }
            sb.Append('\n');
        }

        sb.Append("---\n\n");
        sb.Append(report.Summary);
        return sb.ToString();
    }

    private static ItemDiagnostic DiagnoseItem(
        GeneratedEvalItem item,
        ConnectorSchema schema,
        HashSet<string> unindexedFields)
    {
        var issues = new List<string>();

        foreach (var fact in item.SupportingFacts)
        {
            var eqIndex = fact.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex < 0) continue;
            var field = fact[..eqIndex].Trim().ToLowerInvariant();
            if (unindexedFields.Contains(field))
            {
                issues.Add($"References unindexed field \"{field}\" — Copilot may not find this data");
            }
        }

        if (!schema.HasSummaryItems && AggregationPattern.IsMatch(item.Prompt))
        {
            issues.Add("Aggregation question without summary items — Copilot may give unreliable results");
        }

        if (BroadPattern.IsMatch(item.Prompt))
        {
            issues.Add("Broad/exhaustive question — connector retrieval returns limited results");
        }

        var severity = issues.Count == 0
            ? DiagnosticSeverity.Ok
            : issues.Any(i => i.Contains("unindexed", StringComparison.Ordinal))
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;

        return new ItemDiagnostic
        {
            Prompt = item.Prompt,
            Issues = issues,
            Severity = severity,
        };
    }
}

/// <summary>
/// Severity ranking for a single eval-item diagnostic. Mirrors the TS
/// string-union <c>'ok' | 'warning' | 'error'</c>.
/// </summary>
public enum DiagnosticSeverity
{
    Ok,
    Warning,
    Error,
}

/// <summary>
/// Connector schema. Mirrors TS <c>ConnectorSchema</c>.
/// </summary>
public sealed record ConnectorSchema
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("contentFields")] public IReadOnlyList<string>? ContentFields { get; init; }
    [JsonPropertyName("titleField")] public string? TitleField { get; init; }
    [JsonPropertyName("urlField")] public string? UrlField { get; init; }
    [JsonPropertyName("hasSummaryItems")] public bool HasSummaryItems { get; init; }
    [JsonPropertyName("connectionDescription")] public string? ConnectionDescription { get; init; }
}

/// <summary>
/// Diagnostic result for a single eval item. Mirrors TS
/// <c>ItemDiagnostic</c>.
/// </summary>
public sealed record ItemDiagnostic
{
    public required string Prompt { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
}

/// <summary>
/// Field-indexing coverage. Mirrors TS <c>FieldCoverageReport</c>.
/// </summary>
public sealed record FieldCoverageReport
{
    public required IReadOnlyList<string> IndexedFields { get; init; }
    public required IReadOnlyList<string> UnindexedFields { get; init; }
    public required int QuestionsTargetingUnindexed { get; init; }
    public required int CoveragePercentage { get; init; }
}

/// <summary>
/// Aggregated diagnostic report. Mirrors TS <c>DiagnosticReport</c>.
/// </summary>
public sealed record DiagnosticReport
{
    public required string ConnectorName { get; init; }
    public required int TotalItems { get; init; }
    public required IReadOnlyList<ItemDiagnostic> ItemDiagnostics { get; init; }
    public required FieldCoverageReport FieldCoverage { get; init; }
    public required IReadOnlyList<string> AggregationWarnings { get; init; }
    public required string Summary { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ConnectorSchema))]
internal partial class ConnectorSchemaJsonContext : JsonSerializerContext
{
}
