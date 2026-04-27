# EvalScore — PowerShell

This is the PowerShell implementation of [EvalScore](../README.md). It reads evaluation datasets, sends prompts to WorkIQ / Microsoft 365 Copilot, records actual answers, scores semantic similarity, and writes a completed results file plus markdown report.

Use this implementation when you want a PowerShell-native workflow or Windows automation without Node.js.

## Prerequisites

- PowerShell 7+
- `workiq` CLI available on `PATH`
- Microsoft 365 Copilot / WorkIQ access for the account authenticated by `workiq`
- ImportExcel for XLSX input/output:

  ```powershell
  Install-Module ImportExcel -Scope CurrentUser
  ```

- Pester 5+ for tests:

  ```powershell
  Install-Module Pester -MinimumVersion 5.0.0 -Scope CurrentUser
  ```

Verify WorkIQ before running evaluations:

```powershell
workiq ask -q "Say hello"
```

## Setup / Preflight

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\powershell

.\Invoke-Evaluation.ps1 -Setup

# Optional tenant targeting
.\Invoke-Evaluation.ps1 -Setup -TenantId "your-tenant-id"
```

The setup flow checks WorkIQ prerequisites and connectivity. Use `-SkipPreflight` for later runs once your environment is known-good.

## Basic Usage

```powershell
# Run a CSV eval set
.\Invoke-Evaluation.ps1 -InputFile ..\..\eval-output\environment-datasets-eval.csv

# Add a system prompt
.\Invoke-Evaluation.ps1 `
  -InputFile ..\..\eval-output\environment-datasets-eval.csv `
  -SystemPrompt "Answer concisely using the indexed connector data."

# Use a system prompt file and custom threshold
.\Invoke-Evaluation.ps1 `
  -InputFile ..\..\eval-output\environment-datasets-eval.csv `
  -SystemPromptFile ..\..\prompts\system.md `
  -Threshold 80

# Write output elsewhere
.\Invoke-Evaluation.ps1 `
  -InputFile ..\..\eval-output\environment-datasets-eval.csv `
  -OutputDir ..\..\output

# Target a specific M365 tenant
.\Invoke-Evaluation.ps1 `
  -InputFile ..\..\eval-output\environment-datasets-eval.csv `
  -TenantId "your-tenant-id"
```

## Parameters

| Parameter | Required | Description |
|-----------|----------|-------------|
| `-InputFile <path>` | Yes, unless `-Setup` | CSV, TSV, XLSX, or JSON evaluation file |
| `-SystemPrompt <text>` | No | Text prepended to each prompt |
| `-SystemPromptFile <path>` | No | File containing a system prompt |
| `-OutputDir <path>` | No | Output directory; default `./output` |
| `-Threshold <int>` | No | Pass/fail threshold, 0–100; default `70` |
| `-TenantId <string>` | No | Microsoft 365 tenant ID for WorkIQ |
| `-Setup` | No | Run preflight checks only |
| `-SkipPreflight` | No | Skip setup checks |

## Input Columns

The reader normalizes common header variants.

| Logical field | Accepted examples |
|---------------|-------------------|
| Prompt | `prompt`, `question`, `Prompt`, `Question` |
| Expected answer | `expected_answer`, `expectedAnswer`, `Expected Answer` |
| Source location | `source_location`, `sourceLocation`, `Source Location` |
| Actual answer | `actual_answer`, `actualAnswer`, `Actual Answer` |

## Output

The PowerShell implementation writes:

- `<input-name>-results.<ext>` with actual answers and similarity scores
- `<input-name>-report.md` with summary metrics and per-question details

The exit code is:

| Code | Meaning |
|------|---------|
| 0 | All rows passed threshold |
| 1 | Evaluation completed but one or more rows failed |
| 2 | Runtime/configuration error |

## Architecture

```text
powershell/
├── Invoke-Evaluation.ps1  # CLI entry point
├── src/
│   ├── Types.ps1          # Shared classes
│   ├── Readers.ps1        # CSV, TSV, XLSX, JSON readers
│   ├── Writers.ps1        # Matching output writers
│   ├── WorkIQClient.ps1   # Persistent WorkIQ MCP client
│   ├── Evaluator.ps1      # Prompt evaluation loop
│   ├── Scorer.ps1         # Semantic similarity scoring
│   ├── Reporter.ps1       # Markdown report generation
│   └── Setup.ps1          # Preflight checks
└── tests/                 # Pester tests
```

The WorkIQ client starts a persistent MCP session once, accepts the WorkIQ EULA through MCP, and reuses that session for prompt evaluation and scoring.

## Testing

```powershell
Invoke-Pester -Path tests -Output Detailed
Invoke-Pester -Path tests\Readers.Tests.ps1 -Output Detailed
Invoke-Pester -Path tests -Output Detailed -Filter @{ FullName = '*CSV*' }
```

PowerShell tests require Pester 5 or later. Pester 3.x cannot run these tests because they use Pester 5 constructs such as `BeforeAll`.

