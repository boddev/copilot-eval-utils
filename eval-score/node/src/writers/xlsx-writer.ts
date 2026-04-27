import * as xlsx from 'xlsx';
import { EvalRow } from '../types';

export async function writeXlsx(rows: EvalRow[], outputPath: string): Promise<void> {
  const records = rows.map((row) => ({
    prompt: row.prompt,
    expected_answer: row.expectedAnswer,
    source_location: row.sourceLocation,
    actual_answer: row.actualAnswer,
    similarity_score: row.similarityScore ?? '',
  }));

  const worksheet = xlsx.utils.json_to_sheet(records);
  const workbook = xlsx.utils.book_new();
  xlsx.utils.book_append_sheet(workbook, worksheet, 'Results');
  xlsx.writeFile(workbook, outputPath);
}
