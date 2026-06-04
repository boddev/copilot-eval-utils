using System.Threading.Channels;

namespace EvalToolkit.Core;

/// <summary>Severity of a log entry, ordered ascending.</summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// A single structured log line produced by an engine. Engines never
/// write to <see cref="Console"/> directly — they push to an
/// <see cref="ILogSink"/> which fans out to:
/// <list type="bullet">
///   <item>per-job <c>events.jsonl</c> on disk (matches existing on-disk format)</item>
///   <item>an in-process <see cref="System.Threading.Channels.Channel{T}"/>
///     the UI thread drains via <c>DispatcherQueue.TryEnqueue</c>
///     (plan Section 6.8)</item>
/// </list>
/// Property values are JSON-serializable so the on-disk and in-memory
/// representations stay identical.
/// </summary>
public sealed record LogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required LogLevel Level { get; init; }

    /// <summary>The engine subsystem ("evalgen.profiler", "evalscore.judge.workiq", …).</summary>
    public required string Category { get; init; }

    public required string Message { get; init; }

    /// <summary>Job id this entry belongs to, or null for app-global lines.</summary>
    public string? JobId { get; init; }

    /// <summary>Optional structured payload (kept compact — large blobs go elsewhere).</summary>
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }

    /// <summary>Optional captured exception (serialized into <see cref="Properties"/> on disk).</summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Sink for log entries produced by engine code. Implementations MUST
/// be thread-safe — multiple worker tasks call into the same sink
/// concurrently (Section 6.7 of the plan).
/// </summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>
/// Coarse-grained progress event for a long-running job. Distinct from
/// <see cref="LogEntry"/> because the UI binds the progress bar to
/// <see cref="Completed"/> / <see cref="Total"/> directly, while log
/// entries scroll past in a separate panel.
/// </summary>
public sealed record ProgressEvent
{
    public required string JobId { get; init; }
    public required string Phase { get; init; }
    public required int Completed { get; init; }
    public required int Total { get; init; }

    /// <summary>Human-readable status (matches the right-side status text in the existing Eval UI).</summary>
    public string? StatusText { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>Sink for progress events. See <see cref="ILogSink"/> for thread-safety contract.</summary>
public interface IProgressSink
{
    void Report(ProgressEvent progress);
}

/// <summary>
/// Combined log + progress sink. Engine code typically takes one of
/// these so it doesn't have to thread two separate sinks through every
/// call. The default in-process implementation backs both surfaces
/// with bounded <see cref="Channel{T}"/>s that the UI thread drains.
/// </summary>
public interface IJobEventSink : ILogSink, IProgressSink
{
}
