import * as fs from 'fs';
import * as path from 'path';
import { EvalRow, Assertion } from './types';

/**
 * EvalGen EvalSet JSON format (mirrors eval-gen/src/types.ts EvalSet)
 */
interface EvalSetJson {
  version: string;
  generated_at: string;
  description: string;
  source_file: string;
  item_count: number;
  items: Array<{
    id?: string;
    prompt: string;
    expected_answer: string;
    source_location: string;
    assertions?: Assertion[];
    category?: string;
    difficulty?: string;
    grounding_confidence?: string;
  }>;
  warnings?: string[];
  metadata?: {
    model?: string;
    evalgen_version?: string;
  };
}

/**
 * Load an EvalGen EvalSet JSON directly as EvalRows with assertions pre-attached.
 * This is the preferred integration path — no need for separate --sidecar.
 */
export function loadEvalSet(evalsetPath: string): {
  rows: EvalRow[];
  warnings: string[];
  metadata: Record<string, string>;
} {
  const absPath = path.resolve(evalsetPath);
  if (!fs.existsSync(absPath)) {
    throw new Error(`EvalSet file not found: ${absPath}`);
  }

  const content = fs.readFileSync(absPath, 'utf-8');
  let evalSet: EvalSetJson;
  try {
    evalSet = JSON.parse(content);
  } catch {
    throw new Error(`Invalid JSON in EvalSet file: ${absPath}`);
  }

  if (!evalSet.items || !Array.isArray(evalSet.items)) {
    throw new Error('Invalid EvalSet format: missing "items" array');
  }

  if (evalSet.version && !evalSet.version.startsWith('1.')) {
    process.stderr.write(`  ⚠️  EvalSet version ${evalSet.version} may not be compatible\n`);
  }

  const rows: EvalRow[] = evalSet.items.map(item => ({
    prompt: item.prompt,
    expectedAnswer: item.expected_answer,
    sourceLocation: item.source_location,
    actualAnswer: '',
    assertions: item.assertions,
  }));

  // Collect metadata for reporting
  const metadata: Record<string, string> = {};
  if (evalSet.description) metadata.description = evalSet.description;
  if (evalSet.source_file) metadata.source_file = evalSet.source_file;
  if (evalSet.generated_at) metadata.generated_at = evalSet.generated_at;
  if (evalSet.metadata?.model) metadata.model = evalSet.metadata.model;
  if (evalSet.metadata?.evalgen_version) metadata.evalgen_version = evalSet.metadata.evalgen_version;

  // Category metadata for per-category reporting
  for (let i = 0; i < rows.length && i < evalSet.items.length; i++) {
    const item = evalSet.items[i];
    // Store category/difficulty as non-enumerable properties for reporting
    (rows[i] as any)._category = item.category;
    (rows[i] as any)._difficulty = item.difficulty;
    (rows[i] as any)._confidence = item.grounding_confidence;
    (rows[i] as any)._id = item.id;
  }

  return {
    rows,
    warnings: evalSet.warnings ?? [],
    metadata,
  };
}
