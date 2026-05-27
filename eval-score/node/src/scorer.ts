import { EvalRow, EvaluatorName, MetricResult, ScoringResult } from './types';
import { evaluateRowAssertions } from './assertion-checker';
import { createJudge, Judge, metricFromJudge } from './judge-providers';
import { WorkIQClient } from './workiq-client';
import { DEFAULT_M365_EVALUATORS, deriveRowStatus, resolveRowEvaluators, setRowError } from './eval-document';
import { createThrottleGate } from './throttle-gate';

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
    fallbackJudgeProvider?: 'github-copilot' | 'azure-openai' | 'none';
    judge?: Judge;
    fallbackJudge?: Judge;
    judgeAgentId?: string;
    evaluators?: EvaluatorName[];
    concurrency?: number;
    delayMs?: number;
    threshold?: number;
    onProgress?: (completed: number, total: number) => void;
    onRowComplete?: (rows: EvalRow[], row: EvalRow, index: number) => void | Promise<void>;
  }
): Promise<EvalRow[]> {
  const total = rows.length;
  const judge = options?.judge ?? createJudge(options?.judgeProvider ?? 'workiq', client, options?.tenantId, options?.judgeAgentId);
  const fallbackJudge = options?.fallbackJudge ?? createDefaultFallbackJudge(judge, client, options?.tenantId, options?.fallbackJudgeProvider);
  const evaluators = options?.evaluators ?? DEFAULT_M365_EVALUATORS;
  const concurrency = Math.max(1, Math.min(options?.concurrency ?? 1, total || 1));
  const delayMs = Math.max(0, options?.delayMs ?? DELAY_MS);
  const throttleGate = createThrottleGate(concurrency);
  let nextIndex = 0;
  let completed = rows.filter(row => row.similarityScore !== undefined).length;

  async function processRow(i: number): Promise<void> {
    const row = rows[i];

    if (row.similarityScore !== undefined) {
      return;
    }

    if (!row.actualAnswer || row.actualAnswer.startsWith('[ERROR:')) {
      row.similarityScore = 0;
      const effectiveEvaluators = resolveRowEvaluators(row, evaluators);
      const primary = firstLlmEvaluator(effectiveEvaluators) ?? 'Similarity';
      row.metrics = mergeMetrics(row.metrics, [{
        name: primary,
        score: 0,
        passed: false,
        reason: !row.actualAnswer ? 'Actual answer is empty.' : 'Actual answer contains an error.',
        provider: judge.provider,
        model: judge.model,
        scale: '0-100',
        threshold: options?.threshold,
      }]);
      setRowError(row, row.actualAnswer ? 'agentRequestFailed' : 'turnSkipped', !row.actualAnswer ? 'Actual answer is empty.' : row.actualAnswer);
      completed++;
      options?.onProgress?.(i + 1, total);
      process.stderr.write(`\rScoring answer ${i + 1}/${total}...`);
      await options?.onRowComplete?.(rows, row, i);
      return;
    }

    process.stderr.write(`\rScoring answer ${i + 1}/${total}...`);

    try {
      const effectiveEvaluators = resolveRowEvaluators(row, evaluators);
      const metrics: MetricResult[] = [];
      const llmEvaluators = effectiveEvaluators.filter(isLlmEvaluator);
      for (const evaluator of llmEvaluators) {
        const scored = await throttleGate.run(() => scoreWithFallback(judge, fallbackJudge, row, evaluator));
        metrics.push(metricFromJudge(scored.score, scored.judge, options?.threshold, evaluator));
      }
      metrics.push(...evaluateDeterministicMetrics(row, effectiveEvaluators, options?.threshold));
      const primaryMetric = metrics.find(metric => metric.name === 'Similarity') ??
        metrics.find(metric => metric.name === 'SemanticSimilarity') ??
        metrics[0];
      row.metrics = mergeMetrics(row.metrics, metrics);
      if (primaryMetric) {
        row.similarityScore = primaryMetric.score ?? 0;
        row.status = deriveRowStatus(row, options?.threshold);
      } else {
        row.similarityScore = undefined;
        row.status = undefined;
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      process.stderr.write(`\nWarning: Scoring failed for row ${i + 1}: ${message}, setting to 0\n`);
      row.similarityScore = 0;
      row.metrics = mergeMetrics(row.metrics, [{
        name: firstLlmEvaluator(resolveRowEvaluators(row, evaluators)) ?? 'Similarity',
        score: 0,
        passed: false,
        reason: message,
        provider: judge.provider,
        model: judge.model,
        scale: '0-100',
        threshold: options?.threshold,
      }]);
      row.status = deriveRowStatus(row, options?.threshold);
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

function createDefaultFallbackJudge(
  primary: Judge,
  client: WorkIQClient,
  tenantId?: string,
  configuredProvider?: 'github-copilot' | 'azure-openai' | 'none',
): Judge | undefined {
  if (primary.provider !== 'workiq') return undefined;
  const disabled = process.env.EVALSCORE_DISABLE_GITHUB_FALLBACK?.toLowerCase();
  if (configuredProvider === 'none' || disabled === '1' || disabled === 'true' || disabled === 'yes') return undefined;
  const envProvider = process.env.EVALSCORE_FALLBACK_JUDGE_PROVIDER as 'github-copilot' | 'azure-openai' | 'none' | undefined;
  const provider = configuredProvider ?? (envProvider === 'azure-openai' || envProvider === 'github-copilot' ? envProvider : 'github-copilot');
  return createJudge(provider, client, tenantId);
}

async function scoreWithFallback(
  judge: Judge,
  fallbackJudge: Judge | undefined,
  row: EvalRow,
  evaluator: EvaluatorName,
): Promise<{ score: Awaited<ReturnType<Judge['score']>>; judge: Judge }> {
  try {
    return { score: await judge.score(row, evaluator), judge };
  } catch (err) {
    if (!fallbackJudge || !isFallbackEligible(err)) throw err;
    const message = err instanceof Error ? err.message : String(err);
    process.stderr.write(`\nWarning: ${judge.provider} judge failed for ${evaluator}; falling back to ${fallbackJudge.provider}: ${message}\n`);
    const fallbackScore = await fallbackJudge.score(row, evaluator);
    return {
      score: {
        ...fallbackScore,
        reason: fallbackScore.reason
          ? `Fallback from ${judge.provider} due to: ${message}. ${fallbackScore.reason}`
          : `Fallback from ${judge.provider} due to: ${message}.`,
      },
      judge: fallbackJudge,
    };
  }
}

function isFallbackEligible(err: unknown): boolean {
  const message = err instanceof Error ? err.message.toLowerCase() : String(err).toLowerCase();
  return (
    message.includes('timed out') ||
    message.includes('timeout') ||
    message.includes('could not parse score') ||
    message.includes('ask_work_iq') ||
    message.includes('mcp') ||
    message.includes('429') ||
    message.includes('rate limit') ||
    message.includes('throttl') ||
    message.includes('temporarily unavailable') ||
    message.includes('503') ||
    message.includes('502') ||
    message.includes('504')
  );
}

export function calculateScoringResult(rows: EvalRow[], passThreshold: number): ScoringResult {
  const scores = rows
    .map(row => row.similarityScore)
    .filter((score): score is number => score !== undefined);
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
  if (!value) return DEFAULT_M365_EVALUATORS;
  const all: EvaluatorName[] = [
    'Similarity',
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
  aliases.set('semantic', 'Similarity');
  aliases.set('semanticsimilarity', 'Similarity');
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
  return names.length > 0 ? names : DEFAULT_M365_EVALUATORS;
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

function isLlmEvaluator(evaluator: EvaluatorName): boolean {
  return evaluator === 'Similarity' ||
    evaluator === 'SemanticSimilarity' ||
    evaluator === 'Relevance' ||
    evaluator === 'Coherence' ||
    evaluator === 'Groundedness';
}

function firstLlmEvaluator(evaluators: EvaluatorName[]): EvaluatorName | undefined {
  return evaluators.find(isLlmEvaluator);
}
