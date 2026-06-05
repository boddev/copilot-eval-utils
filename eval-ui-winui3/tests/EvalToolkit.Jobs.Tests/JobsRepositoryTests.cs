using System;
using System.IO;
using System.Linq;
using EvalToolkit.Jobs;

namespace EvalToolkit.Jobs.Tests;

public sealed class JobsRepositoryTests : IDisposable
{
    private readonly string _workspace;
    private readonly JobsRepository _repo = new();

    public JobsRepositoryTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "evaltoolkit-jobs-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string MakeJobDir(string folderName)
    {
        string path = Path.Combine(_workspace, "jobs", folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ListJobs_MissingWorkspace_ReturnsEmpty()
    {
        string nonExisting = Path.Combine(_workspace, "does-not-exist");
        var result = _repo.ListJobs(nonExisting);
        Assert.Empty(result);
    }

    [Fact]
    public void ListJobs_MissingJobsSubdirectory_ReturnsEmpty()
    {
        var result = _repo.ListJobs(_workspace);
        Assert.Empty(result);
    }

    [Fact]
    public void ListJobs_WithMetadata_PopulatesAllFields()
    {
        var jobDir = MakeJobDir("20251201-101530-123-my-eval");
        JobMetadataStore.Write(jobDir, new JobMetadata
        {
            JobId = "20251201-101530-123-my-eval",
            Status = JobStatus.Complete,
            Description = "Customer feedback eval",
            Provider = "m365-copilot",
            RecordsRead = 50,
            ItemsGenerated = 12,
            Warnings = new[] { "minor warning" },
            OutputPaths = new[] { "eval-set.csv" },
            StartedUtc = new DateTime(2025, 12, 1, 10, 15, 30, DateTimeKind.Utc),
        });

        var summaries = _repo.ListJobs(_workspace);

        var summary = Assert.Single(summaries);
        Assert.Equal("20251201-101530-123-my-eval", summary.JobId);
        Assert.Equal("Customer feedback eval", summary.DisplayName);
        Assert.Equal(JobStatus.Complete, summary.Status);
        Assert.Equal(50, summary.RecordsRead);
        Assert.Equal(12, summary.ItemsGenerated);
        Assert.True(summary.HasWarnings);
        Assert.Equal("m365-copilot", summary.Provider);
        Assert.Contains("eval-set.csv", summary.OutputPaths);
    }

    [Fact]
    public void ListJobs_LegacyFolder_NoMetadata_SynthesizesUnknownSummary()
    {
        // GPT-5.5 review answer #6: legacy folders surface as Unknown,
        // not hidden.
        var jobDir = MakeJobDir("20251101-090000-000-legacy-import");
        File.WriteAllText(Path.Combine(jobDir, "eval-set.csv"), "id,name\n1,a\n");

        var summary = Assert.Single(_repo.ListJobs(_workspace));

        Assert.Equal(JobStatus.Unknown, summary.Status);
        Assert.Equal("legacy-import", summary.DisplayName);
        Assert.Null(summary.RecordsRead);
        Assert.Null(summary.ItemsGenerated);
        Assert.False(summary.HasWarnings);
        Assert.Null(summary.Provider);
        Assert.Contains("eval-set.csv", summary.OutputPaths);
    }

    [Fact]
    public void ListJobs_CorruptJobJson_FallsBack_To_UnknownSummary()
    {
        // GPT-5.5 review #6: one bad folder must not break the entire list.
        var goodDir = MakeJobDir("20251201-101530-123-good");
        JobMetadataStore.Write(goodDir, new JobMetadata
        {
            JobId = "20251201-101530-123-good",
            Status = JobStatus.Complete,
            Description = "good",
            Provider = "p",
            StartedUtc = DateTime.UtcNow,
        });

        var badDir = MakeJobDir("20251201-101535-000-bad");
        File.WriteAllText(JobMetadataStore.PathFor(badDir), "{ broken");

        var summaries = _repo.ListJobs(_workspace);

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.JobId.EndsWith("-good", StringComparison.Ordinal) && s.Status == JobStatus.Complete);
        Assert.Contains(summaries, s => s.JobId.EndsWith("-bad", StringComparison.Ordinal) && s.Status == JobStatus.Unknown);
    }

    [Fact]
    public void ListJobs_SortsNewestFirst_ByTimestampPrefix()
    {
        MakeJobDir("20251201-100000-000-oldest");
        MakeJobDir("20251201-100100-000-middle");
        MakeJobDir("20251201-100200-000-newest");

        var summaries = _repo.ListJobs(_workspace);

        Assert.Equal(3, summaries.Count);
        Assert.EndsWith("-newest", summaries[0].JobId, StringComparison.Ordinal);
        Assert.EndsWith("-middle", summaries[1].JobId, StringComparison.Ordinal);
        Assert.EndsWith("-oldest", summaries[2].JobId, StringComparison.Ordinal);
    }

    [Fact]
    public void ListJobs_IgnoresTempMetadataFiles_InDiscoveredOutputs()
    {
        var jobDir = MakeJobDir("20251201-101530-123-temp-job");
        File.WriteAllText(Path.Combine(jobDir, "eval-set.csv"), "id,name\n");
        File.WriteAllText(Path.Combine(jobDir, JobMetadataStore.FileName + JobMetadataStore.TempSuffix), "{}");

        var summary = Assert.Single(_repo.ListJobs(_workspace));

        Assert.Contains("eval-set.csv", summary.OutputPaths);
        Assert.DoesNotContain(summary.OutputPaths, p =>
            p.EndsWith(JobMetadataStore.TempSuffix, StringComparison.OrdinalIgnoreCase));
    }
}
