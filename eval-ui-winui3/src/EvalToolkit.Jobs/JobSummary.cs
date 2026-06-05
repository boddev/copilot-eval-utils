using System;
using System.Collections.Generic;

namespace EvalToolkit.Jobs;

/// <summary>
/// Sidebar-display projection of a job on disk. Decoupled from
/// <see cref="JobMetadata"/> so the repository can synthesize entries
/// for legacy/imported folders that have no <c>job.json</c>.
/// </summary>
/// <param name="JobId">
/// Folder name (which equals <see cref="JobMetadata.JobId"/> when metadata
/// is present). Used as a stable identity key for selection.
/// </param>
/// <param name="Path">Absolute path to the job folder.</param>
/// <param name="DisplayName">
/// User-facing title — preferred order: description from metadata,
/// then the slug portion of the folder name, then the folder name.
/// </param>
/// <param name="CreatedUtc">
/// Best-effort creation timestamp parsed from the folder-name prefix
/// (<c>yyyyMMdd-HHmmss-fff</c>); falls back to filesystem CreationTimeUtc.
/// </param>
/// <param name="Status">
/// <see cref="JobStatus.Unknown"/> for legacy folders without metadata
/// (or with malformed metadata) so the sidebar can render them muted
/// without hiding them.
/// </param>
/// <param name="RecordsRead">Number of dataset records read, when known.</param>
/// <param name="ItemsGenerated">Number of validated eval items written, when known.</param>
/// <param name="HasWarnings">
/// True when <see cref="JobMetadata.Warnings"/> contains at least one entry.
/// Used to drive a sidebar warning glyph.
/// </param>
/// <param name="Provider">Provider wire string used for the run, when known.</param>
/// <param name="OutputPaths">Output file paths relative to <see cref="Path"/>.</param>
public sealed record JobSummary(
    string JobId,
    string Path,
    string DisplayName,
    DateTime CreatedUtc,
    JobStatus Status,
    int? RecordsRead,
    int? ItemsGenerated,
    bool HasWarnings,
    string? Provider,
    IReadOnlyList<string> OutputPaths);
