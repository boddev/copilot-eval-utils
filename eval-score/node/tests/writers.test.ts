import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { EvalRow } from '../src/types';
import { writeCsv } from '../src/writers/csv-writer';
import { writeJson } from '../src/writers/json-writer';
import { writeEvalFile } from '../src/writers';
import { readCsv } from '../src/readers/csv-reader';

let tmpDir: string;

beforeAll(() => {
  tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'evalcli-writers-'));
});

afterAll(() => {
  fs.rmSync(tmpDir, { recursive: true, force: true });
});

function makeSampleRows(): EvalRow[] {
  return [
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
      actualAnswer: 'Paris',
      similarityScore: 100,
    },
  ];
}

describe('writeCsv', () => {
  it('writes CSV that can be read back with correct data', async () => {
    const outputPath = path.join(tmpDir, 'round-trip.csv');
    const rows = makeSampleRows();

    await writeCsv(rows, outputPath);

    const readBack = await readCsv(outputPath);
    expect(readBack).toHaveLength(2);
    expect(readBack[0].prompt).toBe('What is 2+2?');
    expect(readBack[0].expectedAnswer).toBe('4');
    expect(readBack[0].sourceLocation).toBe('math.docx');
    expect(readBack[0].actualAnswer).toBe('Four');
    expect(readBack[1].prompt).toBe('Capital of France?');
    expect(readBack[1].actualAnswer).toBe('Paris');
  });
});

describe('writeJson', () => {
  it('writes JSON with snake_case keys', async () => {
    const outputPath = path.join(tmpDir, 'output.json');
    const rows = makeSampleRows();

    await writeJson(rows, outputPath);

    const content = JSON.parse(fs.readFileSync(outputPath, 'utf-8'));
    expect(content).toHaveLength(2);
    expect(content[0]).toHaveProperty('prompt', 'What is 2+2?');
    expect(content[0]).toHaveProperty('expected_answer', '4');
    expect(content[0]).toHaveProperty('source_location', 'math.docx');
    expect(content[0]).toHaveProperty('actual_answer', 'Four');
    expect(content[0]).toHaveProperty('similarity_score', 85);
    expect(content[1]).toHaveProperty('similarity_score', 100);
  });
});

describe('writeEvalFile', () => {
  it('generates output filename as {basename}-results.{ext}', async () => {
    const rows = makeSampleRows();
    const outputPath = await writeEvalFile(rows, 'evaluation-data.csv', tmpDir, 'csv');

    expect(path.basename(outputPath)).toBe('evaluation-data-results.csv');
    expect(fs.existsSync(outputPath)).toBe(true);
  });

  it('handles rows with undefined similarityScore without errors', async () => {
    const rows: EvalRow[] = [
      {
        prompt: 'Test question?',
        expectedAnswer: 'Answer',
        sourceLocation: 'test.docx',
        actualAnswer: 'Response',
        similarityScore: undefined,
      },
    ];

    const outputPath = path.join(tmpDir, 'no-score.csv');
    await expect(writeCsv(rows, outputPath)).resolves.toBeUndefined();

    const content = fs.readFileSync(outputPath, 'utf-8');
    expect(content).toContain('Test question?');
  });
});
