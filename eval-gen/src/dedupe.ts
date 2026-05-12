import * as fs from 'fs';
import * as path from 'path';
import { Assertion, EvalSet, GeneratedEvalItem } from './types';

interface PriorEvalItem {
  prompt: string;
  sourceLocation: string;
  assertionSignature: string;
  sourceFile: string;
  filePath: string;
}

export interface AvoidanceSet {
  items: PriorEvalItem[];
  files: string[];
  warnings: string[];
}

export interface AvoidanceFilterResult {
  items: GeneratedEvalItem[];
  removedCount: number;
  duplicatePromptCount: number;
  duplicateSourceLocationCount: number;
  assertionOverlapCount: number;
  warnings: string[];
}

export function normalizePrompt(prompt: string): string {
  return prompt
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

export function isNearDuplicatePrompt(a: string, b: string): boolean {
  const normalizedA = normalizePrompt(a);
  const normalizedB = normalizePrompt(b);
  if (!normalizedA || !normalizedB) return false;
  if (normalizedA === normalizedB) return true;

  const shorter = normalizedA.length < normalizedB.length ? normalizedA : normalizedB;
  const longer = normalizedA.length < normalizedB.length ? normalizedB : normalizedA;
  return longer.includes(shorter) && shorter.length / longer.length > 0.8;
}

export function normalizeAssertion(assertion: Assertion): string {
  switch (assertion.type) {
    case 'must_contain':
      return `${assertion.type}:${normalizeAssertionValue(assertion.value)}:${assertion.wholeWord === true ? 'whole' : 'partial'}`;
    case 'must_contain_any':
      return `${assertion.type}:${assertion.values.map(normalizeAssertionValue).sort().join('|')}`;
    case 'must_not_contain':
      return `${assertion.type}:${normalizeAssertionValue(assertion.value)}`;
    default:
      return JSON.stringify(assertion);
  }
}

export function assertionSignature(assertions: Assertion[]): string {
  return assertions
    .map(normalizeAssertion)
    .sort()
    .join('||');
}

function normalizeAssertionValue(value: string): string {
  return value.toLowerCase().replace(/\s+/g, ' ').trim();
}

function sourceKey(sourceFile: string, sourceLocation: string): string {
  return `${sourceFile}\u0000${sourceLocation}`;
}

function discoverSidecars(inputPath: string): string[] {
  const stat = fs.statSync(inputPath);
  if (stat.isFile()) {
    return inputPath.endsWith('.evalgen.json') ? [inputPath] : [];
  }
  if (!stat.isDirectory()) {
    return [];
  }

  const files: string[] = [];
  for (const entry of fs.readdirSync(inputPath, { withFileTypes: true })) {
    const entryPath = path.join(inputPath, entry.name);
    if (entry.isDirectory()) {
      files.push(...discoverSidecars(entryPath));
    } else if (entry.isFile() && entry.name.endsWith('.evalgen.json')) {
      files.push(entryPath);
    }
  }
  return files;
}

export function loadAvoidanceSet(inputs: string[] | undefined, excludePaths: string[] = []): AvoidanceSet {
  if (!inputs || inputs.length === 0) {
    return { items: [], files: [], warnings: [] };
  }

  const excluded = new Set(excludePaths.map(p => path.resolve(p).toLowerCase()));
  const sidecars = new Set<string>();
  for (const input of inputs) {
    const resolved = path.resolve(input);
    if (!fs.existsSync(resolved)) {
      throw new Error(`Avoidance eval set path not found: ${resolved}`);
    }
    for (const sidecar of discoverSidecars(resolved)) {
      const abs = path.resolve(sidecar);
      if (!excluded.has(abs.toLowerCase())) {
        sidecars.add(abs);
      }
    }
  }

  const items: PriorEvalItem[] = [];
  const warnings: string[] = [];
  const files: string[] = [];

  for (const filePath of Array.from(sidecars).sort()) {
    let parsed: EvalSet;
    try {
      parsed = JSON.parse(fs.readFileSync(filePath, 'utf-8')) as EvalSet;
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      warnings.push(`Skipped avoidance eval set ${filePath}: ${message}`);
      continue;
    }

    if (!Array.isArray(parsed.items)) {
      warnings.push(`Skipped avoidance eval set ${filePath}: missing items array`);
      continue;
    }

    files.push(filePath);
    const sourceFile = parsed.source_file || '';
    for (const item of parsed.items) {
      if (!item || typeof item.prompt !== 'string') continue;
      items.push({
        prompt: item.prompt,
        sourceLocation: typeof item.source_location === 'string' ? item.source_location : '',
        assertionSignature: assertionSignature(Array.isArray(item.assertions) ? item.assertions : []),
        sourceFile,
        filePath,
      });
    }
  }

  return { items, files, warnings };
}

export function filterAgainstAvoidance(
  items: GeneratedEvalItem[],
  avoidance: AvoidanceSet,
  currentSourceFile: string,
): AvoidanceFilterResult {
  const relevantPriorItems = avoidance.items.filter(item =>
    !item.sourceFile || item.sourceFile === currentSourceFile
  );
  const promptSet = new Set(relevantPriorItems.map(item => normalizePrompt(item.prompt)).filter(Boolean));
  const sourceLocationSet = new Set(
    relevantPriorItems
      .filter(item => item.sourceLocation)
      .map(item => sourceKey(item.sourceFile || currentSourceFile, item.sourceLocation))
  );
  const assertionSignatureSet = new Set(
    relevantPriorItems
      .map(item => item.assertionSignature)
      .filter(signature => signature.length > 0)
  );
  const priorPrompts = relevantPriorItems.map(item => item.prompt);
  const kept: GeneratedEvalItem[] = [];
  const warnings: string[] = [...avoidance.warnings];
  let duplicatePromptCount = 0;
  let duplicateSourceLocationCount = 0;
  let assertionOverlapCount = 0;

  for (const item of items) {
    const normalizedPrompt = normalizePrompt(item.prompt);
    const promptDuplicate = promptSet.has(normalizedPrompt)
      || priorPrompts.some(prompt => isNearDuplicatePrompt(item.prompt, prompt));
    const sourceDuplicate = item.source_location
      ? sourceLocationSet.has(sourceKey(currentSourceFile, item.source_location))
      : false;
    const signature = assertionSignature(item.assertions);
    const assertionOverlap = signature.length > 0 && assertionSignatureSet.has(signature);

    if (assertionOverlap) {
      assertionOverlapCount++;
    }

    if (promptDuplicate || sourceDuplicate) {
      if (promptDuplicate) duplicatePromptCount++;
      if (sourceDuplicate) duplicateSourceLocationCount++;
      continue;
    }

    kept.push(item);
    if (normalizedPrompt) {
      promptSet.add(normalizedPrompt);
      priorPrompts.push(item.prompt);
    }
    if (item.source_location) {
      sourceLocationSet.add(sourceKey(currentSourceFile, item.source_location));
    }
    if (signature.length > 0) {
      assertionSignatureSet.add(signature);
    }
  }

  if (assertionOverlapCount > 0) {
    warnings.push(
      `${assertionOverlapCount} generated item(s) reused an assertion signature from prior eval sets. ` +
      'They were kept because assertion-only matches can be legitimate; review them if you require zero assertion overlap.'
    );
  }

  return {
    items: kept,
    removedCount: items.length - kept.length,
    duplicatePromptCount,
    duplicateSourceLocationCount,
    assertionOverlapCount,
    warnings,
  };
}
