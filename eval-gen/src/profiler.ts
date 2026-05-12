import { DatasetProfile, ColumnProfile, InputFormat } from './types';

/**
 * Infer a single value's data type.
 */
function inferValueType(value: unknown): Exclude<ColumnProfile['dataType'], 'null' | 'mixed'> {
  if (typeof value === 'boolean') return 'boolean';
  if (typeof value === 'number') return 'number';

  const str = String(value);
  // Check if it's a date
  if (/^\d{4}-\d{2}-\d{2}/.test(str) || /^\d{1,2}\/\d{1,2}\/\d{2,4}/.test(str)) {
    const parsed = new Date(str);
    if (!isNaN(parsed.getTime())) return 'date';
  }

  // Check if it's a number stored as string
  if (!isNaN(Number(str)) && str.trim() !== '') return 'number';

  return 'string';
}

function mergeTypes(types: Set<Exclude<ColumnProfile['dataType'], 'null' | 'mixed'>>): ColumnProfile['dataType'] {
  if (types.size === 0) return 'null';
  if (types.size === 1) return types.values().next().value as ColumnProfile['dataType'];
  return 'mixed';
}

/**
 * Profile a single column
 */
function profileColumn(name: string, records: Record<string, unknown>[]): ColumnProfile {
  const types = new Set<Exclude<ColumnProfile['dataType'], 'null' | 'mixed'>>();
  const uniqueValues = new Set<string>();
  const sampleValueStrings: string[] = [];
  let valueCounts: Record<string, number> | undefined = {};
  let nullCount = 0;
  let numericMin: number | undefined;
  let numericMax: number | undefined;
  let minDateTime: number | undefined;
  let maxDateTime: number | undefined;

  for (const record of records) {
    const value = record[name];
    if (value === null || value === undefined || value === '') {
      nullCount++;
      continue;
    }

    const key = String(value);
    if (!uniqueValues.has(key)) {
      uniqueValues.add(key);
      if (sampleValueStrings.length < 10) {
        sampleValueStrings.push(key);
      }
    }

    if (valueCounts) {
      valueCounts[key] = (valueCounts[key] || 0) + 1;
      if (Object.keys(valueCounts).length > 20) {
        valueCounts = undefined;
      }
    }

    const valueType = inferValueType(value);
    types.add(valueType);

    const numericValue = Number(value);
    if (!isNaN(numericValue)) {
      numericMin = numericMin === undefined ? numericValue : Math.min(numericMin, numericValue);
      numericMax = numericMax === undefined ? numericValue : Math.max(numericMax, numericValue);
    }

    const str = String(value);
    if (/^\d{4}-\d{2}-\d{2}/.test(str) || /^\d{1,2}\/\d{1,2}\/\d{2,4}/.test(str)) {
      const parsedDate = new Date(str);
      if (!isNaN(parsedDate.getTime())) {
        const time = parsedDate.getTime();
        minDateTime = minDateTime === undefined ? time : Math.min(minDateTime, time);
        maxDateTime = maxDateTime === undefined ? time : Math.max(maxDateTime, time);
      }
    }
  }

  const uniqueCount = uniqueValues.size;
  const dataType = mergeTypes(types);

  // Sample up to 10 unique values
  const sampleValues = sampleValueStrings.map(s => {
    if (dataType === 'number') return Number(s);
    return s;
  });

  const profile: ColumnProfile = {
    name,
    dataType,
    nullCount,
    uniqueCount,
    totalCount: records.length,
    sampleValues,
  };

  // Value counts for low-cardinality categorical columns
  if (uniqueCount <= 20 && dataType === 'string' && valueCounts) {
    profile.valueCounts = valueCounts;
  }

  // Min/max for numeric columns
  if (dataType === 'number' && numericMin !== undefined && numericMax !== undefined) {
    profile.min = numericMin;
    profile.max = numericMax;
  }

  // Min/max for date columns
  if (dataType === 'date' && minDateTime !== undefined && maxDateTime !== undefined) {
    profile.min = new Date(minDateTime).toISOString();
    profile.max = new Date(maxDateTime).toISOString();
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
