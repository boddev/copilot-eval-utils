import * as fs from 'fs';
import * as path from 'path';
import { EvalRow, Assertion, AssertionResult } from './types';

/**
 * EvalGen sidecar JSON format
 */
interface EvalGenSidecar {
  version: string;
  items: Array<{
    prompt: string;
    assertions?: Assertion[];
  }>;
}

/**
 * Load assertions from an EvalGen sidecar JSON file and attach them to eval rows.
 * Matches by prompt text (case-insensitive, trimmed).
 */
export function loadAssertionsFromSidecar(
  rows: EvalRow[],
  sidecarPath: string,
): EvalRow[] {
  const absPath = path.resolve(sidecarPath);
  if (!fs.existsSync(absPath)) {
    throw new Error(`Sidecar file not found: ${absPath}`);
  }

  const content = fs.readFileSync(absPath, 'utf-8');
  const sidecar: EvalGenSidecar = JSON.parse(content);

  if (!sidecar.items || !Array.isArray(sidecar.items)) {
    throw new Error('Invalid sidecar format: missing "items" array');
  }

  // Build a lookup map by normalized prompt
  const assertionMap = new Map<string, Assertion[]>();
  for (const item of sidecar.items) {
    if (item.prompt && item.assertions && item.assertions.length > 0) {
      const key = item.prompt.trim().toLowerCase();
      assertionMap.set(key, item.assertions);
    }
  }

  // Attach assertions to matching rows
  for (const row of rows) {
    const key = row.prompt.trim().toLowerCase();
    const assertions = assertionMap.get(key);
    if (assertions) {
      row.assertions = assertions;
    }
  }

  return rows;
}

/**
 * Evaluate a single assertion against an actual answer.
 */
export function evaluateAssertion(assertion: Assertion, actualAnswer: string): AssertionResult {
  const answer = actualAnswer.toLowerCase();

  switch (assertion.type) {
    case 'must_contain': {
      const target = assertion.value.toLowerCase();
      let passed: boolean;
      if (assertion.wholeWord) {
        // Word-boundary matching to avoid false positives on short values
        const regex = new RegExp(`\\b${target.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i');
        passed = regex.test(actualAnswer);
      } else {
        passed = answer.includes(target);
      }
      return {
        assertion,
        passed,
        detail: passed
          ? `✅ Found "${assertion.value}"`
          : `❌ Missing "${assertion.value}"`,
      };
    }

    case 'must_contain_any': {
      const found = assertion.values.find(v => answer.includes(v.toLowerCase()));
      const passed = !!found;
      return {
        assertion,
        passed,
        detail: passed
          ? `✅ Found "${found}"`
          : `❌ None found: ${assertion.values.map(v => `"${v}"`).join(', ')}`,
      };
    }

    case 'must_not_contain': {
      const target = assertion.value.toLowerCase();
      const passed = !answer.includes(target);
      return {
        assertion,
        passed,
        detail: passed
          ? `✅ Correctly absent: "${assertion.value}"`
          : `❌ Unexpectedly found "${assertion.value}"`,
      };
    }

    default:
      return {
        assertion,
        passed: false,
        detail: `⚠️ Unknown assertion type`,
      };
  }
}

/**
 * Evaluate all assertions for a single eval row.
 */
export function evaluateRowAssertions(row: EvalRow): AssertionResult[] {
  if (!row.assertions || row.assertions.length === 0) return [];
  if (!row.actualAnswer || row.actualAnswer.startsWith('[ERROR:')) return [];

  return row.assertions.map(a => evaluateAssertion(a, row.actualAnswer));
}

/**
 * Evaluate assertions for all rows and attach results.
 */
export function evaluateAllAssertions(rows: EvalRow[]): EvalRow[] {
  for (const row of rows) {
    row.assertionResults = evaluateRowAssertions(row);
  }
  return rows;
}
