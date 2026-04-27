import * as fs from 'fs';
import * as path from 'path';
import { DatasetProfile, GeneratedEvalItem, QuestionCategory } from './types';

/**
 * Connector schema definition — describes which fields are indexed and searchable
 */
export interface ConnectorSchema {
  /** Name of the connector */
  name?: string;
  /** Fields indexed in the connector content (searchable by Copilot) */
  contentFields: string[];
  /** Fields used as the item title */
  titleField?: string;
  /** Fields used as the item URL */
  urlField?: string;
  /** Whether summary/aggregation items are ingested */
  hasSummaryItems?: boolean;
  /** Description of the connection shown to Copilot */
  connectionDescription?: string;
}

/**
 * Diagnostic result for a single eval item
 */
export interface ItemDiagnostic {
  prompt: string;
  issues: string[];
  severity: 'ok' | 'warning' | 'error';
}

/**
 * Overall diagnostic report
 */
export interface DiagnosticReport {
  connectorName: string;
  totalItems: number;
  itemDiagnostics: ItemDiagnostic[];
  fieldCoverage: FieldCoverageReport;
  aggregationWarnings: string[];
  summary: string;
}

/**
 * Report on which dataset fields are vs aren't indexed in the connector
 */
export interface FieldCoverageReport {
  indexedFields: string[];
  unindexedFields: string[];
  questionsTargetingUnindexed: number;
  coveragePercentage: number;
}

/**
 * Load a connector schema from a JSON file
 */
export function loadConnectorSchema(schemaPath: string): ConnectorSchema {
  const absPath = path.resolve(schemaPath);
  if (!fs.existsSync(absPath)) {
    throw new Error(`Connector schema file not found: ${absPath}`);
  }

  const content = fs.readFileSync(absPath, 'utf-8');
  const schema = JSON.parse(content) as ConnectorSchema;

  if (!schema.contentFields || !Array.isArray(schema.contentFields)) {
    throw new Error('Connector schema must have a "contentFields" array');
  }

  return schema;
}

/**
 * Analyze field coverage: which dataset fields are indexed in the connector
 */
export function analyzeFieldCoverage(
  profile: DatasetProfile,
  schema: ConnectorSchema,
): FieldCoverageReport {
  const datasetFields = profile.columns.map(c => c.name.toLowerCase());
  const indexedSet = new Set(schema.contentFields.map(f => f.toLowerCase()));

  const indexedFields = datasetFields.filter(f => indexedSet.has(f));
  const unindexedFields = datasetFields.filter(f => !indexedSet.has(f));

  const total = datasetFields.length;
  const coveragePercentage = total > 0 ? Math.round((indexedFields.length / total) * 100) : 0;

  return {
    indexedFields,
    unindexedFields,
    questionsTargetingUnindexed: 0, // populated later
    coveragePercentage,
  };
}

/**
 * Check a single eval item for connector-related issues
 */
function diagnoseItem(
  item: GeneratedEvalItem,
  schema: ConnectorSchema,
  unindexedFields: Set<string>,
): ItemDiagnostic {
  const issues: string[] = [];

  // Check if supporting facts reference unindexed fields
  for (const fact of item.supporting_facts) {
    const eqIndex = fact.indexOf('=');
    if (eqIndex < 0) continue;
    const field = fact.substring(0, eqIndex).trim().toLowerCase();
    if (unindexedFields.has(field)) {
      issues.push(`References unindexed field "${field}" — Copilot may not find this data`);
    }
  }

  // Flag aggregation questions if no summary items
  if (!schema.hasSummaryItems) {
    const aggregationPatterns = /\b(how many|count|total|average|sum|all)\b/i;
    if (aggregationPatterns.test(item.prompt)) {
      issues.push('Aggregation question without summary items — Copilot may give unreliable results');
    }
  }

  // Flag overly broad questions
  const broadPatterns = /\b(list all|show all|every|summarize all|everything)\b/i;
  if (broadPatterns.test(item.prompt)) {
    issues.push('Broad/exhaustive question — connector retrieval returns limited results');
  }

  const severity = issues.length === 0 ? 'ok'
    : issues.some(i => i.includes('unindexed')) ? 'error'
    : 'warning';

  return { prompt: item.prompt, issues, severity };
}

/**
 * Run full connector diagnostics on generated eval items
 */
export function runDiagnostics(
  items: GeneratedEvalItem[],
  profile: DatasetProfile,
  schema: ConnectorSchema,
): DiagnosticReport {
  const fieldCoverage = analyzeFieldCoverage(profile, schema);
  const unindexedSet = new Set(fieldCoverage.unindexedFields);

  const itemDiagnostics = items.map(item => diagnoseItem(item, schema, unindexedSet));

  // Count questions targeting unindexed fields
  fieldCoverage.questionsTargetingUnindexed = itemDiagnostics.filter(
    d => d.issues.some(i => i.includes('unindexed'))
  ).length;

  // Collect aggregation warnings
  const aggregationWarnings = itemDiagnostics
    .filter(d => d.issues.some(i => i.includes('Aggregation')))
    .map(d => d.prompt);

  // Build summary
  const errorCount = itemDiagnostics.filter(d => d.severity === 'error').length;
  const warnCount = itemDiagnostics.filter(d => d.severity === 'warning').length;
  const okCount = itemDiagnostics.filter(d => d.severity === 'ok').length;

  const summary = [
    `Connector: ${schema.name ?? 'Unknown'}`,
    `Field coverage: ${fieldCoverage.coveragePercentage}% (${fieldCoverage.indexedFields.length}/${fieldCoverage.indexedFields.length + fieldCoverage.unindexedFields.length} fields indexed)`,
    `Questions: ${okCount} ok, ${warnCount} warnings, ${errorCount} errors`,
    fieldCoverage.questionsTargetingUnindexed > 0
      ? `⚠️ ${fieldCoverage.questionsTargetingUnindexed} question(s) target unindexed fields`
      : '✅ All questions target indexed fields',
    aggregationWarnings.length > 0
      ? `⚠️ ${aggregationWarnings.length} aggregation question(s) without summary items`
      : '',
  ].filter(Boolean).join('\n');

  return {
    connectorName: schema.name ?? 'Unknown',
    totalItems: items.length,
    itemDiagnostics,
    fieldCoverage,
    aggregationWarnings,
    summary,
  };
}

/**
 * Format diagnostic report as markdown
 */
export function formatDiagnosticReport(report: DiagnosticReport): string {
  const lines: string[] = [];

  lines.push('# Connector Diagnostics Report');
  lines.push('');
  lines.push(`**Connector:** ${report.connectorName}`);
  lines.push(`**Questions analyzed:** ${report.totalItems}`);
  lines.push('');

  // Field coverage
  lines.push('## Field Coverage');
  lines.push('');
  lines.push(`Coverage: ${report.fieldCoverage.coveragePercentage}%`);
  lines.push('');

  if (report.fieldCoverage.indexedFields.length > 0) {
    lines.push('**Indexed (searchable by Copilot):**');
    for (const f of report.fieldCoverage.indexedFields) {
      lines.push(`- ✅ ${f}`);
    }
    lines.push('');
  }

  if (report.fieldCoverage.unindexedFields.length > 0) {
    lines.push('**Not indexed (Copilot cannot search these):**');
    for (const f of report.fieldCoverage.unindexedFields) {
      lines.push(`- ❌ ${f}`);
    }
    lines.push('');
  }

  // Issue details
  const issues = report.itemDiagnostics.filter(d => d.severity !== 'ok');
  if (issues.length > 0) {
    lines.push('## Issues Found');
    lines.push('');
    for (const item of issues) {
      const icon = item.severity === 'error' ? '🔴' : '🟡';
      lines.push(`### ${icon} ${item.prompt}`);
      for (const issue of item.issues) {
        lines.push(`- ${issue}`);
      }
      lines.push('');
    }
  }

  // Aggregation warnings
  if (report.aggregationWarnings.length > 0) {
    lines.push('## Aggregation Warnings');
    lines.push('');
    lines.push('These questions may produce unreliable results because the connector does not ingest summary items:');
    lines.push('');
    for (const q of report.aggregationWarnings) {
      lines.push(`- ${q}`);
    }
    lines.push('');
  }

  lines.push('---');
  lines.push('');
  lines.push(report.summary);

  return lines.join('\n');
}
