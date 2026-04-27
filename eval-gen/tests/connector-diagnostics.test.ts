import { describe, it, expect } from 'vitest';
import * as path from 'path';
import {
  loadConnectorSchema,
  analyzeFieldCoverage,
  runDiagnostics,
  formatDiagnosticReport,
} from '../src/connector-diagnostics';
import { readDatasetFile } from '../src/readers';
import { profileDataset } from '../src/profiler';
import { GeneratedEvalItem } from '../src/types';

const FIXTURES = path.join(__dirname, 'fixtures');

describe('loadConnectorSchema', () => {
  it('loads valid connector schema', () => {
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));
    expect(schema.name).toBe('WSP Suppliers Connector');
    expect(schema.contentFields.length).toBeGreaterThan(0);
    expect(schema.contentFields).toContain('supplier_name');
  });

  it('throws on missing file', () => {
    expect(() => loadConnectorSchema('nonexistent.json')).toThrow('not found');
  });
});

describe('analyzeFieldCoverage', () => {
  it('identifies indexed and unindexed fields', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));

    const coverage = analyzeFieldCoverage(profile, schema);
    expect(coverage.indexedFields.length).toBeGreaterThan(0);
    expect(coverage.unindexedFields.length).toBeGreaterThan(0);

    // contract_value and renewal_date are NOT in the connector schema
    expect(coverage.unindexedFields).toContain('contract_value');
    expect(coverage.unindexedFields).toContain('renewal_date');

    // supplier_name IS in the connector schema
    expect(coverage.indexedFields).toContain('supplier_name');
  });

  it('reports coverage percentage', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));

    const coverage = analyzeFieldCoverage(profile, schema);
    expect(coverage.coveragePercentage).toBeGreaterThan(0);
    expect(coverage.coveragePercentage).toBeLessThan(100); // Not all fields are indexed
  });
});

describe('runDiagnostics', () => {
  function makeItem(overrides: Partial<GeneratedEvalItem> = {}): GeneratedEvalItem {
    return {
      prompt: 'Who owns Acme Corp?',
      expected_answer: 'Jane Smith',
      source_location: 'suppliers.csv:row 1',
      assertions: [],
      category: 'single_record_lookup',
      difficulty: 'easy',
      supporting_facts: ['owner=Jane Smith', 'supplier_name=Acme Corp'],
      grounding_confidence: 'high',
      ...overrides,
    };
  }

  it('flags questions targeting unindexed fields', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));

    const items = [
      makeItem({
        prompt: 'What is the contract value for Acme Corp?',
        supporting_facts: ['contract_value=250000'],
      }),
    ];

    const report = runDiagnostics(items, profile, schema);
    expect(report.itemDiagnostics[0].severity).toBe('error');
    expect(report.itemDiagnostics[0].issues.some(i => i.includes('unindexed'))).toBe(true);
  });

  it('flags aggregation questions without summary items', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));

    const items = [
      makeItem({
        prompt: 'How many suppliers have high risk rating?',
        supporting_facts: ['risk_rating=High'],
      }),
    ];

    const report = runDiagnostics(items, profile, schema);
    expect(report.aggregationWarnings.length).toBeGreaterThan(0);
  });

  it('passes clean questions', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));

    const items = [
      makeItem({
        prompt: 'Who owns Acme Corp?',
        supporting_facts: ['owner=Jane Smith', 'supplier_name=Acme Corp'],
      }),
    ];

    const report = runDiagnostics(items, profile, schema);
    expect(report.itemDiagnostics[0].severity).toBe('ok');
  });
});

describe('formatDiagnosticReport', () => {
  it('produces readable markdown', () => {
    const { records, format } = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    const profile = profileDataset(records, 'suppliers.csv', format);
    const schema = loadConnectorSchema(path.join(FIXTURES, 'connector-schema.json'));

    const items: GeneratedEvalItem[] = [
      {
        prompt: 'Who owns Acme Corp?',
        expected_answer: 'Jane Smith',
        source_location: 'suppliers.csv:row 1',
        assertions: [],
        category: 'single_record_lookup',
        difficulty: 'easy',
        supporting_facts: ['owner=Jane Smith'],
        grounding_confidence: 'high',
      },
    ];

    const report = runDiagnostics(items, profile, schema);
    const markdown = formatDiagnosticReport(report);

    expect(markdown).toContain('# Connector Diagnostics Report');
    expect(markdown).toContain('WSP Suppliers Connector');
    expect(markdown).toContain('Field Coverage');
  });
});
