#!/usr/bin/env node

import * as fs from 'fs';
import * as path from 'path';
import { Command } from 'commander';
import { CliOptions, LLMClient, LLMProvider } from './types';
import { readDatasetFile } from './readers';
import { profileDataset } from './profiler';
import { extractFacts } from './fact-extractor';
import { generateIntents, draftQuestions } from './question-generator';
import { groundAllAnswers } from './answer-grounder';
import { generateAllAssertions } from './assertion-generator';
import { buildEvalItems, validateEvalSet } from './validator';
import { formatReview } from './reviewer';
import { writeEvalCsv, writeSidecarJson, writeReviewMarkdown } from './writers';
import { createLLMClient } from './llm-client';
import { loadConnectorSchema, runDiagnostics, formatDiagnosticReport } from './connector-diagnostics';
import { ApiSource, DatabaseSource, WebSource } from './sources';

/** Extended CLI options with source-type support */
interface ExtendedOptions extends CliOptions {
  sourceType?: 'api' | 'database' | 'web';
  sourceUrl?: string;
  openapiSpec?: string;
  connectionString?: string;
  endpoints?: string[];
  authHeader?: string;
  dryRun?: boolean;
}

function splitCsvOption(value: string | undefined): string[] | undefined {
  return value ? value.split(',').map((s: string) => s.trim()).filter(Boolean) : undefined;
}

async function main(): Promise<void> {
  const program = new Command();

  program
    .name('eval-gen')
    .description('Generate evaluation sets for Microsoft 365 Copilot connector testing')
    .command('generate')
    .option('--file <path>', 'File, directory, or comma-separated files (CSV, JSON, XLSX)')
    .option('--source-type <type>', 'Data source type: api, database, or web')
    .option('--source-url <url>', 'URL for API or web source')
    .option('--openapi-spec <url>', 'OpenAPI/Swagger spec URL (for API source)')
    .option('--connection-string <str>', 'Database connection string')
    .option('--endpoints <list>', 'Comma-separated API endpoints to sample')
    .option('--auth-header <header>', 'Authorization header (e.g., "Bearer token")')
    .requiredOption('--description <text>', 'Plain-text description of what this data is')
    .option('--count <number>', 'Number of questions to generate (25-50)', '30')
    .option('--output <path>', 'Output file path', './output/eval-set.csv')
    .option('--connector-schema <path>', 'Optional connector schema JSON for field awareness')
    .option('--no-review', 'Skip review output generation')
    .option('--provider <name>', 'LLM provider: m365-copilot, m365-copilot-api, azure-openai, github-copilot, or command', 'm365-copilot')
    .option('--model <name>', 'Azure OpenAI model deployment name', 'gpt-4o')
    .option('--llm-command <command>', 'Command to run when --provider command is selected')
    .option('--m365-time-zone <zone>', 'Time zone for Microsoft 365 Copilot Chat API locationHint')
    .option('--m365-tenant <tenantId>', 'Microsoft Entra tenant ID for Microsoft 365 Copilot authentication')
    .option('--extensions <list>', 'Comma-separated file extensions to include when --file is a directory')
    .option('--dry-run', 'Profile and diagnose only, no LLM calls')
    .action(async (opts) => {
      const options: ExtendedOptions = {
        file: opts.file as string ?? '',
        description: opts.description as string,
        count: Math.min(50, Math.max(10, Number(opts.count))),
        output: opts.output as string,
        connectorSchema: opts.connectorSchema as string | undefined,
        noReview: opts.noReview === true,
        model: opts.model as string,
        provider: opts.provider as LLMProvider,
        llmCommand: opts.llmCommand as string | undefined,
        m365TimeZone: opts.m365TimeZone as string | undefined,
        m365TenantId: opts.m365Tenant as string | undefined,
        extensions: splitCsvOption(opts.extensions as string | undefined),
        sourceType: opts.sourceType as ExtendedOptions['sourceType'],
        sourceUrl: opts.sourceUrl as string | undefined,
        openapiSpec: opts.openapiSpec as string | undefined,
        connectionString: opts.connectionString as string | undefined,
        endpoints: splitCsvOption(opts.endpoints as string | undefined),
        authHeader: opts.authHeader as string | undefined,
        dryRun: opts.dryRun === true,
      };

      await runGenerate(options);
    });

  // Also support running without subcommand for convenience
  program
    .option('--file <path>', 'File, directory, or comma-separated files (CSV, JSON, XLSX)')
    .option('--source-type <type>', 'Data source type: api, database, or web')
    .option('--source-url <url>', 'URL for API or web source')
    .option('--openapi-spec <url>', 'OpenAPI/Swagger spec URL')
    .option('--connection-string <str>', 'Database connection string')
    .option('--endpoints <list>', 'Comma-separated API endpoints')
    .option('--auth-header <header>', 'Authorization header')
    .option('--description <text>', 'Plain-text description of what this data is')
    .option('--count <number>', 'Number of questions to generate (25-50)', '30')
    .option('--output <path>', 'Output file path', './output/eval-set.csv')
    .option('--connector-schema <path>', 'Optional connector schema JSON')
    .option('--no-review', 'Skip review output generation')
    .option('--provider <name>', 'LLM provider: m365-copilot, m365-copilot-api, azure-openai, github-copilot, or command', 'm365-copilot')
    .option('--model <name>', 'Azure OpenAI model deployment name', 'gpt-4o')
    .option('--llm-command <command>', 'Command to run when --provider command is selected')
    .option('--m365-time-zone <zone>', 'Time zone for Microsoft 365 Copilot Chat API locationHint')
    .option('--m365-tenant <tenantId>', 'Microsoft Entra tenant ID for Microsoft 365 Copilot authentication')
    .option('--extensions <list>', 'Comma-separated file extensions to include when --file is a directory')
    .option('--dry-run', 'Profile and diagnose only, no LLM calls')
    .action(async (opts) => {
      if ((opts.file || opts.sourceType) && opts.description) {
        const options: ExtendedOptions = {
          file: opts.file as string ?? '',
          description: opts.description as string,
          count: Math.min(50, Math.max(10, Number(opts.count || '30'))),
          output: opts.output as string || './output/eval-set.csv',
          connectorSchema: opts.connectorSchema as string | undefined,
          noReview: opts.noReview === true,
          model: opts.model as string || 'gpt-4o',
          provider: opts.provider as LLMProvider,
          llmCommand: opts.llmCommand as string | undefined,
          m365TimeZone: opts.m365TimeZone as string | undefined,
          m365TenantId: opts.m365Tenant as string | undefined,
          extensions: splitCsvOption(opts.extensions as string | undefined),
          sourceType: opts.sourceType as ExtendedOptions['sourceType'],
          sourceUrl: opts.sourceUrl as string | undefined,
          openapiSpec: opts.openapiSpec as string | undefined,
          connectionString: opts.connectionString as string | undefined,
          endpoints: splitCsvOption(opts.endpoints as string | undefined),
          authHeader: opts.authHeader as string | undefined,
          dryRun: opts.dryRun === true,
        };
        await runGenerate(options);
      }
    });

  await program.parseAsync(process.argv);
}

async function runGenerate(options: ExtendedOptions): Promise<void> {
  console.error('╔══════════════════════════════════════════════╗');
  console.error('║          EvalGen - Generating Eval Set        ║');
  console.error('╚══════════════════════════════════════════════╝');
  if (options.sourceType) {
    console.error(`  Source:      ${options.sourceType} (${options.sourceUrl || options.connectionString || ''})`);
  } else {
    console.error(`  File:        ${path.resolve(options.file)}`);
  }
  console.error(`  Description: ${options.description.slice(0, 60)}${options.description.length > 60 ? '...' : ''}`);
  console.error(`  Count:       ${options.count}`);
  console.error(`  Output:      ${path.resolve(options.output)}`);
  if (!options.dryRun) {
    console.error(`  Provider:    ${options.provider ?? 'm365-copilot'}`);
  }
  console.error('');

  let client: LLMClient | undefined;
  const totalSteps = options.dryRun ? 3 : 7;
  let step = 1;

  if (!options.dryRun) {
    console.error(`Step ${step++}/${totalSteps}: Authenticating provider...`);
    client = createLLMClient({
      provider: options.provider,
      model: options.model,
      command: options.llmCommand,
      m365TimeZone: options.m365TimeZone,
      m365TenantId: options.m365TenantId,
    });
    if (client.authenticate) {
      await client.authenticate();
      console.error('  Provider authentication verified');
    } else {
      console.error('  Provider has no authentication preflight; continuing');
    }
  }

  // 1. Read dataset (from file or source adapter)
  console.error(`Step ${step++}/${totalSteps}: Reading dataset...`);
  let records: Record<string, unknown>[];
  let format: string;
  let sourceName: string;

  if (options.sourceType) {
    // Use a source adapter
    let adapter;
    const headers: Record<string, string> = {};
    if (options.authHeader) {
      headers['Authorization'] = options.authHeader;
    }

    switch (options.sourceType) {
      case 'api':
        if (!options.sourceUrl) throw new Error('--source-url is required for API source');
        adapter = new ApiSource({
          baseUrl: options.sourceUrl,
          specUrl: options.openapiSpec,
          headers,
          endpoints: options.endpoints,
        });
        break;
      case 'database':
        if (!options.connectionString) throw new Error('--connection-string is required for database source');
        adapter = new DatabaseSource({
          type: 'sqlite', // Default; could parse from connection string
          connectionString: options.connectionString,
        });
        break;
      case 'web':
        if (!options.sourceUrl) throw new Error('--source-url is required for web source');
        adapter = new WebSource({
          url: options.sourceUrl,
          headers,
        });
        break;
      default:
        throw new Error(`Unknown source type: ${options.sourceType}`);
    }

    const result = await adapter.fetch();
    records = result.records;
    format = result.format;
    sourceName = result.sourceName;
  } else if (options.file) {
    const fileResult = readDatasetFile(options.file, { extensions: options.extensions });
    records = fileResult.records;
    format = fileResult.format;
    sourceName = fileResult.sourceFiles.length > 1
      ? fileResult.sourceFiles.join(', ')
      : fileResult.sourceFiles[0] ?? path.basename(options.file);
  } else {
    throw new Error('Either --file or --source-type is required');
  }

  console.error(`  Loaded ${records.length} records (${format} format)`);

  // 2. Profile dataset
  console.error(`Step ${step++}/${totalSteps}: Profiling dataset...`);
  const profile = profileDataset(records, sourceName, format as any);
  console.error(`  Found ${profile.columns.length} columns`);
  console.error(`  Candidate keys: ${profile.candidateKeyColumns.join(', ') || '(none detected)'}`);
  console.error(`  Candidate titles: ${profile.candidateTitleColumns.join(', ') || '(none detected)'}`);

  // 3. Extract facts
  console.error(`Step ${step++}/${totalSteps}: Extracting facts...`);
  const facts = extractFacts(records, profile, 200);
  console.error(`  Extracted ${facts.length} facts from stratified sample`);

  // Dry-run: profile + diagnostics only, no LLM calls
  if (options.dryRun) {
    console.log('\n=== Dry Run Complete ===');
    console.log(`  Records:     ${records.length}`);
    console.log(`  Columns:     ${profile.columns.length}`);
    console.log(`  Facts:       ${facts.length}`);
    console.log(`  Key columns: ${profile.candidateKeyColumns.join(', ') || '(none)'}`);

    if (options.connectorSchema) {
      const schema = loadConnectorSchema(options.connectorSchema);
      const fieldCov = require('./connector-diagnostics').analyzeFieldCoverage(profile, schema);
      console.log(`  Indexed:     ${fieldCov.indexedFields.length}/${fieldCov.indexedFields.length + fieldCov.unindexedFields.length} fields`);
      if (fieldCov.unindexedFields.length > 0) {
        console.log(`  Unindexed:   ${fieldCov.unindexedFields.join(', ')}`);
      }
    }
    console.log('\n  No LLM calls were made. Remove --dry-run to generate questions.');
    return;
  }

  // 4. Generate questions (LLM)
  console.error(`Step ${step++}/${totalSteps}: Generating questions via LLM...`);
  if (!client) {
    throw new Error('LLM client was not initialized');
  }

  const intents = await generateIntents(profile, facts, options.description, options.count, client);
  console.error(`  Generated ${intents.length} question intents`);

  const drafted = await draftQuestions(intents, facts, records, profile, options.description, client);
  console.error(`  Drafted ${drafted.length} questions with answers`);

  // 5. Ground answers + generate assertions
  console.error(`Step ${step++}/${totalSteps}: Grounding answers and generating assertions...`);
  const grounded = groundAllAnswers(drafted, records, sourceName);
  const assertionMap = generateAllAssertions(grounded);

  const totalAssertions = Array.from(assertionMap.values()).reduce((sum, a) => sum + a.length, 0);
  console.error(`  Grounded ${grounded.length} answers, generated ${totalAssertions} assertions`);

  // 6. Validate + export
  console.error(`Step ${step++}/${totalSteps}: Validating and exporting...`);
  const evalItems = buildEvalItems(grounded, assertionMap);
  const { validated, result } = validateEvalSet(evalItems, records.length);

  // Print validation summary
  if (result.issues.length > 0) {
    console.error('  Validation notes:');
    for (const issue of result.issues) {
      console.error(`    ⚠️  ${issue}`);
    }
  }

  // Write outputs
  const csvPath = writeEvalCsv(validated, options.output);

  // Run connector diagnostics if schema provided (collect warnings for sidecar)
  let diagnosticsPath: string | undefined;
  let diagnosticWarnings: string[] | undefined;
  if (options.connectorSchema) {
    console.error('\nRunning connector diagnostics...');
    const schema = loadConnectorSchema(options.connectorSchema);
    const diagnostics = runDiagnostics(validated, profile, schema);
    const diagContent = formatDiagnosticReport(diagnostics);
    diagnosticsPath = options.output.replace(/\.(csv|xlsx|json)$/i, '-diagnostics.md');
    const absDiagPath = path.resolve(diagnosticsPath);
    fs.mkdirSync(path.dirname(absDiagPath), { recursive: true });
    fs.writeFileSync(absDiagPath, diagContent, 'utf-8');
    diagnosticsPath = absDiagPath;

    // Collect warnings for EvalSet
    diagnosticWarnings = diagnostics.itemDiagnostics
      .filter(d => d.severity !== 'ok')
      .flatMap(d => d.issues.map(i => `${d.prompt}: ${i}`));

    const errors = diagnostics.itemDiagnostics.filter(d => d.severity === 'error').length;
    const warnings = diagnostics.itemDiagnostics.filter(d => d.severity === 'warning').length;
    console.error(`  Field coverage: ${diagnostics.fieldCoverage.coveragePercentage}%`);
    console.error(`  Issues: ${errors} errors, ${warnings} warnings`);
  }

  // Write sidecar JSON (with diagnostics warnings if any)
  const jsonPath = writeSidecarJson(validated, options.description, sourceName, options.output, {
    warnings: diagnosticWarnings,
    model: options.model,
  });

  let reviewPath: string | undefined;
  if (!options.noReview) {
    const reviewContent = formatReview(validated, result, options.description, sourceName);
    reviewPath = writeReviewMarkdown(reviewContent, options.output);
  }

  // Final summary
  console.log('');
  console.log('=== EvalGen Complete ===');
  console.log(`  Eval CSV:      ${csvPath}`);
  console.log(`  Sidecar JSON:  ${jsonPath}`);
  if (reviewPath) {
    console.log(`  Review doc:    ${reviewPath}`);
  }
  if (diagnosticsPath) {
    console.log(`  Diagnostics:   ${diagnosticsPath}`);
  }
  console.log(`  Questions:     ${validated.length}`);
  console.log(`  Assertions:    ${totalAssertions}`);
  console.log(`  Coverage:      ${Math.round(result.coverageScore * 100)}%`);
  console.log(`  Duplicates:    ${result.duplicatesRemoved} removed`);

  const highConf = validated.filter(i => i.grounding_confidence === 'high').length;
  const medConf = validated.filter(i => i.grounding_confidence === 'medium').length;
  const lowConf = validated.filter(i => i.grounding_confidence === 'low').length;
  console.log(`  Confidence:    ${highConf} high, ${medConf} medium, ${lowConf} low`);
  console.log('');
  if (!options.noReview && reviewPath) {
    console.log(`  📋 Review the generated questions in: ${reviewPath}`);
    console.log(`  Edit the CSV directly to adjust questions/answers before running EvalScore.`);
  }
}

main().catch((err: Error) => {
  console.error(`\nError: ${err.message}`);
  process.exit(2);
});
