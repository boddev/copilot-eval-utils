import {
  EvalDocument,
  EvalDocumentTurn,
  EvalError,
  EvalRow,
  EvalStatus,
  EvaluatorMap,
  EvaluatorName,
  MetricResult,
} from './types';

export const DEFAULT_M365_EVALUATORS: EvaluatorName[] = ['Relevance', 'Coherence'];

const SCORE_KEY: Partial<Record<EvaluatorName, string>> = {
  SemanticSimilarity: 'similarity',
  Similarity: 'similarity',
  Relevance: 'relevance',
  Coherence: 'coherence',
  Groundedness: 'groundedness',
  Citations: 'citations',
  ExactMatch: 'exactMatch',
  PartialMatch: 'partialMatch',
};

export function resolveRowEvaluators(row: EvalRow, runEvaluators: EvaluatorName[]): EvaluatorName[] {
  const defaults = evaluatorMapNames(row.documentDefaultEvaluators);
  const base = defaults.length > 0 ? defaults : runEvaluators;
  const overrides = evaluatorMapNames(row.evaluators);
  if (overrides.length === 0) return normalizeEvaluatorList(base);
  return normalizeEvaluatorList(row.evaluatorsMode === 'replace' ? overrides : [...base, ...overrides]);
}

export function deriveRowStatus(row: EvalRow, threshold = 70): EvalStatus {
  if (row.error || !row.actualAnswer || row.actualAnswer.startsWith('[ERROR:')) return 'error';

  const metricStatuses = (row.metrics ?? [])
    .map(metric => metric.passed)
    .filter((value): value is boolean => value !== undefined);

  if (metricStatuses.length > 0) {
    const passed = metricStatuses.filter(Boolean).length;
    if (passed === metricStatuses.length) return 'pass';
    if (passed === 0) return 'fail';
    return 'partial';
  }

  return (row.similarityScore ?? 0) >= threshold ? 'pass' : 'fail';
}

export function setRowError(row: EvalRow, code: EvalError['code'], message: string): void {
  row.error = { code, message };
  row.status = 'error';
}

export function rowsToEvalDocument(
  rows: EvalRow[],
  options?: {
    metadata?: Record<string, unknown>;
    defaultEvaluators?: EvaluatorMap;
    threshold?: number;
    inputFile?: string;
    target?: unknown;
    judgeProvider?: string;
    runEvaluators?: EvaluatorName[];
  },
): EvalDocument {
  const threshold = options?.threshold ?? 70;
  const metadata = {
    ...(options?.metadata ?? {}),
    evaluatedAt: new Date().toISOString(),
    cliVersion: 'eval-score',
    extensions: {
      ...((options?.metadata?.extensions as Record<string, unknown> | undefined) ?? {}),
      evalscore: {
        inputFile: options?.inputFile,
        target: options?.target,
        judgeProvider: options?.judgeProvider,
        evaluators: options?.runEvaluators,
        canonicalScoreScale: '0-100',
      },
    },
  };

  return {
    schemaVersion: '1.4.0',
    metadata,
    default_evaluators: options?.defaultEvaluators ?? rows[0]?.documentDefaultEvaluators,
    items: buildDocumentItems(rows, threshold),
  };
}

function buildDocumentItems(rows: EvalRow[], threshold: number): EvalDocument['items'] {
  const orderedGroups = new Map<string, EvalRow[]>();
  for (const row of rows) {
    const groupKey = row.turnIndex !== undefined
      ? `thread:${row.threadId ?? row.id ?? row.itemIndex ?? row.prompt}`
      : `single:${row.itemIndex ?? rows.indexOf(row)}:${row.id ?? row.prompt}`;
    if (!orderedGroups.has(groupKey)) orderedGroups.set(groupKey, []);
    orderedGroups.get(groupKey)!.push(row);
  }

  return Array.from(orderedGroups.values()).map(group => {
    const first = group[0];
    if (first.turnIndex !== undefined || group.length > 1 && group.some(row => row.turnIndex !== undefined)) {
      const turns = [...group]
        .sort((a, b) => (a.turnIndex ?? 0) - (b.turnIndex ?? 0))
        .map(row => rowToDocumentTurn(row, threshold));
      const statuses = turns.map(turn => turn.status ?? 'fail');
      return {
        name: first.threadName ?? first.id,
        description: first.threadDescription,
        conversation_id: first.conversationId,
        turns,
        summary: {
          turns_total: turns.length,
          turns_passed: statuses.filter(status => status === 'pass').length,
          turns_failed: statuses.filter(status => status === 'fail').length,
          turns_partial: statuses.filter(status => status === 'partial').length,
          turns_errored: statuses.filter(status => status === 'error').length,
          overall_status: summarizeStatuses(statuses),
        },
        extensions: compactObject({
          evalscore: compactObject({
            item_id: first.id,
          }),
        }),
      };
    }

    return rowToDocumentTurn(first, threshold);
  });
}

function rowToDocumentTurn(row: EvalRow, threshold: number): EvalDocumentTurn {
  const status = row.status ?? deriveRowStatus(row, threshold);
  return compactObject({
    prompt: row.prompt,
    expected_response: row.expectedAnswer || undefined,
    response: row.actualAnswer || undefined,
    context: row.context || row.sourceLocation || undefined,
    evaluators: row.evaluators,
    evaluators_mode: row.evaluatorsMode,
    citations: normalizeCitations(row),
    scores: metricsToScores(row.metrics, threshold),
    status,
    error: row.error,
    extensions: compactObject({
      evalscore: compactObject({
        item_id: row.id,
        item_index: row.itemIndex,
        turn_index: row.turnIndex,
        source_location: row.sourceLocation || undefined,
        canonical_score_0_100: row.similarityScore,
        response_metadata: row.responseMetadata,
        assertions: row.assertions,
        assertion_results: row.assertionResults,
      }),
    }),
  }) as EvalDocumentTurn;
}

function metricsToScores(metrics: MetricResult[] | undefined, threshold: number): Record<string, unknown> | undefined {
  if (!metrics?.length) return undefined;
  const scores: Record<string, unknown> = {};
  for (const metric of metrics) {
    const key = SCORE_KEY[metric.name];
    if (!key) continue;
    if (metric.name === 'ExactMatch') {
      scores[key] = {
        match: Boolean(metric.passed),
        result: metric.passed ? 'pass' : 'fail',
        reason: metric.reason,
        score_0_100: metric.score,
      };
      continue;
    }
    if (metric.name === 'PartialMatch') {
      const partialScore = (metric.score ?? 0) / 100;
      scores[key] = {
        score: partialScore,
        result: metric.passed ? 'pass' : 'fail',
        threshold: threshold / 100,
        reason: metric.reason,
        score_0_100: metric.score,
      };
      continue;
    }
    if (metric.name === 'Citations') {
      const citations = metric.passed ? 1 : 0;
      scores[key] = {
        count: citations,
        result: metric.passed ? 'pass' : 'fail',
        threshold: 1,
        reason: metric.reason,
        score_0_100: metric.score,
      };
      continue;
    }
    scores[key] = {
      score: toM365FivePointScore(metric.score ?? 0),
      result: metric.passed ? 'pass' : 'fail',
      threshold: toM365FivePointScore(metric.threshold ?? threshold),
      reason: metric.reason,
      score_0_100: metric.score,
      provider: metric.provider,
      model: metric.model,
      rubricVersion: metric.rubricVersion,
    };
  }
  return Object.keys(scores).length > 0 ? scores : undefined;
}

function normalizeCitations(row: EvalRow): Array<Record<string, unknown>> | undefined {
  if (!row.citations?.length) return undefined;
  return row.citations.map((citation, index) => compactObject({
    index: index + 1,
    text: citation.title,
    source: citation.url ?? citation.sourceLocation,
    raw: citation.raw,
  }));
}

function summarizeStatuses(statuses: EvalStatus[]): EvalStatus {
  if (statuses.some(status => status === 'error')) return 'error';
  if (statuses.every(status => status === 'pass')) return 'pass';
  if (statuses.every(status => status === 'fail')) return 'fail';
  return 'partial';
}

function evaluatorMapNames(map: EvaluatorMap | undefined): EvaluatorName[] {
  if (!map) return [];
  return Object.keys(map).map(name => name === 'Similarity' ? 'Similarity' : name as EvaluatorName);
}

export function normalizeEvaluatorList(names: EvaluatorName[]): EvaluatorName[] {
  const result: EvaluatorName[] = [];
  for (const name of names) {
    const normalized = name === 'SemanticSimilarity' ? 'Similarity' : name;
    if (!result.includes(normalized)) result.push(normalized);
  }
  return result.length > 0 ? result : DEFAULT_M365_EVALUATORS;
}

function toM365FivePointScore(score0To100: number): number {
  if (score0To100 <= 0) return 1;
  return Math.round(Math.max(1, Math.min(5, score0To100 / 20)) * 10) / 10;
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  for (const key of Object.keys(value)) {
    if (value[key] === undefined) delete value[key];
  }
  return value;
}
