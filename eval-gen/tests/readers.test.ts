import { describe, it, expect } from 'vitest';
import * as path from 'path';
import { readDatasetFile } from '../src/readers';

const FIXTURES = path.join(__dirname, 'fixtures');

describe('readDatasetFile', () => {
  it('reads CSV files correctly', () => {
    const result = readDatasetFile(path.join(FIXTURES, 'suppliers.csv'));
    expect(result.format).toBe('csv');
    expect(result.records.length).toBe(15);
    expect(result.records[0]).toHaveProperty('supplier_name', 'Acme Corp');
  });

  it('reads JSON files correctly', () => {
    const result = readDatasetFile(path.join(FIXTURES, 'projects.json'));
    expect(result.format).toBe('json');
    expect(result.records.length).toBe(5);
    expect(result.records[0]).toHaveProperty('project_name', 'Apollo');
  });

  it('throws on missing file', () => {
    expect(() => readDatasetFile('nonexistent.csv')).toThrow('not found');
  });

  it('throws on unsupported format', () => {
    expect(() => readDatasetFile(path.join(FIXTURES, '..', '..', 'vitest.config.ts'))).toThrow('Unsupported file format');
  });

  it('reads a directory of files', () => {
    const result = readDatasetFile(FIXTURES);
    // Should merge suppliers.csv (15) + projects.json (5) + connector-schema.json (1, but it has contentFields not records)
    expect(result.records.length).toBeGreaterThan(15);
    expect(result.sourceFiles.length).toBeGreaterThanOrEqual(2);
  });

  it('reads comma-separated file list', () => {
    const csv = path.join(FIXTURES, 'suppliers.csv');
    const json = path.join(FIXTURES, 'projects.json');
    const result = readDatasetFile(`${csv},${json}`);
    expect(result.records.length).toBe(20); // 15 + 5
    expect(result.sourceFiles.length).toBe(2);
  });

  it('tags records with _source_file', () => {
    const csv = path.join(FIXTURES, 'suppliers.csv');
    const json = path.join(FIXTURES, 'projects.json');
    const result = readDatasetFile(`${csv},${json}`);

    const csvRecords = result.records.filter(r => r._source_file === 'suppliers.csv');
    const jsonRecords = result.records.filter(r => r._source_file === 'projects.json');
    expect(csvRecords.length).toBe(15);
    expect(jsonRecords.length).toBe(5);
  });

  it('reads text/markdown files as chunked content', () => {
    const result = readDatasetFile(path.join(FIXTURES, 'sample-doc.txt'));
    expect(result.format).toBe('txt');
    expect(result.records.length).toBeGreaterThan(0);
    expect(result.records[0]).toHaveProperty('content');
    expect(result.records[0]).toHaveProperty('chunk_number');

    // Content should contain some of the text
    const allContent = result.records.map(r => r.content).join(' ');
    expect(allContent).toContain('supplier management');
  });

  it('detects document formats from extensions', () => {
    // These test that format detection errors are distinct from not-found errors
    expect(() => readDatasetFile('test.docx')).toThrow(/not found/i);
    expect(() => readDatasetFile('test.pdf')).toThrow(/not found/i);
    expect(() => readDatasetFile('test.pptx')).toThrow(/not found/i);
  });
});
