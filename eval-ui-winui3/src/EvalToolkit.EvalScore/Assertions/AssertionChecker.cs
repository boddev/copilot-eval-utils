using System.Text.RegularExpressions;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Assertions;

/// <summary>
/// Evaluates EvalGen-style assertions against a row's actual answer.
/// Mirrors <c>eval-score/node/src/assertion-checker.ts</c>.
///
/// <para>TS contract:
/// <list type="bullet">
///   <item><c>must_contain</c>: case-insensitive substring; with
///     <c>wholeWord=true</c>, word-boundary regex with all regex
///     metacharacters in the value escaped.</item>
///   <item><c>must_contain_any</c>: case-insensitive substring of ANY
///     listed value; details report the first match.</item>
///   <item><c>must_not_contain</c>: case-insensitive substring; passes
///     when absent.</item>
///   <item>Detail strings use the original-cased value, but match
///     against a lowercased haystack.</item>
///   <item>Rows with no actual answer or an <c>[ERROR:</c> prefix
///     produce an empty assertion-result list (not failures).</item>
/// </list></para>
/// </summary>
public static class AssertionChecker
{
    /// <summary>
    /// Evaluate a single assertion against the row's actual answer text.
    /// </summary>
    public static AssertionResult EvaluateAssertion(Assertion assertion, string actualAnswer)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        actualAnswer ??= string.Empty;
        string lower = actualAnswer.ToLowerInvariant();

        switch (assertion.Type)
        {
            case AssertionType.MustContain:
                return EvaluateMustContain(assertion, actualAnswer, lower);
            case AssertionType.MustContainAny:
                return EvaluateMustContainAny(assertion, lower);
            case AssertionType.MustNotContain:
                return EvaluateMustNotContain(assertion, lower);
            default:
                return new AssertionResult
                {
                    Assertion = assertion,
                    Passed = false,
                    Detail = "⚠️ Unknown assertion type",
                };
        }
    }

    private static AssertionResult EvaluateMustContain(Assertion assertion, string actualAnswer, string lowerAnswer)
    {
        string value = assertion.Value ?? string.Empty;
        string target = value.ToLowerInvariant();
        bool passed;
        if (assertion.WholeWord == true)
        {
            // TS: new RegExp(`\\b${target.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i')
            string escaped = Regex.Escape(target);
            var rx = new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            passed = rx.IsMatch(actualAnswer);
        }
        else
        {
            passed = lowerAnswer.Contains(target, StringComparison.Ordinal);
        }
        return new AssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = passed
                ? $"✅ Found \"{value}\""
                : $"❌ Missing \"{value}\"",
        };
    }

    private static AssertionResult EvaluateMustContainAny(Assertion assertion, string lowerAnswer)
    {
        IReadOnlyList<string> values = assertion.Values ?? Array.Empty<string>();
        string? found = null;
        foreach (string v in values)
        {
            if (lowerAnswer.Contains(v.ToLowerInvariant(), StringComparison.Ordinal))
            {
                found = v;
                break;
            }
        }
        bool passed = found is not null;
        return new AssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = passed
                ? $"✅ Found \"{found}\""
                : $"❌ None found: {string.Join(", ", values.Select(v => $"\"{v}\""))}",
        };
    }

    private static AssertionResult EvaluateMustNotContain(Assertion assertion, string lowerAnswer)
    {
        string value = assertion.Value ?? string.Empty;
        string target = value.ToLowerInvariant();
        bool passed = !lowerAnswer.Contains(target, StringComparison.Ordinal);
        return new AssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = passed
                ? $"✅ Correctly absent: \"{value}\""
                : $"❌ Unexpectedly found \"{value}\"",
        };
    }

    /// <summary>
    /// Evaluate every assertion attached to a row. Returns an empty list
    /// if the row has no assertions, or if the row's actual answer is
    /// empty or carries an <c>[ERROR:</c> prefix.
    /// </summary>
    public static List<AssertionResult> EvaluateRowAssertions(EvalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Assertions is null || row.Assertions.Count == 0)
        {
            return new List<AssertionResult>();
        }
        if (string.IsNullOrEmpty(row.ActualAnswer) || row.ActualAnswer.StartsWith("[ERROR:", StringComparison.Ordinal))
        {
            return new List<AssertionResult>();
        }
        var results = new List<AssertionResult>(row.Assertions.Count);
        foreach (Assertion a in row.Assertions)
        {
            results.Add(EvaluateAssertion(a, row.ActualAnswer));
        }
        return results;
    }

    /// <summary>
    /// Mutate every row: attach its assertion results in place. Returns
    /// the same list for fluent chaining.
    /// </summary>
    public static IList<EvalRow> EvaluateAllAssertions(IList<EvalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (EvalRow row in rows)
        {
            row.AssertionResults = EvaluateRowAssertions(row);
        }
        return rows;
    }
}
