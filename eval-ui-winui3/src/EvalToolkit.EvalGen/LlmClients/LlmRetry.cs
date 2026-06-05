namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Local retry helper for the EvalGen LLM clients. Ports the simple
/// retry pattern shared by <c>WorkIQCopilotClient</c>,
/// <c>WorkIQA2AClient</c>, and <c>Microsoft365CopilotChatClient</c> in
/// <c>eval-gen/src/llm-client.ts</c>.
///
/// <para>Behavior contract (per TS source):</para>
/// <list type="bullet">
///   <item>Attempt counter starts at <c>1</c> and stops at <c>max(1, MaxAttempts)</c>.</item>
///   <item>Backoff is <c>baseMs * 2^(attempt-1) + jitter * baseMs</c>
///         where <c>jitter ∈ [0, 1)</c> — no max cap (intentionally
///         simpler than <c>WorkIQRetry</c>).</item>
///   <item>If <c>retryable(err)</c> is false OR this was the last attempt,
///         the exception is re-thrown immediately.</item>
///   <item>Per-attempt warning is emitted via <paramref name="onRetry"/>.</item>
///   <item>Azure OpenAI has NO retry — do not wrap it (see GPT-5.5
///         review note: TS Azure path uses a single fetch).</item>
/// </list>
/// </summary>
internal static class LlmRetry
{
    internal sealed class Options
    {
        public int MaxAttempts { get; init; } = 3;
        public int BackoffBaseMs { get; init; } = 2000;
        public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
        public Func<int, TimeSpan, Task>? Sleep { get; init; }
    }

    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        Func<Exception, bool> retryable,
        Options options,
        Action<int, int, Exception, TimeSpan>? onRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(retryable);
        ArgumentNullException.ThrowIfNull(options);

        int maxAttempts = Math.Max(1, options.MaxAttempts);
        int baseMs = Math.Max(0, options.BackoffBaseMs);

        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception err)
            {
                lastError = err;
                if (!retryable(err) || attempt == maxAttempts)
                {
                    throw;
                }

                double exponential = baseMs * Math.Pow(2, attempt - 1);
                double jitter = options.Jitter() * baseMs;
                TimeSpan delay = TimeSpan.FromMilliseconds(exponential + jitter);

                onRetry?.Invoke(attempt, maxAttempts, err, delay);

                if (options.Sleep is not null)
                {
                    await options.Sleep(attempt, delay).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw lastError ?? new InvalidOperationException("LlmRetry exhausted attempts with no captured error.");
    }
}
