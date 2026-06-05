namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// EvalGen assertion attached to an eval row. Mirrors the TS
/// <c>Assertion</c> discriminated union in
/// <c>eval-score/node/src/types.ts</c>. Provided as a tagged record so
/// the AssertionChecker (future slice) can pattern-match cleanly.
/// </summary>
public sealed record Assertion
{
    public required AssertionType Type { get; init; }

    /// <summary>Used by <see cref="AssertionType.MustContain"/> and <see cref="AssertionType.MustNotContain"/>.</summary>
    public string? Value { get; init; }

    /// <summary>Used by <see cref="AssertionType.MustContainAny"/>.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Optional flag for <see cref="AssertionType.MustContain"/>; mirrors TS <c>wholeWord</c>.</summary>
    public bool? WholeWord { get; init; }
}

public enum AssertionType
{
    MustContain,
    MustContainAny,
    MustNotContain,
}

public static class AssertionTypes
{
    public static string ToWireString(this AssertionType type) => type switch
    {
        AssertionType.MustContain => "must_contain",
        AssertionType.MustContainAny => "must_contain_any",
        AssertionType.MustNotContain => "must_not_contain",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static AssertionType FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "must_contain" => AssertionType.MustContain,
            "must_contain_any" => AssertionType.MustContainAny,
            "must_not_contain" => AssertionType.MustNotContain,
            _ => throw new NotSupportedException($"Unknown assertion type: '{value}'"),
        };
    }
}

/// <summary>
/// Result of evaluating a single <see cref="Assertion"/>. Mirrors the TS
/// <c>AssertionResult</c> interface.
/// </summary>
public sealed record AssertionResult
{
    public required Assertion Assertion { get; init; }
    public required bool Passed { get; init; }
    public required string Detail { get; init; }
}
