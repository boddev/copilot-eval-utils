import * as fs from 'fs';
import { parse } from 'csv-parse/sync';
import { EvalRow } from '../types';
import { normalizeHeaders, mapRow } from './normalize';

const HEADER_KEYWORDS = [
  'prompt', 'question',
  'expected_answer', 'expectedanswer', 'expected answer',
  'source_location', 'sourcelocation', 'source location',
  'actual_answer', 'actualanswer', 'actual answer',
];

/**
 * Detect whether the first row of a delimited file is a header row
 * by checking if any field matches a known column name.
 */
function hasHeaderRow(firstLine: string, delimiter: string): boolean {
  const fields = firstLine.split(delimiter).map(f => f.trim().replace(/^"|"$/g, '').toLowerCase());
  return fields.some(f => HEADER_KEYWORDS.includes(f));
}

/**
 * Read a CSV (or TSV) file and return an array of EvalRow objects.
 * Automatically detects whether the first row is a header or data.
 */
export async function readCsv(
  filePath: string,
  delimiter: string = ','
): Promise<EvalRow[]> {
  const content = fs.readFileSync(filePath, 'utf-8');
  const lines = content.split(/\r?\n/).filter(l => l.trim() !== '');

  if (lines.length === 0) {
    return [];
  }

  const firstRowIsHeader = hasHeaderRow(lines[0], delimiter);

  let records: Record<string, string>[];

  if (firstRowIsHeader) {
    records = parse(content, {
      columns: true,
      skip_empty_lines: true,
      delimiter,
      trim: true,
    });
  } else {
    // No header row — assign positional column names
    const positionalHeaders = ['prompt', 'expected_answer', 'source_location', 'actual_answer'];
    const fieldCount = lines[0].split(delimiter).length;
    const columns: string[] = [];
    for (let i = 0; i < fieldCount; i++) {
      columns.push(i < positionalHeaders.length ? positionalHeaders[i] : `column_${i + 1}`);
    }

    records = parse(content, {
      columns,
      skip_empty_lines: true,
      delimiter,
      trim: true,
    });
  }

  if (records.length === 0) {
    return [];
  }

  const rawHeaders = Object.keys(records[0]);
  const headerMap = normalizeHeaders(rawHeaders);

  return records.map((record) => mapRow(record, headerMap));
}
