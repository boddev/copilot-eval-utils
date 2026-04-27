import { describe, it, expect } from 'vitest';
import { generateAssertions, generateAllAssertions } from '../src/assertion-generator';
import { DraftedQuestion } from '../src/types';

describe('generateAssertions', () => {
  it('generates must_contain assertions from supporting facts', () => {
    const question: DraftedQuestion = {
      prompt: 'Who owns Acme Corp?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Jane Smith owns the Acme Corp supplier relationship.',
      supporting_facts: ['owner=Jane Smith', 'supplier_name=Acme Corp'],
      source_location: 'test.csv:row 1',
    };

    const assertions = generateAssertions(question);
    expect(assertions.length).toBeGreaterThan(0);

    const containsJane = assertions.find(
      a => a.type === 'must_contain' && a.value === 'Jane Smith'
    );
    expect(containsJane).toBeDefined();

    const containsAcme = assertions.find(
      a => a.type === 'must_contain' && a.value === 'Acme Corp'
    );
    expect(containsAcme).toBeDefined();
  });

  it('skips very short values', () => {
    const question: DraftedQuestion = {
      prompt: 'What is the ID?',
      category: 'attribute_retrieval',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'The ID is 1.',
      supporting_facts: ['id=1'],
      source_location: 'test.csv:row 1',
    };

    const assertions = generateAssertions(question);
    // "1" is too short (< 2 chars), should be skipped
    const containsOne = assertions.find(
      a => a.type === 'must_contain' && a.value === '1'
    );
    expect(containsOne).toBeUndefined();
  });

  it('deduplicates assertions', () => {
    const question: DraftedQuestion = {
      prompt: 'Who is the owner?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Jane Smith is the owner. Contact Jane Smith for details.',
      supporting_facts: ['owner=Jane Smith', 'contact=Jane Smith'],
      source_location: 'test.csv:row 1',
    };

    const assertions = generateAssertions(question);
    const janeAssertions = assertions.filter(
      a => a.type === 'must_contain' && a.value === 'Jane Smith'
    );
    expect(janeAssertions.length).toBe(1);
  });

  it('limits to 5 assertions per question', () => {
    const question: DraftedQuestion = {
      prompt: 'Tell me everything about this supplier',
      category: 'single_record_lookup',
      difficulty: 'medium',
      referenced_facts: [],
      expected_answer: 'Acme Corp is an Active supplier with High risk, owned by Jane Smith in Canada under WSP CA, providing IT Consulting and Cloud Services with contract value 250000.',
      supporting_facts: [
        'supplier_name=Acme Corp',
        'status=Active',
        'risk_rating=High',
        'owner=Jane Smith',
        'region=Canada',
        'procurement_business_unit=WSP CA',
        'services=IT Consulting, Cloud Services',
        'contract_value=250000',
      ],
      source_location: 'test.csv:row 1',
    };

    const assertions = generateAssertions(question);
    expect(assertions.length).toBeLessThanOrEqual(5);
  });
});

describe('generateAllAssertions', () => {
  it('generates assertions for all questions', () => {
    const questions: DraftedQuestion[] = [
      {
        prompt: 'Q1?',
        category: 'single_record_lookup',
        difficulty: 'easy',
        referenced_facts: [],
        expected_answer: 'Jane Smith',
        supporting_facts: ['owner=Jane Smith'],
        source_location: 'test.csv:row 1',
      },
      {
        prompt: 'Q2?',
        category: 'attribute_retrieval',
        difficulty: 'easy',
        referenced_facts: [],
        expected_answer: 'Acme Corp',
        supporting_facts: ['supplier_name=Acme Corp'],
        source_location: 'test.csv:row 1',
      },
    ];

    const map = generateAllAssertions(questions);
    expect(map.size).toBe(2);
    expect(map.get(0)).toBeDefined();
    expect(map.get(1)).toBeDefined();
  });
});
