import { EvalRow, ScoringResult } from './types';
import { WorkIQClient } from './workiq-client';

const DELAY_MS = 500;

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

export async function scoreAnswers(
  rows: EvalRow[],
  client: WorkIQClient,
  options?: { tenantId?: string; onProgress?: (completed: number, total: number) => void }
): Promise<EvalRow[]> {
  const total = rows.length;

  for (let i = 0; i < total; i++) {
    const row = rows[i];

    if (row.similarityScore !== undefined) {
      continue;
    }

    if (!row.actualAnswer || row.actualAnswer.startsWith('[ERROR:')) {
      row.similarityScore = 0;
      options?.onProgress?.(i + 1, total);
      process.stderr.write(`\rScoring answer ${i + 1}/${total}...`);
      continue;
    }

    process.stderr.write(`\rScoring answer ${i + 1}/${total}...`);

    const scoringPrompt =
      `Compare the following two answers for semantic similarity. Consider whether they convey the same meaning and information, even if worded differently. Rate the similarity on a scale from 0 to 100, where 0 means completely different and 100 means identical in meaning. Respond with ONLY a single number between 0 and 100, nothing else.\n\nExpected Answer: ${row.expectedAnswer}\n\nActual Answer: ${row.actualAnswer}`;

    try {
      const response = await client.ask(scoringPrompt, options?.tenantId);
      const match = response.match(/\d+/);
      if (match) {
        const parsed = parseInt(match[0], 10);
        row.similarityScore = Math.max(0, Math.min(100, parsed));
      } else {
        process.stderr.write(`\nWarning: Could not parse score from response for row ${i + 1}, setting to 0\n`);
        row.similarityScore = 0;
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      process.stderr.write(`\nWarning: Scoring failed for row ${i + 1}: ${message}, setting to 0\n`);
      row.similarityScore = 0;
    }

    options?.onProgress?.(i + 1, total);

    if (i < total - 1) {
      await delay(DELAY_MS);
    }
  }

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
