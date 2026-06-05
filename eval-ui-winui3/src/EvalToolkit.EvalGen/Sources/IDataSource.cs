using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Sources;

/// <summary>
/// Port of <c>eval-gen/src/sources/types.ts SourceResult</c>. The common
/// result shape every <see cref="IDataSource"/> implementation returns.
/// </summary>
/// <param name="Records">Records pulled from the source.</param>
/// <param name="Format">
/// Effective <see cref="InputFormat"/> for downstream readers/profilers
/// (always <c>InputFormat.Json</c> for the current adapters — TS parity).
/// </param>
/// <param name="SourceName">
/// Human-readable label used for row references and report output
/// (hostname for HTTP sources, connection-string label for DBs).
/// </param>
public sealed record SourceResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Records,
    InputFormat Format,
    string SourceName);

/// <summary>
/// Port of <c>eval-gen/src/sources/types.ts DataSourceAdapter</c>. A pluggable
/// non-file data origin (API / database / web crawl) that feeds raw records
/// into the EvalGen pipeline. Implementations should throw on hard failures
/// (no records discovered, auth refused, etc.) so the CLI shim can surface a
/// non-zero exit code.
/// </summary>
public interface IDataSource
{
    /// <summary>
    /// Connect to the source and fetch sample records. May log non-fatal
    /// warnings (e.g., individual endpoint/table failures) to
    /// <paramref name="progress"/>; only throws when no records can be
    /// returned.
    /// </summary>
    Task<SourceResult> FetchAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
