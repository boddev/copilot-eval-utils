using System;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Drives a single eval-score job end-to-end:
/// load EvalSet → start WorkIQ → optional preflight → response evaluator
/// → scorer → write report MD + scored CSV. Stays UI-free so it can be
/// unit-tested with mock WorkIQ clients.
/// </summary>
public interface IEvalScoreJobService
{
    Task<EvalScoreResult> RunAsync(
        EvalScoreRequest request,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raised when a score job transitions to a terminal state
    /// (Complete / Failed / Cancelled). Mirrors
    /// <see cref="IEvalGenJobService.JobStateChanged"/> so the tray-icon
    /// service can fire toasts for both pipelines from a single seam.
    /// Added in slice 28 (GPT-5.5 code-review blocker) — the original
    /// implementation only notified gen completions.
    /// <see cref="JobStateChangedEventArgs.Kind"/> identifies the
    /// pipeline; <see cref="JobStateChangedEventArgs.JobDirectory"/>
    /// holds the request's output directory.
    /// </summary>
    event EventHandler<JobStateChangedEventArgs>? JobStateChanged;
}
