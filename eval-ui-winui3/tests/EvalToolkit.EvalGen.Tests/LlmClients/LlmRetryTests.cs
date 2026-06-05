using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

/// <summary>
/// Tests for the internal LlmRetry helper. Use InternalsVisibleTo (already
/// configured on the EvalGen project) to access it.
/// </summary>
public sealed class LlmRetryTests
{
    [Fact]
    public async Task RunAsync_FirstAttemptSucceeds_NoRetry()
    {
        int calls = 0;
        int result = await LlmRetry.RunAsync<int>(
            _ => { calls++; return Task.FromResult(7); },
            _ => true,
            new LlmRetry.Options { MaxAttempts = 3, BackoffBaseMs = 0 },
            onRetry: null,
            CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_RetriesThenSucceeds()
    {
        int calls = 0;
        int onRetryCount = 0;
        int result = await LlmRetry.RunAsync<int>(
            _ =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("timed out");
                return Task.FromResult(99);
            },
            _ => true,
            new LlmRetry.Options
            {
                MaxAttempts = 3,
                BackoffBaseMs = 0,
                Jitter = () => 0,
                Sleep = (_, _) => Task.CompletedTask,
            },
            onRetry: (_, _, _, _) => onRetryCount++,
            CancellationToken.None);

        Assert.Equal(99, result);
        Assert.Equal(3, calls);
        Assert.Equal(2, onRetryCount);
    }

    [Fact]
    public async Task RunAsync_NonRetryableException_ThrowsImmediately()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await LlmRetry.RunAsync<int>(
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("auth bad");
                },
                _ => false,
                new LlmRetry.Options { MaxAttempts = 5, BackoffBaseMs = 0 },
                onRetry: null,
                CancellationToken.None);
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_ExhaustsAttempts_ThrowsLastError()
    {
        int calls = 0;
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await LlmRetry.RunAsync<int>(
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("attempt " + calls);
                },
                _ => true,
                new LlmRetry.Options
                {
                    MaxAttempts = 3,
                    BackoffBaseMs = 0,
                    Jitter = () => 0,
                    Sleep = (_, _) => Task.CompletedTask,
                },
                onRetry: null,
                CancellationToken.None);
        });
        Assert.Equal(3, calls);
        Assert.Equal("attempt 3", ex.Message);
    }

    [Fact]
    public async Task RunAsync_BackoffDelayFollowsExponentialFormula()
    {
        var delays = new List<TimeSpan>();
        try
        {
            await LlmRetry.RunAsync<int>(
                _ => throw new InvalidOperationException("retry me"),
                _ => true,
                new LlmRetry.Options
                {
                    MaxAttempts = 4,
                    BackoffBaseMs = 100,
                    Jitter = () => 0,
                    Sleep = (_, d) => { delays.Add(d); return Task.CompletedTask; },
                },
                onRetry: null,
                CancellationToken.None);
        }
        catch (InvalidOperationException) { /* expected */ }

        // base * 2^(attempt-1): attempt=1 -> 100, attempt=2 -> 200, attempt=3 -> 400
        Assert.Equal(3, delays.Count);
        Assert.Equal(100, delays[0].TotalMilliseconds);
        Assert.Equal(200, delays[1].TotalMilliseconds);
        Assert.Equal(400, delays[2].TotalMilliseconds);
    }

    [Fact]
    public async Task RunAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await LlmRetry.RunAsync<int>(
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(1);
                },
                _ => true,
                new LlmRetry.Options { MaxAttempts = 3, BackoffBaseMs = 0 },
                onRetry: null,
                cts.Token);
        });
    }
}
