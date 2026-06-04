namespace EvalToolkit.WorkIQ;

/// <summary>
/// Options passed to WorkIQ calls. Mirrors <c>WorkIQAskOptions</c> in
/// <c>eval-score/node/src/workiq-client.ts</c>.
/// </summary>
public sealed record WorkIQAskOptions(
    string? TenantId = null,
    string? AgentId = null,
    string? ConversationId = null);

/// <summary>
/// WorkIQ answer with optional A2A/MCP metadata. Mirrors <c>WorkIQResponse</c>
/// in <c>eval-score/node/src/workiq-client.ts</c>.
/// </summary>
public sealed record WorkIQResponse(
    string Text,
    IReadOnlyList<Citation>? Citations = null,
    object? Raw = null,
    string? ConversationId = null);

/// <summary>Citation metadata returned by WorkIQ transports.</summary>
public sealed record Citation(
    string? Title = null,
    string? Url = null,
    string? SourceLocation = null,
    object? Raw = null);

/// <summary>
/// Shared WorkIQ client surface for EvalGen and EvalScore. Ports the TS
/// <c>WorkIQClient</c> interface from <c>eval-score/node/src/workiq-client.ts</c>.
/// </summary>
public interface IWorkIQClient : IAsyncDisposable
{
    Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default);

    Task<WorkIQResponse> AskWithMetadataAsync(
        string prompt,
        WorkIQAskOptions? options = null,
        CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Base WorkIQ client exception.</summary>
public class WorkIQException : Exception
{
    public WorkIQException(string message)
        : base(message)
    {
    }

    public WorkIQException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>HTTP failure surfaced by the A2A WorkIQ client.</summary>
public sealed class WorkIQHttpException : WorkIQException
{
    public WorkIQHttpException(int statusCode, string? retryAfterHeader, string? body)
        : base(BuildMessage(statusCode, retryAfterHeader, body))
    {
        StatusCode = statusCode;
        RetryAfterHeader = retryAfterHeader;
        Body = body;
    }

    public int StatusCode { get; }

    public string? RetryAfterHeader { get; }

    public string? Body { get; }

    private static string BuildMessage(int statusCode, string? retryAfterHeader, string? body)
    {
        string retry = string.IsNullOrWhiteSpace(retryAfterHeader)
            ? string.Empty
            : $" retry-after={retryAfterHeader}";
        string suffix = string.IsNullOrWhiteSpace(body) ? string.Empty : $": {body}";
        return $"WorkIQ A2A HTTP {statusCode}{retry}{suffix}".Trim();
    }
}
