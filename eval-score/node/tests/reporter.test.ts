import { describe, it, expect } from 'vitest';
import { EvalResult, ScoringResult } from '../src/types';
import { generateReport } from '../src/reporter';

function makeSampleEvalResult(overrides: Partial<EvalResult> = {}): EvalResult {
  return {
    rows: [
      {
        prompt: 'What is 2+2?',
        expectedAnswer: '4',
        sourceLocation: 'math.docx',
        actualAnswer: 'Four',
        similarityScore: 85,
      },
      {
        prompt: 'Capital of France?',
        expectedAnswer: 'Paris',
        sourceLocation: 'geo.xlsx',
        actualAnswer: 'London',
        similarityScore: 30,
      },
    ],
    inputFile: 'test-data.csv',
    inputFormat: 'csv',
    timestamp: '2024-01-15T10:00:00Z',
    ...overrides,
  };
}

function makeSampleScoringResult(overrides: Partial<ScoringResult> = {}): ScoringResult {
  return {
    totalQuestions: 2,
    averageScore: 57.5,
    minScore: 30,
    maxScore: 85,
    passCount: 1,
    failCount: 1,
    passThreshold: 70,
    ...overrides,
  };
}

describe('generateReport', () => {
  it('contains all required report sections', () => {
    const report = generateReport(makeSampleEvalResult(), makeSampleScoringResult());

    expect(report).toContain('# Evaluation Report');
    expect(report).toContain('## Summary');
    expect(report).toContain('## Score Distribution');
    expect(report).toContain('## Detailed Results');
  });

  it('includes summary statistics', () => {
    const report = generateReport(makeSampleEvalResult(), makeSampleScoringResult());

    expect(report).toContain('test-data.csv');
    expect(report).toContain('57.5');
    expect(report).toContain('**Min Score:** 30');
    expect(report).toContain('**Max Score:** 85');
    expect(report).toContain('1/2');
  });

  it('shows ✅ for scores above threshold and ❌ for below', () => {
    const report = generateReport(makeSampleEvalResult(), makeSampleScoringResult());

    expect(report).toContain('85/100 ✅');
    expect(report).toContain('30/100 ❌');
  });

  it('truncates system prompt longer than 200 characters', () => {
    const longPrompt = 'A'.repeat(250);
    const evalResult = makeSampleEvalResult({ systemPrompt: longPrompt });
    const report = generateReport(evalResult, makeSampleScoringResult());

    expect(report).toContain('A'.repeat(200) + '...');
    expect(report).not.toContain('A'.repeat(201));
  });

  it('includes the system prompt when provided', () => {
    const evalResult = makeSampleEvalResult({ systemPrompt: 'You are a helpful assistant.' });
    const report = generateReport(evalResult, makeSampleScoringResult());

    expect(report).toContain('You are a helpful assistant.');
  });

  it('omits system prompt line when not provided', () => {
    const evalResult = makeSampleEvalResult({ systemPrompt: undefined });
    const report = generateReport(evalResult, makeSampleScoringResult());

    expect(report).not.toContain('**System Prompt:**');
  });
});
