using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Output of <see cref="IDatasetReader.Read"/>. Mirrors the TS
/// <c>ReadResult</c> interface in <c>eval-gen/src/readers/index.ts</c>.
/// </summary>
public sealed record ReadResult
{
    public required IReadOnlyList<DatasetRow> Records { get; init; }

    public required InputFormat Format { get; init; }
}

/// <summary>
/// Extended result for multi-file / directory reads. Mirrors the TS
/// <c>ReadResult &amp; { sourceFiles: string[] }</c> shape returned by
/// <c>readDatasetFile</c>.
/// </summary>
public sealed record DatasetReadResult
{
    public required IReadOnlyList<DatasetRow> Records { get; init; }

    /// <summary>
    /// Format of the most recently read file ("last format wins" per
    /// TS <c>readDatasetFile</c> ~line 865).
    /// </summary>
    public required InputFormat Format { get; init; }

    /// <summary>Base names of every file that contributed records.</summary>
    public required IReadOnlyList<string> SourceFiles { get; init; }
}
