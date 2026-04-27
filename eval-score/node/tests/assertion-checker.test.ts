import { describe, it, expect } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  evaluateAssertion,
  evaluateRowAssertions,
  evaluateAllAssertions,
  loadAssertionsFromSidecar,
} from '../src/assertion-checker';
import { EvalRow, Assertion } from '../src/types';

describe('evaluateAssertion', () => {
  it('must_contain passes when value is present', () => {
    const assertion: Assertion = { type: 'must_contain', value: 'Jane Smith' };
    const result = evaluateAssertion(assertion, 'The owner is Jane Smith.');
    expect(result.passed).toBe(true);
  });

  it('must_contain fails when value is absent', () => {
    const assertion: Assertion = { type: 'must_contain', value: 'Jane Smith' };
    const result = evaluateAssertion(assertion, 'The owner is John Davis.');
    expect(result.passed).toBe(false);
  });

  it('must_contain is case-insensitive', () => {
    const assertion: Assertion = { type: 'must_contain', value: 'Acme Corp' };
    const result = evaluateAssertion(assertion, 'The supplier is ACME CORP.');
    expect(result.passed).toBe(true);
  });

  it('must_contain_any passes when any value is present', () => {
    const assertion: Assertion = { type: 'must_contain_any', values: ['EMEA', 'Europe', 'UK'] };
    const result = evaluateAssertion(assertion, 'This supplier operates in EMEA.');
    expect(result.passed).toBe(true);
  });

  it('must_contain_any fails when none are present', () => {
    const assertion: Assertion = { type: 'must_contain_any', values: ['EMEA', 'Europe'] };
    const result = evaluateAssertion(assertion, 'This supplier is in North America.');
    expect(result.passed).toBe(false);
  });

  it('must_not_contain passes when value is absent', () => {
    const assertion: Assertion = { type: 'must_not_contain', value: 'unknown' };
    const result = evaluateAssertion(assertion, 'Jane Smith owns this.');
    expect(result.passed).toBe(true);
  });

  it('must_not_contain fails when value is present', () => {
    const assertion: Assertion = { type: 'must_not_contain', value: 'unknown' };
    const result = evaluateAssertion(assertion, 'The status is unknown.');
    expect(result.passed).toBe(false);
  });
});

describe('evaluateRowAssertions', () => {
  it('evaluates all assertions for a row', () => {
    const row: EvalRow = {
      prompt: 'Who owns Acme Corp?',
      expectedAnswer: 'Jane Smith',
      sourceLocation: 'test.csv:row 1',
      actualAnswer: 'Jane Smith owns Acme Corp.',
      assertions: [
        { type: 'must_contain', value: 'Jane Smith' },
        { type: 'must_contain', value: 'Acme Corp' },
        { type: 'must_not_contain', value: 'John Davis' },
      ],
    };

    const results = evaluateRowAssertions(row);
    expect(results.length).toBe(3);
    expect(results.every(r => r.passed)).toBe(true);
  });

  it('returns empty for rows without assertions', () => {
    const row: EvalRow = {
      prompt: 'Test?',
      expectedAnswer: 'Answer',
      sourceLocation: 'test.csv:row 1',
      actualAnswer: 'Response',
    };

    const results = evaluateRowAssertions(row);
    expect(results.length).toBe(0);
  });

  it('returns empty for error responses', () => {
    const row: EvalRow = {
      prompt: 'Test?',
      expectedAnswer: 'Answer',
      sourceLocation: 'test.csv:row 1',
      actualAnswer: '[ERROR: timeout]',
      assertions: [{ type: 'must_contain', value: 'Answer' }],
    };

    const results = evaluateRowAssertions(row);
    expect(results.length).toBe(0);
  });
});

describe('evaluateAllAssertions', () => {
  it('attaches results to all rows', () => {
    const rows: EvalRow[] = [
      {
        prompt: 'Q1?',
        expectedAnswer: 'A1',
        sourceLocation: 'test.csv:row 1',
        actualAnswer: 'Jane Smith owns it.',
        assertions: [{ type: 'must_contain', value: 'Jane Smith' }],
      },
      {
        prompt: 'Q2?',
        expectedAnswer: 'A2',
        sourceLocation: 'test.csv:row 2',
        actualAnswer: 'The status is Active.',
        assertions: [{ type: 'must_contain', value: 'Active' }],
      },
    ];

    evaluateAllAssertions(rows);
    expect(rows[0].assertionResults?.length).toBe(1);
    expect(rows[1].assertionResults?.length).toBe(1);
  });
});

describe('loadAssertionsFromSidecar', () => {
  it('loads assertions from sidecar JSON', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'evalcli-test-'));
    const sidecarPath = path.join(tmpDir, 'test.evalgen.json');

    try {
      const sidecar = {
        version: '1.0',
        items: [
          {
            prompt: 'Who owns Acme Corp?',
            assertions: [
              { type: 'must_contain', value: 'Jane Smith' },
            ],
          },
        ],
      };
      fs.writeFileSync(sidecarPath, JSON.stringify(sidecar), 'utf-8');

      const rows: EvalRow[] = [
        {
          prompt: 'Who owns Acme Corp?',
          expectedAnswer: 'Jane Smith',
          sourceLocation: 'test.csv:row 1',
          actualAnswer: '',
        },
      ];

      loadAssertionsFromSidecar(rows, sidecarPath);
      expect(rows[0].assertions?.length).toBe(1);
      expect(rows[0].assertions?.[0].type).toBe('must_contain');
    } finally {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });

  it('throws on missing sidecar file', () => {
    const rows: EvalRow[] = [];
    expect(() => loadAssertionsFromSidecar(rows, 'nonexistent.json')).toThrow('Sidecar file not found');
  });
});
