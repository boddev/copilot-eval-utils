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
///     non-empty trimmed environment value among the supplied names,
///     or <see cref="string.Empty"/> when none are set.</item>
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
    /// variable is unset, blank, or doesn't have a usable leading
    /// integer; or if the parsed value is &lt;= 0 or exceeds
    /// <see cref="int.MaxValue"/>.
    ///
    /// **Mirrors JavaScript <c>Number.parseInt(raw, 10)</c> semantics**
    /// (NOT <c>int.TryParse</c>'s stricter contract) up to the final
    /// Int32 clamp. That means:
    /// <list type="bullet">
    ///   <item><c>"30s"</c> → 30 (leading digits consumed, trailing junk ignored).</item>
    ///   <item><c>"1e3"</c> → 1 (parseInt stops at <c>e</c> in base-10 mode; this is NOT scientific notation).</item>
    ///   <item><c>""</c> / <c>null</c> / no leading digit → default.</item>
    ///   <item>Zero or negative → default (matches TS clamp <c>parsed > 0</c>).</item>
    ///   <item><b>Intentional divergence:</b> <c>"99999999999"</c> → default in C# because we clamp at <see cref="int.MaxValue"/>; TS preserves the full JS Number value. Justified because every consumer of this helper today is an <c>int</c>-typed timeout / attempt count where any value past Int32 is operationally pathological. The parity harness explicitly excludes overflow inputs.</item>
    /// </list>
    /// Real-world reason this matters: a user setting
    /// <c>EVALSCORE_WORKIQ_TIMEOUT_MS=30000ms</c> (typo with unit
    /// suffix) gets 30000 ms on both sides, not silently default to
    /// 300000 on one and 30000 on the other.
    /// </summary>
    public static int ParsePositiveIntEnv(string name, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        // TS: `if (!raw) return defaultValue;` — empty string is falsy.
        // Whitespace-only is truthy as a string but `parseInt(' ', 10)`
        // returns NaN, which then falls through. We collapse both paths
        // through ParseLeadingIntJsLike.
        if (raw is null || raw.Length == 0)
        {
            return defaultValue;
        }
        long? parsed = ParseLeadingIntJsLike(raw);
        if (!parsed.HasValue || parsed.Value <= 0 || parsed.Value > int.MaxValue)
        {
            return defaultValue;
        }
        return (int)parsed.Value;
    }

    /// <summary>
    /// Match <c>Number.parseInt(value, 10)</c>'s leading-digit
    /// behavior: skip leading whitespace, accept an optional <c>+</c>
    /// or <c>-</c> sign, then consume the longest run of decimal
    /// digits. Returns null if no digit follows; returns the parsed
    /// value (in a <see cref="long"/> to survive Int32 overflow) on
    /// success.
    /// </summary>
    internal static long? ParseLeadingIntJsLike(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        int i = 0;
        // Skip whitespace (JS parseInt strips standard WhiteSpace + LineTerminator).
        while (i < value.Length && char.IsWhiteSpace(value[i]))
        {
            i++;
        }
        if (i >= value.Length)
        {
            return null;
        }

        bool negative = false;
        if (value[i] == '+' || value[i] == '-')
        {
            negative = value[i] == '-';
            i++;
        }

        int digitStart = i;
        while (i < value.Length && value[i] >= '0' && value[i] <= '9')
        {
            i++;
        }
        int digitCount = i - digitStart;
        if (digitCount == 0)
        {
            return null;
        }

        // Manual accumulate so we can detect overflow into the return
        // (rather than relying on Convert which throws / wraps).
        long accum = 0;
        for (int j = digitStart; j < i; j++)
        {
            long next = (accum * 10) + (value[j] - '0');
            // Overflow check: long overflow into negative or wrap.
            if (next < accum)
            {
                return null;
            }
            accum = next;
        }
        return negative ? -accum : accum;
    }

    /// <summary>
    /// Returns the first non-empty environment variable value among
    /// <paramref name="names"/>, trimmed of leading/trailing whitespace,
    /// or <see cref="string.Empty"/> (NOT null) if none are set.
    ///
    /// Mirrors the TS <c>getFirstEnv(...names)</c> helper in
    /// <c>eval-score/node/src/workiq-client.ts</c> which does
    /// <c>process.env[name]?.trim()</c> before the truthy check, then
    /// returns <c>''</c> when nothing matches. Returning the empty
    /// string (not <c>null</c>) keeps the wire shape parity-identical:
    /// any caller using <c>?? fallback</c> would diverge between the
    /// two implementations because TS's empty string skips ??-chain
    /// fallback while C#'s null does not. Per Opus-4.8 round-2 review:
    /// pin this contract before <c>workiq-clients-port</c> because A2A
    /// config code paths chain heavily off these helpers.
    /// </summary>
    public static string GetFirstEnv(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (string name in names)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            return raw.Trim();
        }
        return string.Empty;
    }
}
