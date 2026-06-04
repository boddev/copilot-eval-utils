using System.Text.Json.Serialization;

namespace EvalToolkit.Core;

/// <summary>
/// Assertion that validates a Copilot response. Mirrors the TS
/// <c>Assertion</c> discriminated union:
/// <code>
/// type Assertion =
///   | { type: 'must_contain'; value: string; wholeWord?: boolean }
///   | { type: 'must_contain_any'; values: string[] }
///   | { type: 'must_not_contain'; value: string };
/// </code>
///
/// Modeled as an abstract record with a polymorphic <c>$type</c>
/// discriminator. <see cref="AssertionJsonConverter"/> reads and writes
/// the TS shape (<c>type</c> field) on the wire so eval files round-trip
/// identically between the two implementations.
/// </summary>
[JsonConverter(typeof(AssertionJsonConverter))]
public abstract record Assertion
{
    /// <summary>The string discriminator on the wire (matches the TS <c>type</c> field).</summary>
    public abstract string TypeTag { get; }
}

/// <summary>The Copilot response MUST contain <see cref="Value"/>.</summary>
public sealed record MustContainAssertion : Assertion
{
    public required string Value { get; init; }

    /// <summary>If true, match must respect word boundaries (not a substring).</summary>
    public bool WholeWord { get; init; }

    public override string TypeTag => "must_contain";
}

/// <summary>The Copilot response MUST contain at least one of <see cref="Values"/>.</summary>
public sealed record MustContainAnyAssertion : Assertion
{
    public required IReadOnlyList<string> Values { get; init; }
    public override string TypeTag => "must_contain_any";
}

/// <summary>The Copilot response MUST NOT contain <see cref="Value"/>.</summary>
public sealed record MustNotContainAssertion : Assertion
{
    public required string Value { get; init; }
    public override string TypeTag => "must_not_contain";
}
