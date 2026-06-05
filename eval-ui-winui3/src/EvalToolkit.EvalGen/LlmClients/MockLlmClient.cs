namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Mock LLM client for tests. Ports <c>MockLLMClient</c> from
/// <c>eval-gen/src/llm-client.ts</c>: callers register
/// (promptSubstring → response) mappings and the first matching
/// substring wins; otherwise the default response is returned.
/// </summary>
public sealed class MockLlmClient : ILlmClient
{
    private readonly Dictionary<string, object?> _responses = [];
    private readonly object? _defaultResponse;

    public MockLlmClient(object? defaultResponse = null)
    {
        _defaultResponse = defaultResponse ?? new { };
    }

    public void SetResponse(string promptSubstring, object response)
    {
        ArgumentNullException.ThrowIfNull(promptSubstring);
        ArgumentNullException.ThrowIfNull(response);
        _responses[promptSubstring] = response;
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        foreach ((string substring, object? response) in _responses)
        {
            if (prompt.Contains(substring, StringComparison.Ordinal))
            {
                return Task.FromResult((T)response!);
            }
        }
        return Task.FromResult((T)_defaultResponse!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
