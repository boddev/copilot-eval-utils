import { Fact, DatasetProfile, ColumnProfile } from './types';

/**
 * Extract atomic facts from dataset records using stratified sampling.
 * Selects diverse records covering common values, rare values, extremes, and nulls.
 */
export function extractFacts(
  records: Record<string, unknown>[],
  profile: DatasetProfile,
  maxFacts: number = 200,
): Fact[] {
  const facts: Fact[] = [];
  const selectedIndices = selectStratifiedIndices(records, profile, maxFacts);

  let factId = 0;
  for (const rowIndex of selectedIndices) {
    const record = records[rowIndex];
    // Use per-record _source_file if available (multi-file mode), else profile.fileName
    const fileLabel = record._source_file ? String(record._source_file) : profile.fileName;
    const rowRef = `${fileLabel}:row ${rowIndex + 1}`;

    for (const col of profile.columns) {
      const value = record[col.name];
      if (value === null || value === undefined || value === '') continue;
      if (col.name === '_source_file') continue; // Skip the metadata field

      facts.push({
        id: `f-${++factId}`,
        field: col.name,
        value,
        rowReference: rowRef,
        record: { ...record },
      });
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
): number[] {
  const targetRecords = Math.min(records.length, Math.ceil(maxFacts / Math.max(1, profile.columns.length)));
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
 * Get a summary of facts for LLM context (limits token usage)
 */
export function summarizeFacts(facts: Fact[], maxRecords: number = 15): string {
  const grouped = groupFactsByRecord(facts);
  const lines: string[] = [];

  let count = 0;
  for (const [rowRef, recordFacts] of grouped) {
    if (count >= maxRecords) break;

    const fields = recordFacts
      .map(f => `${f.field}=${JSON.stringify(f.value)}`)
      .join(', ');
    lines.push(`[${rowRef}] ${fields}`);
    count++;
  }

  return lines.join('\n');
}
