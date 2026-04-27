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
