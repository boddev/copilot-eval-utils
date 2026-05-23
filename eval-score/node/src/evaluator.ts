import { EvalRow } from './types';
import { WorkIQClient, buildPrompt } from './workiq-client';

export interface EvaluateOptions {
  systemPrompt?: string;
  connectorId?: string;
  connectorPromptHint?: boolean;
  tenantId?: string;
  agentId?: string;
  concurrency?: number;
  delayMs?: number;
  onProgress?: (completed: number, total: number, currentPrompt: string) => void;
  onRowComplete?: (rows: EvalRow[], row: EvalRow, index: number) => void | Promise<void>;
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
  const concurrency = Math.max(1, Math.min(options?.concurrency ?? 1, total || 1));
  const delayMs = Math.max(0, options?.delayMs ?? DELAY_MS);
  let nextIndex = 0;
  let completed = rows.filter(row => Boolean(row.actualAnswer)).length;

  async function processRow(i: number): Promise<void> {
    const row = rows[i];

    if (row.actualAnswer) {
      return;
    }

    process.stderr.write(`\rProcessing prompt ${i + 1}/${total}...`);

    const fullPrompt = buildPrompt(
      row.prompt,
      options?.systemPrompt,
      options?.connectorId,
      options?.connectorPromptHint ?? false,
    );

    try {
      if (client.askWithMetadata) {
        const response = await client.askWithMetadata(fullPrompt, {
          tenantId: options?.tenantId,
          agentId: options?.agentId,
          conversationId: row.conversationId,
        });
        row.actualAnswer = response.text.trim();
        row.citations = response.citations;
        row.responseMetadata = response.raw;
        row.conversationId = response.conversationId ?? row.conversationId;
      } else {
        const response = await client.ask(fullPrompt, options?.tenantId);
        row.actualAnswer = response.trim();
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      row.actualAnswer = `[ERROR: ${message}]`;
    }

    completed++;
    options?.onProgress?.(completed, total, row.prompt);
    await options?.onRowComplete?.(rows, row, i);

    if (i < total - 1 && delayMs > 0) {
      await delay(delayMs);
    }
  }

  async function worker(): Promise<void> {
    while (nextIndex < total) {
      const current = nextIndex++;
      await processRow(current);
    }
  }

  await Promise.all(Array.from({ length: concurrency }, () => worker()));

  process.stderr.write('\n');
  return rows;
}
