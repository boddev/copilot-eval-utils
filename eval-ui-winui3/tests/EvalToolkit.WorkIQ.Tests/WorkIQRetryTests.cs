using System.Diagnostics;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.WorkIQ.Tests;

public class WorkIQRetryTests
{
    [Theory]
    [InlineData("Timed out waiting for MCP response")]
    [InlineData("timeout while reading")]
    [InlineData("MCP process failed")]
    [InlineData("process is not running")]
    [InlineData("process exited unexpectedly")]
    [InlineData("ECONNRESET")]
    [InlineData("EPIPE")]
    [InlineData("ETIMEDOUT")]
    [InlineData("socket hang up")]
    [InlineData("HTTP 429")]
    [InlineData("rate limit exceeded")]
    [InlineData("throttled by service")]
    [InlineData("HTTP 503")]
    [InlineData("HTTP 502")]
    [InlineData("HTTP 504")]
    [InlineData("temporarily unavailable")]
    [InlineData("empty response")]
    public void IsRetryableWorkIQError_MatchesTsMessagePatterns(string message)
    {
        Assert.True(WorkIQRetry.IsRetryableWorkIQError(new WorkIQException(message)));
    }

    [Theory]
    [InlineData("EULA must be accepted")]
    [InlineData("Unauthorized")]
    [InlineData("forbidden")]
    [InlineData("HTTP 401")]
    [InlineData("HTTP 403")]
    public void IsRetryableWorkIQError_DoesNotRetryAuthOrEulaFailures(string message)
    {
        Assert.False(WorkIQRetry.IsRetryableWorkIQError(new WorkIQException(message)));
    }

    [Theory]
    [InlineData("You've reached the limit on the number of requests.")]
    [InlineData("Youve reached the limit on the number of requests.")]
    [InlineData("We reached your request limit.")]
    [InlineData("Too many requests right now.")]
    [InlineData("rate-limited")]
    [InlineData("Please try again in a little while.")]
    [InlineData("Please try again in a few minutes.")]
    public void LooksLikeRateLimitText_MatchesTsBodyPatterns(string text)
    {
        Assert.True(WorkIQRetry.LooksLikeRateLimitText(text));
    }

    [Fact]
    public void LooksLikeRateLimitText_IgnoresNullAndOrdinaryText()
    {
        Assert.False(WorkIQRetry.LooksLikeRateLimitText(null));
        Assert.False(WorkIQRetry.LooksLikeRateLimitText("Here is the answer."));
    }

    [Fact]
    public void ParseRetryAfterMs_ReadsHttpHeaderSeconds()
    {
        var exception = new WorkIQHttpException(429, "7", "{} ");
        Assert.Equal(7000, WorkIQRetry.ParseRetryAfterMs(exception));
    }

    [Theory]
    [InlineData("{\"retryAfter\":4}", 4000)]
    [InlineData("{\"retry_after\":5}", 5000)]
    [InlineData("{\"error\":{\"retry-after\":6}}", 6000)]
    [InlineData("{\"retryAfterMs\":250}", 250)]
    public void ParseRetryAfterMs_ReadsBodyJsonForms(string body, double expected)
    {
        var exception = new WorkIQHttpException(429, null, body);
        Assert.Equal(expected, WorkIQRetry.ParseRetryAfterMs(exception));
    }

    [Fact]
    public void ParseRetryAfterMs_ReadsTsMessageFallback()
    {
        Assert.Equal(3000, WorkIQRetry.ParseRetryAfterMs(new WorkIQException("HTTP 429 retry-after=3: slow down")));
        Assert.Equal(2000, WorkIQRetry.ParseRetryAfterMs(new WorkIQException("HTTP 429 retry-after: 2")));
    }

    [Fact]
    public void ComputeBackoffMs_MatchesTsExponentialPlusJitterAndCap()
    {
        var options = new WorkIQRetryOptions
        {
            BackoffBaseMs = 2000,
            BackoffMaxMs = 5000,
            Jitter = () => 0.5,
        };
        Assert.Equal(3000, WorkIQRetry.ComputeBackoffMs(options, 1));
        Assert.Equal(5000, WorkIQRetry.ComputeBackoffMs(options, 3));
    }

    [Fact]
    public async Task BuildResiliencePipeline_RetriesRetryableFailuresAndResetsClient()
    {
        var client = new RetryTrackingClient();
        var options = new WorkIQRetryOptions
        {
            MaxAttempts = 3,
            BackoffBaseMs = 1,
            BackoffMaxMs = 5,
            Jitter = () => 0,
        };
        int attempts = 0;
        var stopwatch = Stopwatch.StartNew();
        string result = await WorkIQRetry.BuildResiliencePipeline<string>(client, options).ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new TimeoutException("Timed out waiting for MCP response");
            }
            if (attempts == 2)
            {
                throw new WorkIQHttpException(503, null, string.Empty);
            }
            return new ValueTask<string>("ok");
        }, CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, client.ResetCount);
        Assert.True(stopwatch.ElapsedMilliseconds >= 2);
    }

    [Fact]
    public async Task BuildResiliencePipeline_StopsAtMaxAttempts()
    {
        var client = new RetryTrackingClient();
        var options = new WorkIQRetryOptions
        {
            MaxAttempts = 2,
            BackoffBaseMs = 0,
            BackoffMaxMs = 0,
            Jitter = () => 0,
        };
        int attempts = 0;
        await Assert.ThrowsAsync<WorkIQHttpException>(async () =>
            await WorkIQRetry.BuildResiliencePipeline<string>(client, options).ExecuteAsync<string>(_ =>
            {
                attempts++;
                throw new WorkIQHttpException(429, null, string.Empty);
            }, CancellationToken.None));
        Assert.Equal(2, attempts);
        Assert.Equal(1, client.ResetCount);
    }

    private sealed class RetryTrackingClient : IWorkIQClient
    {
        public int ResetCount { get; private set; }

        public Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(prompt);
        }

        public Task<WorkIQResponse> AskWithMetadataAsync(string prompt, WorkIQAskOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkIQResponse(prompt));
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
