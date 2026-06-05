using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/validator.ts</c>. Converts drafted questions +
/// assertions into <see cref="GeneratedEvalItem"/>s, deduplicates by
/// normalized prompt, and computes category-balance / coverage validation.
/// </summary>
public static class Validator
{
    private const double CoverageTarget = 0.75;
    private const int CliCountCap = 50;

    /// <summary>
    /// Build <see cref="GeneratedEvalItem"/>s from drafted questions and
    /// their assertion map. Mirrors TS <c>buildEvalItems</c>.
    /// </summary>
    public static IReadOnlyList<GeneratedEvalItem> BuildEvalItems(
        IReadOnlyList<DraftedQuestion> questions,
        IReadOnlyDictionary<int, IReadOnlyList<Assertion>> assertionMap)
    {
        var result = new List<GeneratedEvalItem>(questions.Count);
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var referencedRows = new HashSet<string>(StringComparer.Ordinal);
            if (q.ReferencedRows is not null)
            {
                foreach (var r in q.ReferencedRows) referencedRows.Add(r);
            }
            if (!string.IsNullOrEmpty(q.SourceLocation)) referencedRows.Add(q.SourceLocation);

            result.Add(new GeneratedEvalItem
            {
                Id = GenerateItemId(q.Prompt, q.SourceLocation),
                Prompt = q.Prompt,
                ExpectedAnswer = q.ExpectedAnswer,
                SourceLocation = q.SourceLocation,
                Assertions = assertionMap.TryGetValue(i, out var asserts) ? asserts : Array.Empty<Assertion>(),
                Category = q.Category,
                Difficulty = q.Difficulty,
                SupportingFacts = q.SupportingFacts.ToList(),
                GroundingConfidence = AnswerGrounder.ComputeGroundingConfidence(q),
                ReferencedRows = referencedRows.ToList(),
            });
        }
        return result;
    }

    /// <summary>Validate a generated eval set: dedup, check balance, check coverage.</summary>
    public static (IReadOnlyList<GeneratedEvalItem> Validated, ValidationResult Result) ValidateEvalSet(
        IReadOnlyList<GeneratedEvalItem> items,
        int totalRows)
    {
        var issues = new List<string>();

        var (deduplicated, removedCount) = DeduplicateQuestions(items);
        if (removedCount > 0) issues.Add($"Removed {removedCount} duplicate question(s)");

        var categoryBalance = CheckCategoryBalance(deduplicated);
        var totalItems = deduplicated.Count;

        foreach (var (cat, weight) in QuestionCategories.DefaultWeights)
        {
            var count = categoryBalance.TryGetValue(cat, out var c) ? c : 0;
            var actual = (double)count / Math.Max(1, totalItems);
            if (Math.Abs(actual - weight) > 0.15)
            {
                issues.Add($"Category \"{cat.ToWireString()}\" is {(actual < weight ? "under" : "over")}-represented ({(int)Math.Round(actual * 100, MidpointRounding.AwayFromZero)}% vs {(int)Math.Round(weight * 100, MidpointRounding.AwayFromZero)}% target)");
            }
        }

        var coverageScore = ComputeCoverage(deduplicated, totalRows);
        var referencedRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in deduplicated)
        {
            if (!string.IsNullOrEmpty(item.SourceLocation)) referencedRows.Add(item.SourceLocation);
            if (item.ReferencedRows is not null)
            {
                foreach (var r in item.ReferencedRows)
                {
                    if (!string.IsNullOrEmpty(r)) referencedRows.Add(r);
                }
            }
        }
        var uniqueRowsReferenced = referencedRows.Count;

        var realisticTouchBudget = deduplicated.Count * 3;
        var realisticMaxCoverage = totalRows > 0
            ? Math.Min(1.0, (double)realisticTouchBudget / totalRows)
            : 0.0;
        var recommendedCountForTarget = totalRows > 0
            ? (int)Math.Ceiling((totalRows * CoverageTarget) / 3.0)
            : 0;
        var datasetSampledNotExhaustive = totalRows > 0 && recommendedCountForTarget > CliCountCap;

        int Pct(double v) => (int)Math.Round(v * 100, MidpointRounding.AwayFromZero);

        if (totalRows > 0 && deduplicated.Count > 0 && coverageScore < CoverageTarget)
        {
            if (realisticMaxCoverage >= CoverageTarget)
            {
                issues.Add(
                    $"Coverage {Pct(coverageScore)}% ({uniqueRowsReferenced}/{totalRows} rows) is below the {Pct(CoverageTarget)}% target. " +
                    $"The current count ({deduplicated.Count}) can reach the target — re-run to encourage broader row spread, or bump --count slightly.");
            }
            else if (recommendedCountForTarget <= CliCountCap)
            {
                issues.Add(
                    $"Coverage {Pct(coverageScore)}% ({uniqueRowsReferenced}/{totalRows} rows). " +
                    $"For ≥{Pct(CoverageTarget)}% coverage on this dataset, increase --count from {deduplicated.Count} to ~{recommendedCountForTarget}.");
            }
            else
            {
                issues.Add(
                    $"Dataset is large ({totalRows} rows) — exhaustive coverage isn't practical for an eval set. " +
                    $"This eval set tests {uniqueRowsReferenced} representative rows ({Pct(coverageScore)}%). " +
                    "For broader testing, generate multiple eval sets with focused --description values targeting different segments of your data " +
                    "(e.g., by category, time period, or status).");
            }
        }

        var lowConfidence = deduplicated.Count(i => i.GroundingConfidence == GroundingConfidence.Low);
        if (lowConfidence > totalItems * 0.2)
        {
            issues.Add($"{lowConfidence} question(s) have low grounding confidence");
        }

        var passed = issues.Count == 0 || (removedCount == 0 && coverageScore >= 0.2);

        return (deduplicated, new ValidationResult
        {
            Passed = passed,
            TotalItems = deduplicated.Count,
            DuplicatesRemoved = removedCount,
            CategoryBalance = categoryBalance,
            CoverageScore = coverageScore,
            Issues = issues,
            UniqueRowsReferenced = uniqueRowsReferenced,
            TotalRows = totalRows,
            RealisticMaxCoverage = realisticMaxCoverage,
            RecommendedCountForTarget = recommendedCountForTarget,
            DatasetSampledNotExhaustive = datasetSampledNotExhaustive,
        });
    }

    private static (IReadOnlyList<GeneratedEvalItem> Deduplicated, int RemovedCount) DeduplicateQuestions(
        IReadOnlyList<GeneratedEvalItem> items)
    {
        var seen = new List<string>();
        var deduplicated = new List<GeneratedEvalItem>();
        int removedCount = 0;

        foreach (var item in items)
        {
            var normalized = Dedupe.NormalizePrompt(item.Prompt);
            bool isDuplicate = false;
            foreach (var existing in seen)
            {
                if (existing == normalized || Dedupe.IsNearDuplicatePrompt(normalized, existing))
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
            {
                seen.Add(normalized);
                deduplicated.Add(item);
            }
            else
            {
                removedCount++;
            }
        }

        return (deduplicated, removedCount);
    }

    private static Dictionary<QuestionCategory, int> CheckCategoryBalance(IReadOnlyList<GeneratedEvalItem> items)
    {
        var balance = new Dictionary<QuestionCategory, int>();
        foreach (var cat in QuestionCategories.DefaultWeights.Keys)
        {
            balance[cat] = 0;
        }
        foreach (var item in items)
        {
            balance.TryGetValue(item.Category, out var c);
            balance[item.Category] = c + 1;
        }
        return balance;
    }

    private static double ComputeCoverage(IReadOnlyList<GeneratedEvalItem> items, int totalRows)
    {
        if (totalRows <= 0) return 0;
        var referencedRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.SourceLocation)) referencedRows.Add(item.SourceLocation);
            if (item.ReferencedRows is not null)
            {
                foreach (var r in item.ReferencedRows)
                {
                    if (!string.IsNullOrEmpty(r)) referencedRows.Add(r);
                }
            }
        }
        var realisticTouchBudget = Math.Max(items.Count * 3, 50);
        var denom = Math.Min(totalRows, realisticTouchBudget);
        return denom > 0 ? (double)referencedRows.Count / denom : 0;
    }

    private static string GenerateItemId(string prompt, string sourceLocation)
    {
        var input = Encoding.UTF8.GetBytes($"{prompt}|{sourceLocation}");
        var hash = SHA256.HashData(input);
        var sb = new StringBuilder(12);
        for (int i = 0; i < 6; i++)
        {
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
