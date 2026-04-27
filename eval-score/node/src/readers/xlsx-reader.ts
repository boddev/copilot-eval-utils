import * as XLSX from 'xlsx';
import { EvalRow } from '../types';
import { normalizeHeaders, mapRow } from './normalize';

/**
 * Read an Excel (.xlsx) file and return an array of EvalRow objects.
 * Reads the first sheet of the workbook.
 */
export async function readXlsx(filePath: string): Promise<EvalRow[]> {
  const workbook = XLSX.readFile(filePath);
  const sheetName = workbook.SheetNames[0];

  if (!sheetName) {
    throw new Error(`No sheets found in workbook: ${filePath}`);
  }

  const sheet = workbook.Sheets[sheetName];
  const records: Record<string, string>[] = XLSX.utils.sheet_to_json(sheet, {
    defval: '',
  });

  if (records.length === 0) {
    return [];
  }

  const rawHeaders = Object.keys(records[0]);
  const headerMap = normalizeHeaders(rawHeaders);

  return records.map((record) => mapRow(record, headerMap));
}
