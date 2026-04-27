# EvalGen

EvalGen generates evaluation sets for Microsoft 365 Copilot connector testing. It reads source data, profiles it, extracts grounded facts, asks an LLM provider to draft natural-language questions, grounds expected answers back to the source facts, and exports files that can be reviewed and run through [EvalScore](../eval-score/README.md).

EvalGen does **not** run prompts against your connector or score live Copilot responses. It creates the test set. Use [EvalScore](../eval-score/README.md) for response evaluation.

## What EvalGen Produces

For an output path like `../eval-output/environment-datasets-eval.csv`, EvalGen writes:

| File | Purpose |
|------|---------|
| `environment-datasets-eval.csv` | EvalScore-compatible rows with `prompt`, `expected_answer`, `source_location`, and empty `actual_answer` |
| `environment-datasets-eval.evalgen.json` | Rich sidecar with categories, assertions, grounding confidence, supporting facts, warnings, and metadata |
| `environment-datasets-eval-review.md` | Human-readable review document for inspecting generated questions before live evaluation |
| `environment-datasets-eval-diagnostics.md` | Connector diagnostics when `--connector-schema` is supplied |

## How It Works

1. **Read data** — Loads a file, recursive directory, API source, database source, or web source.
2. **Profile schema** — Identifies columns, data types, cardinality, null counts, candidate keys, and sample records.
3. **Extract facts** — Uses stratified sampling to pull diverse facts from the source data.
4. **Generate intents** — Uses a configured LLM provider to create question intents by category.
5. **Draft questions** — Produces natural-language prompts and expected answers.
6. **Ground answers** — Verifies expected answers against extracted source facts.
7. **Generate assertions** — Creates machine-checkable assertions such as `must_contain`.
8. **Validate and export** — Deduplicates, checks category balance, computes coverage, and writes outputs.

## Quick Start

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI
.\install-tools.cmd

cd .\eval-gen

# Optional but recommended: verify the default M365/WorkIQ provider.
workiq ask -q "Say hello"

eval-gen `
  --file "..\environment-datasets" `
  --extensions csv `
  --description "Environmental datasets for the NGO environment Copilot connector, including Our World in Data CO2 and greenhouse gas metrics plus World Bank climate and environmental indicators by country or region and year." `
  --count 50 `
  --connector-schema ".\examples\environment-datasets-connector-schema.json" `
  --output "..\eval-output\environment-datasets-eval.csv"
```

Use `--dry-run` to validate profiling and connector diagnostics without calling an LLM provider:

```powershell
eval-gen `
  --file "..\environment-datasets" `
  --extensions csv `
  --description "Environmental datasets for the NGO environment Copilot connector." `
  --connector-schema ".\examples\environment-datasets-connector-schema.json" `
  --output "..\eval-output\environment-datasets-eval.csv" `
  --dry-run
```

## Running the Generated Eval Set

After review, run the generated CSV through EvalScore:

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI\eval-score\node

eval-score `
  --input ..\..\eval-output\environment-datasets-eval.csv `
  --sidecar ..\..\eval-output\environment-datasets-eval.evalgen.json
```

The sidecar enables assertion-aware scoring in addition to semantic similarity scoring.

## Supported Inputs

### File and Directory Sources

`--file` accepts:

| Shape | Example |
|-------|---------|
| Single file | `--file ".\data\records.csv"` |
| Directory | `--file "..\environment-datasets"` |
| Comma-separated files | `--file ".\a.csv,.\b.json"` |

Directory discovery is recursive. Use `--extensions` when a folder contains multiple representations of the same data:

```powershell
--file "..\environment-datasets" --extensions csv
```

Supported file formats include CSV, TSV, JSON, JSONL, XLSX, DOCX, PDF, PPTX, TXT, and Markdown. CSV is usually preferred for tabular connector datasets because each row becomes a record.

### Source Adapter Options

EvalGen also has early source-adapter support:

| Option | Purpose |
|--------|---------|
| `--source-type api` | Fetch data from an API source |
| `--source-type database` | Fetch data from a database source |
| `--source-type web` | Fetch data from a web source |
| `--source-url <url>` | API or web source URL |
| `--openapi-spec <url>` | Optional OpenAPI/Swagger spec for API sampling |
| `--connection-string <str>` | Database connection string |
| `--endpoints <list>` | Comma-separated API endpoints to sample |
| `--auth-header <header>` | Authorization header for API/web requests |

For the current environment connector dataset, prefer `--file ..\environment-datasets --extensions csv`.

## LLM Providers

EvalGen uses an LLM provider to draft question intents and prompts. The default provider is `m365-copilot`, which routes through WorkIQ MCP.

| Provider | Use when | Authentication |
|----------|----------|----------------|
| `m365-copilot` | Default M365 Copilot path through WorkIQ | `workiq` CLI handles M365 auth/session |
| `m365-copilot-api` | Advanced direct Microsoft Graph beta Chat API testing | Delegated Graph token / Azure CLI scopes |
| `azure-openai` | Azure OpenAI deployment should generate the eval set | `EVALGEN_AZURE_OPENAI_ENDPOINT` and `EVALGEN_AZURE_OPENAI_KEY` |
| `github-copilot` | GitHub Copilot CLI should generate the eval set | Local `gh copilot` authentication |
| `command` | Any custom local provider | Command reads JSON from stdin and prints JSON to stdout |

### Microsoft 365 Copilot via WorkIQ

```powershell
workiq ask -q "Say hello"

eval-gen `
  --provider m365-copilot `
  --file "..\environment-datasets" `
  --extensions csv `
  --description "Environmental datasets for the NGO environment Copilot connector." `
  --count 50 `
  --output "..\eval-output\environment-datasets-eval.csv"
```

### Azure OpenAI

```powershell
$env:EVALGEN_AZURE_OPENAI_ENDPOINT = "https://your-endpoint.openai.azure.com"
$env:EVALGEN_AZURE_OPENAI_KEY = "your-key"
$env:EVALGEN_MODEL = "gpt-4o"

eval-gen `
  --provider azure-openai `
  --file "..\environment-datasets" `
  --extensions csv `
  --description "Environmental datasets for the NGO environment Copilot connector." `
  --count 50 `
  --output "..\eval-output\environment-datasets-eval.csv"
```

### GitHub Copilot CLI

```powershell
gh copilot -- --help

eval-gen `
  --provider github-copilot `
  --model gpt-5.2 `
  --file "..\environment-datasets" `
  --extensions csv `
  --description "Environmental datasets for the NGO environment Copilot connector." `
  --count 50 `
  --output "..\eval-output\environment-datasets-eval.csv"
```

### Custom Command

```powershell
eval-gen `
  --provider command `
  --llm-command "node .\my-provider.js" `
  --file ".\data.csv" `
  --description "My data source" `
  --output ".\output\eval-set.csv"
```

The command receives JSON on stdin:

```json
{
  "prompt": "...",
  "schemaDescription": "..."
}
```

It must print a JSON object matching the requested schema.

## Connector Schema Diagnostics

`--connector-schema` points to a small JSON file that describes which fields are indexed by the connector:

```json
{
  "name": "NGO Environment Connector",
  "contentFields": ["country", "year", "co2", "indicatorName", "value"],
  "titleField": "country",
  "urlField": null,
  "hasSummaryItems": false,
  "connectionDescription": "Environmental datasets by country or region and year."
}
```

EvalGen uses this to flag:

- Generated questions that rely on unindexed fields
- Aggregation questions when summary items are not indexed
- Overly broad questions such as "list all"

The environment dataset schema is available at:

```text
eval-gen\examples\environment-datasets-connector-schema.json
```

## CLI Options

| Option | Description | Default |
|--------|-------------|---------|
| `--file <path>` | File, directory, or comma-separated files | Required unless `--source-type` is used |
| `--source-type <type>` | Source adapter: `api`, `database`, or `web` | — |
| `--source-url <url>` | API or web source URL | — |
| `--openapi-spec <url>` | OpenAPI/Swagger spec URL | — |
| `--connection-string <str>` | Database connection string | — |
| `--endpoints <list>` | Comma-separated API endpoints | — |
| `--auth-header <header>` | Authorization header for source fetch | — |
| `--description <text>` | Plain-text description of the data | Required |
| `--count <n>` | Questions to generate; clamped to 10–50 | 30 |
| `--output <path>` | Output CSV path | `./output/eval-set.csv` |
| `--connector-schema <path>` | Connector schema JSON for diagnostics | — |
| `--no-review` | Skip review markdown generation | false |
| `--provider <name>` | `m365-copilot`, `m365-copilot-api`, `azure-openai`, `github-copilot`, or `command` | `m365-copilot` |
| `--model <name>` | Model/deployment name where supported | `gpt-4o` |
| `--llm-command <command>` | Command for `--provider command` | — |
| `--m365-time-zone <zone>` | Direct Graph API location hint | Local time zone |
| `--m365-tenant <tenantId>` | Direct Graph API tenant ID | Current tenant |
| `--extensions <list>` | File extensions to include for directory input | All supported |
| `--dry-run` | Profile and diagnose only; no LLM calls | false |

## Environment Variables

| Variable | Description |
|----------|-------------|
| `EVALGEN_PROVIDER` | Default provider override |
| `EVALGEN_MODEL` | Model/deployment name for providers that support it |
| `EVALGEN_AZURE_OPENAI_ENDPOINT` | Azure OpenAI endpoint URL |
| `EVALGEN_AZURE_OPENAI_KEY` | Azure OpenAI API key |
| `EVALGEN_M365_COPILOT_TOKEN` | Delegated Graph token for `m365-copilot-api` |
| `EVALGEN_M365_COPILOT_TIME_ZONE` | Direct Graph API location hint |
| `EVALGEN_M365_TENANT_ID` | Direct Graph API tenant ID |
| `EVALGEN_M365_COPILOT_SCOPE` | Direct Graph API scope override |
| `EVALGEN_LLM_COMMAND` | Command for `command` provider |

## Question Categories

| Category | Target % | Description |
|----------|----------|-------------|
| `single_record_lookup` | 30% | Ask about a specific entity or record |
| `attribute_retrieval` | 20% | Retrieve a value from a known record |
| `filtered_find` | 20% | Find records matching filters |
| `temporal` | 10% | Ask about dates, years, or time windows |
| `comparison` | 10% | Compare values across records |
| `edge_case` | 10% | Ask about missing, ambiguous, or non-existent values |

## Assertion Types

| Type | Check |
|------|-------|
| `must_contain` | Actual answer must contain a substring |
| `must_contain_any` | Actual answer must contain at least one listed value |
| `must_not_contain` | Actual answer must not contain a substring |

## Development

```powershell
cd C:\Users\bodonnell\src\EvaluationCLI\eval-gen
npm install
npm run build
npm run lint
npm test
```

Do not commit `node_modules` or `dist`; they are ignored by the repository `.gitignore`.

