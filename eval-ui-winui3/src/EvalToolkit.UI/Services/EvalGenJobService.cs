using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Diagnostics;
using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.EvalGen.Pipeline;
using EvalToolkit.EvalGen.Readers;
using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Default <see cref="IEvalGenJobService"/>: writes outputs under
/// <c>{WorkspaceRoot}/jobs/{timestamp}-{slug}/</c> and emits coarse
/// phase progress for the Step 3 UI. Mirrors the CLI's
/// <c>GenerateCommand.RunAsync</c> flow but skips
/// console-specific concerns (banner, exit codes).
/// </summary>
public sealed class EvalGenJobService : IEvalGenJobService
{
    public async Task<JobResult> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Paths.Count == 0)
        {
            throw new InvalidOperationException("Dataset selection is empty.");
        }
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new InvalidOperationException("Description is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Resolve job directory.
        string jobDirectory = CreateJobDirectory(request.WorkspaceRoot, request.Description);
        progress?.Report(new JobProgress(
            "Starting",
            0,
            $"Job folder: {jobDirectory}",
            jobDirectory));

        // 2. Read dataset (sync — caller wraps this in Task.Run).
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new JobProgress("Reading", null, "Reading dataset…"));

        var readOptions = new ReadDatasetOptions
        {
            Extensions = SplitExtensions(request.Extensions),
        };

        var readResult = DatasetReader.ReadDatasetFiles(
            request.Paths.Select(p => p.Path),
            readOptions);

        var records = readResult.Records
            .Select<EvalToolkit.EvalGen.Readers.DatasetRow, IReadOnlyDictionary<string, object?>>(
                DatasetRowToDictionary)
            .ToList();

        string sourceName = readResult.SourceFiles.Count switch
        {
            > 1 => string.Join(", ", readResult.SourceFiles),
            1 => readResult.SourceFiles[0],
            _ => request.Paths[0].Path,
        };

        progress?.Report(new JobProgress(
            "Reading",
            null,
            $"Loaded {records.Count} record(s) from {readResult.SourceFiles.Count} file(s) ({readResult.Format})."));

        if (records.Count == 0)
        {
            throw new InvalidOperationException("No records loaded from the selected paths.");
        }

        // 3. Optional connector-schema load (pre-pipeline so we fail fast).
        ConnectorSchema? connectorSchema = null;
        DatasetProfile? datasetProfile = null;
        if (!string.IsNullOrWhiteSpace(request.ConnectorSchemaPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new JobProgress(
                "Reading",
                null,
                $"Loading connector schema: {request.ConnectorSchemaPath}"));
            connectorSchema = ConnectorDiagnostics.LoadSchema(request.ConnectorSchemaPath);
            datasetProfile = Profiler.ProfileDataset(records, sourceName, readResult.Format);
        }

        // 4. Build LLM client.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new JobProgress(
            "Generating",
            null,
            $"Provider: {request.Provider.ToWireString()}"));

        await using var llmClient = LlmClientFactory.Create(new LlmClientOptions
        {
            Provider = request.Provider,
            Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
            M365TenantId = string.IsNullOrWhiteSpace(request.M365TenantId) ? null : request.M365TenantId,
        });

        // 5. Run pipeline. Forward Pipeline phase strings as Generating-phase log lines.
        // Use an inline (synchronous) adapter so phase messages don't reorder relative
        // to surrounding service progress when both fire from the same worker thread.
        cancellationToken.ThrowIfCancellationRequested();
        IProgress<string> pipelineProgress = new InlineProgress<string>(phase =>
            progress?.Report(new JobProgress("Generating", null, phase + "…")));

        var pipelineResult = await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = sourceName,
            Format = readResult.Format,
            Description = request.Description,
            Count = request.Count,
            LlmClient = llmClient,
            CancellationToken = cancellationToken,
            Progress = pipelineProgress,
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new JobProgress(
            "Generating",
            null,
            $"Generated {pipelineResult.Validated.Count} validated item(s)."));

        var allWarnings = new List<string>(pipelineResult.Warnings);

        if (connectorSchema is not null && datasetProfile is not null)
        {
            var diag = ConnectorDiagnostics.RunDiagnostics(
                pipelineResult.Validated, datasetProfile, connectorSchema);
            string diagReport = ConnectorDiagnostics.FormatDiagnosticReport(diag);
            foreach (var line in diagReport.Split('\n'))
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length > 0)
                {
                    progress?.Report(new JobProgress("Generating", null, trimmed));
                }
            }
        }

        foreach (var w in pipelineResult.Warnings)
        {
            progress?.Report(new JobProgress("Generating", null, "⚠ " + w));
        }

        // 6. Write outputs.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new JobProgress("Writing", null, "Writing eval CSV…"));

        string csvPath = Path.Combine(jobDirectory, "eval-set.csv");
        var csvWriter = new EvalCsvWriter();
        csvPath = csvWriter.Write(pipelineResult.Validated, csvPath);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new JobProgress("Writing", null, "Writing sidecar JSON…"));

        var sidecarWriter = new SidecarJsonWriter();
        string sidecarPath = sidecarWriter.Write(
            pipelineResult.Validated,
            request.Description,
            sourceName,
            csvPath);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new JobProgress("Writing", null, "Writing review markdown…"));

        string reviewPath = Path.Combine(jobDirectory, "eval-set-review.md");
        var reviewContent = Reviewer.FormatReview(
            pipelineResult.Validated,
            pipelineResult.Validation,
            request.Description,
            sourceName);
        var reviewWriter = new ReviewMarkdownWriter();
        reviewPath = reviewWriter.Write(reviewContent, reviewPath);

        progress?.Report(new JobProgress(
            "Complete",
            100,
            $"Wrote {pipelineResult.Validated.Count} item(s) to {jobDirectory}."));

        return new JobResult(
            RecordsRead: records.Count,
            ItemsGenerated: pipelineResult.Validated.Count,
            JobDirectory: jobDirectory,
            CsvPath: csvPath,
            SidecarPath: sidecarPath,
            ReviewPath: reviewPath,
            Warnings: allWarnings);
    }

    /// <summary>
    /// Default workspace root used by <see cref="App"/> when no override
    /// is configured. Placed under LocalAppData so it's per-user and
    /// survives across MSIX upgrades.
    /// </summary>
    public static string DefaultWorkspaceRoot()
    {
        string? envRoot = Environment.GetEnvironmentVariable("EVALTOOLKIT_WORKSPACE_DIR");
        string root = !string.IsNullOrWhiteSpace(envRoot)
            ? envRoot
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EvalToolkit",
                "workspace");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateJobDirectory(string workspaceRoot, string description)
    {
        string jobsRoot = Path.Combine(workspaceRoot, "jobs");
        Directory.CreateDirectory(jobsRoot);
        string timestamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture);
        string slug = SlugifyDescription(description);
        string folder = string.IsNullOrEmpty(slug)
            ? timestamp
            : $"{timestamp}-{slug}";
        string path = Path.Combine(jobsRoot, folder);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SlugifyDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var span = description.AsSpan();
        var sb = new System.Text.StringBuilder(Math.Min(span.Length, 32));
        bool lastWasDash = false;
        foreach (var ch in span)
        {
            if (sb.Length >= 32) break;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private static List<string>? SplitExtensions(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimStart('.'))
            .Where(s => s.Length > 0)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private static Dictionary<string, object?> DatasetRowToDictionary(EvalToolkit.EvalGen.Readers.DatasetRow row)
    {
        var dict = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);
        foreach (var kv in row.Entries)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> adapter. Unlike <see cref="Progress{T}"/>
    /// (which marshals through the captured SynchronizationContext, queuing callbacks
    /// to the ThreadPool when none is installed), this invokes the callback inline on
    /// the reporting thread. That guarantees messages from the inner pipeline arrive
    /// in source-order with surrounding service progress events.
    /// </summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public InlineProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
