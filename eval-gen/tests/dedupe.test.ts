import { describe, it, expect } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { filterAgainstAvoidance, loadAvoidanceSet } from '../src/dedupe';
import { EvalSet, GeneratedEvalItem } from '../src/types';

function makeItem(overrides: Partial<GeneratedEvalItem> = {}): GeneratedEvalItem {
  return {
    id: 'item-1',
    prompt: 'Who is the trial sponsor?',
    expected_answer: 'Contoso sponsors the trial.',
    source_location: 'records.jsonl:row 1',
    assertions: [{ type: 'must_contain', value: 'Contoso' }],
    category: 'single_record_lookup',
    difficulty: 'easy',
    supporting_facts: ['sponsor=Contoso'],
    grounding_confidence: 'high',
    referenced_rows: ['records.jsonl:row 1'],
    ...overrides,
  };
}

function writeSidecar(filePath: string, items: GeneratedEvalItem[], sourceFile = 'records.jsonl'): void {
  const evalSet: EvalSet = {
    version: '1.0',
    generated_at: '2026-01-01T00:00:00.000Z',
    description: 'Clinical trials records',
    source_file: sourceFile,
    item_count: items.length,
    items,
  };
  fs.writeFileSync(filePath, JSON.stringify(evalSet, null, 2), 'utf-8');
}

describe('cross-run dedupe', () => {
  it('loads evalgen sidecars from a directory and filters prompt/source duplicates', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'eval-gen-dedupe-'));
    try {
      const sidecarPath = path.join(tempDir, 'prior.evalgen.json');
      writeSidecar(sidecarPath, [
        makeItem({ prompt: 'Who is the trial sponsor?', source_location: 'records.jsonl:row 1' }),
        makeItem({ id: 'item-2', prompt: 'What condition is studied?', source_location: 'records.jsonl:row 2' }),
      ]);

      const avoidance = loadAvoidanceSet([tempDir]);
      const result = filterAgainstAvoidance([
        makeItem({ id: 'new-1', prompt: 'Who is the trial sponsor?', source_location: 'records.jsonl:row 99' }),
        makeItem({ id: 'new-2', prompt: 'Which trial starts at row one?', source_location: 'records.jsonl:row 1' }),
        makeItem({ id: 'new-3', prompt: 'What is the enrollment status?', source_location: 'records.jsonl:row 3' }),
      ], avoidance, 'records.jsonl');

      expect(avoidance.files).toEqual([sidecarPath]);
      expect(avoidance.items).toHaveLength(2);
      expect(result.items.map(item => item.id)).toEqual(['new-3']);
      expect(result.removedCount).toBe(2);
      expect(result.duplicatePromptCount).toBe(1);
      expect(result.duplicateSourceLocationCount).toBe(1);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('warns but does not remove on assertion-only overlap', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'eval-gen-dedupe-'));
    try {
      const sidecarPath = path.join(tempDir, 'prior.evalgen.json');
      writeSidecar(sidecarPath, [
        makeItem({ prompt: 'Who is the trial sponsor?', source_location: 'records.jsonl:row 1' }),
      ]);

      const avoidance = loadAvoidanceSet([sidecarPath]);
      const result = filterAgainstAvoidance([
        makeItem({ id: 'new-1', prompt: 'Which organization funds this study?', source_location: 'records.jsonl:row 2' }),
      ], avoidance, 'records.jsonl');

      expect(result.items).toHaveLength(1);
      expect(result.removedCount).toBe(0);
      expect(result.assertionOverlapCount).toBe(1);
      expect(result.warnings.some(warning => warning.includes('assertion signature'))).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('ignores prior source locations from a different source file', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'eval-gen-dedupe-'));
    try {
      const sidecarPath = path.join(tempDir, 'prior.evalgen.json');
      writeSidecar(sidecarPath, [
        makeItem({ prompt: 'Different prompt?', source_location: 'records.jsonl:row 1' }),
      ], 'other-records.jsonl');

      const avoidance = loadAvoidanceSet([sidecarPath]);
      const result = filterAgainstAvoidance([
        makeItem({ id: 'new-1', prompt: 'Which trial starts at row one?', source_location: 'records.jsonl:row 1' }),
      ], avoidance, 'records.jsonl');

      expect(result.items).toHaveLength(1);
      expect(result.removedCount).toBe(0);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
