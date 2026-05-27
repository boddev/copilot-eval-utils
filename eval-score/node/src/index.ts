#!/usr/bin/env node

import * as fs from 'fs';
import * as path from 'path';
import { Command } from 'commander';
import { EvalResult, CliOptions, JudgeProvider } from './types';
import { readEvalFile } from './readers';
import { writeEvalFile } from './writers';
import { A2AWorkIQClient, CliWorkIQClient, WorkIQClient, resolveSystemPrompt } from './workiq-client';
import { evaluatePrompts } from './evaluator';
import { scoreAnswers, calculateScoringResult, parseEvaluators } from './scorer';
import { generateHtmlReport, generateReport, writeHtmlReport, writeReport } from './reporter';
import { loadAssertionsFromSidecar, evaluateAllAssertions } from './assertion-checker';
import { loadEvalSet } from './evalset-loader';
import { runPreflight, printPreflightResults } from './setup';

async function main(): Promise<void> {
  const program = new Command();

  program
    .name('eval-score')
    .description('Evaluate WorkIQ answers against a known-correct dataset')
    .option('--input <path>', 'Path to evaluation dataset')
    .option('--system-prompt <text>', 'Inline system prompt')
    .option('--system-prompt-file <path>', 'Path to system prompt file')
    .option('--connector-id <id>', 'Microsoft 365 Copilot connector ID to target')
    .option('--m365-agent-id <id>', 'Microsoft 365 Copilot agent ID to target through WorkIQ A2A')
    .option('--judge-agent-id <id>', 'Microsoft 365 Copilot agent ID used as the WorkIQ judge over A2A (enables concurrent scoring); falls back to EVALSCORE_JUDGE_AGENT_ID')
    .option('--connector-prompt-hint', 'Inject connector targeting text into WorkIQ prompts', false)
    .option('--no-connector-prompt-hint', 'Do not inject connector targeting text into WorkIQ prompts')
    .option('--judge-provider <provider>', 'Scoring provider: workiq, github-copilot, azure-openai', 'workiq')
    .option('--fallback-judge-provider <provider>', 'Backup semantic judge when WorkIQ judging fails: github-copilot, azure-openai, or none')
    .option('--evaluators <names>', 'Comma-separated evaluators or "all"', 'Relevance,Coherence')
    .option('--concurrency <number>', 'Maximum concurrent request workers', '1')
    .option('--delay-ms <number>', 'Delay between requests per worker', '500')
    .option('--checkpoint-file <path>', 'JSON checkpoint file for partial high-volume results')
    .option('--force-score', 'Re-score rows even when the input already contains scores/status')
    .option('--output-dir <path>', 'Output directory', './output')
    .option('--threshold <number>', 'Pass/fail threshold (0-100)', '70')
    .option('--tenant-id <id>', 'Microsoft 365 tenant ID to target')
    .option('--sidecar <path>', 'EvalGen sidecar JSON for assertion-aware scoring')
    .option('--evalset <path>', 'Load EvalGen EvalSet JSON directly (includes assertions)')
    .option('--setup', 'Run preflight checks and setup only')
    .option('--skip-preflight', 'Skip preflight checks')
    .parse(process.argv);

  const opts = program.opts();

  // Handle setup-only mode
  if (opts.setup) {
    const preflightResult = await runPreflight({
      tenantId: opts.tenantId as string | undefined,
      skipConnectivityTest: false,
    });
    printPreflightResults(preflightResult);
    process.exit(preflightResult.passed ? 0 : 1);
  }

  const options: CliOptions & Record<string, unknown> = {
    input: opts.input as string ?? '',
    systemPrompt: opts.systemPrompt as string | undefined,
    systemPromptFile: opts.systemPromptFile as string | undefined,
    connectorId: opts.connectorId as string | undefined,
    m365AgentId: opts.m365AgentId as string | undefined,
    judgeAgentId: (opts.judgeAgentId as string | undefined) ?? process.env.EVALSCORE_JUDGE_AGENT_ID,
    connectorPromptHint: opts.connectorPromptHint as boolean | undefined,
    judgeProvider: opts.judgeProvider as JudgeProvider,
    fallbackJudgeProvider: opts.fallbackJudgeProvider as 'github-copilot' | 'azure-openai' | 'none' | undefined,
    evaluators: opts.evaluators as string | undefined,
    concurrency: parsePositiveInt(opts.concurrency, 1),
    delayMs: parseNonNegativeInt(opts.delayMs, 500),
    checkpointFile: opts.checkpointFile as string | undefined,
    forceScore: opts.forceScore as boolean | undefined,
    outputDir: opts.outputDir as string,
    threshold: Number(opts.threshold),
    tenantId: opts.tenantId as string | undefined,
    sidecar: opts.sidecar as string | undefined,
  };

  const evalsetPath = opts.evalset as string | undefined;
  const judgeProvider = validateJudgeProvider(options.judgeProvider ?? 'workiq');
  const fallbackJudgeProvider = validateFallbackJudgeProvider(options.fallbackJudgeProvider as string | undefined);
  const evaluators = parseEvaluators(options.evaluators);
  const concurrency = options.concurrency ?? 1;
  const delayMs = options.delayMs ?? 500;

  if (!options.input && !evalsetPath) {
    throw new Error('--input <path> or --evalset <path> is required. Use --setup to run preflight checks only.');
  }

  // Validate input file exists
  const inputPath = evalsetPath ? path.resolve(evalsetPath) : path.resolve(options.input);
  if (!fs.existsSync(inputPath)) {
    throw new Error(`Input file not found: ${inputPath}`);
  }

  // Run preflight checks (unless skipped)
  if (!opts.skipPreflight) {
    const preflightResult = await runPreflight({
      tenantId: options.tenantId,
      skipConnectivityTest: true,
    });
    printPreflightResults(preflightResult);

    if (!preflightResult.passed) {
      console.error('  Use --skip-preflight to bypass these checks.');
      process.exit(1);
    }
  }

  // Resolve system prompt
  const systemPrompt = resolveSystemPrompt(options.systemPrompt, options.systemPromptFile);

  // Print startup banner to stderr
  console.error('╔══════════════════════════════════════════════╗');
  console.error('║          EvalScore - Starting           ║');
  console.error('╚══════════════════════════════════════════════╝');
  console.error(`  Input file:    ${inputPath}`);
  console.error(`  Output dir:    ${path.resolve(options.outputDir)}`);
  console.error(`  Threshold:     ${options.threshold}%`);
  if (options.tenantId) {
    console.error(`  Tenant ID:     ${options.tenantId}`);
  }
  if (options.connectorId) {
    console.error(`  Connector ID:  ${options.connectorId}`);
  }
  if (options.m365AgentId) {
    console.error(`  M365 Agent ID: ${options.m365AgentId}`);
  }
  if (options.judgeAgentId) {
    console.error(`  Judge Agent ID: ${options.judgeAgentId}`);
  }
  console.error(`  Judge:        ${judgeProvider}`);
  console.error(`  Evaluators:   ${evaluators.join(', ')}`);
  console.error(`  Concurrency:  ${concurrency}`);
  if (systemPrompt) {
    const preview = systemPrompt.length > 60 ? systemPrompt.slice(0, 60) + '...' : systemPrompt;
    console.error(`  System prompt: ${preview}`);
  }
  console.error('');

  // Ensure output directory exists
  fs.mkdirSync(path.resolve(options.outputDir), { recursive: true });

  // Read input file
  console.error('Reading input file...');
  let rows: import('./types').EvalRow[];
  let format: import('./types').InputFormat;

  if (evalsetPath) {
    // Direct EvalSet JSON loading — the preferred integration path
    const evalSetResult = loadEvalSet(evalsetPath);
    rows = evalSetResult.rows;
    format = 'json';

    console.error(`  Loaded ${rows.length} evaluation rows from EvalSet JSON`);
    const withAssertions = rows.filter(r => r.assertions && r.assertions.length > 0).length;
    const totalAssertionsCount = rows.reduce((sum, r) => sum + (r.assertions?.length ?? 0), 0);
    console.error(`  ${totalAssertionsCount} assertions across ${withAssertions} questions`);

    // Print EvalSet metadata
    if (Object.keys(evalSetResult.metadata).length > 0) {
      console.error(`  EvalSet: ${evalSetResult.metadata.description ?? ''}`);
      if (evalSetResult.metadata.model) console.error(`  Model: ${evalSetResult.metadata.model}`);
    }

    // Print warnings from connector diagnostics
    if (evalSetResult.warnings.length > 0) {
      console.error(`\n  ⚠️  ${evalSetResult.warnings.length} connector diagnostic warning(s):`);
      for (const w of evalSetResult.warnings.slice(0, 5)) {
        console.error(`    - ${w}`);
      }
      if (evalSetResult.warnings.length > 5) {
        console.error(`    ... and ${evalSetResult.warnings.length - 5} more`);
      }
    }
  } else {
    const fileResult = await readEvalFile(inputPath);
    rows = fileResult.rows;
    format = fileResult.format;
    console.error(`  Loaded ${rows.length} evaluation rows (${format} format)`);

    // Load assertions from sidecar if provided
    if (options.sidecar) {
      console.error('Loading assertions from sidecar...');
      loadAssertionsFromSidecar(rows, options.sidecar);
      const withAssertions = rows.filter(r => r.assertions && r.assertions.length > 0).length;
      const totalAssertionsCount = rows.reduce((sum, r) => sum + (r.assertions?.length ?? 0), 0);
      console.error(`  Loaded ${totalAssertionsCount} assertions across ${withAssertions} questions`);
    }
  }

  const checkpointFile = path.resolve(
    options.checkpointFile ??
    path.join(options.outputDir, `${path.basename(inputPath, path.extname(inputPath))}-checkpoint.json`)
  );

  if (options.forceScore) {
    for (const row of rows) {
      row.similarityScore = undefined;
      row.metrics = undefined;
      if (row.error?.code === 'evaluatorsFailed') {
        row.error = undefined;
      }
      row.status = undefined;
      row.assertionResults = undefined;
    }
  }
  const checkpoint = async () => writeCheckpoint(checkpointFile, rows, {
    inputFile: inputPath,
    judgeProvider,
    evaluators,
    target: buildTarget(options),
  });

  // Scoring client selection:
  //   - If judging over A2A (judgeProvider=workiq + a judgeAgentId), reuse the
  //     A2A response client so the judge runs over REST and can scale with
  //     --concurrency.
  //   - If judging over WorkIQ without a dedicated judge agent, we must use
  //     the local MCP/CLI client (A2A requires an agentId), and that path is
  //     serialized.
  //   - For non-WorkIQ judges (github-copilot, azure-openai), the scoring
  //     client is unused; reuse responseClient harmlessly.
  const useA2AJudge = judgeProvider === 'workiq' && !!options.m365AgentId && !!options.judgeAgentId;
  const responseClient: WorkIQClient = options.m365AgentId ? new A2AWorkIQClient() : new CliWorkIQClient();
  const scoringClient: WorkIQClient = judgeProvider === 'workiq' && options.m365AgentId && !options.judgeAgentId
    ? new CliWorkIQClient()
    : responseClient;

  console.error(options.m365AgentId ? 'Starting WorkIQ A2A target...' : 'Starting WorkIQ session...');
  await responseClient.start?.(options.tenantId);
  if (scoringClient !== responseClient) {
    await scoringClient.start?.(options.tenantId);
  }
  console.error(options.m365AgentId ? '  WorkIQ A2A target ready.\n' : '  WorkIQ MCP session started.\n');

  try {
    // Evaluate prompts
    console.error('Evaluating prompts...');
    const responseConcurrency = options.m365AgentId ? concurrency : 1;
    if (!options.m365AgentId && concurrency > 1) {
      console.error('  WorkIQ MCP response generation is serialized; use --judge-provider github-copilot or azure-openai for concurrent scoring.');
    }

    const evaluatedRows = await evaluatePrompts(rows, responseClient, {
      systemPrompt,
      connectorId: options.connectorId,
      connectorPromptHint: options.connectorPromptHint ?? false,
      tenantId: options.tenantId,
      agentId: options.m365AgentId,
      concurrency: responseConcurrency,
      delayMs,
      onProgress: (completed, total, currentPrompt) => {
        const preview = currentPrompt.length > 50 ? currentPrompt.slice(0, 50) + '...' : currentPrompt;
        console.error(`  [${completed}/${total}] ${preview}`);
      },
      onRowComplete: checkpoint,
    });

    // Score answers
    console.error('\nScoring answers...');
    // WorkIQ judging can only parallelize when it runs over A2A (judgeAgentId
    // supplied). Otherwise it goes through the single MCP stdio child and
    // must stay serialized.
    const scoringConcurrency = judgeProvider === 'workiq' && !useA2AJudge ? 1 : concurrency;
    const scoredRows = await scoreAnswers(evaluatedRows, scoringClient, {
      tenantId: options.tenantId,
      judgeProvider,
      fallbackJudgeProvider,
      evaluators,
      judgeAgentId: useA2AJudge ? options.judgeAgentId : undefined,
      concurrency: scoringConcurrency,
      delayMs,
      threshold: options.threshold,
      onProgress: (completed, total) => {
        console.error(`  [${completed}/${total}] Scoring...`);
      },
      onRowComplete: checkpoint,
    });

    // Evaluate assertions (if loaded via --evalset or --sidecar)
    const hasAssertions = scoredRows.some(r => r.assertions && r.assertions.length > 0);
    if (hasAssertions) {
      console.error('\nEvaluating assertions...');
      evaluateAllAssertions(scoredRows);
      const totalAssertions = scoredRows.reduce((s, r) => s + (r.assertionResults?.length ?? 0), 0);
      const passedAssertions = scoredRows.reduce((s, r) => s + (r.assertionResults?.filter(a => a.passed).length ?? 0), 0);
      console.error(`  ${passedAssertions}/${totalAssertions} assertions passed`);
    }

    // Calculate scoring result
    const scoringResult = calculateScoringResult(scoredRows, options.threshold);

    // Build EvalResult
    const evalResult: EvalResult = {
      rows: scoredRows,
      inputFile: inputPath,
      inputFormat: format,
      timestamp: new Date().toISOString(),
      systemPrompt,
      target: buildTarget(options),
      judgeProvider,
      evaluators,
      metadata: extractDocumentMetadata(scoredRows),
      defaultEvaluators: scoredRows[0]?.documentDefaultEvaluators,
    };

    // Generate and write report
    console.error('\nGenerating report...');
    const report = generateReport(evalResult, scoringResult);
    const reportPath = await writeReport(report, path.resolve(options.outputDir), inputPath);
    const htmlReport = generateHtmlReport(evalResult, scoringResult);
    const htmlReportPath = await writeHtmlReport(htmlReport, path.resolve(options.outputDir), inputPath);

    // Write completed evaluation file
    const evalOutputPath = await writeEvalFile(
      scoredRows,
      inputPath,
      path.resolve(options.outputDir),
      format,
      {
        metadata: evalResult.metadata,
        defaultEvaluators: evalResult.defaultEvaluators,
        target: evalResult.target,
        judgeProvider: evalResult.judgeProvider,
        evaluators: evalResult.evaluators,
        threshold: options.threshold,
      },
    );

    // Print summary to stdout
    const passRate = scoringResult.totalQuestions > 0
      ? ((scoringResult.passCount / scoringResult.totalQuestions) * 100).toFixed(1)
      : '0.0';

    console.log('\n=== Evaluation Complete ===');
    console.log(`  Report:          ${reportPath}`);
    console.log(`  HTML report:     ${htmlReportPath}`);
    console.log(`  Evaluation file: ${evalOutputPath}`);
    console.log(`  Average score:   ${scoringResult.averageScore.toFixed(1)}%`);
    console.log(`  Pass rate:       ${passRate}% (${scoringResult.passCount}/${scoringResult.totalQuestions})`);
    console.log(`  Threshold:       ${scoringResult.passThreshold}%`);

    // Assertion summary
    if (scoringResult.totalAssertions > 0) {
      const assertRate = ((scoringResult.assertionsPassed / scoringResult.totalAssertions) * 100).toFixed(1);
      console.log(`  Assertions:      ${assertRate}% (${scoringResult.assertionsPassed}/${scoringResult.totalAssertions})`);
    }

    // Exit with code 0 if all pass, 1 if any fail
    if (scoringResult.failCount > 0) {
      console.log(`\n  ✗ ${scoringResult.failCount} question(s) below threshold`);
      process.exit(1);
    } else {
      console.log('\n  ✓ All questions passed');
      process.exit(0);
    }
  } finally {
    responseClient.stop?.();
    if (scoringClient !== responseClient) {
      scoringClient.stop?.();
    }
  }
}

function validateJudgeProvider(value: string): JudgeProvider {
  if (value === 'workiq' || value === 'github-copilot' || value === 'azure-openai') {
    return value;
  }
  throw new Error(`Unsupported judge provider "${value}". Supported providers: workiq, github-copilot, azure-openai`);
}

function validateFallbackJudgeProvider(value?: string): 'github-copilot' | 'azure-openai' | 'none' | undefined {
  if (!value) return undefined;
  if (value === 'none' || value === 'github-copilot' || value === 'azure-openai') {
    return value;
  }
  throw new Error(`Unsupported fallback judge provider "${value}". Supported fallback providers: github-copilot, azure-openai, none`);
}

function parsePositiveInt(value: unknown, defaultValue: number): number {
  const parsed = Number.parseInt(String(value ?? ''), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : defaultValue;
}

function parseNonNegativeInt(value: unknown, defaultValue: number): number {
  const parsed = Number.parseInt(String(value ?? ''), 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : defaultValue;
}

function buildTarget(options: CliOptions): EvalResult['target'] {
  if (options.m365AgentId) {
    return { type: 'm365-agent', agentId: options.m365AgentId };
  }
  if (options.connectorId) {
    return { type: 'connector', connectorId: options.connectorId };
  }
  return { type: 'workiq' };
}

async function writeCheckpoint(
  checkpointFile: string,
  rows: import('./types').EvalRow[],
  metadata: Pick<EvalResult, 'inputFile' | 'target' | 'judgeProvider' | 'evaluators'>,
): Promise<void> {
  await fs.promises.mkdir(path.dirname(checkpointFile), { recursive: true });
  const { rowsToEvalDocument } = await import('./eval-document');
  const payload = rowsToEvalDocument(rows, {
    metadata: { evaluatedAt: new Date().toISOString() },
    defaultEvaluators: rows[0]?.documentDefaultEvaluators,
    inputFile: metadata.inputFile,
    target: metadata.target,
    judgeProvider: metadata.judgeProvider,
    runEvaluators: metadata.evaluators,
  });
  await fs.promises.writeFile(checkpointFile, JSON.stringify(payload, null, 2), 'utf-8');
}

function extractDocumentMetadata(rows: import('./types').EvalRow[]): Record<string, unknown> | undefined {
  const metadata = rows
    .map(row => row.responseMetadata as { documentMetadata?: Record<string, unknown> } | undefined)
    .find(value => value?.documentMetadata)?.documentMetadata;
  return metadata;
}

main().catch((err: Error) => {
  console.error(`\nError: ${err.message}`);
  process.exit(2);
});
