using EvalToolkit.Core;

namespace EvalToolkit.WorkIQ;

/// <summary>Shared defaults for WorkIQ transports and retry behavior.</summary>
public static class WorkIQOptionsDefaults
{
    public const int DefaultTimeoutMs = 300_000;
    public const int DefaultMaxAttempts = 3;
    public const int DefaultBackoffBaseMs = 2_000;
    public const int DefaultBackoffMaxMs = 60_000;

    public static int ParseTimeoutMs()
    {
        int evalGenFallback = EnvHelpers.ParsePositiveIntEnv(
            EnvVars.EvalGenLlmTimeoutMs,
            DefaultTimeoutMs);
        return EnvHelpers.ParsePositiveIntEnv(
            EnvVars.EvalScoreWorkIqTimeoutMs,
            evalGenFallback);
    }
}

/// <summary>
/// Retry knobs matching <c>withRetry</c> in
/// <c>eval-score/node/src/workiq-client.ts</c>.
/// </summary>
public sealed class WorkIQRetryOptions
{
    public int MaxAttempts { get; init; } = EnvHelpers.ParsePositiveIntEnv(
        EnvVars.EvalScoreWorkIqMaxAttempts,
        WorkIQOptionsDefaults.DefaultMaxAttempts);

    public int BackoffBaseMs { get; init; } = EnvHelpers.ParsePositiveIntEnv(
        EnvVars.EvalScoreWorkIqBackoffMs,
        WorkIQOptionsDefaults.DefaultBackoffBaseMs);

    public int BackoffMaxMs { get; init; } = EnvHelpers.ParsePositiveIntEnv(
        EnvVars.EvalScoreWorkIqBackoffMaxMs,
        WorkIQOptionsDefaults.DefaultBackoffMaxMs);

    /// <summary>
    /// Jitter source returning a value in [0, 1). Defaults to
    /// <see cref="Random.Shared"/>; tests can inject 0 for deterministic
    /// TS-equivalent backoff assertions.
    /// </summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    public static WorkIQRetryOptions FromValues(int? maxAttempts, int? backoffBaseMs, int? backoffMaxMs = null)
    {
        return new WorkIQRetryOptions
        {
            MaxAttempts = Math.Max(1, maxAttempts ?? EnvHelpers.ParsePositiveIntEnv(
                EnvVars.EvalScoreWorkIqMaxAttempts,
                WorkIQOptionsDefaults.DefaultMaxAttempts)),
            BackoffBaseMs = Math.Max(0, backoffBaseMs ?? EnvHelpers.ParsePositiveIntEnv(
                EnvVars.EvalScoreWorkIqBackoffMs,
                WorkIQOptionsDefaults.DefaultBackoffBaseMs)),
            BackoffMaxMs = Math.Max(0, backoffMaxMs ?? EnvHelpers.ParsePositiveIntEnv(
                EnvVars.EvalScoreWorkIqBackoffMaxMs,
                WorkIQOptionsDefaults.DefaultBackoffMaxMs)),
        };
    }
}

/// <summary>Options for the persistent MCP <c>workiq mcp</c> client.</summary>
public sealed class CliWorkIQClientOptions
{
    public int? TimeoutMs { get; init; }

    public int? MaxAttempts { get; init; }

    public int? BackoffBaseMs { get; init; }

    public int? BackoffMaxMs { get; init; }

    public WorkIQRetryOptions? RetryOptions { get; init; }
}

/// <summary>Options for the HTTP A2A WorkIQ client.</summary>
public sealed class A2AWorkIQClientOptions
{
    public string? Endpoint { get; init; }

    public string? AccessToken { get; init; }

    public string? TokenCommand { get; init; }

    public string? AuthMode { get; init; }

    public IA2ATokenProvider? TokenProvider { get; init; }

    public HttpClient? HttpClient { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxAttempts { get; init; }

    public int? BackoffBaseMs { get; init; }

    public int? BackoffMaxMs { get; init; }

    public WorkIQRetryOptions? RetryOptions { get; init; }
}
