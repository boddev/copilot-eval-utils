import { DraftedQuestion, Assertion } from './types';

/**
 * Generate simple assertions for a question based on its supporting facts and expected answer.
 * v1 assertion types: must_contain, must_contain_any, must_not_contain
 */
export function generateAssertions(question: DraftedQuestion): Assertion[] {
  const assertions: Assertion[] = [];
  const answer = question.expected_answer ?? '';

  // Extract key values from supporting facts → must_contain assertions
  for (const factStr of question.supporting_facts ?? []) {
    const eqIndex = factStr.indexOf('=');
    if (eqIndex < 0) continue;

    const value = factStr.substring(eqIndex + 1).trim().replace(/^"|"$/g, '');

    // Only assert on values that are meaningful (not too short, not too long)
    if (value.length >= 2 && value.length <= 80 && answer.toLowerCase().includes(value.toLowerCase())) {
      // Use wholeWord matching for short values (<5 chars) to avoid false positives
      const useWholeWord = value.length < 5 && /^[a-zA-Z]+$/.test(value);
      assertions.push({ type: 'must_contain', value, ...(useWholeWord ? { wholeWord: true } : {}) });
    }
  }

  // For edge_case category, generate must_not_contain for the error disclaimer pattern
  if (question.category === 'edge_case') {
    // Edge case questions should NOT get a confident wrong answer
    // This is handled by the eval itself, not assertions
  }

  // Deduplicate assertions by value
  const seen = new Set<string>();
  const unique: Assertion[] = [];
  for (const a of assertions) {
    const key = `${a.type}:${'value' in a ? a.value : ''}`;
    if (!seen.has(key)) {
      seen.add(key);
      unique.push(a);
    }
  }

  // Limit to 5 assertions per question to keep it manageable
  return unique.slice(0, 5);
}

/**
 * Generate assertions for all questions
 */
export function generateAllAssertions(
  questions: DraftedQuestion[],
): Map<number, Assertion[]> {
  const map = new Map<number, Assertion[]>();
  for (let i = 0; i < questions.length; i++) {
    map.set(i, generateAssertions(questions[i]));
  }
  return map;
}
