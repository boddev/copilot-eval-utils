import { describe, it, expect } from 'vitest';
import { groundAnswer, groundAllAnswers, computeGroundingConfidence } from '../src/answer-grounder';
import { DraftedQuestion } from '../src/types';

const mockRecords: Record<string, unknown>[] = [
  { supplier_name: 'Acme Corp', owner: 'Jane Smith', status: 'Active', risk_rating: 'High' },
  { supplier_name: 'GlobalTech', owner: 'John Davis', status: 'Active', risk_rating: 'Medium' },
  { supplier_name: 'Northern Services', owner: 'Sarah Chen', status: 'Active', risk_rating: 'Low' },
];

describe('groundAnswer', () => {
  it('verifies supporting facts against actual records', () => {
    const question: DraftedQuestion = {
      prompt: 'Who owns Acme Corp?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Jane Smith owns Acme Corp.',
      supporting_facts: ['owner=Jane Smith', 'supplier_name=Acme Corp'],
      source_location: 'test.csv:row 1',
    };

    const grounded = groundAnswer(question, mockRecords, 'test.csv');
    expect(grounded.supporting_facts).toContain('owner=Jane Smith');
    expect(grounded.supporting_facts).toContain('supplier_name=Acme Corp');
  });

  it('returns original question if row reference is invalid', () => {
    const question: DraftedQuestion = {
      prompt: 'Test?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Test answer',
      supporting_facts: ['field=value'],
      source_location: 'test.csv:row 999',
    };

    const grounded = groundAnswer(question, mockRecords, 'test.csv');
    expect(grounded.prompt).toBe('Test?');
  });

  it('handles missing source_location gracefully', () => {
    const question: DraftedQuestion = {
      prompt: 'Test?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Answer',
      supporting_facts: [],
      source_location: '',
    };

    const grounded = groundAnswer(question, mockRecords, 'test.csv');
    expect(grounded).toBeDefined();
  });
});

describe('groundAllAnswers', () => {
  it('grounds all questions', () => {
    const questions: DraftedQuestion[] = [
      {
        prompt: 'Who owns Acme Corp?',
        category: 'single_record_lookup',
        difficulty: 'easy',
        referenced_facts: [],
        expected_answer: 'Jane Smith',
        supporting_facts: ['owner=Jane Smith'],
        source_location: 'test.csv:row 1',
      },
      {
        prompt: 'What is GlobalTech risk rating?',
        category: 'attribute_retrieval',
        difficulty: 'easy',
        referenced_facts: [],
        expected_answer: 'Medium',
        supporting_facts: ['risk_rating=Medium'],
        source_location: 'test.csv:row 2',
      },
    ];

    const grounded = groundAllAnswers(questions, mockRecords, 'test.csv');
    expect(grounded.length).toBe(2);
  });
});

describe('computeGroundingConfidence', () => {
  it('returns high when facts match answer', () => {
    const q: DraftedQuestion = {
      prompt: 'Who owns Acme Corp?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Jane Smith owns Acme Corp.',
      supporting_facts: ['owner=Jane Smith', 'supplier_name=Acme Corp'],
      source_location: 'test.csv:row 1',
    };

    expect(computeGroundingConfidence(q)).toBe('high');
  });

  it('returns low when no facts match', () => {
    const q: DraftedQuestion = {
      prompt: 'Random question?',
      category: 'edge_case',
      difficulty: 'hard',
      referenced_facts: [],
      expected_answer: 'Some unrelated answer',
      supporting_facts: ['field=xyz123'],
      source_location: 'test.csv:row 1',
    };

    expect(computeGroundingConfidence(q)).toBe('low');
  });

  it('returns low when no supporting facts', () => {
    const q: DraftedQuestion = {
      prompt: 'Test?',
      category: 'single_record_lookup',
      difficulty: 'easy',
      referenced_facts: [],
      expected_answer: 'Answer',
      supporting_facts: [],
      source_location: 'test.csv:row 1',
    };

    expect(computeGroundingConfidence(q)).toBe('low');
  });
});
