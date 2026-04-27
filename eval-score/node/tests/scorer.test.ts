import { describe, it, expect } from 'vitest';
import { EvalRow } from '../src/types';
import { scoreAnswers, calculateScoringResult } from '../src/scorer';
import { MockWorkIQClient, WorkIQClient } from '../src/workiq-client';

function makeRow(overrides: Partial<EvalRow> = {}): EvalRow {
  return {
    prompt: 'Test question?',
    expectedAnswer: 'Expected answer',
    sourceLocation: 'test.docx',
    actualAnswer: 'Actual answer',
    ...overrides,
  };
}

describe('scoreAnswers', () => {
  it('parses a numeric score from the client response', async () => {
    const client = new MockWorkIQClient({}, '85');
    const rows = [makeRow()];

    await scoreAnswers(rows, client);

    expect(rows[0].similarityScore).toBe(85);
  });

  it('clamps scores above 100 to 100', async () => {
    const client = new MockWorkIQClient({}, '150');
    const rows = [makeRow()];

    await scoreAnswers(rows, client);

    expect(rows[0].similarityScore).toBe(100);
  });

  it('extracts a number from a verbose response', async () => {
    const client = new MockWorkIQClient({}, 'I think about 75 percent');
    const rows = [makeRow()];

    await scoreAnswers(rows, client);

    expect(rows[0].similarityScore).toBe(75);
  });

  it('sets score to 0 for error answers', async () => {
    const rows = [makeRow({ actualAnswer: '[ERROR: timeout]' })];
    const client = new MockWorkIQClient({}, '90');

    await scoreAnswers(rows, client);

    expect(rows[0].similarityScore).toBe(0);
  });

  it('sets score to 0 for empty actualAnswer', async () => {
    const rows = [makeRow({ actualAnswer: '' })];
    const client = new MockWorkIQClient({}, '90');

    await scoreAnswers(rows, client);

    expect(rows[0].similarityScore).toBe(0);
  });
});

describe('calculateScoringResult', () => {
  it('computes correct statistics from scored rows', () => {
    const rows: EvalRow[] = [
      makeRow({ similarityScore: 90 }),
      makeRow({ similarityScore: 60 }),
      makeRow({ similarityScore: 80 }),
      makeRow({ similarityScore: 40 }),
    ];

    const result = calculateScoringResult(rows, 70);

    expect(result.totalQuestions).toBe(4);
    expect(result.averageScore).toBe(67.5);
    expect(result.minScore).toBe(40);
    expect(result.maxScore).toBe(90);
    expect(result.passCount).toBe(2);
    expect(result.failCount).toBe(2);
    expect(result.passThreshold).toBe(70);
  });

  it('handles rows with undefined similarityScore as 0', () => {
    const rows: EvalRow[] = [
      makeRow({ similarityScore: 100 }),
      makeRow({ similarityScore: undefined }),
    ];

    const result = calculateScoringResult(rows, 70);

    expect(result.totalQuestions).toBe(2);
    expect(result.averageScore).toBe(50);
    expect(result.minScore).toBe(0);
    expect(result.maxScore).toBe(100);
    expect(result.passCount).toBe(1);
    expect(result.failCount).toBe(1);
  });
});
