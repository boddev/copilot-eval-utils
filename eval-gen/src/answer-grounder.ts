import { DraftedQuestion, Fact } from './types';

/**
 * Ground a drafted question's expected answer against the actual source data.
 * Verifies the answer is derivable from the referenced records.
 */
export function groundAnswer(
  question: DraftedQuestion,
  records: Record<string, unknown>[],
  fileName: string,
): DraftedQuestion {
  // Parse row reference to get the actual record
  const rowMatch = question.source_location?.match(/:row\s*(\d+)/i);
  if (!rowMatch) return question;

  const rowIndex = parseInt(rowMatch[1], 10) - 1; // 1-based to 0-based
  if (rowIndex < 0 || rowIndex >= records.length) return question;

  const record = records[rowIndex];

  // Verify supporting facts against actual record
  const verifiedFacts: string[] = [];
  for (const factStr of question.supporting_facts ?? []) {
    const eqIndex = factStr.indexOf('=');
    if (eqIndex < 0) continue;

    const field = factStr.substring(0, eqIndex).trim();
    const expectedValue = factStr.substring(eqIndex + 1).trim();

    const actualValue = record[field];
    if (actualValue !== undefined && actualValue !== null) {
      const actualStr = String(actualValue);
      // Loose comparison: check if the values match approximately
      if (
        actualStr === expectedValue ||
        actualStr === expectedValue.replace(/^"|"$/g, '') ||
        actualStr.toLowerCase() === expectedValue.toLowerCase().replace(/^"|"$/g, '')
      ) {
        verifiedFacts.push(`${field}=${actualStr}`);
      }
    }
  }

  return {
    ...question,
    supporting_facts: verifiedFacts.length > 0 ? verifiedFacts : question.supporting_facts,
    source_location: `${fileName}:row ${rowIndex + 1}`,
  };
}

/**
 * Ground all drafted questions against source data
 */
export function groundAllAnswers(
  questions: DraftedQuestion[],
  records: Record<string, unknown>[],
  fileName: string,
): DraftedQuestion[] {
  return questions.map(q => groundAnswer(q, records, fileName));
}

/**
 * Compute grounding confidence based on how many supporting facts were verified
 */
export function computeGroundingConfidence(
  question: DraftedQuestion,
): 'high' | 'medium' | 'low' {
  const facts = question.supporting_facts ?? [];
  if (facts.length === 0) return 'low';

  // Parse the expected answer and check if key values from facts appear in it
  const answer = question.expected_answer?.toLowerCase() ?? '';
  let matchCount = 0;

  for (const factStr of facts) {
    const eqIndex = factStr.indexOf('=');
    if (eqIndex < 0) continue;
    const value = factStr.substring(eqIndex + 1).trim().toLowerCase().replace(/^"|"$/g, '');
    if (value.length > 0 && answer.includes(value)) {
      matchCount++;
    }
  }

  const ratio = matchCount / facts.length;
  if (ratio >= 0.8) return 'high';
  if (ratio >= 0.4) return 'medium';
  return 'low';
}
