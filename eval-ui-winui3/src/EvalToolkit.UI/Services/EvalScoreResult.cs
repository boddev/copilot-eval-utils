namespace EvalToolkit.UI.Services;

/// <summary>
/// Outcome of a successful <see cref="IEvalScoreJobService.RunAsync"/>.
/// Carries the absolute paths of the report + scored CSV so the Step 5
/// panel can wire Open buttons and the WebView2 results viewer.
/// </summary>
public sealed record EvalScoreResult(
    string ReportPath,
    string? ScoredCsvPath,
    int TotalScored,
    int PassCount,
    int FailCount,
    double AverageScore);
