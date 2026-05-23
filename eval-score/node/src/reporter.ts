import * as fs from 'fs/promises';
import * as path from 'path';
import { EvalResult, ScoringResult } from './types';

interface ScoreBucket {
  label: string;
  range: string;
  min: number;
  max: number;
  count: number;
}

function buildScoreBuckets(rows: EvalResult['rows']): ScoreBucket[] {
  const buckets: ScoreBucket[] = [
    { label: 'Excellent', range: '90-100', min: 90, max: 100, count: 0 },
    { label: 'Good', range: '70-89', min: 70, max: 89, count: 0 },
    { label: 'Fair', range: '50-69', min: 50, max: 69, count: 0 },
    { label: 'Poor', range: '0-49', min: 0, max: 49, count: 0 },
  ];

  for (const row of rows) {
    if (row.similarityScore == null) continue;
    const score = row.similarityScore;
    for (const bucket of buckets) {
      if (score >= bucket.min && score <= bucket.max) {
        bucket.count++;
        break;
      }
    }
  }

  return buckets;
}

function makeBar(count: number, maxCount: number): string {
  if (maxCount === 0) return '';
  const maxWidth = 20;
  const width = Math.round((count / maxCount) * maxWidth);
  return '█'.repeat(width);
}

function truncate(text: string, maxLength: number): string {
  if (text.length <= maxLength) return text;
  return text.slice(0, maxLength) + '...';
}

function formatPercentage(count: number, total: number): string {
  if (total === 0) return '0';
  return ((count / total) * 100).toFixed(0);
}

/**
 * Generate a markdown evaluation report from evaluation and scoring results.
 */
export function generateReport(evalResult: EvalResult, scoringResult: ScoringResult): string {
  const lines: string[] = [];

  // Title
  lines.push('# Evaluation Report');
  lines.push('');

  // Summary
  lines.push('## Summary');
  lines.push('');
  lines.push(`- **Input File:** ${evalResult.inputFile}`);
  lines.push(`- **Input Format:** ${evalResult.inputFormat}`);
  lines.push(`- **Timestamp:** ${evalResult.timestamp}`);

  if (evalResult.systemPrompt != null) {
    lines.push(`- **System Prompt:** ${truncate(evalResult.systemPrompt, 200)}`);
  }
  if (evalResult.target) {
    const target = evalResult.target.type === 'm365-agent'
      ? `M365 Agent ID: ${evalResult.target.agentId}`
      : evalResult.target.type === 'connector'
        ? `Connector ID: ${evalResult.target.connectorId}`
        : 'WorkIQ default';
    lines.push(`- **Response Target:** ${target}`);
  }
  if (evalResult.judgeProvider) {
    lines.push(`- **Judge Provider:** ${evalResult.judgeProvider}`);
  }
  if (evalResult.evaluators?.length) {
    lines.push(`- **Evaluators:** ${evalResult.evaluators.join(', ')}`);
  }

  lines.push(`- **Total Questions:** ${scoringResult.totalQuestions}`);
  lines.push(`- **Average Score:** ${scoringResult.averageScore.toFixed(1)}`);
  lines.push(`- **Min Score:** ${scoringResult.minScore}`);
  lines.push(`- **Max Score:** ${scoringResult.maxScore}`);

  const passPercentage = formatPercentage(scoringResult.passCount, scoringResult.totalQuestions);
  lines.push(`- **Pass Rate:** ${scoringResult.passCount}/${scoringResult.totalQuestions} (${passPercentage}%)`);
  lines.push(`- **Pass Threshold:** ${scoringResult.passThreshold}`);

  // Assertion summary (if any assertions were evaluated)
  if (scoringResult.totalAssertions > 0) {
    const assertPassPct = formatPercentage(scoringResult.assertionsPassed, scoringResult.totalAssertions);
    lines.push(`- **Assertions:** ${scoringResult.assertionsPassed}/${scoringResult.totalAssertions} passed (${assertPassPct}%)`);
  }

  lines.push('');

  // Score Distribution
  lines.push('## Score Distribution');
  lines.push('');

  const buckets = buildScoreBuckets(evalResult.rows);
  const maxCount = Math.max(...buckets.map((b) => b.count));

  for (const bucket of buckets) {
    const bar = makeBar(bucket.count, maxCount);
    const pct = formatPercentage(bucket.count, scoringResult.totalQuestions);
    lines.push(`${bucket.label} (${bucket.range}): ${bar} ${bucket.count} (${pct}%)`);
  }

  lines.push('');

  // Per-category breakdown (if category data is available from EvalSet)
  const categoryRows = new Map<string, { scores: number[]; assertionsPassed: number; assertionsFailed: number }>();
  for (const row of evalResult.rows) {
    const cat = (row as any)._category;
    if (!cat) continue;
    if (!categoryRows.has(cat)) {
      categoryRows.set(cat, { scores: [], assertionsPassed: 0, assertionsFailed: 0 });
    }
    const entry = categoryRows.get(cat)!;
    entry.scores.push(row.similarityScore ?? 0);
    if (row.assertionResults) {
      entry.assertionsPassed += row.assertionResults.filter(r => r.passed).length;
      entry.assertionsFailed += row.assertionResults.filter(r => !r.passed).length;
    }
  }

  if (categoryRows.size > 0) {
    lines.push('## Results by Category');
    lines.push('');
    lines.push('| Category | Questions | Avg Score | Pass Rate | Assertions |');
    lines.push('|----------|-----------|-----------|-----------|------------|');
    for (const [cat, data] of categoryRows) {
      const avg = data.scores.length > 0
        ? (data.scores.reduce((a, b) => a + b, 0) / data.scores.length).toFixed(1)
        : 'N/A';
      const passing = data.scores.filter(s => s >= scoringResult.passThreshold).length;
      const passRate = data.scores.length > 0 ? `${formatPercentage(passing, data.scores.length)}%` : 'N/A';
      const assertTotal = data.assertionsPassed + data.assertionsFailed;
      const assertStr = assertTotal > 0 ? `${data.assertionsPassed}/${assertTotal}` : '-';
      lines.push(`| ${cat} | ${data.scores.length} | ${avg} | ${passRate} | ${assertStr} |`);
    }
    lines.push('');
  }

  // Detailed Results
  lines.push('## Detailed Results');
  lines.push('');

  evalResult.rows.forEach((row, index) => {
    const n = index + 1;
    const promptPreview = truncate(row.prompt, 60);
    lines.push(`### Question ${n}: ${promptPreview}`);
    lines.push('');

    if (row.similarityScore != null) {
      const passed = row.similarityScore >= scoringResult.passThreshold;
      const icon = passed ? '✅' : '❌';
      lines.push(`**Score:** ${row.similarityScore}/100 ${icon}`);
    } else {
      lines.push('**Score:** N/A');
    }

    if (row.metrics && row.metrics.length > 0) {
      lines.push('');
      lines.push('**Metrics:**');
      for (const metric of row.metrics) {
        const value = metric.score !== undefined ? `${metric.score}/100` : String(metric.passed ?? 'N/A');
        const icon = metric.passed === undefined ? '' : metric.passed ? ' ✅' : ' ❌';
        const provider = metric.model ? `${metric.provider}/${metric.model}` : metric.provider;
        const reason = metric.reason ? ` - ${truncate(metric.reason, 120)}` : '';
        lines.push(`- **${metric.name}:** ${value}${icon} (${provider})${reason}`);
      }
    }

    lines.push('');
    lines.push(`**Source:** ${row.sourceLocation}`);
    lines.push('');
    lines.push('**Prompt:**');
    lines.push(`> ${row.prompt}`);
    lines.push('');
    lines.push('**Expected Answer:**');
    lines.push(`> ${row.expectedAnswer}`);
    lines.push('');
    lines.push('**Actual Answer:**');
    lines.push(`> ${row.actualAnswer}`);
    lines.push('');

    // Assertion results (if present)
    if (row.assertionResults && row.assertionResults.length > 0) {
      lines.push('**Assertions:**');
      for (const ar of row.assertionResults) {
        lines.push(`- ${ar.detail}`);
      }
      const passed = row.assertionResults.filter(r => r.passed).length;
      const total = row.assertionResults.length;
      lines.push(`- **Result:** ${passed}/${total} assertions passed`);
      lines.push('');
    }
  });

  return lines.join('\n');
}

/**
 * Write a report string to a markdown file in the specified output directory.
 * Creates the output directory if it doesn't exist.
 * Returns the full path of the written file.
 */
export async function writeReport(
  report: string,
  outputDir: string,
  inputFile: string,
): Promise<string> {
  await fs.mkdir(outputDir, { recursive: true });

  const baseName = path.basename(inputFile, path.extname(inputFile));
  const outputPath = path.join(outputDir, `${baseName}-report.md`);

  await fs.writeFile(outputPath, report, 'utf-8');

  return outputPath;
}

export function generateHtmlReport(evalResult: EvalResult, scoringResult: ScoringResult): string {
  const rows = evalResult.rows.map((row, index) => {
    const status = row.status ?? ((row.similarityScore ?? 0) >= scoringResult.passThreshold ? 'pass' : 'fail');
    return `<tr><td>${index + 1}</td><td>${escapeHtml(status)}</td><td>${row.similarityScore ?? ''}</td><td>${escapeHtml(truncate(row.prompt, 120))}</td><td>${escapeHtml(truncate(row.actualAnswer, 200))}</td></tr>`;
  }).join('\n');

  return [
    '<!doctype html>',
    '<html lang="en">',
    '<head>',
    '<meta charset="utf-8">',
    '<title>EvalScore Report</title>',
    '<style>body{font-family:Segoe UI,Arial,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:.5rem;vertical-align:top}.pass{color:#107c10}.fail,.error{color:#a4262c}.partial{color:#8a6d00}</style>',
    '</head>',
    '<body>',
    '<h1>Evaluation Report</h1>',
    '<section>',
    `<p><strong>Input:</strong> ${escapeHtml(evalResult.inputFile)}</p>`,
    `<p><strong>Average score:</strong> ${scoringResult.averageScore.toFixed(1)} / 100</p>`,
    `<p><strong>Pass rate:</strong> ${scoringResult.passCount}/${scoringResult.totalQuestions}</p>`,
    `<p><strong>Judge:</strong> ${escapeHtml(evalResult.judgeProvider ?? '')}</p>`,
    '</section>',
    '<table>',
    '<thead><tr><th>#</th><th>Status</th><th>Score</th><th>Prompt</th><th>Actual response</th></tr></thead>',
    `<tbody>${rows}</tbody>`,
    '</table>',
    '</body>',
    '</html>',
  ].join('\n');
}

export async function writeHtmlReport(
  html: string,
  outputDir: string,
  inputFile: string,
): Promise<string> {
  await fs.mkdir(outputDir, { recursive: true });

  const baseName = path.basename(inputFile, path.extname(inputFile));
  const outputPath = path.join(outputDir, `${baseName}-report.html`);

  await fs.writeFile(outputPath, html, 'utf-8');

  return outputPath;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
