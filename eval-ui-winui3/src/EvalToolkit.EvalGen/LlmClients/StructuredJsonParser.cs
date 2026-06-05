using System.Text.Json;
using System.Text.RegularExpressions;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Ports <c>parseStructuredJson</c> from <c>eval-gen/src/llm-client.ts</c>.
///
/// Extraction order (preserved exactly from TS — GPT-5.5 review flagged
/// that we must NOT fall through to brace extraction when a fenced block
/// is found but invalid):
/// <list type="number">
///   <item>Strip SGR ANSI color escapes <c>\u001b[…m</c> and trim.</item>
///   <item>Try <c>JSON.parse(stripped)</c>.</item>
///   <item>Look for a fenced code block (<c>```json … ```</c>). If found,
///         parse the inside — and throw if parsing fails (no fallback).</item>
///   <item>Otherwise slice from the first <c>{</c> to the last <c>}</c>
///         and parse.</item>
///   <item>If none of the above produced JSON, throw the TS error
///         <c>"LLM response did not contain a JSON object"</c>.</item>
/// </list>
/// </summary>
public static partial class StructuredJsonParser
{
    [GeneratedRegex(@"\u001b\[[0-9;]*m")]
    private static partial Regex AnsiSgrRegex();

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex FencedRegex();

    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web);

    public static T Parse<T>(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string stripped = AnsiSgrRegex().Replace(content, string.Empty).Trim();

        if (TryDeserialize<T>(stripped, out T? direct))
        {
            return direct!;
        }

        Match fenced = FencedRegex().Match(stripped);
        if (fenced.Success && fenced.Groups[1].Success)
        {
            // TS throws here if the fenced block is invalid — no fallback to brace extraction.
            return JsonSerializer.Deserialize<T>(fenced.Groups[1].Value.Trim(), s_options)
                ?? throw new InvalidOperationException("LLM response did not contain a JSON object");
        }

        int start = stripped.IndexOf('{');
        int end = stripped.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return JsonSerializer.Deserialize<T>(stripped.AsSpan(start, end - start + 1), s_options)
                ?? throw new InvalidOperationException("LLM response did not contain a JSON object");
        }

        throw new InvalidOperationException("LLM response did not contain a JSON object");
    }

    private static bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, s_options);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
