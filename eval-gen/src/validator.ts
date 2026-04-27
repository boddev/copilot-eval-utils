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
    const normalized = item.prompt
      .toLowerCase()
      .replace(/[^a-z0-9\s]/g, '')
      .replace(/\s+/g, ' ')
      .trim();

    // Check for exact or near-duplicate
    let isDuplicate = false;
    for (const existing of seen) {
      if (existing === normalized) {
        isDuplicate = true;
        break;
      }
      // Simple substring overlap check
      const shorter = normalized.length < existing.length ? normalized : existing;
      const longer = normalized.length < existing.length ? existing : normalized;
      if (longer.includes(shorter) && shorter.length / longer.length > 0.8) {
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
 * Compute coverage: what fraction of unique source locations are referenced
 */
function computeCoverage(
  items: GeneratedEvalItem[],
  totalRows: number,
): number {
  const referencedRows = new Set(items.map(i => i.source_location));
  return totalRows > 0 ? referencedRows.size / Math.min(totalRows, items.length * 2) : 0;
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
  return questions.map((q, i) => ({
    id: generateItemId(q.prompt, q.source_location),
    prompt: q.prompt,
    expected_answer: q.expected_answer,
    source_location: q.source_location,
    assertions: assertionMap.get(i) ?? [],
    category: q.category,
    difficulty: q.difficulty,
    supporting_facts: q.supporting_facts ?? [],
    grounding_confidence: computeGroundingConfidence(q),
  }));
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
  if (coverageScore < 0.3) {
    issues.push(`Low source coverage (${Math.round(coverageScore * 100)}%) — questions reference too few records`);
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
    },
  };
}
