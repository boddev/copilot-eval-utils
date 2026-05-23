import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';
import { stringify } from 'csv-stringify/sync';
import { GeneratedEvalItem, EvalSet, M365EvalDocument, M365EvalItem } from './types';

/**
 * Write EvalScore-compatible CSV
 * Columns: prompt, expected_answer, source_location, actual_answer
 */
export function writeEvalCsv(
  items: GeneratedEvalItem[],
  outputPath: string,
): string {
  const rows = items.map(item => ({
    prompt: item.prompt,
    expected_answer: item.expected_answer,
    source_location: item.source_location,
    actual_answer: '',
  }));

  const csv = stringify(rows, {
    header: true,
    columns: ['prompt', 'expected_answer', 'source_location', 'actual_answer'],
  });

  const absPath = path.resolve(outputPath);
  fs.mkdirSync(path.dirname(absPath), { recursive: true });
  fs.writeFileSync(absPath, csv, 'utf-8');
  return absPath;
}

/**
 * Write rich sidecar JSON with full metadata including assertions
 */
export function writeSidecarJson(
  items: GeneratedEvalItem[],
  description: string,
  sourceFile: string,
  outputPath: string,
  options?: {
    warnings?: string[];
    model?: string;
    avoidanceEvalsets?: string[];
    avoidanceItemsCompared?: number;
    crossRunDuplicatesRemoved?: number;
    crossRunAssertionOverlaps?: number;
  },
): string {
  const evalSet: EvalSet = {
    version: '1.0',
    generated_at: new Date().toISOString(),
    description,
    source_file: sourceFile,
    item_count: items.length,
    items,
    warnings: options?.warnings,
    metadata: {
      model: options?.model ?? 'unknown',
      evalgen_version: '1.0.0',
      avoidance_evalsets: options?.avoidanceEvalsets,
      avoidance_items_compared: options?.avoidanceItemsCompared,
      cross_run_duplicates_removed: options?.crossRunDuplicatesRemoved,
      cross_run_assertion_overlaps: options?.crossRunAssertionOverlaps,
    },
  };

  const jsonPath = outputPath.replace(/\.(csv|xlsx|json)$/i, '.evalgen.json');
  const absPath = path.resolve(jsonPath);
  fs.mkdirSync(path.dirname(absPath), { recursive: true });
  fs.writeFileSync(absPath, JSON.stringify(evalSet, null, 2), 'utf-8');
  return absPath;
}

/**
 * Write an m365/evalscore-compatible JSON document whose items each contain
 * multiple prompt turns. EvalGen marks these as synthetic threads so EvalScore
 * can keep the prompts grouped without carrying conversation context between
 * otherwise independent generated questions.
 */
export function writeM365MultiPromptJson(
  items: GeneratedEvalItem[],
  description: string,
  sourceFile: string,
  outputPath: string,
  options: {
    promptsPerThread: number;
    warnings?: string[];
    model?: string;
  },
): string {
  const promptsPerThread = Math.max(2, Math.min(20, Math.trunc(options.promptsPerThread)));
  const document: M365EvalDocument = {
    schemaVersion: '1.4.0',
    metadata: {
      source: 'eval-gen',
      description,
      source_file: sourceFile,
      generated_at: new Date().toISOString(),
      evalgen_version: '1.0.0',
      model: options.model ?? 'unknown',
      multi_prompt: true,
      prompts_per_thread: promptsPerThread,
      grouping: 'sequential_chunk',
      warnings: options.warnings,
    },
    items: chunkItems(items, promptsPerThread).map((group, index) =>
      buildMultiPromptItem(group, index, promptsPerThread)
    ),
  };

  const absPath = path.resolve(outputPath);
  fs.mkdirSync(path.dirname(absPath), { recursive: true });
  fs.writeFileSync(absPath, JSON.stringify(document, null, 2), 'utf-8');
  return absPath;
}

function chunkItems(items: GeneratedEvalItem[], promptsPerThread: number): GeneratedEvalItem[][] {
  const chunks: GeneratedEvalItem[][] = [];
  for (let i = 0; i < items.length; i += promptsPerThread) {
    chunks.push(items.slice(i, i + promptsPerThread));
  }
  return chunks;
}

function buildMultiPromptItem(
  group: GeneratedEvalItem[],
  index: number,
  promptsPerThread: number,
): M365EvalItem {
  const id = `evalgen-multi-prompt-${index + 1}-${stableGroupHash(group)}`;
  const categories = Array.from(new Set(group.map(item => item.category))).join(', ');
  return {
    name: `EvalGen multi-prompt evaluator ${index + 1}`,
    description: `Synthetic multi-prompt evaluator group (${categories || 'mixed categories'}). Prompts are evaluated independently by default.`,
    turns: group.map(item => ({
      prompt: item.prompt,
      expected_response: item.expected_answer,
      context: buildTurnContext(item),
      extensions: {
        evalgen: {
          item_id: item.id,
          source_location: item.source_location,
          assertions: item.assertions,
          category: item.category,
          difficulty: item.difficulty,
          supporting_facts: item.supporting_facts,
          grounding_confidence: item.grounding_confidence,
          referenced_rows: item.referenced_rows,
          synthetic_thread: true,
          conversation_chaining: false,
        },
      },
    })),
    extensions: {
      evalgen: {
        synthetic_thread: true,
        conversation_chaining: false,
        grouping: 'sequential_chunk',
        prompts_per_thread: promptsPerThread,
        thread_id: id,
      },
    },
  };
}

function stableGroupHash(group: GeneratedEvalItem[]): string {
  return crypto.createHash('sha256')
    .update(group.map(item => item.id || `${item.prompt}|${item.source_location}`).join('|'))
    .digest('hex')
    .slice(0, 12);
}

function buildTurnContext(item: GeneratedEvalItem): string | undefined {
  const parts = [
    item.source_location ? `Source: ${item.source_location}` : undefined,
    ...item.supporting_facts,
  ].filter((part): part is string => Boolean(part));
  return parts.length > 0 ? parts.join('\n') : undefined;
}

/**
 * Write the review markdown file
 */
export function writeReviewMarkdown(
  content: string,
  outputPath: string,
): string {
  const mdPath = outputPath.replace(/\.(csv|xlsx|json)$/i, '-review.md');
  const absPath = path.resolve(mdPath);
  fs.mkdirSync(path.dirname(absPath), { recursive: true });
  fs.writeFileSync(absPath, content, 'utf-8');
  return absPath;
}
