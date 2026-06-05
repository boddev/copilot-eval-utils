using System;
using System.IO;
using EvalToolkit.Jobs;

namespace EvalToolkit.Jobs.Tests;

public sealed class JobMetadataStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public JobMetadataStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Write_Then_TryRead_RoundTrips_AllFields()
    {
        var jobDir = Path.Combine(_tmpDir, "20251201-101530-123-my-job");
        var original = new JobMetadata
        {
            JobId = "20251201-101530-123-my-job",
            Status = JobStatus.Complete,
            Description = "Generate eval set",
            Provider = "azure-openai",
            Model = "gpt-4o",
            SourceName = "data.csv",
            RecordsRead = 42,
            ItemsGenerated = 10,
            Warnings = new[] { "warn1", "warn2" },
            OutputPaths = new[] { "eval-set.csv", "eval-set.evalgen.json", "eval-set-review.md" },
            StartedUtc = new DateTime(2025, 12, 1, 10, 15, 30, DateTimeKind.Utc),
            CompletedUtc = new DateTime(2025, 12, 1, 10, 16, 45, DateTimeKind.Utc),
        };

        JobMetadataStore.Write(jobDir, original);
        var roundtrip = JobMetadataStore.TryRead(jobDir);

        // Asserting field-by-field rather than record equality because
        // IReadOnlyList<string> compares by reference under record-eq.
        Assert.NotNull(roundtrip);
        Assert.Equal(original.SchemaVersion, roundtrip!.SchemaVersion);
        Assert.Equal(original.JobId, roundtrip.JobId);
        Assert.Equal(original.Status, roundtrip.Status);
        Assert.Equal(original.Description, roundtrip.Description);
        Assert.Equal(original.Provider, roundtrip.Provider);
        Assert.Equal(original.Model, roundtrip.Model);
        Assert.Equal(original.SourceName, roundtrip.SourceName);
        Assert.Equal(original.RecordsRead, roundtrip.RecordsRead);
        Assert.Equal(original.ItemsGenerated, roundtrip.ItemsGenerated);
        Assert.Equal(original.Warnings, roundtrip.Warnings);
        Assert.Equal(original.OutputPaths, roundtrip.OutputPaths);
        Assert.Equal(original.StartedUtc, roundtrip.StartedUtc);
        Assert.Equal(original.CompletedUtc, roundtrip.CompletedUtc);
        Assert.Equal(original.ErrorMessage, roundtrip.ErrorMessage);
    }

    [Fact]
    public void Write_PersistsStatus_AsString_NotInteger()
    {
        // GPT-5.5 review #5: JSON enum serialization must be stable/readable.
        var jobDir = Path.Combine(_tmpDir, "20251201-101530-123-enum-job");
        JobMetadataStore.Write(jobDir, new JobMetadata
        {
            JobId = "x",
            Status = JobStatus.Failed,
            Description = "d",
            Provider = "p",
            StartedUtc = DateTime.UtcNow,
        });

        string json = File.ReadAllText(JobMetadataStore.PathFor(jobDir));
        Assert.Contains("\"Failed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\": 3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_IsAtomic_ReplacesExistingFile()
    {
        var jobDir = Path.Combine(_tmpDir, "atomic-job");
        var first = new JobMetadata
        {
            JobId = "j",
            Status = JobStatus.InProgress,
            Description = "first",
            Provider = "p",
            StartedUtc = DateTime.UtcNow,
        };
        var second = first with { Status = JobStatus.Complete, Description = "second" };

        JobMetadataStore.Write(jobDir, first);
        JobMetadataStore.Write(jobDir, second);

        var after = JobMetadataStore.TryRead(jobDir);
        Assert.NotNull(after);
        Assert.Equal("second", after!.Description);
        Assert.Equal(JobStatus.Complete, after.Status);

        // No leftover .tmp sibling.
        Assert.False(File.Exists(JobMetadataStore.PathFor(jobDir) + JobMetadataStore.TempSuffix));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        Assert.Null(JobMetadataStore.TryRead(_tmpDir));
    }

    [Fact]
    public void TryRead_CorruptJson_ReturnsNull_Instead_Of_Throwing()
    {
        var jobDir = Path.Combine(_tmpDir, "corrupt-job");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(JobMetadataStore.PathFor(jobDir), "{ this is not valid json");

        Assert.Null(JobMetadataStore.TryRead(jobDir));
    }

    [Fact]
    public void TryRead_UnknownStatusString_FallsBackToUnknown()
    {
        // GPT-5.5 review #6: malformed/unknown status must not break the read.
        var jobDir = Path.Combine(_tmpDir, "future-status-job");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(JobMetadataStore.PathFor(jobDir), """
            {
              "SchemaVersion": "1",
              "JobId": "j",
              "Status": "SomeFutureStatus",
              "Description": "d",
              "Provider": "p",
              "StartedUtc": "2025-12-01T00:00:00Z"
            }
            """);

        // System.Text.Json throws on unknown enum names by default — so we
        // expect TryRead to swallow that and return null, letting the
        // repository synthesize a JobSummary with Status = Unknown.
        Assert.Null(JobMetadataStore.TryRead(jobDir));
    }
}
