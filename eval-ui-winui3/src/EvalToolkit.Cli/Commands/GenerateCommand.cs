using System.CommandLine;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Diagnostics;
using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.EvalGen.Pipeline;
using EvalToolkit.EvalGen.Readers;
using EvalToolkit.EvalGen.Sources;
using EvalToolkit.EvalGen.Writers;

namespace EvalToolkit.Cli.Commands;

/// <summary>
/// <c>eval-gen-native generate</c> shim. Ports the <c>generate</c> command in
/// <c>eval-gen/src/index.ts</c>: reads a dataset (file or non-file source),
/// runs the in-memory <see cref="EvalGenPipeline"/>, writes the EvalScore CSV,
/// the <c>.evalgen.json</c> sidecar, and the optional review markdown.
///
/// <para>
/// MVP coverage (slice 19): file + API/database/web source adapters, count
/// clamping, multi-prompt resolution, avoidance set loading, dry-run, all six
/// LLM providers via <see cref="LlmClientFactory"/>, and connector-schema
/// diagnostics. Multi-prompt writer wiring is parity-faithful but the writer
/// itself is exercised only when <c>--multi-prompt</c> is supplied.
/// </para>
/// </summary>
internal static class GenerateCommand
{
    public static Command Build()
    {
        var fileOption = new Option<string?>("--file") { Description = "File, directory, or comma-separated files (CSV, JSON, XLSX, ...)." };
        var sourceTypeOption = new Option<string?>("--source-type") { Description = "Data source type: api, database, or web." };
        var sourceUrlOption = new Option<string?>("--source-url") { Description = "URL for API or web source." };
        var openapiSpecOption = new Option<string?>("--openapi-spec") { Description = "OpenAPI / Swagger spec URL (for API source)." };
        var connectionStringOption = new Option<string?>("--connection-string") { Description = "Database connection string." };
        var endpointsOption = new Option<string?>("--endpoints") { Description = "Comma-separated API endpoints to sample." };
        var authHeaderOption = new Option<string?>("--auth-header") { Description = "Authorization header (e.g. \"Bearer token\")." };
        var descriptionOption = new Option<string?>("--description") { Description = "Plain-text description of what this data is.", Required = true };
        var countOption = new Option<int>("--count") { Description = "Number of questions to generate (clamped to 10..50).", DefaultValueFactory = _ => 30 };
        var outputOption = new Option<string>("--output") { Description = "Output file path.", DefaultValueFactory = _ => "./output/eval-set.csv" };
        var connectorSchemaOption = new Option<string?>("--connector-schema") { Description = "Optional connector schema JSON for field-awareness diagnostics." };
        var noReviewOption = new Option<bool>("--no-review") { Description = "Skip review output generation." };
        var providerOption = new Option<string>("--provider") { Description = "LLM provider (m365-copilot, m365-copilot-api, workiq-a2a, azure-openai, github-copilot, command).", DefaultValueFactory = _ => "m365-copilot" };
        var modelOption = new Option<string>("--model") { Description = "Azure OpenAI model deployment name.", DefaultValueFactory = _ => "gpt-4o" };
        var llmCommandOption = new Option<string?>("--llm-command") { Description = "Command to run when --provider command is selected." };
        var m365TimeZoneOption = new Option<string?>("--m365-time-zone") { Description = "Time zone for Microsoft 365 Copilot Chat API locationHint." };
        var m365TenantOption = new Option<string?>("--m365-tenant") { Description = "Microsoft Entra tenant ID for Microsoft 365 Copilot authentication." };
        var extensionsOption = new Option<string?>("--extensions") { Description = "Comma-separated file extensions to include when --file is a directory." };
        var avoidEvalsetsOption = new Option<string?>("--avoid-evalsets") { Description = "Comma-separated .evalgen.json files or directories to avoid duplicating." };
        var multiPromptOption = new Option<bool>("--multi-prompt") { Description = "Also emit m365/evalscore JSON grouped into multi-prompt evaluator items." };
        var multiPromptTurnsOption = new Option<int?>("--multi-prompt-turns") { Description = "Prompts per multi-prompt evaluator item (2-20); enables --multi-prompt." };
        var multiPromptOutputOption = new Option<string?>("--multi-prompt-output") { Description = "Output path for the multi-prompt m365/evalscore JSON document." };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Profile and diagnose only, no LLM calls." };

        var generate = new Command("eval-gen", "Generate an evaluation set for Microsoft 365 Copilot connector testing.");

        Option[] options =
        [
            fileOption, sourceTypeOption, sourceUrlOption, openapiSpecOption, connectionStringOption,
            endpointsOption, authHeaderOption, descriptionOption, countOption, outputOption,
            connectorSchemaOption, noReviewOption, providerOption, modelOption, llmCommandOption,
            m365TimeZoneOption, m365TenantOption, extensionsOption, avoidEvalsetsOption,
            multiPromptOption, multiPromptTurnsOption, multiPromptOutputOption, dryRunOption,
        ];
        foreach (var opt in options) generate.Options.Add(opt);

        generate.SetAction(async (parse, ct) =>
        {
            try
            {
                var args = new GenerateArgs(
                    File: parse.GetValue(fileOption),
                    SourceType: parse.GetValue(sourceTypeOption),
                    SourceUrl: parse.GetValue(sourceUrlOption),
                    OpenapiSpec: parse.GetValue(openapiSpecOption),
                    ConnectionString: parse.GetValue(connectionStringOption),
                    Endpoints: OptionHelpers.SplitCsv(parse.GetValue(endpointsOption)),
                    AuthHeader: parse.GetValue(authHeaderOption),
                    Description: parse.GetValue(descriptionOption) ?? string.Empty,
                    Count: OptionHelpers.ClampGenerateCount(parse.GetValue(countOption)),
                    Output: parse.GetValue(outputOption) ?? "./output/eval-set.csv",
                    ConnectorSchema: parse.GetValue(connectorSchemaOption),
                    NoReview: parse.GetValue(noReviewOption),
                    Provider: LLMProviders.FromWireString(parse.GetValue(providerOption) ?? "m365-copilot"),
                    Model: parse.GetValue(modelOption) ?? "gpt-4o",
                    LlmCommand: parse.GetValue(llmCommandOption),
                    M365TimeZone: parse.GetValue(m365TimeZoneOption),
                    M365TenantId: parse.GetValue(m365TenantOption),
                    Extensions: OptionHelpers.SplitCsv(parse.GetValue(extensionsOption)),
                    AvoidEvalsets: OptionHelpers.SplitCsv(parse.GetValue(avoidEvalsetsOption)),
                    MultiPrompt: OptionHelpers.IsMultiPromptEnabled(
                        parse.GetValue(multiPromptOption),
                        parse.GetValue(multiPromptTurnsOption)),
                    MultiPromptTurns: parse.GetValue(multiPromptTurnsOption),
                    MultiPromptOutput: parse.GetValue(multiPromptOutputOption),
                    DryRun: parse.GetValue(dryRunOption));

                return await RunAsync(args, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return generate;
    }

    internal static async Task<int> RunAsync(GenerateArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.File) && string.IsNullOrWhiteSpace(args.SourceType))
        {
            Console.Error.WriteLine("Error: either --file or --source-type is required.");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(args.Description))
        {
            Console.Error.WriteLine("Error: --description is required.");
            return 1;
        }

        PrintHeader(args);

        // 1. Read dataset.
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records;
        InputFormat format;
        string sourceName;

        if (!string.IsNullOrWhiteSpace(args.SourceType))
        {
            (records, format, sourceName) = await FetchFromSourceAsync(args, ct).ConfigureAwait(false);
        }
        else
        {
            (records, format, sourceName) = ReadFromFile(args);
        }

        Console.Error.WriteLine($"  Loaded {records.Count} records ({format} format)");

        if (records.Count == 0)
        {
            Console.Error.WriteLine("Error: no records loaded from source.");
            return 1;
        }

        // 2. Optional connector-schema diagnostics — load early, run post-pipeline.
        ConnectorSchema? connectorSchema = null;
        DatasetProfile? datasetProfile = null;
        if (!string.IsNullOrWhiteSpace(args.ConnectorSchema))
        {
            connectorSchema = ConnectorDiagnostics.LoadSchema(args.ConnectorSchema);
            datasetProfile = Profiler.ProfileDataset(records, sourceName, format);
        }

        // 3. Dry-run short-circuit (profile + diagnostics only).
        if (args.DryRun)
        {
            Console.Error.WriteLine("Dry run complete (no LLM calls).");
            return 0;
        }

        // 4. Build LLM client + load avoidance set.
        await using var llmClient = LlmClientFactory.Create(new LlmClientOptions
        {
            Provider = args.Provider,
            Model = args.Model,
            Command = args.LlmCommand,
            M365TimeZone = args.M365TimeZone,
            M365TenantId = args.M365TenantId,
        });

        Dedupe.AvoidanceSet? avoidance = null;
        if (args.AvoidEvalsets is { Count: > 0 })
        {
            avoidance = Dedupe.LoadAvoidanceSet(args.AvoidEvalsets);
            Console.Error.WriteLine($"  Avoidance set: {avoidance.Files.Count} sidecar(s) loaded");
        }

        // 5. Run the pipeline.
        var pipelineResult = await EvalGenPipeline.RunAsync(new EvalGenPipeline.Options
        {
            Records = records,
            SourceName = sourceName,
            Format = format,
            Description = args.Description,
            Count = args.Count,
            LlmClient = llmClient,
            Avoidance = avoidance,
            CancellationToken = ct,
        }).ConfigureAwait(false);

        Console.Error.WriteLine($"  Generated {pipelineResult.Validated.Count} validated items");
        foreach (var warning in pipelineResult.Warnings)
        {
            Console.Error.WriteLine($"  ⚠ {warning}");
        }

        if (connectorSchema is not null && datasetProfile is not null)
        {
            var diagReport = ConnectorDiagnostics.RunDiagnostics(
                pipelineResult.Validated, datasetProfile, connectorSchema);
            Console.Error.WriteLine(ConnectorDiagnostics.FormatDiagnosticReport(diagReport));
        }

        // 6. Write outputs.
        var csvWriter = new EvalCsvWriter();
        var csvPath = csvWriter.Write(pipelineResult.Validated, args.Output);
        Console.Error.WriteLine($"  Wrote eval CSV: {csvPath}");

        var sidecarWriter = new SidecarJsonWriter();
        var sidecarPath = sidecarWriter.Write(
            pipelineResult.Validated,
            args.Description,
            sourceName,
            args.Output);
        Console.Error.WriteLine($"  Wrote sidecar:  {sidecarPath}");

        if (!args.NoReview)
        {
            var reviewContent = Reviewer.FormatReview(
                pipelineResult.Validated,
                pipelineResult.Validation,
                args.Description,
                sourceName);
            var reviewWriter = new ReviewMarkdownWriter();
            var reviewPath = OptionHelpers.RewriteOutputExtension(args.Output, "-review.md");
            var written = reviewWriter.Write(reviewContent, reviewPath);
            Console.Error.WriteLine($"  Wrote review:   {written}");
        }

        if (args.MultiPrompt)
        {
            int turns = OptionHelpers.ResolveMultiPromptTurns(args.MultiPromptTurns, true) ?? 3;
            var mpWriter = new M365MultiPromptWriter();
            var mpPath = args.MultiPromptOutput
                ?? OptionHelpers.DeriveMultiPromptOutputPath(args.Output);
            var written = mpWriter.Write(
                pipelineResult.Validated,
                args.Description,
                sourceName,
                mpPath,
                new M365MultiPromptOptions { PromptsPerThread = turns, Model = args.Model });
            Console.Error.WriteLine($"  Wrote multi-prompt: {written}");
        }

        Console.Error.WriteLine("Done.");
        return 0;
    }

    private static Dictionary<string, object?> DatasetRowToDictionary(DatasetRow row)
    {
        var dict = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);
        foreach (var kv in row.Entries)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }

    private static (IReadOnlyList<IReadOnlyDictionary<string, object?>> Records, InputFormat Format, string SourceName)
        ReadFromFile(GenerateArgs args)
    {
        var readResult = DatasetReader.ReadDatasetFile(args.File!, new ReadDatasetOptions
        {
            Extensions = args.Extensions,
        });
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records = readResult.Records
            .Select<DatasetRow, IReadOnlyDictionary<string, object?>>(DatasetRowToDictionary)
            .ToList();
        string sourceName = readResult.SourceFiles.Count > 1
            ? string.Join(", ", readResult.SourceFiles)
            : readResult.SourceFiles.Count == 1
                ? readResult.SourceFiles[0]
                : Path.GetFileName(args.File!);
        return (records, readResult.Format, sourceName);
    }

    private static async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Records, InputFormat Format, string SourceName)>
        FetchFromSourceAsync(GenerateArgs args, CancellationToken ct)
    {
        IDataSource adapter;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(args.AuthHeader))
        {
            headers["Authorization"] = args.AuthHeader;
        }

        switch (args.SourceType!.Trim().ToLowerInvariant())
        {
            case "api":
                if (string.IsNullOrWhiteSpace(args.SourceUrl))
                    throw new InvalidOperationException("--source-url is required for API source");
                adapter = new ApiSource(new ApiSourceOptions
                {
                    BaseUrl = args.SourceUrl,
                    SpecUrl = args.OpenapiSpec,
                    Headers = headers.Count > 0 ? headers : null,
                    Endpoints = args.Endpoints,
                });
                break;
            case "database":
                if (string.IsNullOrWhiteSpace(args.ConnectionString))
                    throw new InvalidOperationException("--connection-string is required for database source");
                adapter = new DatabaseSource(new DatabaseSourceOptions
                {
                    Type = DatabaseType.Sqlite,
                    ConnectionString = args.ConnectionString,
                });
                break;
            case "web":
                if (string.IsNullOrWhiteSpace(args.SourceUrl))
                    throw new InvalidOperationException("--source-url is required for web source");
                adapter = new WebSource(new WebSourceOptions
                {
                    Url = args.SourceUrl,
                    Headers = headers.Count > 0 ? headers : null,
                });
                break;
            default:
                throw new InvalidOperationException($"Unknown source type: {args.SourceType}");
        }

        try
        {
            var progress = new Progress<string>(msg => Console.Error.WriteLine($"  {msg}"));
            var result = await adapter.FetchAsync(progress, ct).ConfigureAwait(false);
            return (result.Records, result.Format, result.SourceName);
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    private static void PrintHeader(GenerateArgs args)
    {
        Console.Error.WriteLine("╔══════════════════════════════════════════════╗");
        Console.Error.WriteLine("║          EvalGen — Generating Eval Set        ║");
        Console.Error.WriteLine("╚══════════════════════════════════════════════╝");
        if (!string.IsNullOrWhiteSpace(args.SourceType))
        {
            string detail = args.SourceUrl ?? args.ConnectionString ?? string.Empty;
            Console.Error.WriteLine($"  Source:      {args.SourceType} ({detail})");
        }
        else
        {
            Console.Error.WriteLine($"  File:        {Path.GetFullPath(args.File!)}");
        }
        string descSnippet = args.Description.Length > 60
            ? args.Description[..60] + "..."
            : args.Description;
        Console.Error.WriteLine($"  Description: {descSnippet}");
        Console.Error.WriteLine($"  Count:       {args.Count}");
        Console.Error.WriteLine($"  Output:      {Path.GetFullPath(args.Output)}");
        if (args.AvoidEvalsets is { Count: > 0 })
        {
            Console.Error.WriteLine($"  Avoiding:    {string.Join(", ", args.AvoidEvalsets.Select(Path.GetFullPath))}");
        }
        if (args.MultiPrompt)
        {
            int turns = OptionHelpers.ResolveMultiPromptTurns(args.MultiPromptTurns, true) ?? 3;
            Console.Error.WriteLine($"  MultiPrompt: {turns} prompts per evaluator item");
        }
        if (!args.DryRun)
        {
            Console.Error.WriteLine($"  Provider:    {args.Provider.ToWireString()}");
        }
        Console.Error.WriteLine();
    }
}

internal sealed record GenerateArgs(
    string? File,
    string? SourceType,
    string? SourceUrl,
    string? OpenapiSpec,
    string? ConnectionString,
    IReadOnlyList<string>? Endpoints,
    string? AuthHeader,
    string Description,
    int Count,
    string Output,
    string? ConnectorSchema,
    bool NoReview,
    LLMProvider Provider,
    string Model,
    string? LlmCommand,
    string? M365TimeZone,
    string? M365TenantId,
    IReadOnlyList<string>? Extensions,
    IReadOnlyList<string>? AvoidEvalsets,
    bool MultiPrompt,
    int? MultiPromptTurns,
    string? MultiPromptOutput,
    bool DryRun);
