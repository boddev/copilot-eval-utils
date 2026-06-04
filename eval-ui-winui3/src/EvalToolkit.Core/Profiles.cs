using System.Text.Json.Serialization;

namespace EvalToolkit.Core;

/// <summary>
/// A single atomic fact extracted from source data. Mirrors the TS
/// <c>Fact</c> interface in <c>eval-gen/src/types.ts</c>.
/// </summary>
public sealed record Fact
{
    public required string Id { get; init; }
    public required string Field { get; init; }

    /// <summary>The raw value pulled from the source row. JSON-serializable.</summary>
    public required object? Value { get; init; }

    /// <summary>Human-readable row reference (e.g. <c>"suppliers.csv:row 12"</c>).</summary>
    public required string RowReference { get; init; }

    /// <summary>The full source row this fact was extracted from.</summary>
    public required IReadOnlyDictionary<string, object?> Record { get; init; }
}

/// <summary>
/// Column profile from dataset analysis. Matches the TS
/// <c>ColumnProfile</c> interface.
/// </summary>
public sealed record ColumnProfile
{
    public required string Name { get; init; }

    /// <summary>One of: string, number, boolean, date, null, mixed.</summary>
    public required string DataType { get; init; }

    public required int NullCount { get; init; }
    public required int UniqueCount { get; init; }
    public required int TotalCount { get; init; }
    public required IReadOnlyList<object?> SampleValues { get; init; }

    /// <summary>For categorical columns with low cardinality.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? ValueCounts { get; init; }

    /// <summary>Min/max for numeric/date columns.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Min { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Max { get; init; }
}

/// <summary>
/// Result of profiling a dataset. Matches the TS <c>DatasetProfile</c>
/// interface.
/// </summary>
public sealed record DatasetProfile
{
    public required string FileName { get; init; }
    public required InputFormat Format { get; init; }
    public required int RowCount { get; init; }
    public required IReadOnlyList<ColumnProfile> Columns { get; init; }

    /// <summary>~20 representative sample records.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRecords { get; init; }

    public required IReadOnlyList<string> CandidateKeyColumns { get; init; }
    public required IReadOnlyList<string> CandidateTitleColumns { get; init; }
}
