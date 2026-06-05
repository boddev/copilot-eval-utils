using System.Globalization;
using System.Text;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/reviewer.ts</c>. Formats a generated eval set as
/// a human-readable markdown review document.
/// </summary>
public static class Reviewer
{
    /// <summary>
    /// Format the generated eval set as a human-readable review document.
    /// Mirrors TS <c>formatReview</c>. The optional <paramref name="now"/>
    /// parameter is the timestamp used in the header; defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public static string FormatReview(
        IReadOnlyList<GeneratedEvalItem> items,
        ValidationResult validation,
        string description,
        string sourceFile,
        DateTimeOffset? now = null)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", inv);

        sb.AppendLine("# EvalGen Review");
        sb.AppendLine();
        sb.AppendLine(inv, $"**Source:** {sourceFile}");
        sb.AppendLine(inv, $"**Description:** {description}");
        sb.AppendLine(inv, $"**Generated:** {timestamp}");
        sb.AppendLine(inv, $"**Total questions:** {validation.TotalItems}");
        sb.AppendLine(inv, $"**Duplicates removed:** {validation.DuplicatesRemoved}");
        if (validation.UniqueRowsReferenced is { } urr && validation.TotalRows is { } tr && tr > 0)
        {
            sb.AppendLine(inv, $"**Coverage:** {(int)Math.Round(validation.CoverageScore * 100, MidpointRounding.AwayFromZero)}% — {urr} of {tr} source rows tested");
            if (validation.DatasetSampledNotExhaustive == true)
            {
                sb.AppendLine();
                sb.AppendLine("> ℹ️ **Representative sample.** This dataset is too large for an eval set to exhaustively cover every record. The questions above test a diverse, stratified sample. For broader testing, generate multiple eval sets with focused `--description` values targeting different segments (e.g., by category, time period, or status).");
            }
            else if (validation.RecommendedCountForTarget is { } recommended
                && validation.CoverageScore < 0.75
                && recommended > validation.TotalItems)
            {
                sb.AppendLine();
                sb.AppendLine(inv, $"> ℹ️ For ≥75% coverage of this dataset, re-run with `--count {recommended}`.");
            }
        }
        else
        {
            sb.AppendLine(inv, $"**Coverage score:** {(int)Math.Round(validation.CoverageScore * 100, MidpointRounding.AwayFromZero)}%");
        }
        sb.AppendLine();

        // Category distribution
        sb.AppendLine("## Category Distribution");
        sb.AppendLine();
        sb.AppendLine("| Category | Count |");
        sb.AppendLine("|----------|-------|");
        foreach (var (cat, count) in validation.CategoryBalance)
        {
            sb.AppendLine(inv, $"| {cat.ToWireString()} | {count} |");
        }
        sb.AppendLine();

        // Validation issues
        if (validation.Issues.Count > 0)
        {
            sb.AppendLine("## Validation Notes");
            sb.AppendLine();
            foreach (var issue in validation.Issues)
            {
                sb.AppendLine(inv, $"- ⚠️ {issue}");
            }
            sb.AppendLine();
        }

        // Questions detail
        sb.AppendLine("## Generated Questions");
        sb.AppendLine();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.AppendLine(inv, $"### Q{i + 1}: {item.Prompt}");
            sb.AppendLine();
            sb.AppendLine(inv, $"- **Category:** {item.Category.ToWireString()}");
            sb.AppendLine(inv, $"- **Difficulty:** {item.Difficulty.ToWireString()}");
            sb.AppendLine(inv, $"- **Confidence:** {item.GroundingConfidence.ToWireString()}");
            sb.AppendLine(inv, $"- **Source:** {item.SourceLocation}");
            sb.AppendLine(inv, $"- **Expected Answer:** {item.ExpectedAnswer}");

            if (item.Assertions.Count > 0)
            {
                sb.AppendLine("- **Assertions:**");
                foreach (var a in item.Assertions)
                {
                    switch (a)
                    {
                        case MustContainAssertion mc:
                            sb.AppendLine(inv, $"  - ✅ Must contain: \"{mc.Value}\"");
                            break;
                        case MustContainAnyAssertion mca:
                            sb.AppendLine(inv, $"  - ✅ Must contain any: {string.Join(", ", mca.Values.Select(v => $"\"{v}\""))}");
                            break;
                        case MustNotContainAssertion mnc:
                            sb.AppendLine(inv, $"  - ❌ Must NOT contain: \"{mnc.Value}\"");
                            break;
                    }
                }
            }

            if (item.SupportingFacts.Count > 0)
            {
                sb.AppendLine(inv, $"- **Supporting Facts:** {string.Join("; ", item.SupportingFacts)}");
            }
            sb.AppendLine();
        }

        // Mirror TS Array.join('\n') — no trailing newline. AppendLine emits
        // Environment.NewLine; we normalize to '\n' and trim final newline.
        var raw = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (raw.EndsWith('\n')) raw = raw[..^1];
        return raw;
    }
}
