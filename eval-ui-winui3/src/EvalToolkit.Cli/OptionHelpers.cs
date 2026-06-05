using System.Globalization;
using System.Text.RegularExpressions;

namespace EvalToolkit.Cli;

/// <summary>
/// Shared option-parsing helpers ported from the TS CLI (<c>eval-gen/src/index.ts</c>
/// and <c>eval-score/node/src/index.ts</c>). Centralized here so both
/// <c>eval-gen-native</c> and <c>eval-score-native</c> behave identically to
/// their commander.js counterparts.
/// </summary>
internal static partial class OptionHelpers
{
    /// <summary>
    /// Mirrors TS <c>splitCsvOption</c>: splits a CSV-style flag value, trims
    /// whitespace, and drops empty entries. Returns null when input is null
    /// or contains no non-empty entries (TS contract).
    /// </summary>
    public static IReadOnlyList<string>? SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>
    /// Mirrors TS <c>parsePositiveInt</c>: parse a string, fall back to
    /// <paramref name="fallback"/> on failure or when value &lt; 1.
    /// </summary>
    public static int ParsePositiveInt(string? value, int fallback)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1)
        {
            return parsed;
        }
        return fallback;
    }

    /// <summary>
    /// Mirrors TS <c>parseNonNegativeInt</c>: parse a string, fall back to
    /// <paramref name="fallback"/> on failure or when value &lt; 0.
    /// </summary>
    public static int ParseNonNegativeInt(string? value, int fallback)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            return parsed;
        }
        return fallback;
    }

    /// <summary>
    /// Mirrors TS <c>Math.min(50, Math.max(10, Number(opts.count)))</c> clamping
    /// for <c>eval-gen --count</c>.
    /// </summary>
    public static int ClampGenerateCount(int requested)
    {
        return Math.Min(50, Math.Max(10, requested));
    }

    /// <summary>
    /// Mirrors TS <c>isMultiPromptEnabled</c>: returns true when either
    /// <c>--multi-prompt</c> was passed or <c>--multi-prompt-turns</c> was
    /// supplied.
    /// </summary>
    public static bool IsMultiPromptEnabled(bool multiPromptFlag, int? multiPromptTurns)
    {
        return multiPromptFlag || multiPromptTurns is not null;
    }

    /// <summary>
    /// Mirrors TS <c>resolveMultiPromptTurns</c>: when multi-prompt is
    /// enabled, returns a number in [2, 20] (default 3); else returns null.
    /// </summary>
    public static int? ResolveMultiPromptTurns(int? turns, bool multiPromptEnabled)
    {
        if (!multiPromptEnabled) return null;
        var resolved = turns ?? 3;
        return Math.Clamp(resolved, 2, 20);
    }

    /// <summary>
    /// Mirrors TS <c>deriveMultiPromptOutputPath</c>: replaces a trailing
    /// <c>.csv|.xlsx|.json</c> with <c>-multi-prompt.json</c>; otherwise
    /// appends <c>-multi-prompt.json</c> to the path verbatim.
    /// </summary>
    public static string DeriveMultiPromptOutputPath(string baseOutput)
    {
        var rewritten = RewriteOutputExtensionRegex().Replace(baseOutput, "-multi-prompt.json");
        return ReferenceEquals(rewritten, baseOutput) || rewritten == baseOutput
            ? baseOutput + "-multi-prompt.json"
            : rewritten;
    }

    /// <summary>
    /// Rewrites <c>.csv|.xlsx|.json</c> trailing extension to a fixed suffix.
    /// Used to derive sidecar (<c>.evalgen.json</c>) and review
    /// (<c>-review.md</c>) paths from the user-supplied <c>--output</c>.
    /// </summary>
    public static string RewriteOutputExtension(string outputPath, string newSuffix)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(newSuffix);
        return RewriteOutputExtensionRegex().Replace(outputPath, newSuffix);
    }

    [GeneratedRegex(@"\.(csv|xlsx|json)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RewriteOutputExtensionRegex();
}
