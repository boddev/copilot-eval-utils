namespace EvalToolkit.Jobs;

/// <summary>
/// Terminal and transient states a job can be in. Persisted as the
/// string name via <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// (see <see cref="JobMetadata"/>) so storage is durable across
/// enum reorderings.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="InProgress"/> — job is currently running (or was, before a crash).</description></item>
/// <item><description><see cref="Complete"/> — job ran to success and all outputs were written.</description></item>
/// <item><description><see cref="Failed"/> — job threw an exception; partial outputs may exist.</description></item>
/// <item><description><see cref="Cancelled"/> — user cancelled mid-run; partial outputs may exist.</description></item>
/// <item><description><see cref="Unknown"/> — synthesized for legacy folders missing <c>job.json</c>, or when the metadata file is corrupt/unreadable.</description></item>
/// </list>
/// </remarks>
public enum JobStatus
{
    Unknown = 0,
    InProgress = 1,
    Complete = 2,
    Failed = 3,
    Cancelled = 4,
}
