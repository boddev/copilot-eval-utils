import * as fs from 'fs';
import * as path from 'path';
import { stringify } from 'csv-stringify/sync';
import { GeneratedEvalItem, EvalSet, Assertion } from './types';

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
  options?: { warnings?: string[]; model?: string },
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
    },
  };

  const jsonPath = outputPath.replace(/\.(csv|xlsx|json)$/i, '.evalgen.json');
  const absPath = path.resolve(jsonPath);
  fs.mkdirSync(path.dirname(absPath), { recursive: true });
  fs.writeFileSync(absPath, JSON.stringify(evalSet, null, 2), 'utf-8');
  return absPath;
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
