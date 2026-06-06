using System;
using System.Threading;
using System.Threading.Tasks;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Owns the EvalToolkit jump list on the Windows taskbar. Shows up to
/// five most-recent COMPLETED jobs in a "Recent jobs" category plus a
/// "New evaluation" user task. Each entry re-launches the EXE with a
/// short verb (<c>--job-id &lt;jobId&gt;</c> or <c>--new-evaluation</c>)
/// that <see cref="App.HandleActivation"/> routes via the single-instance
/// activation pipeline so a running primary picks up the click rather
/// than spinning up a second process.
/// </summary>
public interface IJumpListService : IDisposable
{
    /// <summary>
    /// Wires job-completion events so the jump list refreshes whenever
    /// a gen or score job reaches a terminal state. Both services may
    /// be passed; either may be null in tests.
    /// </summary>
    void Initialize(IEvalGenJobService? genJobs, IEvalScoreJobService? scoreJobs);

    /// <summary>
    /// Rebuild the jump list now. Marshals onto the UI thread (the
    /// shell COM APIs require an STA dispatcher); returns the task
    /// that completes once the rebuild has been enqueued (does not
    /// wait for COM to finish). Fire-and-forget for job-event callers
    /// that should not block on COM.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuild the jump list now AND wait for the COM rebuild to
    /// finish. Returns <c>true</c> if the rebuild succeeded,
    /// <c>false</c> if <see cref="JumpList.SaveAsync"/> threw or COM
    /// state was unavailable. Used by the diagnostics service to
    /// produce an actionable green/yellow signal — GPT-5.5
    /// slice-diagnostics plan-review BLOCKER #2.
    /// </summary>
    Task<bool> RefreshAndWaitAsync(CancellationToken cancellationToken = default);

    /// <summary>True once <see cref="Initialize"/> has been called.</summary>
    bool Initialized { get; }

    /// <summary>
    /// Outcome of the most recent rebuild attempt. <c>false</c> until
    /// the first refresh completes.
    /// </summary>
    bool LastRefreshSucceeded { get; }

    /// <summary>UTC timestamp of the most recent rebuild attempt (success or failure).</summary>
    DateTimeOffset? LastRefreshUtc { get; }
}
