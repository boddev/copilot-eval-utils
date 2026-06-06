using System.Globalization;
using EvalToolkit.EvalScore.EvalSet;
using EvalToolkit.EvalScore.Evaluator;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Preflight;
using EvalToolkit.EvalScore.Reporting;
using EvalToolkit.EvalScore.Scoring;
using EvalToolkit.EvalScore.Writers;
using EvalToolkit.Jobs;
using EvalToolkit.UI.Editor;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Default <see cref="IEvalScoreJobService"/>: loads the EvalSet sidecar
/// produced by EvalGen, overlays any user edits saved by the Step 4 row
/// editor onto the in-memory rows, then runs the same response evaluator
/// + scorer pipeline the CLI shim runs
/// (<c>EvalToolkit.Cli.Commands.ScoreCommand</c>). Writes a markdown
/// report and a scored-rows CSV under the job folder for Step 5 to
/// surface.
///
/// <para><b>Why the merge step matters (GPT-5.5 plan-review blocker
/// #1).</b> EvalSetLoader reads the sidecar JSON, but Step 4 edits the
/// CSV. Without a merge, saved CSV edits are silently ignored at scoring
/// time. <see cref="MergeEditedCsvAsync"/> overlays the CSV's
/// prompt / expected_answer / source_location / actual_answer onto the
/// sidecar rows by index. Assertions, evaluators, metadata, and other
/// sidecar-only fields are preserved untouched. If the row counts
/// diverge (rows added / removed in the CSV), the merge is abandoned
/// with a warning so the user can resolve the conflict by regenerating
/// the eval set.</para>
///
/// <para><b>Why the client factory takes the request (plan-review
/// blocker #2).</b> The CLI selects between
/// <see cref="CliWorkIQClient"/> (MCP) and <see cref="A2AWorkIQClient"/>
/// based on the request, and may need a separate MCP scoring client even
/// when responses come from A2A. The factory must observe the request
/// to make the same choices; a hard-coded factory loses that behavior
/// (the CLI shim's <c>RunScoreAsync</c> lines 166-179 are the reference
/// implementation).</para>
///
/// <para><b>EULA approver (plan-review blocker #3).</b> The CLI uses a
/// stdin approver. WinUI has no stdin, so the default
/// <see cref="EulaApprover"/> returns false; in turn,
/// <see cref="EvalScoreRequest.SkipPreflight"/> defaults to <c>true</c>
/// in the wizard until slice 28 (winui-diagnostics) plumbs a proper
/// ContentDialog approver. Tests / callers can override the approver
/// to exercise preflight without a UI.</para>
/// </summary>
public sealed class EvalScoreJobService : IEvalScoreJobService
{
    /// <summary>
    /// Factory seam. Receives the request so it can choose between
    /// <see cref="CliWorkIQClient"/> and <see cref="A2AWorkIQClient"/>
    /// using the same logic as the CLI. Tests override this to return
    /// mock clients.
    /// </summary>
    public Func<EvalScoreRequest, ClientPurpose, IWorkIQClientHandle> ClientFactory { get; init; } =
        DefaultClientFactory;

    /// <summary>
    /// EULA approver. UI default returns false (preflight is skipped by
    /// the wizard's default <see cref="EvalScoreRequest.SkipPreflight"/>);
    /// CLI / tests can supply a real approver to exercise preflight.
    /// </summary>
    public Func<CancellationToken, Task<bool>> EulaApprover { get; init; } =
        _ => Task.FromResult(false);

    /// <summary>
    /// Raised when a score job reaches a terminal state. Slice 28
    /// (GPT-5.5 code-review blocker) added this so the tray-icon
    /// service can notify on background score completions too, not
    /// just gen. Subscriber exceptions are swallowed inside the
    /// raiser; faults must never escape into the scoring pipeline.
    /// </summary>
    public event EventHandler<JobStateChangedEventArgs>? JobStateChanged;

    public async Task<EvalScoreResult> RunAsync(
        EvalScoreRequest request,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EvalSetPath))
        {
            throw new InvalidOperationException("EvalSet path is required.");
        }
        if (!File.Exists(request.EvalSetPath))
        {
            throw new FileNotFoundException(
                $"EvalSet sidecar not found: {request.EvalSetPath}",
                request.EvalSetPath);
        }
        if (string.IsNullOrWhiteSpace(request.OutputDir))
        {
            throw new InvalidOperationException("Output directory is required.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Slice 28 (GPT-5.5 code-review blocker): wrap the pipeline so a
        // terminal state always raises JobStateChanged — the tray-icon
        // service relies on this for score-completion toasts. Validation
        // failures above don't fire (no work was started).
        try
        {

        // 1. Load EvalSet (sync — caller wraps in Task.Run if needed).
        progress?.Report(new JobProgress("Loading", null, $"Loading EvalSet: {request.EvalSetPath}"));
        var load = EvalSetLoader.Load(request.EvalSetPath);
        IList<EvalRow> rows = load.Rows;
        progress?.Report(new JobProgress("Loading", null, $"Loaded {rows.Count} row(s)."));
        foreach (var w in load.Warnings)
        {
            progress?.Report(new JobProgress("Loading", null, "⚠ " + w));
        }
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("EvalSet contains no rows.");
        }

        // 1a. Overlay edited CSV onto sidecar rows if the CSV exists
        // next to the sidecar (the Step 4 editor's saved output).
        await MergeEditedCsvAsync(rows, request.EvalSetPath, progress, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Resolve client selection (mirrors ScoreCommand.cs:166-179).
        bool useA2AResponse = !string.IsNullOrWhiteSpace(request.M365AgentId);
        bool useA2AJudge = request.JudgeProvider == JudgeProvider.WorkIq
            && !string.IsNullOrWhiteSpace(request.M365AgentId)
            && !string.IsNullOrWhiteSpace(request.JudgeAgentId);

        IWorkIQClientHandle responseHandle = ClientFactory(request, ClientPurpose.Response);
        bool needsSeparateScoringClient = request.JudgeProvider == JudgeProvider.WorkIq
            && useA2AResponse
            && string.IsNullOrWhiteSpace(request.JudgeAgentId);
        IWorkIQClientHandle? scoringHandle = needsSeparateScoringClient
            ? ClientFactory(request, ClientPurpose.Scoring)
            : null;

        IWorkIQClient responseClient = responseHandle.Client;
        IWorkIQClient scoringClient = scoringHandle?.Client ?? responseClient;

        try
        {
            progress?.Report(new JobProgress("Connecting", null,
                useA2AResponse ? "Starting WorkIQ A2A target…" : "Starting WorkIQ session…"));
            await responseHandle.StartAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            if (scoringHandle is not null)
            {
                await scoringHandle.StartAsync(request.TenantId, cancellationToken).ConfigureAwait(false);
            }

            // 3. Optional preflight.
            if (!request.SkipPreflight)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new JobProgress("Preflight", null, "Running preflight…"));
                var pre = await Preflight.RunAsync(new PreflightOptions
                {
                    TenantId = request.TenantId,
                    AskClient = (prompt, ct) => scoringClient.AskAsync(prompt, request.TenantId, ct),
                    ApproveEulaAsync = EulaApprover,
                }, cancellationToken).ConfigureAwait(false);
                if (!pre.Passed)
                {
                    var failed = string.Join("; ", pre.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}"));
                    throw new InvalidOperationException(
                        $"Preflight failed — {failed} (re-run with SkipPreflight=true to bypass).");
                }
            }

            // 4. Generate responses.
            cancellationToken.ThrowIfCancellationRequested();
            int responseConcurrency = useA2AResponse ? request.Concurrency : 1;
            progress?.Report(new JobProgress("Responding", 0,
                $"Generating answers for {rows.Count} prompt(s)…"));

            var respEvalOptions = new EvaluateOptions
            {
                SystemPrompt = string.IsNullOrEmpty(request.SystemPrompt) ? null : request.SystemPrompt,
                ConnectorId = request.ConnectorId,
                TenantId = request.TenantId,
                AgentId = request.M365AgentId,
                Concurrency = responseConcurrency,
                DelayMs = request.DelayMs,
                OnProgress = (done, total, _) =>
                {
                    int? pct = total > 0 ? (int)Math.Round(done * 50.0 / total) : null;
                    progress?.Report(new JobProgress(
                        "Responding", pct, $"[{done}/{total}] response generated"));
                },
            };
            await ResponseEvaluator
                .EvaluatePromptsAsync(rows, responseClient, respEvalOptions, cancellationToken)
                .ConfigureAwait(false);

            // 5. Score answers.
            cancellationToken.ThrowIfCancellationRequested();
            int scoringConcurrency = (request.JudgeProvider == JudgeProvider.WorkIq && !useA2AJudge)
                ? 1
                : request.Concurrency;

            progress?.Report(new JobProgress("Scoring", 50,
                $"Scoring with judge: {request.JudgeProvider.ToWireString()}…"));
            var scoreOptions = new ScoreOptions
            {
                TenantId = request.TenantId,
                JudgeProvider = request.JudgeProvider,
                JudgeAgentId = useA2AJudge ? request.JudgeAgentId : null,
                Evaluators = Array.Empty<EvaluatorName>(),
                Concurrency = scoringConcurrency,
                DelayMs = request.DelayMs,
                Threshold = request.Threshold,
                OnProgress = (done, total) =>
                {
                    int? pct = total > 0 ? 50 + (int)Math.Round(done * 50.0 / total) : null;
                    progress?.Report(new JobProgress(
                        "Scoring", pct, $"[{done}/{total}] scored"));
                },
            };
            await Scorer.ScoreAnswersAsync(rows, scoringClient, scoreOptions, cancellationToken)
                        .ConfigureAwait(false);
        }
        finally
        {
            if (scoringHandle is not null)
            {
                await scoringHandle.Client.DisposeAsync().ConfigureAwait(false);
            }
            await responseHandle.Client.DisposeAsync().ConfigureAwait(false);
        }

        // 6. Aggregate + write report.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new JobProgress("Writing", 95, "Writing report…"));

        var roRows = (IReadOnlyList<EvalRow>)rows.ToList();
        ScoringResult summary = ScoringResult.Calculate(roRows, request.Threshold);
        var evalResult = new EvalResult
        {
            Rows = roRows,
            InputFile = request.EvalSetPath,
            InputFormat = InputFormat.Json,
            Timestamp = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            SystemPrompt = string.IsNullOrEmpty(request.SystemPrompt) ? null : request.SystemPrompt,
            JudgeProvider = request.JudgeProvider,
            Evaluators = Array.Empty<EvaluatorName>(),
            Metadata = load.Metadata.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        };
        string reportText = MarkdownReporter.GenerateReport(evalResult, summary);
        string reportPath = await MarkdownReporter
            .WriteReportAsync(reportText, request.OutputDir, request.EvalSetPath, cancellationToken)
            .ConfigureAwait(false);

        // 7. Write scored CSV (engine-level — CLI parity).
        string? scoredCsvPath = null;
        try
        {
            scoredCsvPath = await ResultsCsvWriter
                .WriteAsync(roRows, request.OutputDir, request.EvalSetPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Don't swallow cancellation as a "CSV skipped" warning —
            // it's a real job cancellation and the caller's flow needs
            // to see it (GPT-5.5 slice-26 review IMPORTANT #2).
            throw;
        }
        catch (Exception ex)
        {
            // The report is the primary artifact; don't fail the whole
            // job if scored-CSV write fails (e.g. file locked in Excel).
            progress?.Report(new JobProgress("Writing", 100,
                $"⚠ Scored CSV write skipped: {ex.Message}"));
        }

        progress?.Report(new JobProgress(
            "Complete",
            100,
            $"Scoring complete. Pass={summary.PassCount} Fail={summary.FailCount} Avg={summary.AverageScore}"));

        var scoreResult = new EvalScoreResult(
            ReportPath: reportPath,
            ScoredCsvPath: scoredCsvPath,
            TotalScored: summary.TotalQuestions,
            PassCount: summary.PassCount,
            FailCount: summary.FailCount,
            AverageScore: summary.AverageScore);
        RaiseStateChanged(request.OutputDir, JobStatus.Complete);
        return scoreResult;

        }
        catch (OperationCanceledException)
        {
            // Honor user cancellation — surface as Cancelled, not Failed.
            // (GPT-5.5 slice-26 review: never collapse cancellation into
            // a generic error path.)
            RaiseStateChanged(request.OutputDir, JobStatus.Cancelled);
            throw;
        }
        catch
        {
            RaiseStateChanged(request.OutputDir, JobStatus.Failed);
            throw;
        }
    }

    private void RaiseStateChanged(string jobDirectory, JobStatus status)
    {
        try
        {
            JobStateChanged?.Invoke(
                this,
                new JobStateChangedEventArgs(jobDirectory, status, JobKind.Scoring));
        }
        catch
        {
            // Subscriber faults must never leak into the scoring
            // pipeline (mirrors EvalGenJobService.RaiseStateChanged).
        }
    }

    /// <summary>
    /// Overlay edits saved by the Step 4 row editor onto the in-memory
    /// rows loaded from the sidecar. Only mutates four columns
    /// (prompt, expected_answer, source_location, actual_answer);
    /// assertions / evaluators / metadata / category from the sidecar
    /// are preserved unchanged. If the CSV row count doesn't match the
    /// sidecar row count, the merge is abandoned with a progress
    /// warning — the user can either revert their CSV edits or
    /// regenerate the eval set.
    /// </summary>
    private static async Task MergeEditedCsvAsync(
        IList<EvalRow> sidecarRows,
        string evalSetPath,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? csvPath = ResolveCsvPath(evalSetPath);
        if (csvPath is null || !File.Exists(csvPath))
        {
            return;
        }

        IReadOnlyList<EvalRowRecord> csvRows;
        try
        {
            // The 4-column reader is sync; offload to keep the caller's
            // dispatcher responsive (the file is small but I/O is I/O).
            csvRows = await Task.Run(() => EvalCsvEditor.ReadFlat(csvPath), cancellationToken)
                                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // GPT-5.5 slice-26 review IMPORTANT #2: never swallow
            // cancellation as a benign "could not read CSV" warning.
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new JobProgress("Loading", null,
                $"⚠ Could not read edited CSV ({Path.GetFileName(csvPath)}): {ex.Message}. " +
                "Scoring will proceed with the original sidecar prompts."));
            return;
        }

        if (csvRows.Count != sidecarRows.Count)
        {
            progress?.Report(new JobProgress("Loading", null,
                $"⚠ Edited CSV has {csvRows.Count} row(s) but the sidecar has {sidecarRows.Count}; " +
                "row counts must match to merge edits. Scoring will proceed with the original sidecar prompts."));
            return;
        }

        int changed = 0;
        for (int i = 0; i < sidecarRows.Count; i++)
        {
            var sidecar = sidecarRows[i];
            var edited = csvRows[i];
            bool anyChange =
                !string.Equals(sidecar.Prompt, edited.Prompt, StringComparison.Ordinal) ||
                !string.Equals(sidecar.ExpectedAnswer, edited.ExpectedAnswer, StringComparison.Ordinal) ||
                !string.Equals(sidecar.SourceLocation, edited.SourceLocation, StringComparison.Ordinal) ||
                !string.Equals(sidecar.ActualAnswer, edited.ActualAnswer, StringComparison.Ordinal);
            if (!anyChange)
            {
                continue;
            }
            // Capture whether the prompt is being changed BEFORE overwriting
            // sidecar.Prompt — otherwise the comparison below trivially holds
            // (we'd be comparing edited.Prompt to itself) and the stale-answer
            // safety would never fire.
            bool promptChanged = !string.Equals(sidecar.Prompt, edited.Prompt, StringComparison.Ordinal);

            sidecar.Prompt = edited.Prompt;
            sidecar.ExpectedAnswer = edited.ExpectedAnswer;
            sidecar.SourceLocation = edited.SourceLocation ?? string.Empty;
            // When the prompt has changed, blank out the previous answer
            // so we don't score a stale answer against a new prompt; the
            // response evaluator only fills ActualAnswer when it's null
            // or empty (see ResponseEvaluator skip-if-already-populated).
            if (promptChanged)
            {
                sidecar.ActualAnswer = string.Empty;
            }
            else
            {
                sidecar.ActualAnswer = edited.ActualAnswer ?? string.Empty;
            }
            changed++;
        }
        if (changed > 0)
        {
            progress?.Report(new JobProgress("Loading", null,
                $"Applied edits from {Path.GetFileName(csvPath)} to {changed} row(s)."));
        }
    }

    /// <summary>
    /// Convention: for <c>foo.evalgen.json</c> the CSV is <c>foo.csv</c>
    /// in the same folder. Mirrors how
    /// <c>EvalToolkit.UI.Services.EvalGenJobService</c> emits the pair.
    /// </summary>
    private static string? ResolveCsvPath(string evalSetPath)
    {
        string? dir = Path.GetDirectoryName(evalSetPath);
        string name = Path.GetFileNameWithoutExtension(evalSetPath);
        if (name.EndsWith(".evalgen", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".evalgen".Length];
        }
        if (dir is null) return null;
        return Path.Combine(dir, name + ".csv");
    }

    private static IWorkIQClientHandle DefaultClientFactory(EvalScoreRequest request, ClientPurpose purpose)
    {
        bool useA2A = purpose == ClientPurpose.Response
            && !string.IsNullOrWhiteSpace(request.M365AgentId);
        if (useA2A)
        {
            var client = new A2AWorkIQClient();
            return new WorkIQClientHandle(client, (tenant, ct) => client.StartAsync(tenant, ct));
        }
        else
        {
            var client = new CliWorkIQClient();
            return new WorkIQClientHandle(client, (tenant, ct) => client.StartAsync(tenant, ct));
        }
    }
}

/// <summary>Marks which role a factory-produced client is for.</summary>
public enum ClientPurpose
{
    Response,
    Scoring,
}

/// <summary>
/// Handle bundling an <see cref="IWorkIQClient"/> with a start delegate
/// so the service can call the concrete <c>StartAsync</c> without
/// type-switching (mirrors <c>ScoreCommand.StartClientAsync</c>).
/// </summary>
public interface IWorkIQClientHandle
{
    IWorkIQClient Client { get; }
    Task StartAsync(string? tenantId, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IWorkIQClientHandle"/>.</summary>
public sealed class WorkIQClientHandle(
    IWorkIQClient client,
    Func<string?, CancellationToken, Task> startAsync) : IWorkIQClientHandle
{
    public IWorkIQClient Client => client;
    public Task StartAsync(string? tenantId, CancellationToken cancellationToken) =>
        startAsync(tenantId, cancellationToken);
}
