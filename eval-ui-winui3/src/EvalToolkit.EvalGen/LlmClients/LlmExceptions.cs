namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Thrown when the Microsoft Graph beta Copilot Chat API returns a
/// non-success status. Mirrors TS <c>GraphApiError</c> in
/// <c>eval-gen/src/llm-client.ts</c>. The <see cref="Status"/> and
/// <see cref="ResponseBody"/> are surfaced for retry classification.
/// </summary>
public sealed class GraphApiException : Exception
{
    public int Status { get; }
    public string ResponseBody { get; }

    public GraphApiException(int status, string responseBody)
        : base($"Microsoft 365 Copilot Chat API error ({status}): {responseBody}")
    {
        Status = status;
        ResponseBody = responseBody ?? string.Empty;
    }
}

/// <summary>
/// Thrown when the WorkIQ A2A HTTP endpoint returns an error envelope
/// or a non-2xx status. Mirrors TS <c>WorkIQA2AError</c> in
/// <c>eval-gen/src/llm-client.ts</c>. <see cref="Status"/> == 0 means a
/// JSON-RPC error envelope (no HTTP status — TS uses 0 as a sentinel).
/// </summary>
public sealed class WorkIqA2aLlmException : Exception
{
    public int Status { get; }
    public string ResponseBody { get; }

    public WorkIqA2aLlmException(int status, string responseBody)
        : base(status > 0
            ? $"Work IQ A2A HTTP {status}: {responseBody}"
            : responseBody)
    {
        Status = status;
        ResponseBody = responseBody ?? string.Empty;
    }
}
