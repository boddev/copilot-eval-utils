namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Three message-substring classifiers ported verbatim from
/// <c>eval-gen/src/llm-client.ts</c>:
/// <list type="bullet">
///   <item><c>isRetryableCopilotApiError</c> — M365 Copilot Chat API.</item>
///   <item><c>isRetryableWorkIQError</c> — WorkIQ MCP transport.</item>
///   <item><c>isRetryableA2AError</c> — WorkIQ A2A HTTP transport.</item>
/// </list>
/// Each preserves the exact substring set the TS source matches on so
/// operator-tuned retry behavior survives the port.
/// </summary>
public static class RetryClassifiers
{
    public static bool IsRetryableCopilotApiError(Exception? err)
    {
        if (err is null) return false;
        if (err is GraphApiException g)
        {
            return g.Status == 408 || g.Status == 429 || (g.Status >= 500 && g.Status <= 599);
        }
        string message = (err.Message ?? string.Empty).ToLowerInvariant();
        if (message.Contains("did not return a conversation id") || message.Contains("returned no message text"))
        {
            return true;
        }
        return message.Contains("timed out")
            || message.Contains("timeout")
            || message.Contains("econnreset")
            || message.Contains("epipe")
            || message.Contains("etimedout")
            || message.Contains("socket hang up")
            || message.Contains("fetch failed")
            || message.Contains("network");
    }

    public static bool IsRetryableWorkIQError(Exception? err)
    {
        if (err is null) return false;
        string message = (err.Message ?? string.Empty).ToLowerInvariant();
        if (message.Contains("eula")
            || message.Contains("unauthor")
            || message.Contains("forbidden")
            || message.Contains("401")
            || message.Contains("403"))
        {
            return false;
        }
        return message.Contains("timed out")
            || message.Contains("timeout")
            || message.Contains("mcp process")
            || message.Contains("process is not running")
            || message.Contains("process exited")
            || message.Contains("econnreset")
            || message.Contains("epipe")
            || message.Contains("etimedout")
            || message.Contains("socket hang up")
            || message.Contains("429")
            || message.Contains("rate limit")
            || message.Contains("throttl")
            || message.Contains("503")
            || message.Contains("502")
            || message.Contains("504")
            || message.Contains("temporarily unavailable")
            || message.Contains("empty response");
    }

    public static bool IsRetryableA2AError(Exception? err)
    {
        if (err is null) return false;
        if (err is WorkIqA2aLlmException a)
        {
            // TS uses status==0 for "JSON-RPC error envelope" — non-retryable.
            if (a.Status == 0) return false;
            return a.Status == 408 || a.Status == 429 || (a.Status >= 500 && a.Status <= 599);
        }
        string message = (err.Message ?? string.Empty).ToLowerInvariant();
        return message.Contains("timed out")
            || message.Contains("timeout")
            || message.Contains("econnreset")
            || message.Contains("epipe")
            || message.Contains("etimedout")
            || message.Contains("socket hang up")
            || message.Contains("fetch failed")
            || message.Contains("network")
            || message.Contains("missing result.task")
            || message.Contains("no text artifact");
    }
}
