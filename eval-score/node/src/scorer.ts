import { EvalRow, EvaluatorName, MetricResult, ScoringResult } from './types';
import { evaluateRowAssertions } from './assertion-checker';
import { createJudge, Judge, metricFromJudge } from './judge-providers';
import { WorkIQClient } from './workiq-client';

const DELAY_MS = 500;

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

export async function scoreAnswers(
  rows: EvalRow[],
  client: WorkIQClient,
  options?: {
    tenantId?: string;
    judgeProvider?: 'workiq' | 'github-copilot' | 'azure-openai';
    judge?: Judge;
    evaluators?: EvaluatorName[];
    concurrency?: number;
    delayMs?: number;
    threshold?: number;
    onProgress?: (completed: number, total: number) => void;
    onRowComplete?: (rows: EvalRow[], row: EvalRow, index: number) => void | Promise<void>;
  }
): Promise<EvalRow[]> {
  const total = rows.length;
  const judge = options?.judge ?? createJudge(options?.judgeProvider ?? 'workiq', client, options?.tenantId);
  const evaluators = options?.evaluators ?? ['SemanticSimilarity'];
  const concurrency = Math.max(1, Math.min(options?.concurrency ?? 1, total || 1));
  const delayMs = Math.max(0, options?.delayMs ?? DELAY_MS);
  let nextIndex = 0;
  let completed = rows.filter(row => row.similarityScore !== undefined).length;

  async function processRow(i: number): Promise<void> {
    const row = rows[i];

    if (row.similarityScore !== undefined) {
      return;
    }

    if (!row.actualAnswer || row.actualAnswer.startsWith('[ERROR:')) {
      row.similarityScore = 0;
      row.metrics = mergeMetrics(row.metrics, [{
        name: 'SemanticSimilarity',
        score: 0,
        passed: false,
        reason: !row.actualAnswer ? 'Actual answer is empty.' : 'Actual answer contains an error.',
        provider: judge.provider,
        model: judge.model,
        scale: '0-100',
        threshold: options?.threshold,
      }]);
      completed++;
      options?.onProgress?.(i + 1, total);
      process.stderr.write(`\rScoring answer ${i + 1}/${total}...`);
      await options?.onRowComplete?.(rows, row, i);
      return;
    }

    process.stderr.write(`\rScoring answer ${i + 1}/${total}...`);

    try {
      const score = await judge.score(row);
      row.similarityScore = score.score;
      const metrics: MetricResult[] = [metricFromJudge(score, judge, options?.threshold)];
      metrics.push(...evaluateDeterministicMetrics(row, evaluators, options?.threshold));
      row.metrics = mergeMetrics(row.metrics, metrics);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      process.stderr.write(`\nWarning: Scoring failed for row ${i + 1}: ${message}, setting to 0\n`);
      row.similarityScore = 0;
      row.metrics = mergeMetrics(row.metrics, [{
        name: 'SemanticSimilarity',
        score: 0,
        passed: false,
        reason: message,
        provider: judge.provider,
        model: judge.model,
        scale: '0-100',
        threshold: options?.threshold,
      }]);
    }

    completed++;
    options?.onProgress?.(completed, total);
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

export function calculateScoringResult(rows: EvalRow[], passThreshold: number): ScoringResult {
  const scores = rows.map(row => row.similarityScore ?? 0);
  const totalQuestions = scores.length;
  const sum = scores.reduce((acc, s) => acc + s, 0);
  const averageScore = totalQuestions > 0 ? Math.round((sum / totalQuestions) * 10) / 10 : 0;
  const minScore = totalQuestions > 0 ? Math.min(...scores) : 0;
  const maxScore = totalQuestions > 0 ? Math.max(...scores) : 0;
  const passCount = scores.filter(s => s >= passThreshold).length;
  const failCount = totalQuestions - passCount;

  // Assertion statistics
  let totalAssertions = 0;
  let assertionsPassed = 0;
  let assertionsFailed = 0;
  for (const row of rows) {
    if (row.assertionResults) {
      totalAssertions += row.assertionResults.length;
      assertionsPassed += row.assertionResults.filter(r => r.passed).length;
      assertionsFailed += row.assertionResults.filter(r => !r.passed).length;
    }
  }

  return {
    totalQuestions,
    averageScore,
    minScore,
    maxScore,
    passCount,
    failCount,
    passThreshold,
    totalAssertions,
    assertionsPassed,
    assertionsFailed,
  };
}

export function parseEvaluators(value?: string): EvaluatorName[] {
  if (!value) return ['SemanticSimilarity'];
  const all: EvaluatorName[] = [
    'SemanticSimilarity',
    'Relevance',
    'Coherence',
    'Groundedness',
    'Citations',
    'ExactMatch',
    'PartialMatch',
    'EvalGenAssertions',
  ];
  const aliases = new Map<string, EvaluatorName>(all.map(name => [name.toLowerCase(), name]));
  const parts = value.split(',').map(part => part.trim()).filter(Boolean);
  const names: EvaluatorName[] = [];
  for (const part of parts) {
    if (part.toLowerCase() === 'all') return all;
    const name = aliases.get(part.toLowerCase());
    if (!name) {
      throw new Error(`Unsupported evaluator "${part}". Supported evaluators: ${all.join(', ')}, all`);
    }
    if (!names.includes(name)) names.push(name);
  }
  return names.length > 0 ? names : ['SemanticSimilarity'];
}

function evaluateDeterministicMetrics(row: EvalRow, evaluators: EvaluatorName[], threshold?: number): MetricResult[] {
  const metrics: MetricResult[] = [];
  const actual = normalize(row.actualAnswer);
  const expected = normalize(row.expectedAnswer);

  if (evaluators.includes('ExactMatch')) {
    const passed = actual === expected;
    metrics.push({
      name: 'ExactMatch',
      score: passed ? 100 : 0,
      passed,
      reason: passed ? 'Actual answer exactly matches expected answer after normalization.' : 'Actual answer does not exactly match expected answer.',
      provider: 'deterministic',
      scale: '0-100',
      threshold,
    });
  }

  if (evaluators.includes('PartialMatch')) {
    const passed = expected.length > 0 && (actual.includes(expected) || expected.includes(actual));
    metrics.push({
      name: 'PartialMatch',
      score: passed ? 100 : 0,
      passed,
      reason: passed ? 'Actual and expected answers partially overlap.' : 'Actual and expected answers do not partially overlap.',
      provider: 'deterministic',
      scale: '0-100',
      threshold,
    });
  }

  if (evaluators.includes('Citations')) {
    const hasCitation = Boolean(row.citations?.length) ||
      (row.sourceLocation ? row.actualAnswer.toLowerCase().includes(row.sourceLocation.toLowerCase()) : false);
    metrics.push({
      name: 'Citations',
      score: hasCitation ? 100 : 0,
      passed: hasCitation,
      reason: hasCitation ? 'At least one citation/source reference was detected.' : 'No citation/source reference was detected.',
      provider: 'deterministic',
      scale: '0-100',
      threshold,
    });
  }

  if (evaluators.includes('EvalGenAssertions') && row.assertions?.length) {
    row.assertionResults = evaluateRowAssertions(row);
    const passedCount = row.assertionResults.filter(result => result.passed).length;
    const score = Math.round((passedCount / row.assertionResults.length) * 100);
    metrics.push({
      name: 'EvalGenAssertions',
      score,
      passed: passedCount === row.assertionResults.length,
      reason: `${passedCount}/${row.assertionResults.length} assertions passed.`,
      provider: 'deterministic',
      scale: '0-100',
      threshold,
    });
  }

  return metrics;
}

function mergeMetrics(existing: MetricResult[] | undefined, next: MetricResult[]): MetricResult[] {
  const merged = new Map<EvaluatorName, MetricResult>();
  for (const metric of existing ?? []) merged.set(metric.name, metric);
  for (const metric of next) merged.set(metric.name, metric);
  return Array.from(merged.values());
}

function normalize(value: string): string {
  return value.trim().replace(/\s+/g, ' ').toLowerCase();
}
