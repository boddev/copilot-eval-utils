import * as fs from 'fs';
import { EvalRow, EvaluatorMap, EvaluatorName } from '../types';
import { rowsToEvalDocument } from '../eval-document';

export async function writeJson(
  rows: EvalRow[],
  outputPath: string,
  options?: {
    metadata?: Record<string, unknown>;
    defaultEvaluators?: EvaluatorMap;
    threshold?: number;
    inputFile?: string;
    target?: unknown;
    judgeProvider?: string;
    runEvaluators?: EvaluatorName[];
  },
): Promise<void> {
  const document = rowsToEvalDocument(rows, options);

  fs.writeFileSync(outputPath, JSON.stringify(document, null, 2), 'utf-8');
}
