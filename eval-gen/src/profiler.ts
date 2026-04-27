import { DatasetProfile, ColumnProfile, InputFormat } from './types';

/**
 * Infer column data type from sample values
 */
function inferType(values: unknown[]): ColumnProfile['dataType'] {
  const types = new Set<string>();

  for (const v of values) {
    if (v === null || v === undefined || v === '') {
      types.add('null');
      continue;
    }
    if (typeof v === 'boolean') { types.add('boolean'); continue; }
    if (typeof v === 'number') { types.add('number'); continue; }

    const str = String(v);
    // Check if it's a date
    if (/^\d{4}-\d{2}-\d{2}/.test(str) || /^\d{1,2}\/\d{1,2}\/\d{2,4}/.test(str)) {
      const parsed = new Date(str);
      if (!isNaN(parsed.getTime())) { types.add('date'); continue; }
    }
    // Check if it's a number stored as string
    if (!isNaN(Number(str)) && str.trim() !== '') { types.add('number'); continue; }

    types.add('string');
  }

  types.delete('null');
  if (types.size === 0) return 'null';
  if (types.size === 1) return types.values().next().value as ColumnProfile['dataType'];
  return 'mixed';
}

/**
 * Profile a single column
 */
function profileColumn(name: string, records: Record<string, unknown>[]): ColumnProfile {
  const values = records.map(r => r[name]);
  const nonNull = values.filter(v => v !== null && v !== undefined && v !== '');
  const nullCount = values.length - nonNull.length;

  const uniqueValues = new Set(nonNull.map(v => String(v)));
  const uniqueCount = uniqueValues.size;

  const dataType = inferType(values);

  // Sample up to 10 unique values
  const sampleValues = Array.from(uniqueValues).slice(0, 10).map(s => {
    if (dataType === 'number') return Number(s);
    return s;
  });

  const profile: ColumnProfile = {
    name,
    dataType,
    nullCount,
    uniqueCount,
    totalCount: values.length,
    sampleValues,
  };

  // Value counts for low-cardinality categorical columns
  if (uniqueCount <= 20 && dataType === 'string') {
    const counts: Record<string, number> = {};
    for (const v of nonNull) {
      const key = String(v);
      counts[key] = (counts[key] || 0) + 1;
    }
    profile.valueCounts = counts;
  }

  // Min/max for numeric columns
  if (dataType === 'number' && nonNull.length > 0) {
    const nums = nonNull.map(v => Number(v)).filter(n => !isNaN(n));
    if (nums.length > 0) {
      profile.min = Math.min(...nums);
      profile.max = Math.max(...nums);
    }
  }

  // Min/max for date columns
  if (dataType === 'date' && nonNull.length > 0) {
    const dates = nonNull
      .map(v => new Date(String(v)))
      .filter(d => !isNaN(d.getTime()))
      .sort((a, b) => a.getTime() - b.getTime());
    if (dates.length > 0) {
      profile.min = dates[0].toISOString();
      profile.max = dates[dates.length - 1].toISOString();
    }
  }

  return profile;
}

/**
 * Identify columns likely to be unique keys
 */
function findCandidateKeys(columns: ColumnProfile[], rowCount: number): string[] {
  return columns
    .filter(c => {
      // High uniqueness ratio + not too many nulls
      const uniqueRatio = c.uniqueCount / Math.max(1, c.totalCount - c.nullCount);
      return uniqueRatio > 0.9 && c.nullCount < rowCount * 0.05;
    })
    .map(c => c.name);
}

/**
 * Identify columns likely to be names/titles
 */
function findCandidateTitles(columns: ColumnProfile[]): string[] {
  const titlePatterns = /^(name|title|label|description|display|subject|heading)/i;
  return columns
    .filter(c => c.dataType === 'string' && titlePatterns.test(c.name))
    .map(c => c.name);
}

/**
 * Select stratified sample records from the dataset
 */
function selectSampleRecords(
  records: Record<string, unknown>[],
  columns: ColumnProfile[],
  count: number = 20,
): Record<string, unknown>[] {
  if (records.length <= count) return [...records];

  const selected = new Set<number>();

  // Always include first and last records
  selected.add(0);
  selected.add(records.length - 1);

  // Find a categorical column for stratification
  const categoricalCol = columns.find(c => c.valueCounts && Object.keys(c.valueCounts).length > 1);

  if (categoricalCol?.valueCounts) {
    // Sample proportionally from each category
    const categories = Object.keys(categoricalCol.valueCounts);
    const perCategory = Math.max(1, Math.floor((count - 2) / categories.length));

    for (const category of categories) {
      const matching = records
        .map((r, i) => ({ record: r, index: i }))
        .filter(({ record }) => String(record[categoricalCol.name]) === category);

      for (let j = 0; j < Math.min(perCategory, matching.length); j++) {
        const idx = Math.floor(j * matching.length / perCategory);
        selected.add(matching[idx].index);
      }
    }
  }

  // Fill remaining with evenly-spaced records
  while (selected.size < count && selected.size < records.length) {
    const step = Math.max(1, Math.floor(records.length / (count - selected.size)));
    for (let i = 0; i < records.length && selected.size < count; i += step) {
      selected.add(i);
    }
    // If still not enough, add random
    if (selected.size < count) {
      const idx = Math.floor(Math.random() * records.length);
      selected.add(idx);
    }
  }

  return Array.from(selected)
    .sort((a, b) => a - b)
    .map(i => records[i]);
}

/**
 * Profile a dataset: analyze schema, types, distributions, and select samples
 */
export function profileDataset(
  records: Record<string, unknown>[],
  fileName: string,
  format: InputFormat,
): DatasetProfile {
  if (records.length === 0) {
    throw new Error('Cannot profile an empty dataset');
  }

  // Collect all column names across all records (skip internal metadata fields)
  const columnNames = new Set<string>();
  for (const record of records) {
    for (const key of Object.keys(record)) {
      if (!key.startsWith('_')) {
        columnNames.add(key);
      }
    }
  }

  const columns = Array.from(columnNames).map(name => profileColumn(name, records));
  const sampleRecords = selectSampleRecords(records, columns, 20);
  const candidateKeyColumns = findCandidateKeys(columns, records.length);
  const candidateTitleColumns = findCandidateTitles(columns);

  return {
    fileName,
    format,
    rowCount: records.length,
    columns,
    sampleRecords,
    candidateKeyColumns,
    candidateTitleColumns,
  };
}
