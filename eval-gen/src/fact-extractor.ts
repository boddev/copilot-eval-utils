import { Fact, DatasetProfile, ColumnProfile } from './types';

export interface ExtractFactsOptions {
  /** Hard cap on total facts emitted (default 200) */
  maxFacts?: number;
  /** Target distinct records to sample (default scales with maxFacts; floor 50) */
  targetRecords?: number;
  /** Cap facts emitted per record (default unlimited) */
  maxFactsPerRecord?: number;
}

/**
 * Extract atomic facts from dataset records using stratified sampling.
 * Selects diverse records covering common values, rare values, extremes, and nulls.
 *
 * Accepts either a number (legacy `maxFacts`) or an options object so that
 * `targetRecords` can scale independently — wide-schema datasets otherwise
 * exhaust `maxFacts` after only a handful of rows.
 */
export function extractFacts(
  records: Record<string, unknown>[],
  profile: DatasetProfile,
  optionsOrMaxFacts: number | ExtractFactsOptions = 200,
): Fact[] {
  const opts: ExtractFactsOptions =
    typeof optionsOrMaxFacts === 'number' ? { maxFacts: optionsOrMaxFacts } : optionsOrMaxFacts;
  const maxFacts = opts.maxFacts ?? 200;
  const explicitTargetRecords = opts.targetRecords;
  const maxFactsPerRecord = opts.maxFactsPerRecord ?? Number.POSITIVE_INFINITY;

  const facts: Fact[] = [];
  const selectedIndices = selectStratifiedIndices(
    records,
    profile,
    maxFacts,
    explicitTargetRecords,
  );

  let factId = 0;
  for (const rowIndex of selectedIndices) {
    const record = records[rowIndex];
    const fileLabel = record._source_file ? String(record._source_file) : profile.fileName;
    const rowRef = `${fileLabel}:row ${rowIndex + 1}`;

    let factsForThisRecord = 0;
    for (const col of profile.columns) {
      if (factsForThisRecord >= maxFactsPerRecord) break;
      const value = record[col.name];
      if (value === null || value === undefined || value === '') continue;
      if (col.name === '_source_file') continue;

      facts.push({
        id: `f-${++factId}`,
        field: col.name,
        value,
        rowReference: rowRef,
        record: { ...record },
      });
      factsForThisRecord++;
    }

    if (facts.length >= maxFacts) break;
  }

  return facts.slice(0, maxFacts);
}

/**
 * Select record indices using stratified sampling:
 * - Common values (most frequent category values)
 * - Rare values (least frequent category values)
 * - Extreme values (min/max of numeric columns)
 * - Null-heavy records (records with missing fields)
 * - Evenly spaced for general coverage
 */
function selectStratifiedIndices(
  records: Record<string, unknown>[],
  profile: DatasetProfile,
  maxFacts: number,
  explicitTargetRecords?: number,
): number[] {
  // Decouple row count from column width so wide schemas don't starve the row pool.
  // Default floor of 50 records when available; callers can pass an explicit target.
  const defaultTarget = Math.max(50, Math.ceil(maxFacts / 5));
  const targetRecords = Math.min(
    records.length,
    explicitTargetRecords ?? defaultTarget,
  );
  const selected = new Set<number>();

  // 1. Records with extreme numeric values
  for (const col of profile.columns) {
    if (col.dataType === 'number' && col.min !== undefined && col.max !== undefined) {
      const minIdx = records.findIndex(r => Number(r[col.name]) === col.min);
      const maxIdx = records.findIndex(r => Number(r[col.name]) === col.max);
      if (minIdx >= 0) selected.add(minIdx);
      if (maxIdx >= 0) selected.add(maxIdx);
    }
  }

  // 2. Records from rare categories
  for (const col of profile.columns) {
    if (col.valueCounts) {
      const sorted = Object.entries(col.valueCounts).sort((a, b) => a[1] - b[1]);
      // Rarest category
      if (sorted.length > 0) {
        const rarest = sorted[0][0];
        const idx = records.findIndex(r => String(r[col.name]) === rarest);
        if (idx >= 0) selected.add(idx);
      }
      // Most common category
      if (sorted.length > 1) {
        const most = sorted[sorted.length - 1][0];
        const idx = records.findIndex(r => String(r[col.name]) === most);
        if (idx >= 0) selected.add(idx);
      }
    }
  }

  // 3. Records with null/empty fields
  const nullCols = profile.columns.filter(c => c.nullCount > 0);
  for (const col of nullCols.slice(0, 3)) {
    const idx = records.findIndex(r =>
      r[col.name] === null || r[col.name] === undefined || r[col.name] === ''
    );
    if (idx >= 0) selected.add(idx);
  }

  // 4. Evenly spaced fill
  const remaining = targetRecords - selected.size;
  if (remaining > 0) {
    const step = Math.max(1, Math.floor(records.length / remaining));
    for (let i = 0; i < records.length && selected.size < targetRecords; i += step) {
      selected.add(i);
    }
  }

  return Array.from(selected).sort((a, b) => a - b);
}

/**
 * Group facts by row reference for easier question generation
 */
export function groupFactsByRecord(facts: Fact[]): Map<string, Fact[]> {
  const groups = new Map<string, Fact[]>();
  for (const fact of facts) {
    const existing = groups.get(fact.rowReference) ?? [];
    existing.push(fact);
    groups.set(fact.rowReference, existing);
  }
  return groups;
}

/**
 * Get a summary of facts for LLM context (limits token usage).
 * Each fact line is prefixed with its stable [f-N] ID so the LLM can cite specific facts.
 */
export function summarizeFacts(facts: Fact[], maxRecords: number = 15): string {
  const grouped = groupFactsByRecord(facts);
  const lines: string[] = [];

  let count = 0;
  for (const [rowRef, recordFacts] of grouped) {
    if (count >= maxRecords) break;

    const fields = recordFacts
      .map(f => `[${f.id}] ${f.field}=${JSON.stringify(f.value)}`)
      .join(', ');
    lines.push(`[${rowRef}] ${fields}`);
    count++;
  }

  return lines.join('\n');
}
