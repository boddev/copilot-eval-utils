using EvalToolkit.Core;
using EvalToolkit.Core.Concurrency;

namespace EvalToolkit.EvalGen.Tests;

/// <summary>
/// Tests for the concurrency primitives ported from
/// <c>eval-score/node/src/throttle-gate.ts</c> +
/// <c>eval-score/node/src/evaluator.ts</c>. Each test pins one TS
/// behavior so we know the C# scheduler doesn't drift from the Node
/// pipeline parity gates.
///
/// All env-mutating tests use a single <see cref="ThrottleGateFactoryTests"/>
/// collection so xUnit doesn't run them in parallel (the env helpers
/// read process-global state).
/// </summary>
public class ThrottleGateTests
{
    [Fact]
    public void Constructor_ClampsAboveHardCap()
    {
        using ThrottleGate gate = new(999);
        Assert.Equal(ThrottleGate.HardCap, gate.MaxConcurrent);
    }

    [Fact]
    public void Constructor_ClampsBelowOneToOne()
    {
        using ThrottleGate gate = new(0);
        Assert.Equal(1, gate.MaxConcurrent);
    }

    [Fact]
    public void Constructor_ClampsNegativeToOne()
    {
        using ThrottleGate gate = new(-3);
        Assert.Equal(1, gate.MaxConcurrent);
    }

    [Fact]
    public async Task RunAsync_LimitsConcurrencyToMax()
    {
        using ThrottleGate gate = new(3);
        int active = 0;
        int peak = 0;
        object peakLock = new();

        async Task Op()
        {
            int now = Interlocked.Increment(ref active);
            lock (peakLock)
            {
                if (now > peak)
                {
                    peak = now;
                }
            }
            await Task.Delay(40);
            Interlocked.Decrement(ref active);
        }

        Task[] tasks = Enumerable.Range(0, 10).Select(_ => gate.RunAsync(Op)).ToArray();
        await Task.WhenAll(tasks);

        Assert.True(peak <= 3, $"Peak concurrency {peak} exceeded gate limit 3");
        Assert.True(peak >= 2, $"Peak concurrency {peak} should reach near limit under contention");
    }

    [Fact]
    public async Task RunAsync_ReleasesSlotEvenWhenOperationThrows()
    {
        using ThrottleGate gate = new(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync<int>(() => throw new InvalidOperationException("boom")));
        // If the slot wasn't released, this second call would deadlock.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int result = await gate.RunAsync(() => Task.FromResult(42), cts.Token);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_HonorsCancellationTokenWhileWaiting()
    {
        using ThrottleGate gate = new(1);
        TaskCompletionSource holdSlot = new();
        Task hold = gate.RunAsync(() => holdSlot.Task);

        using var cts = new CancellationTokenSource();
        Task<int> blocked = gate.RunAsync(() => Task.FromResult(1), cts.Token);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        holdSlot.SetResult();
        await hold;
    }
}

[Collection("Env-Mutating")]
public class ThrottleGateFactoryTests : IDisposable
{
    private readonly string? _original;

    public ThrottleGateFactoryTests()
    {
        _original = Environment.GetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency, _original);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Create_HonorsExplicitArgumentOverEnv()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency, "4");
        using ThrottleGate gate = ThrottleGate.Create(2);
        Assert.Equal(2, gate.MaxConcurrent);
    }

    [Fact]
    public void Create_ReadsEnvWhenArgumentUnset()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency, "3");
        using ThrottleGate gate = ThrottleGate.Create();
        Assert.Equal(3, gate.MaxConcurrent);
    }

    [Fact]
    public void Create_ClampsEnvAboveHardCap()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency, "100");
        using ThrottleGate gate = ThrottleGate.Create();
        Assert.Equal(ThrottleGate.HardCap, gate.MaxConcurrent);
    }

    [Fact]
    public void Create_FallsBackToHardCapWhenEnvUnset()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency, null);
        using ThrottleGate gate = ThrottleGate.Create();
        Assert.Equal(ThrottleGate.HardCap, gate.MaxConcurrent);
    }

    [Fact]
    public void Create_FallsBackToDefaultOnUnparseableEnv()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreMaxConcurrency, "garbage");
        using ThrottleGate gate = ThrottleGate.Create();
        Assert.Equal(ThrottleGate.HardCap, gate.MaxConcurrent);
    }
}

[CollectionDefinition("Env-Mutating", DisableParallelization = true)]
#pragma warning disable CA1711 // CollectionDefinition suffix is xUnit-required naming.
public class EnvMutatingTestCollection { }
#pragma warning restore CA1711
