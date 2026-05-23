import * as fs from 'fs';
import * as path from 'path';
import { EvalRow, EvalResult, InputFormat } from '../types';
import { writeCsv } from './csv-writer';
import { writeXlsx } from './xlsx-writer';
import { writeJson } from './json-writer';

export { writeCsv } from './csv-writer';
export { writeXlsx } from './xlsx-writer';
export { writeJson } from './json-writer';

const EXTENSION_MAP: Record<InputFormat, string> = {
  csv: '.csv',
  tsv: '.tsv',
  xlsx: '.xlsx',
  json: '.json',
};

export async function writeEvalFile(
  rows: EvalRow[],
  inputFile: string,
  outputDir: string,
  format: InputFormat,
  options?: Pick<EvalResult, 'metadata' | 'target' | 'judgeProvider' | 'evaluators'> & {
    defaultEvaluators?: EvalResult['defaultEvaluators'];
    threshold?: number;
  },
): Promise<string> {
  fs.mkdirSync(outputDir, { recursive: true });

  const baseName = path.basename(inputFile, path.extname(inputFile));
  const outputFileName = `${baseName}-results.json`;
  const outputPath = path.join(outputDir, outputFileName);

  await writeJson(rows, outputPath, {
    metadata: options?.metadata,
    defaultEvaluators: options?.defaultEvaluators,
    threshold: options?.threshold,
    inputFile,
    target: options?.target,
    judgeProvider: options?.judgeProvider,
    runEvaluators: options?.evaluators,
  });

  return outputPath;
}

export async function writeLegacyEvalFile(
  rows: EvalRow[],
  inputFile: string,
  outputDir: string,
  format: InputFormat,
): Promise<string> {
  fs.mkdirSync(outputDir, { recursive: true });

  const baseName = path.basename(inputFile, path.extname(inputFile));
  const outputFileName = `${baseName}-results${EXTENSION_MAP[format]}`;
  const outputPath = path.join(outputDir, outputFileName);

  switch (format) {
    case 'csv':
      await writeCsv(rows, outputPath);
      break;
    case 'tsv':
      await writeCsv(rows, outputPath, '\t');
      break;
    case 'xlsx':
      await writeXlsx(rows, outputPath);
      break;
    case 'json':
      await writeJson(rows, outputPath);
      break;
  }

  return outputPath;
}
