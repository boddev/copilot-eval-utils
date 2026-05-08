import { describe, it, expect } from 'vitest';
import { buildEvalItems, validateEvalSet } from '../src/validator';
import { DraftedQuestion, Assertion, GeneratedEvalItem } from '../src/types';

function makeItem(overrides: Partial<GeneratedEvalItem> = {}): GeneratedEvalItem {
  return {
    prompt: 'Who owns Acme Corp?',
    expected_answer: 'Jane Smith owns Acme Corp.',
    source_location: 'test.csv:row 1',
    assertions: [{ type: 'must_contain', value: 'Jane Smith' }],
    category: 'single_record_lookup',
    difficulty: 'easy',
    supporting_facts: ['owner=Jane Smith'],
    grounding_confidence: 'high',
    ...overrides,
  };
}

describe('buildEvalItems', () => {
  it('converts drafted questions + assertions to eval items', () => {
    const questions: DraftedQuestion[] = [
      {
        prompt: 'Who owns Acme Corp?',
        category: 'single_record_lookup',
        difficulty: 'easy',
        referenced_facts: [],
        expected_answer: 'Jane Smith owns Acme Corp.',
        supporting_facts: ['owner=Jane Smith', 'supplier_name=Acme Corp'],
        source_location: 'test.csv:row 1',
      },
    ];

    const assertionMap = new Map<number, Assertion[]>();
    assertionMap.set(0, [{ type: 'must_contain', value: 'Jane Smith' }]);

    const items = buildEvalItems(questions, assertionMap);
    expect(items.length).toBe(1);
    expect(items[0].prompt).toBe('Who owns Acme Corp?');
    expect(items[0].assertions.length).toBe(1);
    expect(items[0].grounding_confidence).toBe('high');
  });
});

describe('validateEvalSet', () => {
  it('passes valid eval set', () => {
    const items = [
      makeItem({ prompt: 'Question 1?', source_location: 'test.csv:row 1' }),
      makeItem({ prompt: 'Question 2?', source_location: 'test.csv:row 2' }),
      makeItem({ prompt: 'Question 3?', source_location: 'test.csv:row 3' }),
    ];

    const { validated, result } = validateEvalSet(items, 10);
    expect(validated.length).toBe(3);
    expect(result.duplicatesRemoved).toBe(0);
  });

  it('removes duplicate questions', () => {
    const items = [
      makeItem({ prompt: 'Who owns Acme Corp?' }),
      makeItem({ prompt: 'Who owns Acme Corp?' }),
      makeItem({ prompt: 'What is the risk rating?' }),
    ];

    const { validated, result } = validateEvalSet(items, 10);
    expect(validated.length).toBe(2);
    expect(result.duplicatesRemoved).toBe(1);
  });

  it('removes near-duplicate questions', () => {
    const items = [
      makeItem({ prompt: 'Who owns the Acme Corp supplier relationship?' }),
      makeItem({ prompt: 'Who owns the Acme Corp supplier relationship' }), // without ?
      makeItem({ prompt: 'What is the status?' }),
    ];

    const { validated, result } = validateEvalSet(items, 10);
    expect(result.duplicatesRemoved).toBeGreaterThan(0);
  });

  it('reports category balance', () => {
    const items = [
      makeItem({ category: 'single_record_lookup' }),
      makeItem({ prompt: 'Q2?', category: 'single_record_lookup' }),
      makeItem({ prompt: 'Q3?', category: 'single_record_lookup' }),
    ];

    const { result } = validateEvalSet(items, 10);
    expect(result.categoryBalance.single_record_lookup).toBe(3);
    expect(result.categoryBalance.filtered_find).toBe(0);
  });

  it('computes coverage score', () => {
    const items = [
      makeItem({ source_location: 'test.csv:row 1' }),
      makeItem({ prompt: 'Q2?', source_location: 'test.csv:row 2' }),
      makeItem({ prompt: 'Q3?', source_location: 'test.csv:row 3' }),
    ];

    const { result } = validateEvalSet(items, 10);
    expect(result.coverageScore).toBeGreaterThan(0);
  });

  it('counts referenced_rows in coverage (evidence rows beyond source_location)', () => {
    // 3 questions, each with one primary row + 2 supporting evidence rows = 9 unique rows
    const items = [
      makeItem({
        prompt: 'Q1?',
        source_location: 'test.csv:row 1',
        referenced_rows: ['test.csv:row 1', 'test.csv:row 4', 'test.csv:row 5'],
      }),
      makeItem({
        prompt: 'Q2?',
        source_location: 'test.csv:row 2',
        referenced_rows: ['test.csv:row 2', 'test.csv:row 6', 'test.csv:row 7'],
      }),
      makeItem({
        prompt: 'Q3?',
        source_location: 'test.csv:row 3',
        referenced_rows: ['test.csv:row 3', 'test.csv:row 8', 'test.csv:row 9'],
      }),
    ];

    const { result } = validateEvalSet(items, 12);
    // 9 unique rows; denom = min(12, max(3*3, 50)) = min(12, 50) = 12
    // Expected coverage = 9/12 = 75%
    expect(result.coverageScore).toBeCloseTo(9 / 12, 2);
  });

  it('coverage with only source_location stays bounded but increases with referenced_rows', () => {
    const baseItems = Array.from({ length: 5 }, (_, i) =>
      makeItem({ prompt: `Q${i}?`, source_location: `test.csv:row ${i + 1}` })
    );
    const enrichedItems = baseItems.map((it, i) => ({
      ...it,
      referenced_rows: [it.source_location, `test.csv:row ${i + 20}`],
    }));

    const baseResult = validateEvalSet(baseItems, 100).result;
    const enrichedResult = validateEvalSet(enrichedItems, 100).result;
    expect(enrichedResult.coverageScore).toBeGreaterThan(baseResult.coverageScore);
  });

  it('emits "increase --count" guidance when target is achievable on a modest dataset', () => {
    // 1 question on a 100-row dataset → recommended count = ceil(100*0.75/3) = 25, ≤ CLI cap
    const items = [makeItem({ source_location: 'test.csv:row 1' })];
    const { result } = validateEvalSet(items, 100);
    expect(result.issues.some(i => /increase --count/i.test(i))).toBe(true);
    expect(result.recommendedCountForTarget).toBe(25);
    expect(result.datasetSampledNotExhaustive).toBe(false);
    expect(result.uniqueRowsReferenced).toBe(1);
  });

  it('emits "representative sample" guidance for large datasets where exhaustive coverage is impractical', () => {
    // 30 questions on a 5000-row dataset → recommended count > CLI cap (50)
    const items = Array.from({ length: 30 }, (_, i) =>
      makeItem({ prompt: `Q${i}?`, source_location: `test.csv:row ${i + 1}` })
    );
    const { result } = validateEvalSet(items, 5000);
    expect(result.datasetSampledNotExhaustive).toBe(true);
    expect(result.issues.some(i => /representative/i.test(i))).toBe(true);
    expect(result.issues.some(i => /multiple eval sets/i.test(i))).toBe(true);
  });

  it('exposes uniqueRowsReferenced and totalRows for downstream messaging', () => {
    const items = [
      makeItem({
        prompt: 'Q1?',
        source_location: 'test.csv:row 1',
        referenced_rows: ['test.csv:row 1', 'test.csv:row 2'],
      }),
      makeItem({
        prompt: 'Q2?',
        source_location: 'test.csv:row 3',
        referenced_rows: ['test.csv:row 3'],
      }),
    ];
    const { result } = validateEvalSet(items, 50);
    expect(result.uniqueRowsReferenced).toBe(3);
    expect(result.totalRows).toBe(50);
    expect(result.realisticMaxCoverage).toBeCloseTo(0.12, 2);
  });

  it('flags low-confidence items', () => {
    const items = Array.from({ length: 5 }, (_, i) =>
      makeItem({
        prompt: `Question ${i}?`,
        grounding_confidence: 'low',
        source_location: `test.csv:row ${i + 1}`,
      })
    );

    const { result } = validateEvalSet(items, 10);
    expect(result.issues.some(i => i.includes('low grounding confidence'))).toBe(true);
  });
});
