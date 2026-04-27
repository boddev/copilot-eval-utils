import { GeneratedEvalItem, ValidationResult } from './types';

/**
 * Format the generated eval set as a human-readable review document.
 * Outputs markdown that can be opened in any text editor or rendered in a browser.
 */
export function formatReview(
  items: GeneratedEvalItem[],
  validation: ValidationResult,
  description: string,
  sourceFile: string,
): string {
  const lines: string[] = [];

  lines.push('# EvalGen Review');
  lines.push('');
  lines.push(`**Source:** ${sourceFile}`);
  lines.push(`**Description:** ${description}`);
  lines.push(`**Generated:** ${new Date().toISOString()}`);
  lines.push(`**Total questions:** ${validation.totalItems}`);
  lines.push(`**Duplicates removed:** ${validation.duplicatesRemoved}`);
  lines.push(`**Coverage score:** ${Math.round(validation.coverageScore * 100)}%`);
  lines.push('');

  // Category distribution
  lines.push('## Category Distribution');
  lines.push('');
  lines.push('| Category | Count |');
  lines.push('|----------|-------|');
  for (const [cat, count] of Object.entries(validation.categoryBalance)) {
    lines.push(`| ${cat} | ${count} |`);
  }
  lines.push('');

  // Validation issues
  if (validation.issues.length > 0) {
    lines.push('## Validation Notes');
    lines.push('');
    for (const issue of validation.issues) {
      lines.push(`- ⚠️ ${issue}`);
    }
    lines.push('');
  }

  // Questions detail
  lines.push('## Generated Questions');
  lines.push('');

  for (let i = 0; i < items.length; i++) {
    const item = items[i];
    lines.push(`### Q${i + 1}: ${item.prompt}`);
    lines.push('');
    lines.push(`- **Category:** ${item.category}`);
    lines.push(`- **Difficulty:** ${item.difficulty}`);
    lines.push(`- **Confidence:** ${item.grounding_confidence}`);
    lines.push(`- **Source:** ${item.source_location}`);
    lines.push(`- **Expected Answer:** ${item.expected_answer}`);

    if (item.assertions.length > 0) {
      lines.push(`- **Assertions:**`);
      for (const a of item.assertions) {
        if (a.type === 'must_contain') {
          lines.push(`  - ✅ Must contain: "${a.value}"`);
        } else if (a.type === 'must_contain_any') {
          lines.push(`  - ✅ Must contain any: ${a.values.map(v => `"${v}"`).join(', ')}`);
        } else if (a.type === 'must_not_contain') {
          lines.push(`  - ❌ Must NOT contain: "${a.value}"`);
        }
      }
    }

    if (item.supporting_facts.length > 0) {
      lines.push(`- **Supporting Facts:** ${item.supporting_facts.join('; ')}`);
    }
    lines.push('');
  }

  return lines.join('\n');
}
