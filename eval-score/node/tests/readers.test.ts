import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import { readCsv } from '../src/readers/csv-reader';
import { readJson } from '../src/readers/json-reader';
import { readEvalFile } from '../src/readers';

const FIXTURES_DIR = path.join(__dirname, 'fixtures');

beforeAll(() => {
  fs.mkdirSync(FIXTURES_DIR, { recursive: true });
});

afterAll(() => {
  fs.rmSync(FIXTURES_DIR, { recursive: true, force: true });
});

describe('readCsv', () => {
  it('reads a CSV file and returns correct EvalRow objects', async () => {
    const csvPath = path.join(FIXTURES_DIR, 'basic.csv');
    const content = [
      'prompt,expected_answer,source_location,actual_answer',
      'What is 2+2?,4,math.docx,Four',
      'Who is CEO?,Jane Doe,org-chart.xlsx,Jane Doe',
    ].join('\n');
    fs.writeFileSync(csvPath, content, 'utf-8');

    const rows = await readCsv(csvPath);

    expect(rows).toHaveLength(2);
    expect(rows[0]).toEqual({
      prompt: 'What is 2+2?',
      expectedAnswer: '4',
      sourceLocation: 'math.docx',
      actualAnswer: 'Four',
    });
    expect(rows[1]).toEqual({
      prompt: 'Who is CEO?',
      expectedAnswer: 'Jane Doe',
      sourceLocation: 'org-chart.xlsx',
      actualAnswer: 'Jane Doe',
    });
  });

  it('reads a TSV file with tab delimiter', async () => {
    const tsvPath = path.join(FIXTURES_DIR, 'basic.tsv');
    const content = [
      'prompt\texpected_answer\tsource_location\tactual_answer',
      'What color is sky?\tBlue\tweather.docx\tBlue',
      'Capital of France?\tParis\tgeo.xlsx\tParis',
    ].join('\n');
    fs.writeFileSync(tsvPath, content, 'utf-8');

    const rows = await readCsv(tsvPath, '\t');

    expect(rows).toHaveLength(2);
    expect(rows[0].prompt).toBe('What color is sky?');
    expect(rows[0].expectedAnswer).toBe('Blue');
    expect(rows[1].prompt).toBe('Capital of France?');
    expect(rows[1].sourceLocation).toBe('geo.xlsx');
  });

  it('normalizes alternate header names', async () => {
    const csvPath = path.join(FIXTURES_DIR, 'alt-headers.csv');
    const content = [
      'Question,Expected Answer,Source Location,Actual Answer',
      'What is AI?,Artificial Intelligence,ai.docx,AI stands for Artificial Intelligence',
    ].join('\n');
    fs.writeFileSync(csvPath, content, 'utf-8');

    const rows = await readCsv(csvPath);

    expect(rows).toHaveLength(1);
    expect(rows[0].prompt).toBe('What is AI?');
    expect(rows[0].expectedAnswer).toBe('Artificial Intelligence');
    expect(rows[0].sourceLocation).toBe('ai.docx');
    expect(rows[0].actualAnswer).toBe('AI stands for Artificial Intelligence');
  });

  it('throws when required columns are missing', async () => {
    const csvPath = path.join(FIXTURES_DIR, 'missing-cols.csv');
    const content = [
      'expected_answer,source_location,actual_answer',
      'Answer1,loc1,actual1',
    ].join('\n');
    fs.writeFileSync(csvPath, content, 'utf-8');

    await expect(readCsv(csvPath)).rejects.toThrow(/Missing required column/);
  });
});

describe('readJson', () => {
  it('reads a JSON file with camelCase keys', async () => {
    const jsonPath = path.join(FIXTURES_DIR, 'basic.json');
    const data = [
      {
        prompt: 'What is TypeScript?',
        expectedAnswer: 'A typed superset of JavaScript',
        sourceLocation: 'docs.md',
        actualAnswer: 'TypeScript is a typed JS superset',
      },
      {
        prompt: 'What is Node?',
        expectedAnswer: 'A JS runtime',
        sourceLocation: 'node.md',
        actualAnswer: '',
      },
    ];
    fs.writeFileSync(jsonPath, JSON.stringify(data, null, 2), 'utf-8');

    const rows = await readJson(jsonPath);

    expect(rows).toHaveLength(2);
    expect(rows[0].prompt).toBe('What is TypeScript?');
    expect(rows[0].expectedAnswer).toBe('A typed superset of JavaScript');
    expect(rows[1].prompt).toBe('What is Node?');
    expect(rows[1].actualAnswer).toBe('');
  });

  it('reads m365 eval document items and turns', async () => {
    const jsonPath = path.join(FIXTURES_DIR, 'm365-doc.json');
    const data = {
      schemaVersion: 'm365-copilot-eval-v1',
      items: [
        {
          id: 'multi-turn-1',
          expected_answer: 'Final answer',
          source_location: 'source.docx',
          turns: [
            { prompt: 'First question?', actual_answer: 'First answer' },
            { prompt: 'Follow-up?', expected_answer: 'Follow-up answer', actual_answer: 'Final answer' },
          ],
        },
      ],
    };
    fs.writeFileSync(jsonPath, JSON.stringify(data, null, 2), 'utf-8');

    const rows = await readJson(jsonPath);

    expect(rows).toHaveLength(2);
    expect(rows[0].prompt).toBe('First question?');
    expect(rows[0].expectedAnswer).toBe('Final answer');
    expect(rows[1].expectedAnswer).toBe('Follow-up answer');
  });

  it('reads EvalGen metadata from m365 extensions on multi-prompt turns', async () => {
    const jsonPath = path.join(FIXTURES_DIR, 'm365-evalgen-extension-doc.json');
    const data = {
      schemaVersion: '1.4.0',
      items: [
        {
          id: 'thread-1',
          name: 'EvalGen multi-prompt evaluator 1',
          turns: [
            {
              prompt: 'Who owns Acme Corp?',
              expected_response: 'Jane Smith owns Acme Corp.',
              extensions: {
                evalgen: {
                  item_id: 'item-1',
                  source_location: 'suppliers.csv:row 1',
                  assertions: [{ type: 'must_contain', value: 'Jane Smith' }],
                  category: 'single_record_lookup',
                  difficulty: 'easy',
                  grounding_confidence: 'high',
                  synthetic_thread: true,
                  conversation_chaining: false,
                },
              },
            },
          ],
          extensions: {
            evalgen: {
              synthetic_thread: true,
              conversation_chaining: false,
            },
          },
        },
      ],
    };
    fs.writeFileSync(jsonPath, JSON.stringify(data, null, 2), 'utf-8');

    const rows = await readJson(jsonPath);

    expect(rows).toHaveLength(1);
    expect(rows[0].id).toBe('item-1');
    expect(rows[0].sourceLocation).toBe('suppliers.csv:row 1');
    expect(rows[0].assertions).toEqual([{ type: 'must_contain', value: 'Jane Smith' }]);
    expect(rows[0].conversationChaining).toBe(false);
    expect((rows[0] as typeof rows[number] & { _category?: string })._category).toBe('single_record_lookup');
  });
});

describe('readEvalFile', () => {
  it('detects CSV format from file extension', async () => {
    const csvPath = path.join(FIXTURES_DIR, 'detect.csv');
    const content = [
      'prompt,expected_answer,source_location,actual_answer',
      'Q1?,A1,loc1,act1',
    ].join('\n');
    fs.writeFileSync(csvPath, content, 'utf-8');

    const result = await readEvalFile(csvPath);

    expect(result.format).toBe('csv');
    expect(result.rows).toHaveLength(1);
  });

  it('throws for unsupported file extensions', async () => {
    const txtPath = path.join(FIXTURES_DIR, 'unsupported.txt');
    fs.writeFileSync(txtPath, 'some content', 'utf-8');

    await expect(readEvalFile(txtPath)).rejects.toThrow(/Unsupported file extension/);
  });
});
