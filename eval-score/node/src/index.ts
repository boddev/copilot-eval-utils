#!/usr/bin/env node

import * as fs from 'fs';
import * as path from 'path';
import { Command } from 'commander';
import { EvalResult, CliOptions } from './types';
import { readEvalFile } from './readers';
import { writeEvalFile } from './writers';
import { CliWorkIQClient, resolveSystemPrompt } from './workiq-client';
import { evaluatePrompts } from './evaluator';
import { scoreAnswers, calculateScoringResult } from './scorer';
import { generateReport, writeReport } from './reporter';
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
    outputDir: opts.outputDir as string,
    threshold: Number(opts.threshold),
    tenantId: opts.tenantId as string | undefined,
    sidecar: opts.sidecar as string | undefined,
  };

  const evalsetPath = opts.evalset as string | undefined;

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

  // Create WorkIQ client and start persistent MCP session (auth once)
  console.error('Starting WorkIQ session...');
  const client = new CliWorkIQClient();
  await client.start(options.tenantId);
  console.error('  WorkIQ MCP session started.\n');

  try {
    // Evaluate prompts
    console.error('Evaluating prompts...');
    const evaluatedRows = await evaluatePrompts(rows, client, {
      systemPrompt,
      tenantId: options.tenantId,
      onProgress: (completed, total, currentPrompt) => {
        const preview = currentPrompt.length > 50 ? currentPrompt.slice(0, 50) + '...' : currentPrompt;
        console.error(`  [${completed}/${total}] ${preview}`);
      },
    });

    // Score answers
    console.error('\nScoring answers...');
    const scoredRows = await scoreAnswers(evaluatedRows, client, {
      tenantId: options.tenantId,
      onProgress: (completed, total) => {
        console.error(`  [${completed}/${total}] Scoring...`);
      },
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
    };

    // Generate and write report
    console.error('\nGenerating report...');
    const report = generateReport(evalResult, scoringResult);
    const reportPath = await writeReport(report, path.resolve(options.outputDir), inputPath);

    // Write completed evaluation file
    const evalOutputPath = await writeEvalFile(
      scoredRows,
      inputPath,
      path.resolve(options.outputDir),
      format,
    );

    // Print summary to stdout
    const passRate = scoringResult.totalQuestions > 0
      ? ((scoringResult.passCount / scoringResult.totalQuestions) * 100).toFixed(1)
      : '0.0';

    console.log('\n=== Evaluation Complete ===');
    console.log(`  Report:          ${reportPath}`);
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
    client.stop();
  }
}

main().catch((err: Error) => {
  console.error(`\nError: ${err.message}`);
  process.exit(2);
});
