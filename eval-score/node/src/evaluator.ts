import { EvalRow } from './types';
import { WorkIQClient, buildPrompt } from './workiq-client';

export interface EvaluateOptions {
  systemPrompt?: string;
  tenantId?: string;
  onProgress?: (completed: number, total: number, currentPrompt: string) => void;
}

const DELAY_MS = 500;

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

export async function evaluatePrompts(
  rows: EvalRow[],
  client: WorkIQClient,
  options?: EvaluateOptions
): Promise<EvalRow[]> {
  const total = rows.length;

  for (let i = 0; i < total; i++) {
    const row = rows[i];

    if (row.actualAnswer) {
      continue;
    }

    process.stderr.write(`\rProcessing prompt ${i + 1}/${total}...`);

    const fullPrompt = buildPrompt(row.prompt, options?.systemPrompt);

    try {
      const response = await client.ask(fullPrompt, options?.tenantId);
      row.actualAnswer = response.trim();
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      row.actualAnswer = `[ERROR: ${message}]`;
    }

    options?.onProgress?.(i + 1, total, row.prompt);

    if (i < total - 1) {
      await delay(DELAY_MS);
    }
  }

  process.stderr.write('\n');
  return rows;
}
