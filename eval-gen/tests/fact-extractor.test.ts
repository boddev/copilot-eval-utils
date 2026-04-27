import { describe, it, expect } from 'vitest';
import * as path from 'path';
import { readDatasetFile } from '../src/readers';
import { profileDataset } from '../src/profiler';
import { extractFacts, groupFactsByRecord, summarizeFacts } from '../src/fact-extractor';

const FIXTURES = path.join(__dirname, 'fixtures');

describe('extractFacts', () => {
  it('extracts facts from CSV dataset', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const facts = extractFacts(records, profile, 100);

    expect(facts.length).toBeGreaterThan(0);
    expect(facts.length).toBeLessThanOrEqual(100);

    // Each fact should have required fields
    for (const fact of facts) {
      expect(fact.id).toBeTruthy();
      expect(fact.field).toBeTruthy();
      expect(fact.rowReference).toMatch(/suppliers\.csv:row \d+/);
      expect(fact.record).toBeDefined();
    }
  });

  it('extracts facts from JSON dataset', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'projects.json'));
    const profile = profileDataset(records, 'projects.json', format);
    const facts = extractFacts(records, profile, 50);

    expect(facts.length).toBeGreaterThan(0);
    const projectFact = facts.find(f => f.field === 'project_name');
    expect(projectFact).toBeDefined();
  });

  it('skips null/empty values', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const facts = extractFacts(records, profile, 200);

    // No fact should have null/undefined/empty value
    for (const fact of facts) {
      expect(fact.value).not.toBeNull();
      expect(fact.value).not.toBeUndefined();
      expect(fact.value).not.toBe('');
    }
  });

  it('respects maxFacts limit', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const facts = extractFacts(records, profile, 10);

    expect(facts.length).toBeLessThanOrEqual(10);
  });
});

describe('groupFactsByRecord', () => {
  it('groups facts by row reference', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const facts = extractFacts(records, profile, 100);

    const grouped = groupFactsByRecord(facts);
    expect(grouped.size).toBeGreaterThan(0);

    // Each group should have facts from the same row
    for (const [rowRef, rowFacts] of grouped) {
      for (const fact of rowFacts) {
        expect(fact.rowReference).toBe(rowRef);
      }
    }
  });
});

describe('summarizeFacts', () => {
  it('produces a readable summary', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const facts = extractFacts(records, profile, 100);

    const summary = summarizeFacts(facts, 5);
    expect(summary).toContain('suppliers.csv:row');
    expect(summary.split('\n').length).toBeLessThanOrEqual(5);
  });
});
