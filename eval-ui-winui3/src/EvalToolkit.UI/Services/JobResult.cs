using System.Collections.Generic;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Final outcome of a successful <see cref="IEvalGenJobService.RunAsync"/>
/// invocation. Carries the absolute paths of the generated artifacts so
/// the progress panel can wire Open buttons and the future jobs sidebar
/// (slice 24) can index them.
/// </summary>
public sealed record JobResult(
    int RecordsRead,
    int ItemsGenerated,
    string JobDirectory,
    string CsvPath,
    string SidecarPath,
    string? ReviewPath,
    IReadOnlyList<string> Warnings);
