using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

public sealed class RetryClassifiersTests
{
    [Theory]
    [InlineData("Request timed out", true)]
    [InlineData("did not return a conversation id", true)]
    [InlineData("returned no message text", true)]
    [InlineData("fetch failed", true)]
    [InlineData("Network unreachable", true)]
    [InlineData("Bad request validation failed", false)]
    public void IsRetryableCopilotApiError_ByMessage(string message, bool expected)
    {
        Assert.Equal(expected, RetryClassifiers.IsRetryableCopilotApiError(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public void IsRetryableCopilotApiError_GraphStatus(int status, bool expected)
    {
        Assert.Equal(expected, RetryClassifiers.IsRetryableCopilotApiError(new GraphApiException(status, "body")));
    }

    [Theory]
    [InlineData("Request timed out", true)]
    [InlineData("WorkIQ MCP process exited", true)]
    [InlineData("process is not running", true)]
    [InlineData("HTTP 429 received", true)]
    [InlineData("HTTP 503 received", true)]
    [InlineData("temporarily unavailable", true)]
    [InlineData("empty response", true)]
    [InlineData("Throttled by upstream", true)]
    [InlineData("Unauthorized (401)", false)]   // 401 substring trips the non-retryable guard.
    [InlineData("Forbidden (403)", false)]      // 403 likewise.
    [InlineData("Please accept the EULA", false)]
    public void IsRetryableWorkIQError(string message, bool expected)
    {
        Assert.Equal(expected, RetryClassifiers.IsRetryableWorkIQError(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(400, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    public void IsRetryableA2AError_ByStatus(int status, bool expected)
    {
        Assert.Equal(expected, RetryClassifiers.IsRetryableA2AError(new WorkIqA2aLlmException(status, "body")));
    }

    [Theory]
    [InlineData("network down", true)]
    [InlineData("Work IQ A2A response is missing result.task", true)]
    [InlineData("no text artifact", true)]
    [InlineData("validation failed", false)]
    public void IsRetryableA2AError_ByMessage(string message, bool expected)
    {
        Assert.Equal(expected, RetryClassifiers.IsRetryableA2AError(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsRetryable_NullErr_ReturnsFalse()
    {
        Assert.False(RetryClassifiers.IsRetryableCopilotApiError(null));
        Assert.False(RetryClassifiers.IsRetryableWorkIQError(null));
        Assert.False(RetryClassifiers.IsRetryableA2AError(null));
    }
}
