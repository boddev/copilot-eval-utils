import { EvalRow } from '../types';

/**
 * Maps common header variations to canonical EvalRow field names.
 */
const HEADER_ALIASES: Record<string, keyof EvalRow> = {
  // prompt
  'prompt': 'prompt',
  'question': 'prompt',
  // expectedAnswer
  'expectedanswer': 'expectedAnswer',
  'expected_answer': 'expectedAnswer',
  'expected answer': 'expectedAnswer',
  // sourceLocation
  'sourcelocation': 'sourceLocation',
  'source_location': 'sourceLocation',
  'source location': 'sourceLocation',
  // actualAnswer
  'actualanswer': 'actualAnswer',
  'actual_answer': 'actualAnswer',
  'actual answer': 'actualAnswer',
};

/**
 * Build a mapping from raw column headers to canonical EvalRow field names.
 * Throws if any required column (prompt, expectedAnswer, sourceLocation) is missing.
 */
export function normalizeHeaders(rawHeaders: string[]): Map<string, keyof EvalRow> {
  const mapping = new Map<string, keyof EvalRow>();

  for (const raw of rawHeaders) {
    const key = raw.trim().toLowerCase();
    const canonical = HEADER_ALIASES[key];
    if (canonical) {
      mapping.set(raw, canonical);
    }
  }

  const resolved = new Set(mapping.values());
  const required: (keyof EvalRow)[] = ['prompt', 'expectedAnswer', 'sourceLocation'];
  const missing = required.filter((f) => !resolved.has(f));

  if (missing.length > 0) {
    throw new Error(
      `Missing required column(s): ${missing.join(', ')}. ` +
        `Found columns: [${rawHeaders.join(', ')}]`
    );
  }

  return mapping;
}

/** The EvalRow string fields that readers populate from input data. */
type StringField = 'prompt' | 'expectedAnswer' | 'sourceLocation' | 'actualAnswer';

/**
 * Convert a raw record (keyed by original headers) into an EvalRow
 * using the mapping produced by normalizeHeaders.
 */
export function mapRow(
  record: Record<string, string>,
  headerMap: Map<string, keyof EvalRow>
): EvalRow {
  const values: Record<StringField, string> = {
    prompt: '',
    expectedAnswer: '',
    sourceLocation: '',
    actualAnswer: '',
  };

  for (const [rawHeader, canonical] of headerMap) {
    if (canonical in values) {
      const value = record[rawHeader];
      if (value !== undefined && value !== null) {
        values[canonical as StringField] = String(value);
      }
    }
  }

  return { ...values };
}
