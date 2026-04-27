# EvalScore

EvalScore runs prompt evaluation datasets against WorkIQ / Microsoft 365 Copilot and produces scored results. It is the execution half of this repository: [`eval-gen`](../eval-gen/README.md) can create an eval set, and EvalScore runs that eval set against live Copilot responses.

## What It Does

Given an evaluation dataset with prompts and known expected answers, EvalScore:

1. Reads CSV, TSV, XLSX, or JSON evaluation files.
2. Sends each prompt to WorkIQ / Microsoft 365 Copilot through a persistent WorkIQ MCP session.
3. Records the actual answer returned by Copilot.
4. Scores semantic similarity between expected and actual answers using WorkIQ.
5. Optionally evaluates EvalGen assertions from a sidecar JSON or EvalSet JSON file.
6. Writes a completed results file and a markdown report.

## Implementations

Two implementations are provided because different users and automation environments prefer different runtimes. They are intended to produce equivalent output formats.

| Implementation | Location | Best for |
|----------------|----------|----------|
| **Node.js / TypeScript** | [`node/`](node/README.md) | Development workflows, npm scripts, assertion-aware EvalGen sidecar integration |
| **PowerShell** | [`powershell/`](powershell/README.md) | PowerShell-first environments and Windows automation |

## Install the `eval-score` Command

From the repository root, install and link both toolkit commands:

```cmd
cd C:\Users\bodonnell\src\EvaluationCLI
install-tools.cmd
```

After installation, use `eval-score` directly from Command Prompt instead of invoking `node` or `ts-node` against `src\index.ts`. To remove the command shims, run `uninstall-tools.cmd` from the repository root.

## Input Format

Evaluation datasets can be CSV, TSV, XLSX, or JSON. Column headers are normalized, so common variants are accepted.

| Logical field | Accepted examples | Description |
|---------------|-------------------|-------------|
| Prompt | `prompt`, `question`, `Prompt`, `Question` | Question sent to WorkIQ / Microsoft 365 Copilot |
| Expected answer | `expected_answer`, `expectedAnswer`, `Expected Answer` | Known-correct answer used for scoring |
| Source location | `source_location`, `sourceLocation`, `Source Location` | Source reference for human review |
| Actual answer | `actual_answer`, `actualAnswer`, `Actual Answer` | Copilot response; blank before evaluation |

EvalGen output already uses the expected CSV shape.

## Outputs

Both implementations write output to an output directory, defaulting to `./output` relative to the implementation folder.

| Output | Description |
|--------|-------------|
| `<input-name>-results.<ext>` | Copy of the eval file with actual answers and scoring fields populated |
| `<input-name>-report.md` | Markdown report with average score, pass rate, score distribution, and per-question details |

The Node.js implementation can also load EvalGen assertion data through `--sidecar` or `--evalset`, then include assertion results in the completed output and report.

## Recommended Workflow with EvalGen

1. Generate an eval set:

   ```powershell
   cd C:\Users\bodonnell\src\EvaluationCLI\eval-gen

   eval-gen `
     --file "..\environment-datasets" `
     --extensions csv `
     --description "Environmental datasets for the NGO environment Copilot connector." `
     --count 50 `
     --connector-schema ".\examples\environment-datasets-connector-schema.json" `
     --output "..\eval-output\environment-datasets-eval.csv"
   ```

2. Review `eval-output\environment-datasets-eval-review.md`.

3. Run EvalScore with the generated CSV and sidecar:

   ```powershell
   cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\node

   eval-score `
     --input ..\..\eval-output\environment-datasets-eval.csv `
     --sidecar ..\..\eval-output\environment-datasets-eval.evalgen.json
   ```

## WorkIQ Authentication

EvalScore uses the local `workiq` CLI / MCP server for Microsoft 365 Copilot communication. Authentication is handled by WorkIQ, not by Azure CLI.

Run this before your first evaluation to confirm WorkIQ can communicate with Microsoft 365 Copilot:

```powershell
workiq ask -q "Say hello"
```

If a specific tenant is required, use the implementation-specific tenant option:

- Node.js: `--tenant-id <id>`
- PowerShell: `-TenantId <id>`

## Node.js Implementation

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI
.\install-tools.cmd

eval-score --setup
eval-score --input .\eval-output\environment-datasets-eval.csv
```

See [node/README.md](node/README.md) for all options, examples, and architecture details.

## PowerShell Implementation

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\powershell

.\Invoke-Evaluation.ps1 -Setup
.\Invoke-Evaluation.ps1 -InputFile ..\..\eval-output\environment-datasets-eval.csv
```

See [powershell/README.md](powershell/README.md) for all options, examples, and architecture details.

## Testing

Node.js:

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\node
npm test
```

PowerShell:

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\powershell
Invoke-Pester -Path tests -Output Detailed
```

PowerShell tests require Pester 5 or later.

## Git Hygiene

Do not commit generated `dist` or `node_modules` directories. They are ignored by the repository `.gitignore`; regenerate them locally with `npm install` and `npm run build`.

