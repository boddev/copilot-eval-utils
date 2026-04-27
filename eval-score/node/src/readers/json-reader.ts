import * as fs from 'fs';
import { EvalRow } from '../types';
import { normalizeHeaders, mapRow } from './normalize';

/**
 * Read a JSON file and return an array of EvalRow objects.
 * Expects the file to contain a JSON array of objects.
 */
export async function readJson(filePath: string): Promise<EvalRow[]> {
  const content = fs.readFileSync(filePath, 'utf-8');
  let parsed: unknown;

  try {
    parsed = JSON.parse(content);
  } catch (err) {
    throw new Error(
      `Failed to parse JSON from ${filePath}: ${err instanceof Error ? err.message : String(err)}`
    );
  }

  if (!Array.isArray(parsed)) {
    throw new Error(
      `Expected a JSON array in ${filePath}, but got ${typeof parsed}`
    );
  }

  if (parsed.length === 0) {
    return [];
  }

  const records = parsed as Record<string, string>[];
  const rawHeaders = Object.keys(records[0]);
  const headerMap = normalizeHeaders(rawHeaders);

  return records.map((record) => mapRow(record, headerMap));
}
