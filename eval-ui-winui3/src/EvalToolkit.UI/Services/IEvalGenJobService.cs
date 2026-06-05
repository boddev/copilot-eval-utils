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
}
