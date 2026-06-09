using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EvalToolkit.Jobs;

/// <summary>
/// Filesystem-backed <see cref="IJobsRepository"/>. Reads
/// <c>{workspaceRoot}/jobs/*/</c> and reconstructs a
/// <see cref="JobSummary"/> per folder, preferring an in-folder
/// <c>job.json</c> but synthesizing a best-effort entry for legacy /
/// imported / corrupt-metadata folders so they're never silently dropped.
/// </summary>
public sealed class JobsRepository : IJobsRepository
{
    public const string JobsSubdirectory = "jobs";

    /// <summary>Folder-name timestamp prefix written by <c>EvalGenJobService</c>.</summary>
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    public IReadOnlyList<JobSummary> ListJobs(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string jobsRoot = Path.Combine(workspaceRoot, JobsSubdirectory);
        if (!Directory.Exists(jobsRoot))
        {
            return Array.Empty<JobSummary>();
        }

        string[] jobDirs;
        try
        {
            // Materialize eagerly so a mid-enumeration IO error (folder
            // deleted, ACLs changed) does not bubble out of the foreach.
            jobDirs = Directory.EnumerateDirectories(jobsRoot).ToArray();
        }
        catch (IOException) { return Array.Empty<JobSummary>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<JobSummary>(); }

        var summaries = new List<JobSummary>();
        foreach (var dir in jobDirs)
        {
            try
            {
                summaries.Add(BuildSummary(dir));
            }
            catch (IOException) { /* skip; one bad folder must not break the list */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        // Newest first. The timestamp prefix sorts lexicographically =
        // chronologically; fall back to CreatedUtc when prefix parsing
        // fails (e.g. imported legacy folder names).
        summaries.Sort(static (a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return summaries;
    }

    public void DeleteJob(string workspaceRoot, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // jobId is a folder name, never a path. Reject anything that could
        // escape the jobs directory before it ever reaches the filesystem.
        if (jobId is "." or ".."
            || jobId.IndexOfAny(PathSeparators) >= 0
            || Path.IsPathRooted(jobId))
        {
            throw new ArgumentException($"Invalid job id '{jobId}'.", nameof(jobId));
        }

        string jobsRoot = Path.GetFullPath(Path.Combine(workspaceRoot, JobsSubdirectory));
        string target = Path.GetFullPath(Path.Combine(jobsRoot, jobId));

        // Defense in depth: the resolved target must be a direct child of
        // the jobs root. Parent-equality is stricter than a prefix check
        // and rejects sibling folders sharing the root's name prefix.
        string? parent = Path.GetDirectoryName(target);
        if (parent is null || !PathsEqual(parent, jobsRoot))
        {
            throw new ArgumentException(
                $"Job id '{jobId}' resolves outside the jobs directory.", nameof(jobId));
        }

        if (!Directory.Exists(target))
        {
            return; // already gone — treat as success.
        }

        Directory.Delete(target, recursive: true);
    }

    public void DeleteAllJobs(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string jobsRoot = Path.Combine(workspaceRoot, JobsSubdirectory);
        if (!Directory.Exists(jobsRoot))
        {
            return;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(jobsRoot);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var dir in dirs)
        {
            // Best-effort: one locked folder must not abort the rest. A
            // subsequent refresh shows whatever could not be removed.
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* skip */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }
    }

    private static readonly char[] PathSeparators =
        { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static JobSummary BuildSummary(string jobDirectory)
    {
        string folderName = Path.GetFileName(jobDirectory);
        var metadata = JobMetadataStore.TryRead(jobDirectory);

        DateTime created = TryParseTimestampPrefix(folderName)
            ?? metadata?.StartedUtc
            ?? SafeCreationTimeUtc(jobDirectory);

        if (metadata is not null)
        {
            string display = !string.IsNullOrWhiteSpace(metadata.Description)
                ? metadata.Description
                : ExtractSlug(folderName) ?? folderName;

            return new JobSummary(
                JobId: folderName,
                Path: jobDirectory,
                DisplayName: display,
                CreatedUtc: created,
                Status: metadata.Status,
                RecordsRead: metadata.RecordsRead,
                ItemsGenerated: metadata.ItemsGenerated,
                HasWarnings: metadata.Warnings.Count > 0,
                Provider: metadata.Provider,
                OutputPaths: metadata.OutputPaths);
        }

        // Legacy / imported / corrupt-metadata folder: synthesize.
        return new JobSummary(
            JobId: folderName,
            Path: jobDirectory,
            DisplayName: ExtractSlug(folderName) ?? folderName,
            CreatedUtc: created,
            Status: JobStatus.Unknown,
            RecordsRead: null,
            ItemsGenerated: null,
            HasWarnings: false,
            Provider: null,
            OutputPaths: DiscoverOutputPaths(jobDirectory));
    }

    private static DateTime? TryParseTimestampPrefix(string folderName)
    {
        // Prefix is fixed-length 18 chars ("yyyyMMdd-HHmmss-fff").
        // A trailing "-slug" is optional.
        if (folderName.Length < TimestampFormat.Length) return null;
        string prefix = folderName[..TimestampFormat.Length];
        if (DateTime.TryParseExact(
                prefix, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        return null;
    }

    private static string? ExtractSlug(string folderName)
    {
        // Folder name pattern: "{TimestampFormat}-{slug}" (slug optional).
        int prefixLen = TimestampFormat.Length;
        if (folderName.Length <= prefixLen + 1) return null;
        if (folderName[prefixLen] != '-') return null;
        var slug = folderName[(prefixLen + 1)..];
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }

    private static DateTime SafeCreationTimeUtc(string path)
    {
        try { return File.GetCreationTimeUtc(path); }
        catch (IOException) { return DateTime.UtcNow; }
        catch (UnauthorizedAccessException) { return DateTime.UtcNow; }
    }

    private static IReadOnlyList<string> DiscoverOutputPaths(string jobDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(jobDirectory)
                .Select(Path.GetFileName)
                .Where(name =>
                    !string.IsNullOrEmpty(name) &&
                    !name!.Equals(JobMetadataStore.FileName, StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(JobMetadataStore.TempSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
