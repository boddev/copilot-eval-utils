using System.Text.RegularExpressions;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/assertion-generator.ts</c>. Generates simple
/// must_contain / must_contain_any / must_not_contain assertions from a
/// drafted question's supporting facts and expected answer.
/// </summary>
public static class AssertionGenerator
{
    private static readonly Regex AlphaOnlyRx = new(@"^[a-zA-Z]+$", RegexOptions.Compiled);

    /// <summary>
    /// Generate assertions for a single question. v1 types only.
    /// Mirrors TS <c>generateAssertions</c>.
    /// </summary>
    public static IReadOnlyList<Assertion> GenerateAssertions(DraftedQuestion question)
    {
        var assertions = new List<Assertion>();
        var answer = question.ExpectedAnswer ?? string.Empty;

        foreach (var factStr in question.SupportingFacts)
        {
            var eqIndex = factStr.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex < 0) continue;

            var value = TrimSurroundingQuotes(factStr.Substring(eqIndex + 1).Trim());

            if (value.Length >= 2 && value.Length <= 80
                && answer.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                bool useWholeWord = value.Length < 5 && AlphaOnlyRx.IsMatch(value);
                assertions.Add(new MustContainAssertion { Value = value, WholeWord = useWholeWord });
            }
        }

        // Deduplicate by value (preserve insertion order).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<Assertion>();
        foreach (var a in assertions)
        {
            var key = AssertionKey(a);
            if (seen.Add(key)) unique.Add(a);
        }

        // Limit to 5 assertions per question.
        return unique.Count <= 5 ? unique : unique.Take(5).ToList();
    }

    /// <summary>Generate assertions for all questions, keyed by their index.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<Assertion>> GenerateAllAssertions(
        IReadOnlyList<DraftedQuestion> questions)
    {
        var map = new Dictionary<int, IReadOnlyList<Assertion>>(questions.Count);
        for (int i = 0; i < questions.Count; i++)
        {
            map[i] = GenerateAssertions(questions[i]);
        }
        return map;
    }

    private static string AssertionKey(Assertion a) => a switch
    {
        MustContainAssertion mc => $"must_contain:{mc.Value}",
        MustContainAnyAssertion mca => $"must_contain_any:{string.Join('|', mca.Values)}",
        MustNotContainAssertion mnc => $"must_not_contain:{mnc.Value}",
        _ => $"{a.TypeTag}:",
    };

    private static string TrimSurroundingQuotes(string s)
    {
        var start = s.Length >= 1 && s[0] == '"' ? 1 : 0;
        var end = s.Length >= 1 && s[^1] == '"' ? s.Length - 1 : s.Length;
        return end > start ? s.Substring(start, end - start) : string.Empty;
    }
}
