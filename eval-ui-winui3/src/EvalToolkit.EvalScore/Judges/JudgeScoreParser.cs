using System.Text.Json;
using System.Text.RegularExpressions;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Parses raw judge responses into <see cref="JudgeScore"/>. Mirrors
/// <c>parseJudgeScore</c> in <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>Algorithm (TS-faithful):</para>
/// <list type="number">
///   <item>Trim. If trimmed starts with <c>{</c>, try JSON parse.</item>
///   <item>If JSON parse succeeds AND <c>score</c> is a finite number,
///         return <c>{ score: clamp(score), reason: reason ?? rationale, model }</c>.</item>
///   <item>Otherwise (parse failure OR missing/non-finite score) fall
///         through to numeric extraction.</item>
///   <item>Numeric extraction: <c>/\d+/</c> first integer; clamp.</item>
///   <item>If no digits, throw with truncated preview (.slice(0,120)).</item>
/// </list>
///
/// <para>Clamp: <c>max(0, min(100, round(n)))</c> using
/// <see cref="MidpointRounding.AwayFromZero"/> to match TS
/// <c>Math.round</c>'s half-up rounding for positive scores (per GPT-5.5
/// round-1 review Q3).</para>
/// </summary>
public static partial class JudgeScoreParser
{
    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstIntegerRegex();

    public static JudgeScore Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string trimmed = response.Trim();

        if (trimmed.StartsWith('{') && TryParseJson(trimmed, out JudgeScore? jsonScore))
        {
            return jsonScore!;
        }

        Match match = FirstIntegerRegex().Match(trimmed);
        if (!match.Success)
        {
            // TS: `trimmed.slice(0, 120)` — JS slice clamps end past length.
            string preview = trimmed.Length <= 120 ? trimmed : trimmed[..120];
            throw new InvalidOperationException($"Could not parse score from judge response: {preview}");
        }

        // Parse as double then clamp BEFORE narrowing to int. TS
        // `Number.parseInt` produces an arbitrary-precision JS number;
        // a long digit run like "999999999999999999999" would overflow
        // `int.Parse`. Clamp first → safe int cast.
        double raw = double.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture);
        return new JudgeScore(Clamp(raw));
    }

    /// <summary>
    /// TS clampScore: <c>Math.max(0, Math.min(100, Math.round(n)))</c>.
    /// Uses <see cref="MidpointRounding.AwayFromZero"/> so 0.5 → 1 and
    /// 73.5 → 74 (TS half-up semantics for positive numbers).
    /// Clamps before narrowing to <see cref="int"/> so huge inputs
    /// don't overflow.
    /// </summary>
    private static int Clamp(double n)
    {
        double rounded = Math.Round(n, MidpointRounding.AwayFromZero);
        if (rounded <= 0) return 0;
        if (rounded >= 100) return 100;
        return (int)rounded;
    }

    private static bool TryParseJson(string trimmed, out JudgeScore? score)
    {
        score = null;
        JsonDocument document;
        try
        {
            // TS JSON.parse is strict — no trailing commas, no comments.
            // Use default options to match.
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            // TS: catch-all; fall through to numeric parsing.
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("score", out JsonElement scoreElement)) return false;
            if (scoreElement.ValueKind != JsonValueKind.Number) return false;
            if (!scoreElement.TryGetDouble(out double scoreValue)) return false;
            if (!double.IsFinite(scoreValue)) return false;

            // TS: reason ?? rationale (only string values count; rationale falls back through toOptionalString).
            string? reason = ReadStringProperty(root, "reason") ?? ReadOptionalStringProperty(root, "rationale");
            string? model = ReadOptionalStringProperty(root, "model");

            score = new JudgeScore(Clamp(scoreValue), reason, model);
            return true;
        }
    }

    /// <summary>
    /// TS <c>typeof parsed.reason === 'string' ? parsed.reason : ...</c> —
    /// any string value (including empty) is returned.
    /// </summary>
    private static string? ReadStringProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;
        return element.GetString();
    }

    /// <summary>
    /// TS <c>toOptionalString(value)</c> — only non-empty string values
    /// are kept; empty string becomes <c>undefined</c>.
    /// </summary>
    private static string? ReadOptionalStringProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;
        string? value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
