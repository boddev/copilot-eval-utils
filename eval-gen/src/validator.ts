import * as crypto from 'crypto';
import {
  DraftedQuestion,
  Assertion,
  GeneratedEvalItem,
  ValidationResult,
  QuestionCategory,
  DEFAULT_CATEGORY_WEIGHTS,
} from './types';
import { computeGroundingConfidence } from './answer-grounder';
import { isNearDuplicatePrompt, normalizePrompt } from './dedupe';

/**
 * Deduplicate questions by checking for near-identical prompts.
 * Uses simple normalized string comparison (embedding-based dedup can be added later).
 */
function deduplicateQuestions(items: GeneratedEvalItem[]): {
  deduplicated: GeneratedEvalItem[];
  removedCount: number;
} {
  const seen = new Set<string>();
  const deduplicated: GeneratedEvalItem[] = [];
  let removedCount = 0;

  for (const item of items) {
    const normalized = normalizePrompt(item.prompt);

    // Check for exact or near-duplicate
    let isDuplicate = false;
    for (const existing of seen) {
      if (existing === normalized || isNearDuplicatePrompt(normalized, existing)) {
        isDuplicate = true;
        break;
      }
    }

    if (!isDuplicate) {
      seen.add(normalized);
      deduplicated.push(item);
    } else {
      removedCount++;
    }
  }

  return { deduplicated, removedCount };
}

/**
 * Check category balance against target weights
 */
function checkCategoryBalance(
  items: GeneratedEvalItem[],
): Record<QuestionCategory, number> {
  const counts: Record<string, number> = {};
  for (const item of items) {
    counts[item.category] = (counts[item.category] || 0) + 1;
  }

  const balance: Record<QuestionCategory, number> = {} as Record<QuestionCategory, number>;
  for (const cat of Object.keys(DEFAULT_CATEGORY_WEIGHTS) as QuestionCategory[]) {
    balance[cat] = counts[cat] || 0;
  }
  return balance;
}

/**
 * Compute coverage: fraction of source rows the eval set actually exercises.
 *
 * Counts every row referenced as either a primary `source_location` or as
 * evidence via `referenced_rows` (resolved from supporting_fact_ids). The
 * denominator is bounded by `min(totalRows, items*3)` since each question
 * realistically touches ~1 primary + ~2 supporting rows; this keeps the
 * metric achievable when the eval count is much smaller than the dataset.
 */
function computeCoverage(
  items: GeneratedEvalItem[],
  totalRows: number,
): number {
  if (totalRows <= 0) return 0;
  const referencedRows = new Set<string>();
  for (const item of items) {
    if (item.source_location) referencedRows.add(item.source_location);
    for (const r of item.referenced_rows ?? []) {
      if (r) referencedRows.add(r);
    }
  }
  const realisticTouchBudget = Math.max(items.length * 3, 50);
  const denom = Math.min(totalRows, realisticTouchBudget);
  return denom > 0 ? referencedRows.size / denom : 0;
}

/**
 * Generate a stable ID from prompt + source_location
 */
function generateItemId(prompt: string, sourceLocation: string): string {
  const hash = crypto.createHash('sha256')
    .update(`${prompt}|${sourceLocation}`)
    .digest('hex');
  return hash.slice(0, 12);
}

/**
 * Convert drafted questions + assertions into GeneratedEvalItems
 */
export function buildEvalItems(
  questions: DraftedQuestion[],
  assertionMap: Map<number, Assertion[]>,
): GeneratedEvalItem[] {
  return questions.map((q, i) => {
    const referencedRows = new Set<string>(q.referenced_rows ?? []);
    if (q.source_location) referencedRows.add(q.source_location);
    return {
      id: generateItemId(q.prompt, q.source_location),
      prompt: q.prompt,
      expected_answer: q.expected_answer,
      source_location: q.source_location,
      assertions: assertionMap.get(i) ?? [],
      category: q.category,
      difficulty: q.difficulty,
      supporting_facts: q.supporting_facts ?? [],
      grounding_confidence: computeGroundingConfidence(q),
      referenced_rows: Array.from(referencedRows),
    };
  });
}

/**
 * Validate a generated eval set: dedup, check balance, check coverage
 */
export function validateEvalSet(
  items: GeneratedEvalItem[],
  totalRows: number,
): { validated: GeneratedEvalItem[]; result: ValidationResult } {
  const issues: string[] = [];

  // Deduplicate
  const { deduplicated, removedCount } = deduplicateQuestions(items);
  if (removedCount > 0) {
    issues.push(`Removed ${removedCount} duplicate question(s)`);
  }

  // Category balance
  const categoryBalance = checkCategoryBalance(deduplicated);
  const totalItems = deduplicated.length;
  for (const [cat, weight] of Object.entries(DEFAULT_CATEGORY_WEIGHTS)) {
    const actual = (categoryBalance[cat as QuestionCategory] || 0) / Math.max(1, totalItems);
    const expected = weight;
    if (Math.abs(actual - expected) > 0.15) {
      issues.push(`Category "${cat}" is ${actual < expected ? 'under' : 'over'}-represented (${Math.round(actual * 100)}% vs ${Math.round(expected * 100)}% target)`);
    }
  }

  // Coverage
  const coverageScore = computeCoverage(deduplicated, totalRows);
  const COVERAGE_TARGET = 0.75;
  const CLI_COUNT_CAP = 50; // matches CLI clamp in src/index.ts

  // Compute the actual unique-row count once for richer messaging
  const referencedRows = new Set<string>();
  for (const item of deduplicated) {
    if (item.source_location) referencedRows.add(item.source_location);
    for (const r of item.referenced_rows ?? []) {
      if (r) referencedRows.add(r);
    }
  }
  const uniqueRowsReferenced = referencedRows.size;

  const realisticTouchBudget = deduplicated.length * 3;
  const realisticMaxCoverage = totalRows > 0
    ? Math.min(1.0, realisticTouchBudget / totalRows)
    : 0;
  const recommendedCountForTarget = totalRows > 0
    ? Math.ceil((totalRows * COVERAGE_TARGET) / 3)
    : 0;
  const datasetSampledNotExhaustive = totalRows > 0
    && recommendedCountForTarget > CLI_COUNT_CAP;

  const pct = (v: number) => Math.round(v * 100);

  if (totalRows > 0 && deduplicated.length > 0 && coverageScore < COVERAGE_TARGET) {
    if (realisticMaxCoverage >= COVERAGE_TARGET) {
      // Coverage is achievable with current count — LLM clustered questions
      issues.push(
        `Coverage ${pct(coverageScore)}% (${uniqueRowsReferenced}/${totalRows} rows) is below the ${pct(COVERAGE_TARGET)}% target. ` +
        `The current count (${deduplicated.length}) can reach the target — re-run to encourage broader row spread, or bump --count slightly.`
      );
    } else if (recommendedCountForTarget <= CLI_COUNT_CAP) {
      // Achievable by modestly increasing --count
      issues.push(
        `Coverage ${pct(coverageScore)}% (${uniqueRowsReferenced}/${totalRows} rows). ` +
        `For ≥${pct(COVERAGE_TARGET)}% coverage on this dataset, increase --count from ${deduplicated.length} to ~${recommendedCountForTarget}.`
      );
    } else {
      // Dataset too large for exhaustive coverage — reframe as representative sample
      issues.push(
        `Dataset is large (${totalRows} rows) — exhaustive coverage isn't practical for an eval set. ` +
        `This eval set tests ${uniqueRowsReferenced} representative rows (${pct(coverageScore)}%). ` +
        `For broader testing, generate multiple eval sets with focused --description values targeting different segments of your data ` +
        `(e.g., by category, time period, or status).`
      );
    }
  }

  // Check for low-confidence items
  const lowConfidence = deduplicated.filter(i => i.grounding_confidence === 'low').length;
  if (lowConfidence > totalItems * 0.2) {
    issues.push(`${lowConfidence} question(s) have low grounding confidence`);
  }

  const passed = issues.length === 0 || (removedCount === 0 && coverageScore >= 0.2);

  return {
    validated: deduplicated,
    result: {
      passed,
      totalItems: deduplicated.length,
      duplicatesRemoved: removedCount,
      categoryBalance,
      coverageScore,
      issues,
      uniqueRowsReferenced,
      totalRows,
      realisticMaxCoverage,
      recommendedCountForTarget,
      datasetSampledNotExhaustive,
    },
  };
}
