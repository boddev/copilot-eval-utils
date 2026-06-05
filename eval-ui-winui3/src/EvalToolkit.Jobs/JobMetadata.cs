using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EvalToolkit.Jobs;

/// <summary>
/// Durable per-job metadata persisted to <c>{jobDir}/job.json</c>.
/// Required-init fields are known at job start; nullable fields are
/// only populated once the corresponding phase has run, so they're
/// safe to omit when writing the initial <see cref="JobStatus.InProgress"/>
/// record before dataset read.
/// </summary>
public sealed record JobMetadata
{
    /// <summary>
    /// Forward-compatibility tag. Readers should default unknown values
    /// to <see cref="JobStatus.Unknown"/> rather than fail.
    /// </summary>
    public string SchemaVersion { get; init; } = "1";

    /// <summary>
    /// Job folder name — matches the actual on-disk directory so the
    /// repository can reconcile when the user renames a folder.
    /// </summary>
    public required string JobId { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
    public required JobStatus Status { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// Provider wire string (<c>m365-copilot</c>, <c>azure-openai</c>, etc.)
    /// rather than the .NET enum, so logs survive enum rearrangements.
    /// </summary>
    public required string Provider { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// Comma-joined input source description (file/folder names). Not
    /// known until the dataset reader has run, hence nullable.
    /// </summary>
    public string? SourceName { get; init; }

    public int? RecordsRead { get; init; }

    public int? ItemsGenerated { get; init; }

    /// <summary>
    /// Pipeline + connector-diagnostics warnings collected during the
    /// run. Empty when the job hasn't reached the validation phase yet.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Paths of written output files, relative to the job folder
    /// (typically <c>eval-set.csv</c>, <c>eval-set.evalgen.json</c>,
    /// <c>eval-set-review.md</c>). Empty until writes begin.
    /// </summary>
    public IReadOnlyList<string> OutputPaths { get; init; } = Array.Empty<string>();

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    /// <summary>
    /// Exception/cancellation message on <see cref="JobStatus.Failed"/> or
    /// <see cref="JobStatus.Cancelled"/>; <c>null</c> on success.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
