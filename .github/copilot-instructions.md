# Copilot Instructions — EvalScore

## Build, Test, and Lint

### Node.js (in `eval-score/node/`)

```bash
cd eval-score/node
npm install          # Install dependencies
npm run build        # TypeScript compile (tsc)
npm run lint         # Type-check without emitting (tsc --noEmit)
npm test             # Run all tests (vitest)
npx vitest run tests/readers.test.ts   # Single test file
npx vitest run -t "score clamping"     # Tests matching pattern
```

### PowerShell (in `eval-score/powershell/`)

```powershell
cd eval-score/powershell
Invoke-Pester -Path tests -Output Detailed              # All tests
Invoke-Pester -Path tests\Readers.Tests.ps1              # Single test file
Invoke-Pester -Path tests -Filter @{ FullName = '*CSV*' } # Pattern match
```

Prerequisites: `Install-Module Pester -MinimumVersion 5.0.0 -Scope CurrentUser` and `Install-Module ImportExcel -Scope CurrentUser` (for XLSX).

## Architecture

Two interchangeable implementations (Node.js + PowerShell) that produce identical output formats.

**Data flow:** Read evaluation file → Send each prompt to WorkIQ → Record responses → Score semantic similarity → Write results file + markdown report.

### Node.js (`eval-score/node/`)
- `src/index.ts` — CLI entry (commander), orchestrates the pipeline
- `src/workiq-client.ts` — Pluggable `WorkIQClient` interface; `CliWorkIQClient` calls `workiq` CLI directly, `MockWorkIQClient` for tests
- `src/evaluator.ts` — Sequential evaluation loop with resumability
- `src/scorer.ts` — Similarity scoring (0–100) via WorkIQ
- `src/reporter.ts` — Markdown report generation
- `src/readers/` — Factory-pattern readers (CSV, TSV, XLSX, JSON) with header normalization
- `src/writers/` — Matching writers preserving input format

### PowerShell (`eval-score/powershell/`)
- `Invoke-Evaluation.ps1` — CLI entry script with CmdletBinding parameters
- `src/WorkIQClient.ps1` — `Send-WorkIQRequest` (calls `workiq` CLI), `New-MockWorkIQClient` (testing)
- `src/Evaluator.ps1` — `Invoke-Evaluation` with `-AskClient` scriptblock support
- `src/Scorer.ps1` — `Invoke-Scoring` + `Get-ScoringResult`
- `src/Reporter.ps1` — `New-EvalReport` + `Write-EvalReport`
- `src/Readers.ps1` — `Read-EvalFile` with header normalization
- `src/Writers.ps1` — `Write-EvalFile` preserving format

### Shared
- `environment-datasets/` — Local connector/source datasets for eval generation
- `eval-output/` — Local generated eval sets and reports when present
- `.copilot/skills/evaluate.md` — Copilot CLI skill definition

## Conventions

- **Header normalization:** Readers accept flexible column names (e.g., `prompt`/`Prompt`/`question`, `expected_answer`/`Expected Answer`/`expectedAnswer`). Both implementations use the same alias mappings.
- **Error resilience:** Failed WorkIQ calls produce `[ERROR: message]` in actualAnswer rather than aborting. Scorers assign 0 to error rows.
- **Resumability:** Both evaluator and scorer skip rows that already have values, so re-running continues from where it left off.
- **Output naming:** Output files are `{input-basename}-results.{ext}` and `{input-basename}-report.md`.
- **WorkIQ integration:** Both implementations call the `workiq` CLI directly (`workiq ask -q "question" [-t tenantId]`). Authentication is handled locally by the WorkIQ CLI and M365 Copilot.
- **Exit codes:** 0 = all pass, 1 = some fail, 2 = error (both implementations).
