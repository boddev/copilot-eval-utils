using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// Score parser ported from <c>parseJudgeScore</c> in
/// <c>eval-score/node/src/judge-providers.ts</c>.
/// </summary>
public static partial class WorkIQScoreParser
{
    public static int ParseScore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("score", out JsonElement scoreElement)
                    && scoreElement.ValueKind == JsonValueKind.Number
                    && scoreElement.TryGetDouble(out double jsonScore)
                    && double.IsFinite(jsonScore))
                {
                    return ClampScore(jsonScore);
                }
            }
            catch (JsonException)
            {
                // Fall through to numeric parsing for wrapped/invalid JSON.
            }
        }

        Match match = FirstIntegerRegex().Match(trimmed);
        if (!match.Success)
        {
            string preview = trimmed.Length <= 120 ? trimmed : trimmed[..120];
            throw new WorkIQException($"Could not parse score from judge response: {preview}");
        }

        if (!double.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double parsed))
        {
            return 100;
        }
        return ClampScore(parsed);
    }

    private static int ClampScore(double score)
    {
        double rounded = Math.Floor(score + 0.5d);
        return (int)Math.Max(0, Math.Min(100, rounded));
    }

    [GeneratedRegex("\\d+", RegexOptions.CultureInvariant)]
    private static partial Regex FirstIntegerRegex();
}
