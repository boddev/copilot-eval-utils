using System.Globalization;
using System.Text.RegularExpressions;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/answer-grounder.ts</c>. Verifies drafted answers
/// against actual source records and computes grounding confidence.
/// </summary>
public static class AnswerGrounder
{
    private static readonly Regex RowRx = new(@":row\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Ground a drafted question's expected answer against the actual source
    /// data. Verifies the answer is derivable from the referenced records.
    /// Mirrors TS <c>groundAnswer</c>.
    /// </summary>
    public static DraftedQuestion GroundAnswer(
        DraftedQuestion question,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        string fileName)
    {
        var match = question.SourceLocation is { } sl ? RowRx.Match(sl) : Match.Empty;
        if (!match.Success) return question;

        var rowIndex = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
        if (rowIndex < 0 || rowIndex >= records.Count) return question;

        var record = records[rowIndex];

        var verifiedFacts = new List<string>();
        foreach (var factStr in question.SupportingFacts)
        {
            var eqIndex = factStr.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex < 0) continue;

            var field = factStr.Substring(0, eqIndex).Trim();
            var expectedValue = factStr.Substring(eqIndex + 1).Trim();

            if (record.TryGetValue(field, out var actualValue) && actualValue is not null)
            {
                var actualStr = Profiler.ConvertToString(actualValue);
                var trimmedExpected = TrimSurroundingQuotes(expectedValue);
                if (actualStr == expectedValue
                    || actualStr == trimmedExpected
                    || string.Equals(actualStr, trimmedExpected, StringComparison.OrdinalIgnoreCase))
                {
                    verifiedFacts.Add($"{field}={actualStr}");
                }
            }
        }

        return new DraftedQuestion
        {
            Prompt = question.Prompt,
            Category = question.Category,
            Difficulty = question.Difficulty,
            ExpectedAnswer = question.ExpectedAnswer,
            SupportingFacts = verifiedFacts.Count > 0 ? verifiedFacts : question.SupportingFacts,
            SourceLocation = $"{fileName}:row {rowIndex + 1}",
            SupportingFactIds = question.SupportingFactIds,
            ReferencedFacts = question.ReferencedFacts,
            ReferencedRows = question.ReferencedRows,
            AssignedPrimaryRow = question.AssignedPrimaryRow,
        };
    }

    /// <summary>Ground all drafted questions against source data.</summary>
    public static IReadOnlyList<DraftedQuestion> GroundAllAnswers(
        IReadOnlyList<DraftedQuestion> questions,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        string fileName)
    {
        var result = new List<DraftedQuestion>(questions.Count);
        foreach (var q in questions) result.Add(GroundAnswer(q, records, fileName));
        return result;
    }

    /// <summary>
    /// Compute grounding confidence based on how many supporting facts appear
    /// in the expected answer. Mirrors TS <c>computeGroundingConfidence</c>.
    /// </summary>
    public static GroundingConfidence ComputeGroundingConfidence(DraftedQuestion question)
    {
        var facts = question.SupportingFacts;
        if (facts.Count == 0) return GroundingConfidence.Low;

        var answer = (question.ExpectedAnswer ?? string.Empty).ToLowerInvariant();
        int matchCount = 0;

        foreach (var factStr in facts)
        {
            var eqIndex = factStr.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex < 0) continue;
            var value = TrimSurroundingQuotes(factStr.Substring(eqIndex + 1).Trim()).ToLowerInvariant();
            if (value.Length > 0 && answer.Contains(value, StringComparison.Ordinal))
            {
                matchCount++;
            }
        }

        var ratio = (double)matchCount / facts.Count;
        if (ratio >= 0.8) return GroundingConfidence.High;
        if (ratio >= 0.4) return GroundingConfidence.Medium;
        return GroundingConfidence.Low;
    }

    private static string TrimSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            return s.Substring(1, s.Length - 2);
        }
        if (s.Length >= 1 && (s[0] == '"' || s[^1] == '"'))
        {
            // Mirror TS regex /^"|"$/g — strips a single leading and a single trailing quote independently.
            // Lone quote ("") edge case: start=1, end=0, must clamp to empty string (TS regex would).
            var start = s[0] == '"' ? 1 : 0;
            var end = s[^1] == '"' ? s.Length - 1 : s.Length;
            return end > start ? s.Substring(start, end - start) : string.Empty;
        }
        return s;
    }
}
