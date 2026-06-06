using System;
using System.Threading;
using System.Threading.Tasks;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Drives a single eval-generation job end-to-end (read → LLM → write).
/// Stays UI-free so it can be unit-tested with a fake
/// <c>ILlmClient</c> and a temporary workspace.
/// </summary>
public interface IEvalGenJobService
{
    Task<JobResult> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raised when a job transitions to a new persistent state
    /// (Started / Complete / Failed / Cancelled). Used by the jobs
    /// sidebar to refresh and by the tray-icon service to fire
    /// background-completion toasts. Promoted onto the interface in
    /// slice 28 (GPT-5.5 code-review finding #7) so consumers can take
    /// the abstraction instead of the concrete service and so a future
    /// fake can drive the same event surface in tests.
    /// </summary>
    event EventHandler<JobStateChangedEventArgs>? JobStateChanged;
}
