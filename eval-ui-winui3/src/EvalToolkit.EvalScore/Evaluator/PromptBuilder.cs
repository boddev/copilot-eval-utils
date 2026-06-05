namespace EvalToolkit.EvalScore.Evaluator;

/// <summary>
/// Builds the full prompt sent to a WorkIQ client. Mirrors TS
/// <c>buildPrompt(question, systemPrompt?, connectorId?, connectorPromptHint = true)</c>
/// in <c>eval-score/node/src/workiq-client.ts</c>.
///
/// <para>Composition rules (preserve exactly):
/// <list type="number">
///   <item>When <paramref name="connectorId"/> is non-empty AND
///     <paramref name="connectorPromptHint"/> is true, prepend
///     <c>"Target Microsoft 365 Copilot connector ID: {id}. Always search this connector before answering."</c>.</item>
///   <item>When <paramref name="systemPrompt"/> is non-empty, append it as the next context block.</item>
///   <item>Join context blocks with <c>\n\n</c>, then append
///     <c>\n\n{question}</c>. With zero context blocks, the question is
///     returned verbatim (no trailing newline).</item>
/// </list></para>
///
/// <para><b>TS default for <c>connectorPromptHint</c> is <c>true</c></b> —
/// keep this default on the C# side too. The <see cref="EvaluateOptions"/>
/// surface uses <c>bool?</c> so callers can leave the default in place;
/// resolution to <c>true</c> happens here.</para>
/// </summary>
public static class PromptBuilder
{
    public static string BuildPrompt(
        string question,
        string? systemPrompt = null,
        string? connectorId = null,
        bool connectorPromptHint = true)
    {
        ArgumentNullException.ThrowIfNull(question);
        var contextParts = new List<string>(2);
        if (!string.IsNullOrEmpty(connectorId) && connectorPromptHint)
        {
            contextParts.Add(
                $"Target Microsoft 365 Copilot connector ID: {connectorId}. Always search this connector before answering.");
        }
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            contextParts.Add(systemPrompt);
        }
        if (contextParts.Count == 0)
        {
            return question;
        }
        return string.Join("\n\n", contextParts) + "\n\n" + question;
    }
}
