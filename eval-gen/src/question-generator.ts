import {
  DatasetProfile,
  Fact,
  QuestionIntent,
  DraftedQuestion,
  QuestionCategory,
  DEFAULT_CATEGORY_WEIGHTS,
  LLMClient,
} from './types';
import { summarizeFacts, groupFactsByRecord } from './fact-extractor';

/**
 * Generate question intents using LLM analysis of the dataset profile and facts.
 * Stage A: intent generation (what kinds of questions to ask)
 */
export async function generateIntents(
  profile: DatasetProfile,
  facts: Fact[],
  description: string,
  count: number,
  client: LLMClient,
): Promise<QuestionIntent[]> {
  const factSummary = summarizeFacts(facts, 15);
  const schemaDescription = profile.columns
    .map(c => `${c.name} (${c.dataType}, ${c.uniqueCount} unique, ${c.nullCount} nulls${c.valueCounts ? `, categories: ${Object.keys(c.valueCounts).slice(0, 5).join(', ')}` : ''})`)
    .join('\n  ');

  // Calculate per-category counts
  const categoryTargets = Object.entries(DEFAULT_CATEGORY_WEIGHTS)
    .map(([cat, weight]) => `${cat}: ${Math.max(1, Math.round(count * weight))}`)
    .join(', ');

  const prompt = `Analyze this dataset and generate exactly ${count} question intents for evaluating a Microsoft 365 Copilot connector.

## Dataset Description
${description}

## Dataset Schema (${profile.rowCount} rows)
  ${schemaDescription}

## Sample Records
${factSummary}

## Question Category Targets
${categoryTargets}

## Instructions
Generate question intents that a knowledge worker would naturally ask Copilot about this data.
Each intent should specify:
- "intent": what the question is about (brief description)
- "category": one of [single_record_lookup, attribute_retrieval, filtered_find, temporal, comparison, edge_case]
- "difficulty": easy, medium, or hard
- "target_fields": which columns/fields the question targets
- "target_row_references": which specific rows to reference (use format "${profile.fileName}:row N")

Rules:
- Questions must be answerable from the actual data shown above
- Use natural language as a knowledge worker would type into Copilot
- Avoid exact-count aggregation questions (Copilot connectors don't reliably support these)
- Reference specific entities/values from the sample data
- Spread questions across different records and fields
- Include a few edge cases (asking about values that don't exist, ambiguous queries)

Respond with JSON: {"intents": [...]}`;

  const result = await client.generateStructured<{ intents: QuestionIntent[] }>(
    prompt,
    'Respond with a JSON object containing an "intents" array of question intent objects.',
  );

  return result.intents ?? [];
}

/**
 * Draft full natural-language questions from intents, grounding each in specific facts.
 * Stage B: question drafting with answer grounding
 */
export async function draftQuestions(
  intents: QuestionIntent[],
  facts: Fact[],
  records: Record<string, unknown>[],
  profile: DatasetProfile,
  description: string,
  client: LLMClient,
): Promise<DraftedQuestion[]> {
  const grouped = groupFactsByRecord(facts);

  // Build context: for each intent, find relevant facts
  const intentsWithContext = intents.map(intent => {
    const relevantFacts: Fact[] = [];

    // Match by row reference
    for (const rowRef of intent.target_row_references) {
      const rowFacts = grouped.get(rowRef);
      if (rowFacts) relevantFacts.push(...rowFacts);
    }

    // Match by field name
    if (relevantFacts.length === 0) {
      for (const field of intent.target_fields) {
        const matching = facts.filter(f => f.field === field);
        relevantFacts.push(...matching.slice(0, 5));
      }
    }

    return { intent, facts: relevantFacts };
  });

  const contextBlock = intentsWithContext.map((item, i) => {
    const factLines = item.facts
      .map(f => `  ${f.field}=${JSON.stringify(f.value)} [${f.rowReference}]`)
      .join('\n');
    return `Intent ${i + 1}: ${item.intent.intent} (${item.intent.category}, ${item.intent.difficulty})
Target fields: ${item.intent.target_fields.join(', ')}
Available facts:
${factLines}`;
  }).join('\n\n');

  const prompt = `Draft natural-language questions with expected answers for each intent below.

## Dataset Description
${description}

## Intents with Available Facts
${contextBlock}

## Instructions
For each intent, produce:
- "prompt": A natural-language question as a knowledge worker would type into Copilot
- "category": Same category as the intent
- "difficulty": Same difficulty as the intent
- "expected_answer": The correct answer derived ONLY from the facts shown. Write a concise, natural response.
- "supporting_facts": Array of "field=value" strings that ground the answer
- "source_location": The row reference where the primary answer data is found

Rules:
- The expected_answer MUST be derivable from the facts provided — do not invent data
- Write questions in natural language (not SQL-like or technical)
- Expected answers should be concise but complete
- Include the source_location referencing the specific row

Respond with JSON: {"questions": [...]}`;

  const result = await client.generateStructured<{ questions: DraftedQuestion[] }>(
    prompt,
    'Respond with a JSON object containing a "questions" array of drafted question objects.',
  );

  // Attach fact references
  const questions = result.questions ?? [];
  for (let i = 0; i < questions.length && i < intentsWithContext.length; i++) {
    questions[i].referenced_facts = intentsWithContext[i].facts;
  }

  return questions;
}
