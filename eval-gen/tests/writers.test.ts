import { describe, it, expect } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { writeEvalCsv, writeSidecarJson, writeM365MultiPromptJson } from '../src/writers';
import { GeneratedEvalItem } from '../src/types';

function makeTestItem(): GeneratedEvalItem {
  return {
    id: 'item-1',
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

function makeSecondTestItem(): GeneratedEvalItem {
  return {
    ...makeTestItem(),
    id: 'item-2',
    prompt: 'What is Acme Corp status?',
    expected_answer: 'Acme Corp is active.',
    source_location: 'suppliers.csv:row 2',
    assertions: [{ type: 'must_contain', value: 'active' }],
    supporting_facts: ['status=active'],
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

describe('writeM365MultiPromptJson', () => {
  it('writes schema-native multi-prompt evaluator JSON with EvalGen extensions', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'evalgen-test-'));
    const outPath = path.join(tmpDir, 'test-eval-multi-prompt.json');

    try {
      const items = [makeTestItem(), makeSecondTestItem()];
      const written = writeM365MultiPromptJson(items, 'Test description', 'suppliers.csv', outPath, {
        promptsPerThread: 2,
        model: 'test-model',
      });

      expect(fs.existsSync(written)).toBe(true);
      const content = JSON.parse(fs.readFileSync(written, 'utf-8'));
      expect(content.schemaVersion).toBe('1.4.0');
      expect(content.metadata.multi_prompt).toBe(true);
      expect(content.items).toHaveLength(1);
      expect(content.items[0].id).toBeUndefined();
      expect(content.items[0].turns).toHaveLength(2);
      expect(content.items[0].extensions.evalgen.synthetic_thread).toBe(true);
      expect(content.items[0].extensions.evalgen.conversation_chaining).toBe(false);
      expect(content.items[0].extensions.evalgen.thread_id).toMatch(/^evalgen-multi-prompt-1-/);
      expect(content.items[0].turns[0].expected_response).toBe('Jane Smith owns Acme Corp.');
      expect(content.items[0].turns[0].extensions.evalgen.assertions).toEqual([
        { type: 'must_contain', value: 'Jane Smith' },
      ]);
      expect(content.items[0].turns[0].extensions.evalgen.source_location).toBe('suppliers.csv:row 1');
    } finally {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });
});
