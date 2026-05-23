import { EvalRow } from './types';
import { WorkIQClient, buildPrompt } from './workiq-client';
import { setRowError } from './eval-document';
import { createThrottleGate } from './throttle-gate';

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
  const throttleGate = createThrottleGate(concurrency);
  let completed = rows.filter(row => Boolean(row.actualAnswer)).length;

  async function processRow(i: number, inheritedConversationId?: string): Promise<string | undefined> {
    const row = rows[i];

    if (row.actualAnswer) {
      return row.conversationId ?? inheritedConversationId;
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
        const response = await throttleGate.run(() => client.askWithMetadata!(fullPrompt, {
          tenantId: options?.tenantId,
          agentId: options?.agentId,
          conversationId: row.conversationId ?? inheritedConversationId,
        }));
        row.actualAnswer = response.text.trim();
        row.citations = response.citations;
        row.responseMetadata = response.raw;
        row.conversationId = response.conversationId ?? row.conversationId;
      } else {
        const response = await throttleGate.run(() => client.ask(fullPrompt, options?.tenantId));
        row.actualAnswer = response.trim();
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      row.actualAnswer = `[ERROR: ${message}]`;
      setRowError(row, 'agentRequestFailed', message);
    }

    completed++;
    options?.onProgress?.(completed, total, row.prompt);
    await options?.onRowComplete?.(rows, row, i);

    if (i < total - 1 && delayMs > 0) {
      await delay(delayMs);
    }
    return row.conversationId ?? inheritedConversationId;
  }

  const jobs = buildEvaluationJobs(rows);
  let nextJob = 0;

  async function processJob(job: number[]): Promise<void> {
    let conversationId: string | undefined;
    for (const rowIndex of job) {
      const row = rows[rowIndex];
      const inheritedConversationId = row.conversationChaining === false ? undefined : conversationId;
      const nextConversationId = await processRow(rowIndex, inheritedConversationId);
      conversationId = row.conversationChaining === false ? undefined : nextConversationId;
    }
  }

  async function worker(): Promise<void> {
    while (nextJob < jobs.length) {
      const current = jobs[nextJob++];
      await processJob(current);
    }
  }

  await Promise.all(Array.from({ length: concurrency }, () => worker()));

  process.stderr.write('\n');
  return rows;
}

function buildEvaluationJobs(rows: EvalRow[]): number[][] {
  const jobs: number[][] = [];
  const consumed = new Set<number>();
  for (let i = 0; i < rows.length; i++) {
    if (consumed.has(i)) continue;
    const row = rows[i];
    if (row.turnIndex === undefined) {
      jobs.push([i]);
      consumed.add(i);
      continue;
    }
    const threadId = row.threadId ?? row.id ?? String(row.itemIndex ?? i);
    const indexes = rows
      .map((candidate, index) => ({ candidate, index }))
      .filter(({ candidate, index }) =>
        !consumed.has(index) &&
        candidate.turnIndex !== undefined &&
        (candidate.threadId ?? candidate.id ?? String(candidate.itemIndex ?? index)) === threadId
      )
      .sort((a, b) => (a.candidate.turnIndex ?? 0) - (b.candidate.turnIndex ?? 0))
      .map(({ index }) => index);
    for (const index of indexes) consumed.add(index);
    jobs.push(indexes);
  }
  return jobs;
}
