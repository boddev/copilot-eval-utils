import * as path from 'path';
import { EvalRow, InputFormat } from '../types';
import { readCsv } from './csv-reader';
import { readXlsx } from './xlsx-reader';
import { readJson } from './json-reader';

export { readCsv } from './csv-reader';
export { readXlsx } from './xlsx-reader';
export { readJson } from './json-reader';
export { normalizeHeaders } from './normalize';

const EXTENSION_FORMAT_MAP: Record<string, InputFormat> = {
  '.csv': 'csv',
  '.tsv': 'tsv',
  '.xlsx': 'xlsx',
  '.json': 'json',
};

/**
 * Read an evaluation file, auto-detecting format from the file extension.
 * Returns the parsed rows and the detected format.
 */
export async function readEvalFile(
  filePath: string
): Promise<{ rows: EvalRow[]; format: InputFormat }> {
  const ext = path.extname(filePath).toLowerCase();
  const format = EXTENSION_FORMAT_MAP[ext];

  if (!format) {
    const supported = Object.keys(EXTENSION_FORMAT_MAP).join(', ');
    throw new Error(
      `Unsupported file extension "${ext}". Supported extensions: ${supported}`
    );
  }

  let rows: EvalRow[];

  switch (format) {
    case 'csv':
      rows = await readCsv(filePath);
      break;
    case 'tsv':
      rows = await readCsv(filePath, '\t');
      break;
    case 'xlsx':
      rows = await readXlsx(filePath);
      break;
    case 'json':
      rows = await readJson(filePath);
      break;
  }

  return { rows, format };
}
