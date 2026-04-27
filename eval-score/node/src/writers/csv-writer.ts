import { stringify } from 'csv-stringify/sync';
import * as fs from 'fs';
import { EvalRow } from '../types';

const COLUMNS = ['prompt', 'expected_answer', 'source_location', 'actual_answer', 'similarity_score'];

export async function writeCsv(rows: EvalRow[], outputPath: string, delimiter?: string): Promise<void> {
  const records = rows.map((row) => ({
    prompt: row.prompt,
    expected_answer: row.expectedAnswer,
    source_location: row.sourceLocation,
    actual_answer: row.actualAnswer,
    similarity_score: row.similarityScore ?? '',
  }));

  const output = stringify(records, {
    header: true,
    columns: COLUMNS,
    delimiter: delimiter ?? ',',
  });

  fs.writeFileSync(outputPath, output, 'utf-8');
}
