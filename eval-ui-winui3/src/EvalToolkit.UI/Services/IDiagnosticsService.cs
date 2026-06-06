using System.Threading;
using System.Threading.Tasks;
using EvalToolkit.UI.Models;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Builds a <see cref="DiagnosticsReport"/> snapshot of subsystem
/// health. Used by both:
/// <list type="bullet">
/// <item><description>The GUI <c>Views.DiagnosticsView</c> page.</description></item>
/// <item><description>The headless <c>--diagnostics</c> CLI verb (via
/// <see cref="HeadlessDiagnosticsRunner"/>).</description></item>
/// </list>
/// Implementations should serialize concurrent <see cref="CollectAsync"/>
/// calls so the GUI Refresh button can't run two probes in parallel
/// (GPT-5.5 slice-diagnostics plan-review NON-BLOCKER #11).
/// </summary>
public interface IDiagnosticsService
{
    Task<DiagnosticsReport> CollectAsync(CancellationToken cancellationToken = default);
}
