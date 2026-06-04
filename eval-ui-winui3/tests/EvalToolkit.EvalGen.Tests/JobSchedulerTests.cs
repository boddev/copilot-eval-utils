using EvalToolkit.Core.Concurrency;

namespace EvalToolkit.EvalGen.Tests;

/// <summary>
/// Tests for <see cref="JobScheduler{TRow}"/>. Pin the worker-loop
/// contract from TS <c>evaluator.ts</c>:
/// <list type="bullet">
///   <item>Workers = <c>min(concurrency, jobs.Count, ThrottleGate.HardCap)</c>.</item>
///   <item>Rows within a job execute sequentially.</item>
///   <item>Caller-returned conversationId chains into the next row of the same job.</item>
///   <item>Cancellation aborts further row processing.</item>
/// </list>
/// </summary>
public class JobSchedulerTests
{
    private static EvaluationJob<int>[] JobsOf(params int[][] jobs) =>
        jobs.Select(rows => new EvaluationJob<int>
        {
            RowIndices = rows,
            ThreadKey = string.Join("-", rows),
        }).ToArray();

    [Fact]
    public async Task RunAsync_ProcessesEveryRowOnce()
    {
        int[] rows = Enumerable.Range(0, 6).ToArray();
        var jobs = JobsOf(new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3, 4 }, new[] { 5 });
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();

        JobScheduler<int> scheduler = new(rows, jobs);
        await scheduler.RunAsync(
            (idx, _, _) =>
            {
                seen.Add(idx);
                return Task.FromResult<string?>(null);
            },
            new JobSchedulerOptions { Concurrency = 3, DelayMs = 0 });

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, seen.OrderBy(x => x));
    }

    [Fact]
    public async Task RunAsync_ChainsConversationIdWithinJob()
    {
        int[] rows = new[] { 10, 20, 30 };
        var jobs = JobsOf(new[] { 0, 1, 2 });
        var observed = new List<(int Row, string? Inherited)>();
        var observedLock = new object();

        JobScheduler<int> scheduler = new(rows, jobs);
        await scheduler.RunAsync(
            (idx, inherited, _) =>
            {
                lock (observedLock)
                {
                    observed.Add((idx, inherited));
                }
                return Task.FromResult<string?>($"conv-{idx}");
            },
            new JobSchedulerOptions { Concurrency = 1, DelayMs = 0 });

        Assert.Equal(3, observed.Count);
        Assert.Equal((0, null), observed[0]);
        Assert.Equal((1, "conv-0"), observed[1]);
        Assert.Equal((2, "conv-1"), observed[2]);
    }

    [Fact]
    public async Task RunAsync_DoesNotChainAcrossJobs()
    {
        int[] rows = new[] { 0, 1 };
        var jobs = JobsOf(new[] { 0 }, new[] { 1 });
        var observed = new System.Collections.Concurrent.ConcurrentBag<(int Row, string? Inherited)>();

        JobScheduler<int> scheduler = new(rows, jobs);
        await scheduler.RunAsync(
            (idx, inherited, _) =>
            {
                observed.Add((idx, inherited));
                return Task.FromResult<string?>($"conv-{idx}");
            },
            new JobSchedulerOptions { Concurrency = 1, DelayMs = 0 });

        // Both rows are first-in-their-job, so both inherit null.
        Assert.All(observed, o => Assert.Null(o.Inherited));
    }

    [Fact]
    public async Task RunAsync_LimitsConcurrencyToHardCap()
    {
        int[] rows = Enumerable.Range(0, 20).ToArray();
        var jobs = rows.Select(r => new EvaluationJob<int>
        {
            RowIndices = new[] { r },
            ThreadKey = r.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).ToArray();

        int active = 0;
        int peak = 0;
        var peakLock = new object();

        JobScheduler<int> scheduler = new(rows, jobs);
        await scheduler.RunAsync(
            async (_, _, _) =>
            {
                int now = Interlocked.Increment(ref active);
                lock (peakLock)
                {
                    if (now > peak)
                    {
                        peak = now;
                    }
                }
                await Task.Delay(20, CancellationToken.None);
                Interlocked.Decrement(ref active);
                return null;
            },
            new JobSchedulerOptions { Concurrency = 999, DelayMs = 0 });

        Assert.True(peak <= ThrottleGate.HardCap, $"Peak {peak} exceeded HardCap {ThrottleGate.HardCap}");
    }

    [Fact]
    public async Task RunAsync_AppliesDelayBetweenRowsInJob()
    {
        int[] rows = new[] { 0, 1 };
        var jobs = JobsOf(new[] { 0, 1 });

        var stamps = new List<long>();
        var stampsLock = new object();
        long start = Environment.TickCount64;

        JobScheduler<int> scheduler = new(rows, jobs);
        await scheduler.RunAsync(
            (_, _, _) =>
            {
                lock (stampsLock)
                {
                    stamps.Add(Environment.TickCount64 - start);
                }
                return Task.FromResult<string?>(null);
            },
            new JobSchedulerOptions { Concurrency = 1, DelayMs = 100 });

        Assert.Equal(2, stamps.Count);
        // Allow some slack — Task.Delay is not promise-perfect.
        Assert.True(stamps[1] - stamps[0] >= 80,
            $"Expected ~100ms delay between rows in job, observed {stamps[1] - stamps[0]}ms");
    }

    [Fact]
    public async Task RunAsync_NoJobs_ReturnsImmediately()
    {
        JobScheduler<int> scheduler = new(Array.Empty<int>(), Array.Empty<EvaluationJob<int>>());
        bool called = false;
        await scheduler.RunAsync((_, _, _) =>
        {
            called = true;
            return Task.FromResult<string?>(null);
        });
        Assert.False(called);
    }

    [Fact]
    public async Task RunAsync_HonorsCancellation()
    {
        int[] rows = Enumerable.Range(0, 50).ToArray();
        var jobs = rows.Select(r => new EvaluationJob<int>
        {
            RowIndices = new[] { r },
            ThreadKey = r.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).ToArray();

        using var cts = new CancellationTokenSource();
        int processed = 0;

        JobScheduler<int> scheduler = new(rows, jobs);
        Task run = scheduler.RunAsync(
            async (_, _, ct) =>
            {
                int n = Interlocked.Increment(ref processed);
                if (n >= 3)
                {
                    cts.Cancel();
                }
                await Task.Delay(10, ct);
                return null;
            },
            new JobSchedulerOptions { Concurrency = 1, DelayMs = 0 },
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(processed < rows.Length, $"Processed {processed}/{rows.Length} despite cancellation");
    }
}
