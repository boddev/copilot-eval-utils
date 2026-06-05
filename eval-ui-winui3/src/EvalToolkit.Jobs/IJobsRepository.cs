using System.Collections.Generic;

namespace EvalToolkit.Jobs;

/// <summary>
/// Enumerates persisted jobs under a workspace root. Stateless — callers
/// trigger a refresh whenever they want a new listing.
/// </summary>
public interface IJobsRepository
{
    /// <summary>
    /// Enumerate job folders under <c>{workspaceRoot}/jobs/</c>. Always
    /// safe to call even when the workspace doesn't exist yet (returns
    /// an empty sequence). Sorted newest-first by folder timestamp.
    /// </summary>
    IReadOnlyList<JobSummary> ListJobs(string workspaceRoot);
}
