using System.Collections.Generic;
using EvalToolkit.Core;
using EvalToolkit.UI.Models;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Snapshot of wizard step 1 + step 2 state, ready to hand to
/// <see cref="IEvalGenJobService.RunAsync"/>. Captured by the VM at
/// "Generate" time so subsequent edits to the wizard don't perturb a
/// running job.
/// </summary>
public sealed record JobRequest
{
    public required IReadOnlyList<DatasetPath> Paths { get; init; }
    public required string Description { get; init; }
    public required int Count { get; init; }
    public required string Extensions { get; init; }
    public required LLMProvider Provider { get; init; }
    public required string Model { get; init; }
    public string? M365TenantId { get; init; }
    public string? ConnectorSchemaPath { get; init; }

    /// <summary>
    /// Per-user job root. Default is
    /// <c>%LOCALAPPDATA%\EvalToolkit\workspace</c>. Overridable so
    /// slice 24 can swap to an imported workspace.
    /// </summary>
    public required string WorkspaceRoot { get; init; }
}
