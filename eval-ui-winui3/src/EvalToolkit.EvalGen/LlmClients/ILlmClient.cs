namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Uniform interface for the six LLM providers in <c>eval-gen/src/llm-client.ts</c>.
/// Mirrors the TS <c>LLMClient</c> interface in <c>eval-gen/src/types.ts</c>:
/// optional <c>authenticate()</c> preflight, mandatory
/// <c>generateStructured&lt;T&gt;()</c>, optional <c>close()</c>.
/// </summary>
public interface ILlmClient : IAsyncDisposable
{
    /// <summary>
    /// Optional auth/initialization probe. Providers that don't need one
    /// can return immediately. Mirrors TS <c>authenticate?()</c>.
    /// </summary>
    Task AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a structured JSON response. <paramref name="schemaDescription"/>
    /// is a free-form natural-language schema hint that gets injected into
    /// the system prompt by <see cref="StructuredPromptBuilder"/>. The raw
    /// response is parsed by <see cref="StructuredJsonParser"/> using the
    /// same extraction rules as the TS source.
    /// </summary>
    Task<T> GenerateStructuredAsync<T>(
        string prompt,
        string schemaDescription,
        CancellationToken cancellationToken = default);
}
