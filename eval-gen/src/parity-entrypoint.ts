#!/usr/bin/env node
/**
 * Parity entrypoint for the EvalToolkit C# port.
 *
 * This is the deterministic surface the C# parity harness shells out
 * to. It calls ONLY the readers (and, in the future, writers / scoring
 * math / formatting helpers) — never the LLM-bearing pipeline — and
 * prints a normalized JSON document to stdout.
 *
 * Why a separate entrypoint:
 *   - The real `eval-gen` / `eval-score` CLIs run the full pipeline
 *     (profiler → fact extractor → LLM → ...) which is non-deterministic
 *     and unsuitable for byte-diffing.
 *   - vitest is for unit testing, not for being driven from C#.
 *   - Shelling out to the production CLI would also force the C# side
 *     to deal with progress logging, retries, network calls, etc.
 *
 * Output shape on stdout:
 *   {
 *     "tool": "eval-gen-parity",
 *     "version": "<eval-gen package version>",
 *     "operation": "read",
 *     "fixture": "<absolute path>",
 *     "format": "csv" | "json" | ...,
 *     "records": [...],
 *     "sourceFiles": [...]   // for directory inputs
 *   }
 *
 * Diagnostics go to stderr so stdout stays parseable as a single JSON
 * blob. Exit codes:
 *   0   success — stdout is JSON
 *   2   bad CLI usage — stderr describes the issue
 *   3   reader threw — stderr has the error, stdout is the partial JSON
 *
 * Invocation (called from C# via `node dist/parity-entrypoint.js`
 * after `npm run build` in eval-gen/):
 *
 *   node dist/parity-entrypoint.js read <fixture-path> [--recursive] [--ext csv,json]
 *
 * The CLI surface is intentionally minimal; new operations (`write`,
 * `score`, `format-report`) get added as their port todos start.
 */

import * as path from 'path';
import { readDatasetFile } from './readers/index.js';

// Locked-down, stable JSON serialization. The C# side does the same
// thing (sorted keys, no indentation) so byte diffs are meaningful.
function stableStringify(value: unknown): string {
  return JSON.stringify(value, (_key, val) => {
    if (val && typeof val === 'object' && !Array.isArray(val)) {
      const sorted: Record<string, unknown> = {};
      for (const key of Object.keys(val as Record<string, unknown>).sort()) {
        sorted[key] = (val as Record<string, unknown>)[key];
      }
      return sorted;
    }
    return val;
  });
}

interface Args {
  op: string;
  fixture: string;
  recursive: boolean;
  extensions?: string[];
}

function parseArgs(argv: string[]): Args | null {
  // argv[0] = node, argv[1] = script, argv[2] = op, argv[3] = fixture, ...
  const args = argv.slice(2);
  if (args.length < 2) {
    return null;
  }
  const result: Args = { op: args[0], fixture: args[1], recursive: false };
  for (let i = 2; i < args.length; i++) {
    if (args[i] === '--recursive') {
      result.recursive = true;
    } else if (args[i] === '--ext' && i + 1 < args.length) {
      result.extensions = args[i + 1].split(',').map(e => e.trim()).filter(Boolean);
      i++;
    } else {
      // Reject unknown flags so a typo doesn't silently invert behavior.
      process.stderr.write(`unknown flag: ${args[i]}\n`);
      return null;
    }
  }
  return result;
}

function packageVersion(): string {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const pkg = require('../package.json') as { version?: string };
    return pkg.version ?? '0.0.0';
  } catch {
    return '0.0.0';
  }
}

async function main(): Promise<number> {
  const args = parseArgs(process.argv);
  if (!args) {
    process.stderr.write(
      'usage: parity-entrypoint <op> <fixture> [--recursive] [--ext csv,json,...]\n' +
      'ops: read\n'
    );
    return 2;
  }

  const absFixture = path.resolve(args.fixture);

  switch (args.op) {
    case 'read': {
      try {
        const result = await readDatasetFile(absFixture, {
          recursive: args.recursive,
          extensions: args.extensions,
        });
        const envelope = {
          tool: 'eval-gen-parity',
          version: packageVersion(),
          operation: 'read',
          fixture: absFixture,
          format: result.format,
          records: result.records,
          sourceFiles: result.sourceFiles,
        };
        process.stdout.write(stableStringify(envelope));
        return 0;
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        process.stderr.write(`read failed: ${message}\n`);
        process.stdout.write(stableStringify({
          tool: 'eval-gen-parity',
          version: packageVersion(),
          operation: 'read',
          fixture: absFixture,
          error: message,
        }));
        return 3;
      }
    }
    default:
      process.stderr.write(`unknown op: ${args.op}\n`);
      return 2;
  }
}

main().then(
  code => process.exit(code),
  err => {
    process.stderr.write(`fatal: ${err instanceof Error ? err.stack ?? err.message : String(err)}\n`);
    process.exit(1);
  }
);
