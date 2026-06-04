namespace EvalToolkit.Core.Concurrency;

/// <summary>
/// Async concurrency gate. Mirrors the TS <c>ThrottleGate</c> in
/// <c>eval-score/node/src/throttle-gate.ts</c>: at most
/// <see cref="MaxConcurrent"/> operations may run concurrently; FIFO
/// queue for callers that arrive once the gate is full.
///
/// Implementation note: backed by <see cref="SemaphoreSlim"/>, which
/// already provides async FIFO ordering and cancellation. Wrapping it
/// in a typed gate (rather than asking every caller to remember the
/// release pattern) keeps the call sites symmetric with the TS code
/// and prevents leaks when the inner operation throws.
/// </summary>
public sealed class ThrottleGate : IDisposable
{
    /// <summary>
    /// Hard upper bound on concurrency. The TS code applies the same
    /// cap in <c>createThrottleGate</c>; ported here so a user
    /// configuring a wild value via <see cref="EnvVars.EvalScoreMaxConcurrency"/>
    /// can't blow past it accidentally.
    /// </summary>
    public const int HardCap = 5;

    private readonly SemaphoreSlim _semaphore;
    private int _disposed;

    /// <summary>The actual concurrency level after clamping at <see cref="HardCap"/>.</summary>
    public int MaxConcurrent { get; }

    /// <summary>
    /// Create a gate with the given concurrency, clamped to
    /// <c>[1, <see cref="HardCap"/>]</c>.
    /// </summary>
    public ThrottleGate(int maxConcurrent)
    {
        if (maxConcurrent < 1)
        {
            maxConcurrent = 1;
        }
        if (maxConcurrent > HardCap)
        {
            maxConcurrent = HardCap;
        }
        MaxConcurrent = maxConcurrent;
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>
    /// Run <paramref name="operation"/> under the gate. Throws
    /// <see cref="OperationCanceledException"/> if cancellation fires
    /// while waiting for a slot; the operation itself is responsible
    /// for honoring cancellation once started.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            // Release only if not already disposed — racing the dispose
            // with an in-flight Run is allowed (Dispose blocks acquire,
            // not currently-running ops).
            if (Volatile.Read(ref _disposed) == 0)
            {
                _semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Run <paramref name="operation"/> under the gate (no-result form).
    /// </summary>
    public async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _semaphore.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    /// <summary>
    /// Factory mirroring TS <c>createThrottleGate(maxConcurrent?)</c>:
    /// the supplied value (or <see cref="EnvVars.EvalScoreMaxConcurrency"/>
    /// when unspecified, default <c>5</c>) is clamped to
    /// <c>[1, <see cref="HardCap"/>]</c>. Non-numeric/non-positive
    /// env values fall back to the default, then the same clamp.
    ///
    /// Wire-fidelity note: the TS code reads
    /// <c>EVALSCORE_MAX_CONCURRENCY</c> via <c>parseInt</c>, defaults
    /// to <c>5</c> when unset, and clamps to <c>min(value, 5)</c>.
    /// This factory uses <see cref="EnvHelpers.ParsePositiveIntEnv(string, int)"/>
    /// (default 5) and the same clamp, so an operator setting the env
    /// var on either side sees identical behavior.
    /// </summary>
    public static ThrottleGate Create(int? maxConcurrent = null)
    {
        int configured = maxConcurrent
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalScoreMaxConcurrency, HardCap);
        return new ThrottleGate(configured);
    }
}
