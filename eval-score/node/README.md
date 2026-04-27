# EvalScore — Node.js

This is the TypeScript implementation of [EvalScore](../README.md). It reads evaluation datasets, sends prompts to WorkIQ / Microsoft 365 Copilot, records actual answers, scores semantic similarity, optionally evaluates EvalGen assertions, and writes a completed results file plus markdown report.

Use this implementation when you want npm scripts, TypeScript source, Vitest tests, or assertion-aware EvalGen sidecar integration.

## Prerequisites

- Node.js 18+
- npm
- `workiq` CLI available on `PATH`
- Microsoft 365 Copilot / WorkIQ access for the account authenticated by `workiq`

Verify WorkIQ before running evaluations:

```powershell
workiq ask -q "Say hello"
```

## Install

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI
.\install-tools.cmd
```

This exposes the TypeScript implementation as the `eval-score` command. For local development inside this folder, you can still use `npm run build`, `npm run lint`, and `npm test`.

## Setup / Preflight

```powershell
eval-score --setup

# Optional tenant targeting
eval-score --setup --tenant-id "your-tenant-id"
```

The setup command checks local WorkIQ prerequisites. Use `--skip-preflight` for later runs once your environment is known-good.

## Basic Usage

```powershell
# Run a CSV eval set
eval-score --input ..\..\eval-output\environment-datasets-eval.csv

# Include EvalGen assertions from sidecar JSON
eval-score `
  --input ..\..\eval-output\environment-datasets-eval.csv `
  --sidecar ..\..\eval-output\environment-datasets-eval.evalgen.json

# Load EvalGen sidecar directly as the eval source
eval-score --evalset ..\..\eval-output\environment-datasets-eval.evalgen.json

# Add a system prompt
eval-score `
  --input ..\..\eval-output\environment-datasets-eval.csv `
  --system-prompt "Answer concisely using the indexed connector data."

# Use a system prompt file and custom threshold
eval-score `
  --input ..\..\eval-output\environment-datasets-eval.csv `
  --system-prompt-file ..\..\prompts\system.md `
  --threshold 80

# Write output elsewhere
eval-score `
  --input ..\..\eval-output\environment-datasets-eval.csv `
  --output-dir ..\..\output
```

## Options

| Option | Required | Description |
|--------|----------|-------------|
| `--input <path>` | Yes, unless `--evalset` or `--setup` | CSV, TSV, XLSX, or JSON evaluation file |
| `--evalset <path>` | No | Load EvalGen `.evalgen.json` directly |
| `--sidecar <path>` | No | Load EvalGen assertions/metadata alongside `--input` |
| `--system-prompt <text>` | No | Text prepended to each prompt |
| `--system-prompt-file <path>` | No | File containing a system prompt |
| `--output-dir <path>` | No | Output directory; default `./output` |
| `--threshold <number>` | No | Pass/fail threshold, 0–100; default `70` |
| `--tenant-id <id>` | No | Microsoft 365 tenant ID for WorkIQ |
| `--setup` | No | Run preflight checks only |
| `--skip-preflight` | No | Skip setup checks |

## Input Columns

The reader normalizes common header variants.

| Logical field | Accepted examples |
|---------------|-------------------|
| Prompt | `prompt`, `question`, `Prompt`, `Question` |
| Expected answer | `expected_answer`, `expectedAnswer`, `Expected Answer` |
| Source location | `source_location`, `sourceLocation`, `Source Location` |
| Actual answer | `actual_answer`, `actualAnswer`, `Actual Answer` |

## Output

The Node implementation writes:

- `<input-name>-results.<ext>` with actual answers, similarity scores, and assertion results when available
- `<input-name>-report.md` with summary metrics and per-question details

The exit code is:

| Code | Meaning |
|------|---------|
| 0 | All rows passed threshold |
| 1 | Evaluation completed but one or more rows failed |
| 2 | Runtime/configuration error |

## Architecture

```text
src/
├── index.ts              # CLI entry point and orchestration
├── types.ts              # Shared TypeScript interfaces
├── evaluator.ts          # Prompt evaluation loop and resumability
├── scorer.ts             # Semantic similarity scoring via WorkIQ
├── reporter.ts           # Markdown report generation
├── assertion-checker.ts  # EvalGen assertion evaluation
├── evalset-loader.ts     # Loads EvalGen sidecar JSON directly
├── setup.ts              # Preflight checks
├── workiq-client.ts      # Persistent WorkIQ MCP client
├── readers/              # CSV, TSV, XLSX, JSON readers
└── writers/              # CSV, TSV, XLSX, JSON writers
```

The WorkIQ client starts a persistent MCP session once, accepts the WorkIQ EULA through MCP, and reuses that session for prompt evaluation and scoring.

## Development

```powershell
npm run build
npm run lint
npm test

# Focused tests
npx vitest run tests\readers.test.ts
npx vitest run -t "score clamping"
```

Do not commit `node_modules` or `dist`; both are generated and ignored.

