import { describe, it, expect } from 'vitest';
import * as path from 'path';
import { readDatasetFile } from '../src/readers';
import { profileDataset } from '../src/profiler';

const FIXTURES = path.join(__dirname, 'fixtures');

describe('profileDataset', () => {
  it('profiles CSV dataset correctly', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);

    expect(profile.fileName).toBe('suppliers.csv');
    expect(profile.rowCount).toBe(15);
    expect(profile.columns.length).toBeGreaterThan(0);

    // Check column detection
    const nameCol = profile.columns.find(c => c.name === 'supplier_name');
    expect(nameCol).toBeDefined();
    expect(nameCol!.dataType).toBe('string');

    const valueCol = profile.columns.find(c => c.name === 'contract_value');
    expect(valueCol).toBeDefined();
    expect(valueCol!.dataType).toBe('number');
  });

  it('profiles JSON dataset correctly', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'projects.json'));
    const profile = profileDataset(records, 'projects.json', format);

    expect(profile.fileName).toBe('projects.json');
    expect(profile.rowCount).toBe(5);

    const budgetCol = profile.columns.find(c => c.name === 'budget');
    expect(budgetCol).toBeDefined();
    expect(budgetCol!.dataType).toBe('number');
    expect(budgetCol!.min).toBe(120000);
    expect(budgetCol!.max).toBe(750000);
  });

  it('detects candidate key columns', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);

    // supplier_id should be detected as a candidate key
    expect(profile.candidateKeyColumns).toContain('supplier_id');
  });

  it('detects categorical columns with value counts', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);

    const riskCol = profile.columns.find(c => c.name === 'risk_rating');
    expect(riskCol).toBeDefined();
    expect(riskCol!.valueCounts).toBeDefined();
    expect(riskCol!.valueCounts!['High']).toBeGreaterThan(0);
  });

  it('selects sample records', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);

    expect(profile.sampleRecords.length).toBeGreaterThan(0);
    expect(profile.sampleRecords.length).toBeLessThanOrEqual(20);
  });

  it('detects null counts', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);

    // Row SUP-011 has all empty fields
    const nameCol = profile.columns.find(c => c.name === 'supplier_name');
    expect(nameCol!.nullCount).toBeGreaterThan(0);
  });

  it('throws on empty dataset', () => {
    expect(() => profileDataset([], 'empty.csv', 'csv')).toThrow('Cannot profile an empty dataset');
  });
});
