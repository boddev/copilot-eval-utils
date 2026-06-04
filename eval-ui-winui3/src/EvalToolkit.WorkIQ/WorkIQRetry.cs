using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Polly;
using Polly.Retry;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// Byte-faithful port of the bespoke WorkIQ retry helpers in
/// <c>eval-score/node/src/workiq-client.ts</c>. Polly defaults are not
/// used because the TS classifier is message-text based.
/// </summary>
public static partial class WorkIQRetry
{
    private static readonly Regex[] RateLimitBodyPatterns =
    [
        YouveReachedLimitRegex(),
        ReachedRequestLimitRegex(),
        TooManyRequestsRegex(),
        RateLimitRegex(),
        TryAgainSoonRegex(),
    ];

    public static bool LooksLikeRateLimitText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        return RateLimitBodyPatterns.Any(rx => rx.IsMatch(text));
    }

    public static bool IsRetryableWorkIQError(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        string message = exception.Message.ToLowerInvariant();
        if (message.Contains("eula", StringComparison.Ordinal)
            || message.Contains("unauthor", StringComparison.Ordinal)
            || message.Contains("forbidden", StringComparison.Ordinal)
            || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("403", StringComparison.Ordinal))
        {
            return false;
        }

        return message.Contains("timed out", StringComparison.Ordinal)
            || message.Contains("timeout", StringComparison.Ordinal)
            || message.Contains("mcp process", StringComparison.Ordinal)
            || message.Contains("process is not running", StringComparison.Ordinal)
            || message.Contains("process exited", StringComparison.Ordinal)
            || message.Contains("econnreset", StringComparison.Ordinal)
            || message.Contains("epipe", StringComparison.Ordinal)
            || message.Contains("etimedout", StringComparison.Ordinal)
            || message.Contains("socket hang up", StringComparison.Ordinal)
            || message.Contains("429", StringComparison.Ordinal)
            || message.Contains("rate limit", StringComparison.Ordinal)
            || message.Contains("throttl", StringComparison.Ordinal)
            || message.Contains("503", StringComparison.Ordinal)
            || message.Contains("502", StringComparison.Ordinal)
            || message.Contains("504", StringComparison.Ordinal)
            || message.Contains("temporarily unavailable", StringComparison.Ordinal)
            || message.Contains("empty response", StringComparison.Ordinal);
    }

    public static double? ParseRetryAfterMs(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is WorkIQHttpException httpException)
        {
            double? headerMs = ParseRetryAfterValueMs(httpException.RetryAfterHeader, isMilliseconds: false);
            if (headerMs.HasValue)
            {
                return headerMs;
            }

            double? bodyMs = ParseRetryAfterBodyMs(httpException.Body);
            if (bodyMs.HasValue)
            {
                return bodyMs;
            }
        }

        Match match = RetryAfterMessageRegex().Match(exception.Message);
        if (!match.Success)
        {
            return null;
        }

        return ParseRetryAfterValueMs(match.Groups[1].Value, isMilliseconds: false);
    }

    public static double ComputeBackoffMs(WorkIQRetryOptions options, int attempt)
    {
        ArgumentNullException.ThrowIfNull(options);
        double exponential = options.BackoffBaseMs * Math.Pow(2, attempt - 1);
        double jitter = options.Jitter() * options.BackoffBaseMs;
        double raw = exponential + jitter;
        return Math.Min(raw, options.BackoffMaxMs);
    }

    public static ResiliencePipeline<TResult> BuildResiliencePipeline<TResult>(
        IWorkIQClient client,
        WorkIQRetryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new WorkIQRetryOptions();
        int maxAttempts = Math.Max(1, options.MaxAttempts);
        int maxRetryAttempts = maxAttempts - 1;
        if (maxRetryAttempts == 0)
        {
            return new ResiliencePipelineBuilder<TResult>().Build();
        }

        var retryOptions = new RetryStrategyOptions<TResult>
        {
            MaxRetryAttempts = maxRetryAttempts,
            ShouldHandle = args => new ValueTask<bool>(IsRetryableWorkIQError(args.Outcome.Exception)),
            DelayGenerator = args =>
            {
                Exception? exception = args.Outcome.Exception;
                double delayMs = ParseRetryAfterMs(exception)
                    ?? ComputeBackoffMs(options, args.AttemptNumber + 1);
                return new ValueTask<TimeSpan?>(TimeSpan.FromMilliseconds(delayMs));
            },
            OnRetry = async args =>
            {
                await client.ResetAsync(args.Context.CancellationToken).ConfigureAwait(false);
            },
        };

        return new ResiliencePipelineBuilder<TResult>()
            .AddRetry(retryOptions)
            .Build();
    }

    private static double? ParseRetryAfterBodyMs(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return FindRetryAfterFieldMs(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? FindRetryAfterFieldMs(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (IsRetryAfterMillisecondsProperty(property.Name))
                {
                    double? ms = ParseRetryAfterElementMs(property.Value, isMilliseconds: true);
                    if (ms.HasValue)
                    {
                        return ms;
                    }
                }
                else if (IsRetryAfterSecondsProperty(property.Name))
                {
                    double? ms = ParseRetryAfterElementMs(property.Value, isMilliseconds: false);
                    if (ms.HasValue)
                    {
                        return ms;
                    }
                }

                double? nested = FindRetryAfterFieldMs(property.Value);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                double? nested = FindRetryAfterFieldMs(item);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool IsRetryAfterSecondsProperty(string name)
    {
        return string.Equals(name, "retry-after", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retry_after", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retryAfter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retryAfterSeconds", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retry_after_seconds", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetryAfterMillisecondsProperty(string name)
    {
        return string.Equals(name, "retry-after-ms", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retry_after_ms", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retryAfterMs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retryAfterMilliseconds", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "retry_after_milliseconds", StringComparison.OrdinalIgnoreCase);
    }

    private static double? ParseRetryAfterElementMs(JsonElement element, bool isMilliseconds)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out double value) =>
                ToPositiveMilliseconds(value, isMilliseconds),
            JsonValueKind.String => ParseRetryAfterValueMs(element.GetString(), isMilliseconds),
            _ => null,
        };
    }

    private static double? ParseRetryAfterValueMs(string? value, bool isMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out double parsed))
        {
            return null;
        }

        return ToPositiveMilliseconds(parsed, isMilliseconds);
    }

    private static double? ToPositiveMilliseconds(double value, bool isMilliseconds)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return null;
        }
        return isMilliseconds ? value : value * 1000;
    }

    [GeneratedRegex("you'?ve reached the limit on the number of requests", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YouveReachedLimitRegex();

    [GeneratedRegex("reached.*request.*limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReachedRequestLimitRegex();

    [GeneratedRegex("too many requests", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooManyRequestsRegex();

    [GeneratedRegex("rate.?limit(ed)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RateLimitRegex();

    [GeneratedRegex("try again in a (little while|few)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TryAgainSoonRegex();

    [GeneratedRegex("retry-after[:=]\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryAfterMessageRegex();
}
