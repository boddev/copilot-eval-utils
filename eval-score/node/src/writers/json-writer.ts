import * as fs from 'fs';
import { EvalRow } from '../types';

export async function writeJson(rows: EvalRow[], outputPath: string): Promise<void> {
  const records = rows.map((row) => ({
    prompt: row.prompt,
    expected_answer: row.expectedAnswer,
    source_location: row.sourceLocation,
    actual_answer: row.actualAnswer,
    similarity_score: row.similarityScore ?? null,
    metrics: row.metrics,
    citations: row.citations,
    conversation_id: row.conversationId,
    response_metadata: row.responseMetadata,
    assertions: row.assertions,
    assertion_results: row.assertionResults,
  }));

  fs.writeFileSync(outputPath, JSON.stringify(records, null, 2), 'utf-8');
}
