namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Builds the system+user prompt sent to every LLM provider. Ported from
/// <c>buildStructuredPrompt</c> in <c>eval-gen/src/llm-client.ts</c>.
/// The exact string format (including the leading sentence and the two
/// blank-line separators) is part of the TS contract — providers like
/// Azure OpenAI hash this and reviewers asked we keep it byte-for-byte.
/// </summary>
public static class StructuredPromptBuilder
{
    public static string Build(string prompt, string schemaDescription)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);
        return $"You are a precise data analysis assistant. Always respond with valid JSON matching the requested schema.\n\n{schemaDescription}\n\n{prompt}";
    }
}
