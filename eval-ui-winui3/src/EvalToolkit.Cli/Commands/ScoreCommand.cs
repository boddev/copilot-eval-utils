using System.CommandLine;
using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.EvalSet;
using EvalToolkit.EvalScore.Evaluator;
using EvalToolkit.EvalScore.Models;
using EvalToolkit.EvalScore.Preflight;
using EvalToolkit.EvalScore.Reporting;
using EvalToolkit.EvalScore.Scoring;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.Cli.Commands;

/// <summary>
/// <c>eval-score-native</c> shim. Ports the EvalScore CLI in
/// <c>eval-score/node/src/index.ts</c> for the MVP shape: load an EvalSet
/// (<c>.evalgen.json</c>), run the response evaluator + scorer with the
/// real <see cref="CliWorkIQClient"/>, write a markdown report.
///
/// <para>
/// MVP scope: <c>--evalset</c> input only — the EvalScore CSV reader has
/// not been ported yet (it's a separate readers slice). Adding <c>--input</c>
/// for CSV/XLSX is tracked as a follow-up; for now, supplying a non-EvalSet
/// input prints a clear error and exits 2.
/// </para>
/// </summary>
internal static class ScoreCommand
{
    public static Command Build()
    {
        var inputOption = new Option<string?>("--input") { Description = "CSV/XLSX input (not yet supported by the native CLI — use --evalset)." };
        var evalsetOption = new Option<string?>("--evalset") { Description = "Path to a .evalgen.json EvalSet emitted by eval-gen." };
        var systemPromptOption = new Option<string?>("--system-prompt") { Description = "Inline system prompt to send before each user turn." };
        var systemPromptFileOption = new Option<string?>("--system-prompt-file") { Description = "Path to a file whose contents become the system prompt." };
        var connectorIdOption = new Option<string?>("--connector-id") { Description = "Connector hint sent to WorkIQ." };
        var m365AgentIdOption = new Option<string?>("--m365-agent-id") { Description = "Microsoft 365 Copilot agent id (for the response evaluator)." };
        var judgeAgentIdOption = new Option<string?>("--judge-agent-id") { Description = "WorkIQ agent id used as the judge." };
        var judgeProviderOption = new Option<string>("--judge-provider") { Description = "workiq | azure-openai | github-copilot.", DefaultValueFactory = _ => "workiq" };
        var fallbackJudgeProviderOption = new Option<string?>("--fallback-judge-provider") { Description = "Override or 'none' to disable the default github-copilot fallback." };
        var evaluatorsOption = new Option<string?>("--evaluators") { Description = "Comma-separated evaluator list, or 'all'." };
        var concurrencyOption = new Option<int>("--concurrency") { Description = "Parallel workers (clamped 1..5).", DefaultValueFactory = _ => 1 };
        var delayMsOption = new Option<int>("--delay-ms") { Description = "Per-worker delay between rows in milliseconds.", DefaultValueFactory = _ => 500 };
        var outputDirOption = new Option<string>("--output-dir") { Description = "Output directory for the report.", DefaultValueFactory = _ => "./output" };
        var thresholdOption = new Option<double>("--threshold") { Description = "Pass threshold for scoring (default 70).", DefaultValueFactory = _ => 70.0 };
        var tenantIdOption = new Option<string?>("--tenant-id") { Description = "Microsoft Entra tenant id for WorkIQ." };
        var setupOption = new Option<bool>("--setup") { Description = "Run preflight (WorkIQ EULA + connectivity) and exit." };
        var skipPreflightOption = new Option<bool>("--skip-preflight") { Description = "Skip the implicit preflight check before scoring." };

        var score = new Command("eval-score", "Score the responses of an EvalSet with WorkIQ + judge providers.");

        Option[] options =
        [
            inputOption, evalsetOption, systemPromptOption, systemPromptFileOption, connectorIdOption,
            m365AgentIdOption, judgeAgentIdOption, judgeProviderOption, fallbackJudgeProviderOption,
            evaluatorsOption, concurrencyOption, delayMsOption, outputDirOption, thresholdOption,
            tenantIdOption, setupOption, skipPreflightOption,
        ];
        foreach (var opt in options) score.Options.Add(opt);

        score.SetAction(async (parse, ct) =>
        {
            try
            {
                var args = new ScoreArgs(
                    Input: parse.GetValue(inputOption),
                    EvalSet: parse.GetValue(evalsetOption),
                    SystemPrompt: parse.GetValue(systemPromptOption),
                    SystemPromptFile: parse.GetValue(systemPromptFileOption),
                    ConnectorId: parse.GetValue(connectorIdOption),
                    M365AgentId: parse.GetValue(m365AgentIdOption),
                    JudgeAgentId: parse.GetValue(judgeAgentIdOption),
                    JudgeProvider: parse.GetValue(judgeProviderOption) ?? "workiq",
                    FallbackJudgeProvider: parse.GetValue(fallbackJudgeProviderOption),
                    Evaluators: parse.GetValue(evaluatorsOption),
                    Concurrency: parse.GetValue(concurrencyOption),
                    DelayMs: parse.GetValue(delayMsOption),
                    OutputDir: parse.GetValue(outputDirOption) ?? "./output",
                    Threshold: parse.GetValue(thresholdOption),
                    TenantId: parse.GetValue(tenantIdOption),
                    Setup: parse.GetValue(setupOption),
                    SkipPreflight: parse.GetValue(skipPreflightOption));

                if (args.Setup)
                {
                    return await RunSetupAsync(args, ct).ConfigureAwait(false);
                }
                return await RunScoreAsync(args, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ {ex.Message}");
                return 1;
            }
        });

        return score;
    }

    private static async Task<int> RunSetupAsync(ScoreArgs args, CancellationToken ct)
    {
        Console.Error.WriteLine("🔧 EvalScore preflight");
        await using var client = new CliWorkIQClient();
        await client.StartAsync(args.TenantId, ct).ConfigureAwait(false);

        var preflightOptions = new PreflightOptions
        {
            TenantId = args.TenantId,
            AskClient = (prompt, token) => client.AskAsync(prompt, args.TenantId, token),
            ApproveEulaAsync = ConsoleEulaApprover,
            SkipConnectivityTest = false,
        };
        PreflightResult result = await Preflight.RunAsync(preflightOptions, ct).ConfigureAwait(false);
        foreach (var check in result.Checks)
        {
            string status = check.Passed ? "✅" : "❌";
            Console.Error.WriteLine($"  {status} {check.Name}: {check.Message}");
        }
        return result.Passed ? 0 : 1;
    }

    private static async Task<int> RunScoreAsync(ScoreArgs args, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(args.Input) && string.IsNullOrWhiteSpace(args.EvalSet))
        {
            Console.Error.WriteLine(
                "❌ --input (CSV/XLSX) is not yet supported by the native CLI. " +
                "Pass --evalset path/to/your-eval.evalgen.json instead, or use the Node " +
                "eval-score CLI for legacy CSV/XLSX inputs.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(args.EvalSet))
        {
            Console.Error.WriteLine("❌ --evalset is required (CSV input is not yet supported).");
            return 2;
        }

        Console.Error.WriteLine($"📂 Loading EvalSet: {args.EvalSet}");
        var load = EvalSetLoader.Load(args.EvalSet);
        IList<EvalRow> rows = load.Rows;
        Console.Error.WriteLine($"  Loaded {rows.Count} rows.");
        foreach (string w in load.Warnings)
        {
            Console.Error.WriteLine($"  ⚠️  {w}");
        }

        string systemPrompt = ResolveSystemPrompt(args);
        JudgeProvider judgeProvider = JudgeProviders.FromWireString(args.JudgeProvider);
        bool disableFallback = string.Equals(args.FallbackJudgeProvider, "none", StringComparison.OrdinalIgnoreCase);
        JudgeProvider? fallbackProvider = disableFallback || string.IsNullOrWhiteSpace(args.FallbackJudgeProvider)
            ? null
            : JudgeProviders.FromWireString(args.FallbackJudgeProvider);
        IReadOnlyList<EvaluatorName> evaluators = EvaluatorParser.Parse(args.Evaluators);

        // Mirror TS eval-score/node/src/index.ts:218-236 client selection.
        // - Response generation: A2A only when --m365-agent-id is supplied
        //   (A2A requires an agentId); otherwise MCP via the CLI client.
        // - Scoring: A2A only when judge is WorkIQ *and* both --m365-agent-id
        //   and --judge-agent-id were supplied. Otherwise reuse the response
        //   client or, in the edge case where the user wants WorkIQ judging
        //   without a dedicated judge agent, fall back to a separate MCP
        //   client (A2A doesn't support agent-less calls).
        bool useA2AResponse = !string.IsNullOrWhiteSpace(args.M365AgentId);
        bool useA2AJudge = judgeProvider == JudgeProvider.WorkIq
            && !string.IsNullOrWhiteSpace(args.M365AgentId)
            && !string.IsNullOrWhiteSpace(args.JudgeAgentId);

        IWorkIQClient responseClient = useA2AResponse
            ? new A2AWorkIQClient()
            : new CliWorkIQClient();
        bool needsSeparateScoringClient = judgeProvider == JudgeProvider.WorkIq
            && useA2AResponse
            && string.IsNullOrWhiteSpace(args.JudgeAgentId);
        IWorkIQClient scoringClient = needsSeparateScoringClient
            ? new CliWorkIQClient()
            : responseClient;

        try
        {
            Console.Error.WriteLine(useA2AResponse
                ? "Starting WorkIQ A2A target..."
                : "Starting WorkIQ session...");
            await StartClientAsync(responseClient, args.TenantId, ct).ConfigureAwait(false);
            if (!ReferenceEquals(scoringClient, responseClient))
            {
                await StartClientAsync(scoringClient, args.TenantId, ct).ConfigureAwait(false);
            }
            Console.Error.WriteLine(useA2AResponse
                ? "  WorkIQ A2A target ready."
                : "  WorkIQ MCP session started.");

            if (!args.SkipPreflight)
            {
                Console.Error.WriteLine("🔍 Preflight…");
                IWorkIQClient preflightClient = scoringClient;
                // Normal-run preflight: skip the connectivity probe. TS only
                // runs connectivity on --setup; an unconditional probe here
                // would call AskAsync without an agent id and break A2A
                // (A2AWorkIQClient.AskAsync requires an agent id), and
                // would double-bill MCP runs. Use the default (Skip=true)
                // so EULA still fires but the connectivity check is marked
                // ⏭️  Skipped.
                var pre = await Preflight.RunAsync(new PreflightOptions
                {
                    TenantId = args.TenantId,
                    AskClient = (prompt, token) => preflightClient.AskAsync(prompt, args.TenantId, token),
                    ApproveEulaAsync = ConsoleEulaApprover,
                }, ct).ConfigureAwait(false);
                if (!pre.Passed)
                {
                    foreach (var check in pre.Checks.Where(c => !c.Passed))
                    {
                        Console.Error.WriteLine($"  ❌ {check.Name}: {check.Message}");
                    }
                    Console.Error.WriteLine("Preflight failed — re-run with --skip-preflight to bypass.");
                    return 1;
                }
            }

            // Concurrency for response generation: A2A can parallelize;
            // CliWorkIQClient MCP stdio cannot safely interleave reads/writes,
            // so force serial (TS index.ts:241).
            int responseConcurrency = useA2AResponse ? args.Concurrency : 1;
            if (!useA2AResponse && args.Concurrency > 1)
            {
                Console.Error.WriteLine(
                    "  WorkIQ MCP response generation is serialized; supply --m365-agent-id " +
                    "for A2A concurrency, or use --judge-provider github-copilot|azure-openai " +
                    "for concurrent scoring.");
            }

            Console.Error.WriteLine($"🤖 Generating answers ({rows.Count} prompts)…");
            var evalOptions = new EvaluateOptions
            {
                SystemPrompt = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
                ConnectorId = args.ConnectorId,
                TenantId = args.TenantId,
                AgentId = args.M365AgentId,
                Concurrency = responseConcurrency,
                DelayMs = args.DelayMs,
                OnProgress = (done, total, _) =>
                    Console.Error.WriteLine($"  [{done}/{total}] response generated"),
            };
            await ResponseEvaluator.EvaluatePromptsAsync(rows, responseClient, evalOptions, ct).ConfigureAwait(false);

            // Concurrency for scoring: WorkIQ judging can only parallelize via
            // A2A (judgeAgentId supplied); else stay serial. Non-WorkIQ judges
            // (github-copilot/azure-openai) parallelize freely (TS index.ts:266).
            int scoringConcurrency = (judgeProvider == JudgeProvider.WorkIq && !useA2AJudge)
                ? 1
                : args.Concurrency;

            Console.Error.WriteLine($"⚖️  Scoring with judge: {args.JudgeProvider}…");
            var scoreOptions = new ScoreOptions
            {
                TenantId = args.TenantId,
                JudgeProvider = judgeProvider,
                FallbackJudgeProvider = fallbackProvider,
                DisableFallbackJudge = disableFallback,
                JudgeAgentId = useA2AJudge ? args.JudgeAgentId : null,
                Evaluators = evaluators,
                Concurrency = scoringConcurrency,
                DelayMs = args.DelayMs,
                Threshold = args.Threshold,
                OnProgress = (done, total) =>
                    Console.Error.WriteLine($"  [{done}/{total}] scored"),
            };
            await Scorer.ScoreAnswersAsync(rows, scoringClient, scoreOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(scoringClient, responseClient))
            {
                await DisposeClientAsync(scoringClient).ConfigureAwait(false);
            }
            await DisposeClientAsync(responseClient).ConfigureAwait(false);
        }

        var roRows = (IReadOnlyList<EvalRow>)rows.ToList();
        ScoringResult summary = ScoringResult.Calculate(roRows, args.Threshold);

        var evalResult = new EvalResult
        {
            Rows = roRows,
            InputFile = args.EvalSet!,
            InputFormat = InputFormat.Json,
            Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            SystemPrompt = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            JudgeProvider = judgeProvider,
            Evaluators = evaluators,
            Metadata = load.Metadata.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        };
        string report = MarkdownReporter.GenerateReport(evalResult, summary);
        string outPath = await MarkdownReporter.WriteReportAsync(report, args.OutputDir, args.EvalSet!, ct).ConfigureAwait(false);
        Console.Error.WriteLine($"📝 Report: {outPath}");
        Console.Error.WriteLine(
            $"Done. {summary.TotalQuestions} scored — pass={summary.PassCount} fail={summary.FailCount} " +
            $"avg={summary.AverageScore} min={summary.MinScore} max={summary.MaxScore}");
        return summary.FailCount == 0 ? 0 : 1;
    }

    private static string ResolveSystemPrompt(ScoreArgs args)
    {
        if (!string.IsNullOrEmpty(args.SystemPromptFile))
        {
            return File.ReadAllText(args.SystemPromptFile);
        }
        return args.SystemPrompt ?? string.Empty;
    }

    private static ValueTask DisposeClientAsync(IWorkIQClient client)
        => client.DisposeAsync();

    // TS workiq-client.ts expresses start as optional (`client.start?.()`).
    // In the C# port StartAsync lives on the concrete classes (not on
    // IWorkIQClient), so dispatch by type rather than widening the
    // interface — keeps mock clients used by tests free of the obligation.
    private static Task StartClientAsync(IWorkIQClient client, string? tenantId, CancellationToken ct) => client switch
    {
        CliWorkIQClient cli => cli.StartAsync(tenantId, ct),
        A2AWorkIQClient a2a => a2a.StartAsync(tenantId, ct),
        _ => Task.CompletedTask,
    };

    // EULA approver wired to stdin / stderr for headless CLI runs. The WinUI
    // shell will swap this for a XAML dialog. Matches TS interactive EULA
    // flow from eval-score/node/src/setup.ts.
    private static Task<bool> ConsoleEulaApprover(CancellationToken ct) =>
        EulaService.ApproveEulaAsync(
            reader: async _ =>
            {
                // Console.In.ReadLineAsync is sync under the hood for stdin;
                // wrap in Task.Run so the cancellation token can preempt a
                // blocked stdin read (Ctrl+C still works because the process
                // terminates regardless).
                return await Task.Run(() => Console.In.ReadLine(), ct).ConfigureAwait(false);
            },
            writer: Console.Error.WriteLine,
            cancellationToken: ct);

    private sealed record ScoreArgs(
        string? Input,
        string? EvalSet,
        string? SystemPrompt,
        string? SystemPromptFile,
        string? ConnectorId,
        string? M365AgentId,
        string? JudgeAgentId,
        string JudgeProvider,
        string? FallbackJudgeProvider,
        string? Evaluators,
        int Concurrency,
        int DelayMs,
        string OutputDir,
        double Threshold,
        string? TenantId,
        bool Setup,
        bool SkipPreflight);
}
