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
 *
 * If `assignedRows` is provided, each intent slot is pre-assigned a primary row
 * reference. The LLM is told to use the assigned row as its primary target so
 * questions deterministically spread across the row pool instead of clustering.
 */
export async function generateIntents(
  profile: DatasetProfile,
  facts: Fact[],
  description: string,
  count: number,
  client: LLMClient,
  assignedRows?: string[],
): Promise<QuestionIntent[]> {
  // Show the LLM enough distinct records to spread questions across rows.
  // Scale with requested count (≈2x) and floor at 60 to ensure breadth.
  const sampleRecordCount = Math.min(100, Math.max(60, count * 2));
  const factSummary = summarizeFacts(facts, sampleRecordCount);
  const schemaDescription = profile.columns
    .map(c => `${c.name} (${c.dataType}, ${c.uniqueCount} unique, ${c.nullCount} nulls${c.valueCounts ? `, categories: ${Object.keys(c.valueCounts).slice(0, 5).join(', ')}` : ''})`)
    .join('\n  ');

  // Calculate per-category counts
  const categoryTargets = Object.entries(DEFAULT_CATEGORY_WEIGHTS)
    .map(([cat, weight]) => `${cat}: ${Math.max(1, Math.round(count * weight))}`)
    .join(', ');

  // Pre-assigned row block (optional). When provided, each intent slot gets a
  // distinct row reference so the LLM cannot cluster questions on a few rows.
  let assignmentBlock = '';
  if (assignedRows && assignedRows.length > 0) {
    const slots = assignedRows.slice(0, count).map((row, i) => `  ${i + 1}. ${row}`).join('\n');
    assignmentBlock = `

## Pre-assigned Primary Rows (REQUIRED)
Generate exactly ${assignedRows.length} intents. Each intent slot has a pre-assigned primary row:
${slots}

For intent N, target_row_references MUST start with the assigned row for slot N.
You MAY add additional rows to target_row_references when the category is "comparison" or "filtered_find" and a second row genuinely improves the question.
DO NOT use the same primary row for two different intents.`;
  }

  const prompt = `Analyze this dataset and generate exactly ${count} question intents for evaluating a Microsoft 365 Copilot connector.

## Dataset Description
${description}

## Dataset Schema (${profile.rowCount} rows)
  ${schemaDescription}

## Sample Records (with [f-N] fact IDs)
${factSummary}

## Question Category Targets
${categoryTargets}${assignmentBlock}

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

  const intents = result.intents ?? [];

  // Carry the pre-assigned row through onto each intent for downstream stages.
  // If the LLM dropped the assigned row from target_row_references, prepend it.
  if (assignedRows && assignedRows.length > 0) {
    for (let i = 0; i < intents.length && i < assignedRows.length; i++) {
      const assigned = assignedRows[i];
      intents[i].assigned_primary_row = assigned;
      const refs = intents[i].target_row_references ?? [];
      if (!refs.includes(assigned)) {
        intents[i].target_row_references = [assigned, ...refs];
      }
    }
  }

  return intents;
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
  const factById = new Map(facts.map(f => [f.id, f]));

  // Build context: for each intent, find relevant facts
  const intentsWithContext = intents.map(intent => {
    const relevantFacts: Fact[] = [];
    const seenFactIds = new Set<string>();

    const addFact = (f: Fact) => {
      if (!seenFactIds.has(f.id)) {
        relevantFacts.push(f);
        seenFactIds.add(f.id);
      }
    };

    // Match by row reference (primary path)
    for (const rowRef of intent.target_row_references) {
      const rowFacts = grouped.get(rowRef);
      if (rowFacts) rowFacts.forEach(addFact);
    }

    // Match by field name (fallback when no row facts found)
    if (relevantFacts.length === 0) {
      for (const field of intent.target_fields) {
        const matching = facts.filter(f => f.field === field);
        matching.slice(0, 5).forEach(addFact);
      }
    }

    return { intent, facts: relevantFacts };
  });

  const contextBlock = intentsWithContext.map((item, i) => {
    const factLines = item.facts
      .map(f => `  [${f.id}] ${f.field}=${JSON.stringify(f.value)} [${f.rowReference}]`)
      .join('\n');
    const assigned = item.intent.assigned_primary_row
      ? `\nAssigned primary row: ${item.intent.assigned_primary_row}`
      : '';
    return `Intent ${i + 1}: ${item.intent.intent} (${item.intent.category}, ${item.intent.difficulty})
Target fields: ${item.intent.target_fields.join(', ')}${assigned}
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
- "supporting_fact_ids": Array of fact IDs (e.g., "f-3", "f-7") from the "Available facts" block that the answer is grounded in. Include only facts you actually used.
- "source_location": The row reference where the primary answer data is found (use the assigned primary row when one was specified)

Rules:
- The expected_answer MUST be derivable from the facts provided — do not invent data
- Write questions in natural language (not SQL-like or technical)
- Expected answers should be concise but complete
- supporting_fact_ids must reference IDs that appeared in the Available facts block for this intent
- When an "Assigned primary row" is specified, source_location MUST equal that row

Respond with JSON: {"questions": [...]}`;

  const result = await client.generateStructured<{ questions: (DraftedQuestion & { supporting_fact_ids?: string[] })[] }>(
    prompt,
    'Respond with a JSON object containing a "questions" array of drafted question objects.',
  );

  const questions = result.questions ?? [];

  // Resolve referenced_rows from supporting_fact_ids (preferred path) or
  // fall back to back-matching supporting_facts strings against the intent's
  // candidate fact pool.
  for (let i = 0; i < questions.length && i < intentsWithContext.length; i++) {
    const q = questions[i];
    const ctx = intentsWithContext[i];
    q.referenced_facts = ctx.facts;
    q.assigned_primary_row = ctx.intent.assigned_primary_row;

    const rowSet = new Set<string>();
    if (q.source_location) rowSet.add(q.source_location);

    const cited = Array.isArray(q.supporting_fact_ids) ? q.supporting_fact_ids : [];
    if (cited.length > 0) {
      for (const id of cited) {
        const f = factById.get(id);
        if (f) rowSet.add(f.rowReference);
      }
    } else {
      // Fallback: back-match supporting_facts ("field=value") against this
      // intent's candidate fact pool. Only count rows when the (field, value)
      // pair uniquely identifies a fact in the pool to avoid false positives.
      const pool = ctx.facts;
      for (const factStr of q.supporting_facts ?? []) {
        const eqIndex = factStr.indexOf('=');
        if (eqIndex < 0) continue;
        const field = factStr.substring(0, eqIndex).trim();
        const rawValue = factStr.substring(eqIndex + 1).trim().replace(/^"|"$/g, '');
        const matches = pool.filter(f =>
          f.field === field && String(f.value).replace(/^"|"$/g, '') === rawValue
        );
        if (matches.length === 1) {
          rowSet.add(matches[0].rowReference);
        }
      }
    }

    q.referenced_rows = Array.from(rowSet);
  }

  return questions;
}
