namespace EvalToolkit.UI.Services;

/// <summary>
/// Single progress tick emitted by <see cref="IEvalGenJobService"/>.
/// </summary>
/// <param name="Phase">
/// Coarse phase name (e.g. "Reading", "Generating", "Writing",
/// "Complete", "Cancelled", "Failed"). Drives the Step 3 status text
/// and progress-bar coloration.
/// </param>
/// <param name="Percent">
/// Optional 0..100. <c>null</c> renders an indeterminate progress bar
/// for phases where total work isn't known up front (e.g. LLM calls).
/// </param>
/// <param name="Message">
/// Human-readable detail appended to the log panel. May be empty when
/// only <see cref="Phase"/> / <see cref="Percent"/> changed.
/// </param>
public sealed record JobProgress(string Phase, int? Percent, string Message, string? JobDirectory = null);
