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
}
