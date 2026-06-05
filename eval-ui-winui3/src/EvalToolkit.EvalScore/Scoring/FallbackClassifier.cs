namespace EvalToolkit.EvalScore.Scoring;

/// <summary>
/// Decides whether a primary-judge failure should fall back to the
/// secondary judge. Mirrors TS <c>isFallbackEligible</c> in
/// <c>eval-score/node/src/scorer.ts</c>.
/// </summary>
public static class FallbackClassifier
{
    private static readonly string[] s_eligibleSubstrings =
    {
        "timed out",
        "timeout",
        "could not parse score",
        "ask_work_iq",
        "mcp",
        "429",
        "rate limit",
        "throttl",
        "temporarily unavailable",
        "503",
        "502",
        "504",
    };

    public static bool IsEligible(Exception? error)
    {
        string message = (error?.Message ?? string.Empty).ToLowerInvariant();
        if (message.Length == 0)
        {
            return false;
        }
        foreach (string s in s_eligibleSubstrings)
        {
            if (message.Contains(s, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
