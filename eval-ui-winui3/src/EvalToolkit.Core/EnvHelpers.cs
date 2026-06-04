using System.Globalization;

namespace EvalToolkit.Core;

/// <summary>
/// Environment-variable parsing helpers. Mirrors the small TS
/// <c>parseBoolEnv</c> / <c>parsePositiveIntEnv</c> / <c>getFirstEnv</c>
/// helpers scattered across the Node tools, so the wire behavior is
/// identical:
///
/// <list type="bullet">
///   <item><see cref="ParseBoolEnv"/> matches <c>parseBoolEnv</c> in
///     <c>eval-gen/src/readers/index.ts</c>: accepts <c>true|1|yes|on</c>
///     case-insensitively as true; everything else (including null and
///     empty string) as false.</item>
///   <item><see cref="ParsePositiveIntEnv"/> matches
///     <c>parsePositiveIntEnv</c> in
///     <c>eval-score/node/src/workiq-client.ts</c>: returns the default
///     if unset/blank/non-numeric/&lt;=0.</item>
///   <item><see cref="GetFirstEnv"/> matches <c>getFirstEnv</c> in
///     <c>eval-score/node/src/workiq-client.ts</c>: returns the first
///     non-empty environment value among the supplied names, or null.</item>
/// </list>
/// </summary>
public static class EnvHelpers
{
    /// <summary>
    /// Parse a boolean environment variable. Returns true only for
    /// the literal values <c>true</c>, <c>1</c>, <c>yes</c>, or <c>on</c>
    /// (case-insensitive, leading/trailing whitespace trimmed).
    /// </summary>
    /// <param name="value">Raw value (typically from <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <returns>True if recognized as truthy; false otherwise.</returns>
    public static bool ParseBoolEnv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "true" or "1" or "yes" or "on";
    }

    /// <summary>
    /// Convenience: read and parse a boolean env var by name.
    /// </summary>
    public static bool GetBoolEnv(string name, bool defaultValue = false)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        return ParseBoolEnv(raw);
    }

    /// <summary>
    /// Parse a positive integer env var, returning the default if the
    /// variable is unset, blank, non-numeric, or &lt;= 0. Mirrors the TS
    /// <c>parsePositiveIntEnv</c> contract exactly so that e.g.
    /// <c>EVALSCORE_WORKIQ_MAX_ATTEMPTS=0</c> falls back to the default
    /// rather than disabling retries.
    /// </summary>
    public static int ParsePositiveIntEnv(string name, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return defaultValue;
        }
        return parsed > 0 ? parsed : defaultValue;
    }

    /// <summary>
    /// Returns the first non-empty environment variable value among
    /// <paramref name="names"/>, or null if none are set. Mirrors the TS
    /// <c>getFirstEnv(...names)</c> helper in
    /// <c>eval-score/node/src/workiq-client.ts</c>, which lets us honor
    /// <c>EVALSCORE_*</c> + <c>WORK_IQ_*</c> aliases with a single call.
    /// </summary>
    public static string? GetFirstEnv(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (string name in names)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }
        return null;
    }
}
