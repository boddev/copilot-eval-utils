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

        IEnumerable<string> jobDirs;
        try
        {
            jobDirs = Directory.EnumerateDirectories(jobsRoot);
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
