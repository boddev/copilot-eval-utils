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
    /// wait for COM to finish).
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
