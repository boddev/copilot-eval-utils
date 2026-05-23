import * as fs from 'fs';
import { Assertion, EvalRow, EvaluatorMap, MetricResult } from '../types';
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
    const defaultEvaluators = coerceEvaluatorMap(parsed.default_evaluators);
    const metadata = isRecord(parsed.metadata) ? parsed.metadata : undefined;
    return parsed.items.flatMap((item, itemIndex) => {
      const itemEvalGen = getExtension(item, 'evalgen');
      const itemEvalScore = getExtension(item, 'evalscore');
      if (Array.isArray(item.turns) && item.turns.length > 0) {
        return item.turns.map((turn, turnIndex) => {
          const turnEvalGen = getExtension(turn, 'evalgen');
          const turnEvalScore = getExtension(turn, 'evalscore');
          const threadId = asString(itemEvalGen?.thread_id ?? itemEvalScore?.thread_id) ?? item.id ?? item.name ?? `item-${itemIndex + 1}`;
          const row: EvalRow = {
            prompt: turn.prompt ?? turn.question ?? '',
            expectedAnswer: turn.expected_response ?? turn.expected_answer ?? turn.expectedAnswer ?? item.expected_response ?? item.expected_answer ?? item.expectedAnswer ?? '',
            sourceLocation: asString(turn.source_location ?? turn.sourceLocation ?? turnEvalGen?.source_location ?? turnEvalScore?.source_location ?? item.source_location ?? item.sourceLocation ?? itemEvalGen?.source_location ?? itemEvalScore?.source_location) ?? '',
            actualAnswer: turn.response ?? turn.actual_answer ?? turn.actualAnswer ?? '',
            context: asString(turn.context ?? item.context),
            assertions: coerceAssertions(turn.assertions ?? item.assertions ?? turnEvalGen?.assertions ?? turnEvalScore?.assertions ?? itemEvalGen?.assertions ?? itemEvalScore?.assertions),
            metrics: turn.metrics,
            similarityScore: coerceScore(turn.similarity_score ?? turn.similarityScore),
            conversationId: item.conversation_id ?? item.conversationId ?? turn.conversation_id ?? turn.conversationId,
            conversationChaining: coerceConversationChaining(turn, item),
            citations: turn.citations,
            id: asString(turnEvalGen?.item_id ?? turnEvalScore?.item_id) ?? item.id ?? item.name ?? `item-${itemIndex + 1}`,
            itemIndex,
            turnIndex,
            threadId,
            threadName: item.name,
            threadDescription: item.description,
            documentDefaultEvaluators: defaultEvaluators,
            evaluators: coerceEvaluatorMap(turn.evaluators ?? item.evaluators),
            evaluatorsMode: turn.evaluators_mode ?? item.evaluators_mode,
            responseMetadata: metadata ? { documentMetadata: metadata, raw: turn.response_metadata ?? turn.responseMetadata } : turn.response_metadata ?? turn.responseMetadata,
          };
          attachEvalGenMetadata(row, turnEvalGen ?? itemEvalGen);
          return row;
        });
      }

      const row: EvalRow = {
        prompt: item.prompt ?? item.question ?? '',
        expectedAnswer: item.expected_response ?? item.expected_answer ?? item.expectedAnswer ?? '',
        sourceLocation: asString(item.source_location ?? item.sourceLocation ?? itemEvalGen?.source_location ?? itemEvalScore?.source_location) ?? '',
        actualAnswer: item.response ?? item.actual_answer ?? item.actualAnswer ?? '',
        context: asString(item.context),
        assertions: coerceAssertions(item.assertions ?? itemEvalGen?.assertions ?? itemEvalScore?.assertions),
        metrics: item.metrics,
        similarityScore: coerceScore(item.similarity_score ?? item.similarityScore),
        conversationId: item.conversation_id ?? item.conversationId,
        conversationChaining: coerceConversationChaining(item),
        responseMetadata: metadata ? { documentMetadata: metadata, raw: item.response_metadata ?? item.responseMetadata } : item.response_metadata ?? item.responseMetadata,
        citations: item.citations,
        id: item.id ?? `item-${itemIndex + 1}`,
        itemIndex,
        documentDefaultEvaluators: defaultEvaluators,
        evaluators: coerceEvaluatorMap(item.evaluators),
        evaluatorsMode: item.evaluators_mode,
      };
      attachEvalGenMetadata(row, itemEvalGen);
      return [row];
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
    row.id = asString(record.id);
    row.context = asString(record.context);
    row.evaluators = coerceEvaluatorMap(record.evaluators);
    row.evaluatorsMode = record.evaluators_mode === 'replace' ? 'replace' : record.evaluators_mode === 'extend' ? 'extend' : undefined;
    if (row.similarityScore === undefined) {
      row.similarityScore = coerceScore(record.similarity_score ?? record.similarityScore);
    }
    return row;
  });
}

type M365EvalItem = Record<string, any> & { turns?: Array<Record<string, any>> };

function isM365EvalDocument(value: unknown): value is { items: M365EvalItem[]; default_evaluators?: unknown; metadata?: unknown } {
  if (!value || typeof value !== 'object') return false;
  const record = value as Record<string, unknown>;
  return Array.isArray(record.items);
}

function coerceEvaluatorMap(value: unknown): EvaluatorMap | undefined {
  if (!isRecord(value)) return undefined;
  return value as EvaluatorMap;
}

function coerceAssertions(value: unknown): Assertion[] | undefined {
  return Array.isArray(value) ? value as Assertion[] : undefined;
}

function getExtension(record: Record<string, any>, name: string): Record<string, unknown> | undefined {
  if (!isRecord(record.extensions)) return undefined;
  const extension = record.extensions[name];
  return isRecord(extension) ? extension : undefined;
}

function coerceConversationChaining(...records: Array<Record<string, any>>): boolean | undefined {
  for (const record of records) {
    const evalGen = getExtension(record, 'evalgen');
    const evalScore = getExtension(record, 'evalscore');
    const values = [
      record.conversation_chaining,
      record.conversationChaining,
      evalGen?.conversation_chaining,
      evalScore?.conversation_chaining,
    ];
    if (values.some(value => value === false)) return false;
    if (evalGen?.synthetic_thread === true || evalScore?.synthetic_thread === true) return false;
    if (values.some(value => value === true)) return true;
  }
  return undefined;
}

function attachEvalGenMetadata(row: EvalRow, extension: Record<string, unknown> | undefined): void {
  if (!extension) return;
  const category = asString(extension.category);
  const difficulty = asString(extension.difficulty);
  const confidence = asString(extension.grounding_confidence);
  if (category) (row as EvalRow & { _category?: string })._category = category;
  if (difficulty) (row as EvalRow & { _difficulty?: string })._difficulty = difficulty;
  if (confidence) (row as EvalRow & { _confidence?: string })._confidence = confidence;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function coerceScore(value: unknown): number | undefined {
  if (value === undefined || value === null || value === '') return undefined;
  const parsed = typeof value === 'number' ? value : Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) ? Math.max(0, Math.min(100, parsed)) : undefined;
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
