import { describe, it, expect } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { writeEvalCsv, writeSidecarJson } from '../src/writers';
import { GeneratedEvalItem } from '../src/types';

function makeTestItem(): GeneratedEvalItem {
  return {
    prompt: 'Who owns Acme Corp?',
    expected_answer: 'Jane Smith owns Acme Corp.',
    source_location: 'suppliers.csv:row 1',
    assertions: [{ type: 'must_contain', value: 'Jane Smith' }],
    category: 'single_record_lookup',
    difficulty: 'easy',
    supporting_facts: ['owner=Jane Smith'],
    grounding_confidence: 'high',
  };
}

describe('writeEvalCsv', () => {
  it('writes EvalScore-compatible CSV', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'evalgen-test-'));
    const outPath = path.join(tmpDir, 'test-eval.csv');

    try {
      const items = [makeTestItem()];
      const written = writeEvalCsv(items, outPath);

      expect(fs.existsSync(written)).toBe(true);
      const content = fs.readFileSync(written, 'utf-8');
      expect(content).toContain('prompt,expected_answer,source_location,actual_answer');
      expect(content).toContain('Who owns Acme Corp?');
      expect(content).toContain('Jane Smith owns Acme Corp.');
    } finally {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });
});

describe('writeSidecarJson', () => {
  it('writes rich JSON with assertions', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'evalgen-test-'));
    const outPath = path.join(tmpDir, 'test-eval.csv');

    try {
      const items = [makeTestItem()];
      const written = writeSidecarJson(items, 'Test description', 'suppliers.csv', outPath);

      expect(fs.existsSync(written)).toBe(true);
      const content = JSON.parse(fs.readFileSync(written, 'utf-8'));
      expect(content.version).toBe('1.0');
      expect(content.items.length).toBe(1);
      expect(content.items[0].assertions.length).toBe(1);
      expect(content.items[0].assertions[0].type).toBe('must_contain');
    } finally {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });
});
