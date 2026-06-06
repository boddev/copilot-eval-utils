# Copilot Evaluation Toolkit

This repository is a toolkit for creating and running Microsoft 365 Copilot / WorkIQ evaluations. It contains two engines (EvalGen, EvalScore), two interchangeable user interfaces (Electron `Eval UI`, Windows-native `EvalToolkit`), and an optional Microsoft `m365-copilot-eval` integration:

| Tool | Location | Purpose |
|------|----------|---------|
| **EvalGen** | [`eval-gen/`](eval-gen/README.md) | Generates evaluation datasets: prompts, expected answers, source locations, assertions, and review artifacts from source data. |
| **EvalScore** | [`eval-score/`](eval-score/README.md) | Runs evaluation datasets against WorkIQ / Microsoft 365 Copilot, records actual answers, scores responses, evaluates assertions, and writes reports. |
| **Eval UI** (Electron) | [`eval-ui/`](eval-ui/README.md) | Local browser UI for business users to browse datasets, generate eval sets, review/edit them, and optionally score them. Cross-platform packaging via the existing Electron release pipeline. |
| **EvalToolkit** (WinUI 3) | [`eval-ui-winui3/`](eval-ui-winui3/README.md) | Windows-native MSIX install of the same wizard. Signed with Azure Trusted Signing. Recommended on Windows if you want a native installed app; use the Electron Eval UI if you need cross-platform/local-browser. |
| **m365-copilot-eval adapter** | [`scripts/`](scripts/) | Converts EvalGen output to the `@microsoft/m365-copilot-eval` schema and runs Microsoft's agent evaluation CLI. |

## End-to-End Workflow

1. **Prepare source data** — Collect connector/source datasets such as CSV, JSON, XLSX, or document files.
2. **Generate an eval set** — Use [`eval-gen`](eval-gen/README.md) to produce questions and expected answers grounded in the source data.
3. **Review the eval set** — Inspect the generated CSV, sidecar JSON, and review markdown before running live evaluation.
4. **Run response evaluation** — Use [`eval-score`](eval-score/README.md) to send prompts to WorkIQ / Microsoft 365 Copilot and record actual answers.
5. **Score and report** — EvalScore scores semantic similarity, evaluates assertions when available, and writes a completed results file plus markdown report.

## Repository Structure

```text
EvaluationCLI/
├── eval-gen/                       # Evaluation set generator
│   ├── src/                        # EvalGen TypeScript source
│   ├── tests/                      # EvalGen Vitest tests
│   ├── examples/                   # Example connector schema files
│   └── README.md                   # Deep EvalGen documentation
│
├── eval-score/                 # WorkIQ / M365 Copilot response evaluation
│   ├── node/                       # TypeScript implementation
│   ├── powershell/                 # PowerShell implementation
│   └── README.md                   # Deep EvalScore documentation
│
├── eval-ui/                        # Local browser UI for non-technical users (Electron)
├── eval-ui-winui3/                 # Windows-native WinUI 3 / .NET 10 companion (MSIX)
├── environment-datasets/           # Sample connector/source datasets for eval generation
├── eval-output/                    # Local generated eval sets and reports
├── .copilot/skills/                # Copilot CLI skill definitions
└── .github/                        # Repository-specific Copilot instructions
```

## What Each Tool Produces

### EvalGen

EvalGen produces EvalScore-ready input:

- `<name>.csv` with `prompt`, `expected_answer`, `source_location`, and empty `actual_answer`
- `<name>.evalgen.json` sidecar with assertions and metadata
- `<name>-review.md` for human review
- Optional connector diagnostics when a connector schema is supplied

EvalGen does **not** evaluate live Copilot responses. It prepares the evaluation set.

### EvalScore

EvalScore consumes an eval set and produces scored results:

- `<input-name>-results.<ext>` with `actual_answer`, similarity score, and assertion results when available
- `<input-name>-report.md` with score distribution, pass/fail summary, and per-question details

EvalScore is the tool that sends prompts to WorkIQ / Microsoft 365 Copilot.

### Optional m365-copilot-eval Integration

The repository also includes scripts for using EvalGen output with Microsoft's `@microsoft/m365-copilot-eval` package:

- `scripts\convert-evalgen-to-m365-copilot-eval.ps1` converts EvalGen CSV plus sidecar JSON to the Microsoft eval document schema.
- `scripts\run-environment-m365-copilot-eval.ps1` runs the environment EvalGen workflow, converts the generated eval set, and invokes `runevals`.
- `run-environment-m365-copilot-eval.cmd` is the Command Prompt wrapper.
- `scripts\Get-M365AgentConnectorMap.ps1` discovers deployed Copilot agents that reference Microsoft Graph external connector IDs and writes EvalScore-ready agent mappings.

Example:

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI

.\scripts\run-environment-m365-copilot-eval.ps1 `
  -TenantId 976f427e-0d86-4ecf-ace3-4d1368eb8358 `
  -M365AgentId "<m365-agent-id>" `
  -Count 10 `
  -AcceptEula
```

`@microsoft/m365-copilot-eval` targets M365 Copilot agents, so `-M365AgentId` is an agent ID, not the `ngoenvironment` connector ID. The connector ID is still included in prompt context and output metadata. See [`docs/m365-copilot-eval-feature-gap-plan.md`](docs/m365-copilot-eval-feature-gap-plan.md) for the integration details and EvalScore feature gap plan.

## Discover Agent IDs for Connectors

If you already have Copilot connectors and agents deployed but need the agent IDs for EvalScore, run the standalone discovery script from the repository root:

```powershell
.\scripts\Get-M365AgentConnectorMap.ps1 -TenantId "<tenant-id>"
```

The script enumerates Microsoft Graph external connections, inspects Copilot package declarative agent definitions for `GraphConnectors` capabilities, resolves matching agents through WorkIQ A2A `/.agents`, and writes reusable files under `eval-output`:

| File | Purpose |
|------|---------|
| `eval-output\agent-connectors.json` | Full audit inventory with connections, agents, matches, unresolved entries, warnings, and errors |
| `eval-output\agent-connectors.jsonl` | Automation-ready resolved connector-agent rows |
| `eval-output\agent-connectors.csv` | Spreadsheet-friendly resolved connector-agent rows |

Graph access requires delegated permissions for external connections and Copilot package catalog reads, such as `ExternalConnection.Read.All` and the current beta package-management read permission (`CopilotPackages.Read.All` at the time this script was added), plus access to the Microsoft 365 Copilot package-management surface. The package catalog endpoint is Microsoft Graph beta, requires Microsoft Agent 365 licensing, supports delegated work/school access for list/detail operations, and is available in the global Microsoft Graph cloud. WorkIQ A2A resolution defaults to the public Work IQ Gateway at `https://workiq.svc.cloud.microsoft/a2a` (override with `-WorkIqEndpoint` or `WORK_IQ_A2A_ENDPOINT`) and requires a token via `WORK_IQ_A2A_ACCESS_TOKEN`, `WORK_IQ_A2A_TOKEN_COMMAND`, or `EVALSCORE_A2A_TOKEN_COMMAND`.

Only rows in `matches`, JSONL, and CSV contain A2A-resolved `agentId` values intended for `eval-score --m365-agent-id`; Graph package element IDs are tracked separately because they may differ from EvalScore agent IDs.

## Install and Uninstall the Command-Line Tools

Install both command-line tools once from the repository root:

```cmd
cd C:\Users\bodonnell\src\EvaluationCLI
install-tools.cmd
```

The installer restores dependencies, builds both TypeScript tools, and links command shims. After it completes, both commands are available from Command Prompt:

```cmd
eval-gen --help
eval-score --help
```

To remove the command shims:

```cmd
cd C:\Users\bodonnell\src\EvaluationCLI
uninstall-tools.cmd
```

To also remove local `node_modules` and `dist` directories:

```cmd
uninstall-tools.cmd -CleanLocal
```

## Quick Start

### Choose a UI

The two UIs are functionally equivalent for the eval-generation and scoring wizard; they share the same on-disk file formats (`*-eval.csv`, `*-eval.evalgen.json`, `*-results.csv`, `*-report.md`) so jobs are portable between them.

#### Option A: Eval UI (Electron, cross-platform)

For a guided browser experience, double-click `Start Eval UI.cmd` from the project root. It starts the local Eval UI app and opens a browser for dataset selection, eval generation, review/editing, and optional scoring.

For a downloaded desktop build, use the GitHub Releases page. Each `eval-ui-<short-commit-sha>` release is an automated Windows build from that exact `main` commit. Download `Eval UI-<version>-setup-x64.exe` for an installer, or `Eval UI-<version>-portable-x64.exe` to run without installing; `.blockmap` files are updater metadata and can usually be ignored.

#### Option B: EvalToolkit (WinUI 3, Windows-native MSIX)

Download the MSIX matching your device architecture (`EvalToolkit.UI_<version>_x64.msix` for Intel/AMD, `EvalToolkit.UI_<version>_arm64.msix` for ARM64) from the latest `eval-ui-winui3-v*` release on the GitHub Releases page, then double-click it to install. The MSIX is signed with Azure Trusted Signing and is self-contained — it includes the .NET runtime and Windows App SDK components, so no separate runtime install is required.

Windows 10 1809 (build 17763) or later is required. Double-click install uses App Installer when available; if it is missing or outdated, update App Installer from the Microsoft Store, or install from a PowerShell prompt (replace `<version>` and the architecture suffix with the filename you downloaded):

```powershell
Add-AppxPackage -Path .\EvalToolkit.UI_<version>_x64.msix
```

To update to a newer release, download and install the newer signed MSIX. If an older version is running, add `-ForceApplicationShutdown`:

```powershell
Add-AppxPackage -Path .\EvalToolkit.UI_<version>_x64.msix -ForceApplicationShutdown
```

To uninstall, open Settings → Apps → Apps & features (Windows 10) / Installed apps (Windows 11) → EvalToolkit → Uninstall.

See [`eval-ui-winui3/README.md`](eval-ui-winui3/README.md) for the project layout and developer build instructions.

### Generate an eval set

Generate an eval set from the full environment dataset:

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI

eval-gen `
  --file ".\environment-datasets" `
  --extensions csv `
  --description "Environmental datasets for the NGO environment Copilot connector, including Our World in Data CO2 and greenhouse gas metrics plus World Bank climate and environmental indicators by country or region and year." `
  --count 50 `
  --connector-schema ".\eval-gen\examples\environment-datasets-connector-schema.json" `
  --output ".\eval-output\environment-datasets-eval.csv"
```

Run that eval set against WorkIQ / Microsoft 365 Copilot:

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI

eval-score `
  --input ".\eval-output\environment-datasets-eval.csv" `
  --sidecar ".\eval-output\environment-datasets-eval.evalgen.json" `
  --connector-id "ngoenvironment" `
  --system-prompt-file ".\prompts\ngo-environment-system-prompt.md"
```

To run the full environment workflow reproducibly:

```cmd
cd C:\Users\bodonnell\src\EvaluationCLI
run-environment-eval.cmd -TenantId 976f427e-0d86-4ecf-ace3-4d1368eb8358
```

See the deep-dive documentation for full usage, options, providers, and troubleshooting:

- [EvalGen documentation](eval-gen/README.md)
- [EvalScore documentation](eval-score/README.md)
- [Node.js EvalScore implementation](eval-score/node/README.md)
- [PowerShell EvalScore implementation](eval-score/powershell/README.md)

## Git Hygiene

Do **not** commit generated dependency or build output directories.

- `node_modules/` is generated by package managers.
- `dist/` is generated by TypeScript builds.
- Both are ignored everywhere by `.gitignore`.
- If a `dist` or `node_modules` file appears in Git status, remove it from the index with `git rm --cached` rather than committing it.
