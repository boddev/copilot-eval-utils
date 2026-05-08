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

  it('honors explicit targetRecords (decoupled from maxFacts)', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);

    // Default behavior: small maxFacts caps the row pool because the function
    // stops emitting facts once maxFacts is hit.
    const defaultFacts = extractFacts(records, profile, 20);
    const defaultRows = new Set(defaultFacts.map(f => f.rowReference)).size;

    // With explicit targetRecords + maxFactsPerRecord, the row pool can scale
    // independently of the fact budget — wide schemas no longer starve it.
    const expandedFacts = extractFacts(records, profile, {
      maxFacts: 200,
      targetRecords: Math.min(records.length, records.length),
      maxFactsPerRecord: 2,
    });
    const expandedRows = new Set(expandedFacts.map(f => f.rowReference)).size;
    expect(expandedRows).toBeGreaterThanOrEqual(defaultRows);

    // maxFactsPerRecord is enforced
    const counts = new Map<string, number>();
    for (const f of expandedFacts) counts.set(f.rowReference, (counts.get(f.rowReference) ?? 0) + 1);
    for (const c of counts.values()) {
      expect(c).toBeLessThanOrEqual(2);
    }
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

  it('includes [f-N] fact IDs so the LLM can cite specific facts', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const facts = extractFacts(records, profile, 100);

    const summary = summarizeFacts(facts, 5);
    expect(summary).toMatch(/\[f-\d+\]/);
  });
});
