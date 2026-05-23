import * as fs from 'fs';
import { Assertion, EvalRow, MetricResult } from '../types';
import { normalizeHeaders, mapRow } from './normalize';

/**
 * Read a JSON file and return an array of EvalRow objects.
 * Expects the file to contain a JSON array of objects.
 */
export async function readJson(filePath: string): Promise<EvalRow[]> {
  const content = fs.readFileSync(filePath, 'utf-8');
  let parsed: unknown;

  try {
    parsed = JSON.parse(content);
  } catch (err) {
    throw new Error(
      `Failed to parse JSON from ${filePath}: ${err instanceof Error ? err.message : String(err)}`
    );
  }

  if (isM365EvalDocument(parsed)) {
    return parsed.items.flatMap((item, itemIndex) => {
      if (Array.isArray(item.turns) && item.turns.length > 0) {
        return item.turns.map((turn, turnIndex) => ({
          prompt: turn.prompt ?? turn.question ?? '',
          expectedAnswer: turn.expected_answer ?? turn.expectedAnswer ?? item.expected_answer ?? item.expectedAnswer ?? '',
          sourceLocation: turn.source_location ?? turn.sourceLocation ?? item.source_location ?? item.sourceLocation ?? '',
          actualAnswer: turn.actual_answer ?? turn.actualAnswer ?? '',
          assertions: turn.assertions ?? item.assertions,
          metrics: turn.metrics,
          similarityScore: coerceScore(turn.similarity_score ?? turn.similarityScore),
          conversationId: turn.conversation_id ?? turn.conversationId,
          responseMetadata: turn.response_metadata ?? turn.responseMetadata,
          citations: turn.citations,
          _id: item.id ?? `item-${itemIndex + 1}`,
          _turnIndex: turnIndex,
        }));
      }

      return [{
        prompt: item.prompt ?? item.question ?? '',
        expectedAnswer: item.expected_answer ?? item.expectedAnswer ?? '',
        sourceLocation: item.source_location ?? item.sourceLocation ?? '',
        actualAnswer: item.actual_answer ?? item.actualAnswer ?? '',
        assertions: item.assertions,
        metrics: item.metrics,
        similarityScore: coerceScore(item.similarity_score ?? item.similarityScore),
        conversationId: item.conversation_id ?? item.conversationId,
        responseMetadata: item.response_metadata ?? item.responseMetadata,
        citations: item.citations,
        _id: item.id ?? `item-${itemIndex + 1}`,
      }];
    });
  }

  if (!Array.isArray(parsed)) {
    throw new Error(
      `Expected a JSON array in ${filePath}, but got ${typeof parsed}`
    );
  }

  if (parsed.length === 0) {
    return [];
  }

  const records = parsed as Record<string, unknown>[];
  const rawHeaders = Object.keys(records[0]);
  const headerMap = normalizeHeaders(rawHeaders);

  return records.map((record) => {
    const row = mapRow(record as Record<string, string>, headerMap);
    row.assertions = record.assertions as Assertion[] | undefined;
    row.metrics = record.metrics as MetricResult[] | undefined;
    row.citations = record.citations as EvalRow['citations'];
    row.responseMetadata = record.response_metadata ?? record.responseMetadata;
    row.conversationId = asString(record.conversation_id ?? record.conversationId);
    if (row.similarityScore === undefined) {
      row.similarityScore = coerceScore(record.similarity_score ?? record.similarityScore);
    }
    return row;
  });
}

type M365EvalItem = Record<string, any> & { turns?: Array<Record<string, any>> };

function isM365EvalDocument(value: unknown): value is { items: M365EvalItem[] } {
  if (!value || typeof value !== 'object') return false;
  const record = value as Record<string, unknown>;
  return Array.isArray(record.items);
}

function coerceScore(value: unknown): number | undefined {
  if (value === undefined || value === null || value === '') return undefined;
  const parsed = typeof value === 'number' ? value : Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) ? Math.max(0, Math.min(100, parsed)) : undefined;
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
