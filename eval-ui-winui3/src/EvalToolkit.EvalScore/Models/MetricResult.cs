namespace EvalToolkit.EvalScore.Models;

/// <summary>
/// Per-evaluator metric result. Mirrors the TS <c>MetricResult</c>
/// interface in <c>eval-score/node/src/types.ts</c>.
/// </summary>
public sealed record MetricResult
{
    public required EvaluatorName Name { get; init; }

    /// <summary>Numeric score in the 0-100 range, or null when <see cref="Scale"/> is boolean.</summary>
    public double? Score { get; init; }

    /// <summary>Pass/fail. Null when no threshold was supplied (TS: <c>undefined</c>).</summary>
    public bool? Passed { get; init; }

    public string? Reason { get; init; }

    public required MetricProvider Provider { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// On-wire scale string. Mirrors the TS <c>'0-100' | 'boolean'</c>
    /// union. Stored as enum for type safety; serializers should map to
    /// the original literal.
    /// </summary>
    public required MetricScale Scale { get; init; }

    /// <summary>Rubric version stamp (e.g. <c>"evalscore-m365-rubrics-v1"</c>).</summary>
    public string? RubricVersion { get; init; }

    /// <summary>Threshold used for the <see cref="Passed"/> determination, if any.</summary>
    public double? Threshold { get; init; }
}

/// <summary>Mirrors the TS <c>'0-100' | 'boolean'</c> union.</summary>
public enum MetricScale
{
    ZeroToOneHundred,
    Boolean,
}

public static class MetricScales
{
    public static string ToWireString(this MetricScale scale) => scale switch
    {
        MetricScale.ZeroToOneHundred => "0-100",
        MetricScale.Boolean => "boolean",
        _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, null),
    };

    public static MetricScale FromWireString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "0-100" => MetricScale.ZeroToOneHundred,
            "boolean" => MetricScale.Boolean,
            _ => throw new NotSupportedException($"Unknown metric scale: '{value}'"),
        };
    }
}
