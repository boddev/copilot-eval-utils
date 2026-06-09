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

    /// <summary>
    /// Permanently delete a single job folder
    /// (<c>{workspaceRoot}/jobs/{jobId}</c>) and all of its contents.
    /// <paramref name="jobId"/> must be a single folder-name segment (the
    /// value from <see cref="JobSummary.JobId"/>); paths, traversal
    /// (<c>..</c>) and rooted values are rejected with
    /// <see cref="System.ArgumentException"/>. A missing folder is treated
    /// as success (no-op). I/O and access errors propagate so the caller
    /// can surface them.
    /// </summary>
    void DeleteJob(string workspaceRoot, string jobId);

    /// <summary>
    /// Permanently delete every job folder directly under
    /// <c>{workspaceRoot}/jobs/</c>. Non-job files at the jobs root are
    /// left untouched. Deletion is best-effort per folder: a single
    /// locked or inaccessible folder is skipped rather than aborting the
    /// rest. Safe to call when the jobs directory does not exist.
    /// </summary>
    void DeleteAllJobs(string workspaceRoot);
}
