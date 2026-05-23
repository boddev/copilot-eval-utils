# EvalScore vs. @microsoft/m365-copilot-eval Feature Gap Analysis

Last reviewed: 2026-05-23

## Executive Summary

EvalScore is now close to the desired WorkIQ-first workflow: it can generate actual responses through WorkIQ or a targeted M365 agent ID, score those responses with WorkIQ by default, and optionally use GitHub Copilot or Azure OpenAI as the judge provider. It also preserves EvalScore's 0-100 scoring model, EvalGen assertions, spreadsheet-friendly input/output, and both Node and PowerShell entry points.

`@microsoft/m365-copilot-eval` remains stronger as a schema-native M365 agent evaluation runner. Its major advantages are a first-party `runevals` workflow, bundled Python runtime management, richer v1 eval-document output, true multi-turn thread rollups, per-item/per-turn evaluator overrides, HTML reports, interactive prompt/file discovery flows, A2A agent discovery, and token refresh behavior.

The highest-value remaining gaps for EvalScore are:

1. Emit schema-compliant m365 eval-document JSON output, not just flattened EvalScore rows.
2. Preserve multi-turn threads as first-class threads with per-turn status and thread summaries.
3. Add per-row evaluator override semantics (`evaluators` plus `evaluators_mode: extend|replace`).
4. Improve A2A parity with agent discovery and EvalScore-owned token refresh.
5. Add HTML reporting and structured item statuses (`pass`, `fail`, `partial`, `error`).

## Sources Reviewed

### EvalScore

- Node CLI options and orchestration: `eval-score\node\src\index.ts`
- WorkIQ MCP/A2A client and retry handling: `eval-score\node\src\workiq-client.ts`
- Judge providers: `eval-score\node\src\judge-providers.ts`
- Scoring/evaluator registry: `eval-score\node\src\scorer.ts`
- JSON reader and writers: `eval-score\node\src\readers\json-reader.ts`, `eval-score\node\src\writers\json-writer.ts`
- Report generation: `eval-score\node\src\reporter.ts`
- PowerShell entrypoint and scorer/evaluator modules: `eval-score\powershell\Invoke-Evaluation.ps1`, `eval-score\powershell\src\*.ps1`

### @microsoft/m365-copilot-eval 1.8.0-preview.1

- Package manifest: extracted `package.json`
- README command/setup guidance
- Node wrapper: `src\clients\node-js\bin\runevals.js`, `src\clients\node-js\lib\python-runtime.js`, `src\clients\node-js\lib\venv-manager.js`
- Python CLI: `src\clients\cli\*.py`
- A2A client: `src\clients\cli\api_clients\A2A\a2a_client.py`
- Schema: `schema\v1\eval-document.schema.json`

## Current Capability Matrix

| Area | EvalScore Current State | @microsoft/m365-copilot-eval Current State | Gap / Recommendation |
|------|--------------------------|---------------------------------------------|----------------------|
| Primary workflow | WorkIQ-first CLI that reads an input dataset, asks WorkIQ/M365 Copilot for actual responses, scores them, then writes results and a Markdown report. Node CLI exposes `--input`, `--m365-agent-id`, `--judge-provider`, `--evaluators`, `--concurrency`, `--delay-ms`, `--checkpoint-file`, and related options. | `runevals` is a Node command that wraps a Python evaluation CLI. It can auto-discover prompt files, run inline prompts, run interactive mode, initialize only, manage cache, sign out, and accept the EULA. | EvalScore is better for WorkIQ-first eval scoring; m365-copilot-eval is better for agent-project-local workflows and first-run setup. Consider adding prompt-file discovery and interactive mode only if EvalScore needs to serve as a full standalone eval authoring runner. |
| Runtime model | Native TypeScript implementation plus a parallel PowerShell implementation. No Python runtime management. | Node package bundles and manages a Python 3.13 runtime/venv cache, then runs the Python CLI. | Intentional difference. Keep EvalScore free of Python runtime management unless required for direct Azure AI Evaluation SDK parity. |
| Actual response generation | WorkIQ MCP is the default. Supplying `--m365-agent-id` switches Node to A2A; PowerShell has `-M365AgentId`. Connector ID is available as optional prompt context via `--connector-prompt-hint` / `-ConnectorPromptHint`. | A2A agent evaluation is central. It requires an agent ID, can discover agents from `/.agents`, resolves `/.well-known/agent-card.json`, and sends JSON-RPC `message/send`. | EvalScore covers the required agent-ID path but lacks A2A agent discovery and interactive selection. Add discovery as a high-priority usability improvement. |
| Authentication | WorkIQ MCP auth is delegated to the WorkIQ CLI/MCP. A2A uses static `WORK_IQ_A2A_ENDPOINT` and `WORK_IQ_A2A_ACCESS_TOKEN`. | A2A client accepts delegated bearer auth and can refresh the token once on HTTP 401 when a refresh function is available. Node wrapper has signout/cache behavior. | Add EvalScore-owned A2A token refresh so long-running evaluations do not require manual re-authentication. Consider signout/cache inspection only if EvalScore starts managing auth directly. |
| Azure OpenAI requirement | Not required by default. Azure OpenAI is optional through `--judge-provider azure-openai` and environment variables. | Azure OpenAI/Azure AI credentials are required for standard LLM-as-judge scoring: endpoint, API key, API version, and model/deployment. | EvalScore's WorkIQ default matches the desired product direction. Preserve Azure OpenAI as optional. |
| GitHub Copilot judging | Optional through `--judge-provider github-copilot` and `EVALSCORE_GITHUB_COPILOT_COMMAND`; the command reads the rubric prompt from stdin and returns JSON or a 0-100 score. | Not a built-in scoring provider in the inspected package. | EvalScore has a differentiated optional path. Improve docs/examples for safe command configuration and expected JSON shape. |
| Score scale | Canonical `similarityScore` remains 0-100. Metrics also use 0-100 or boolean scales. | LLM evaluator thresholds are configured around Azure AI evaluator scores, with defaults such as threshold 3 in the evaluator registry. | Keep EvalScore's 0-100 score canonical. If writing m365 schema output, include scale/rubric metadata in extensions to avoid implying Azure's native scoring semantics. |
| Evaluator set | Node accepts `SemanticSimilarity`, `Relevance`, `Coherence`, `Groundedness`, `Citations`, `ExactMatch`, `PartialMatch`, and `EvalGenAssertions`. `SemanticSimilarity` is judge-backed; Exact/Partial/Citations/assertions are deterministic. PowerShell currently implements semantic scoring plus Exact/Partial/Citations deterministic metrics. | Registry contains `Relevance`, `Coherence`, `Groundedness`, `Similarity`, `Citations`, `ExactMatch`, and `PartialMatch`. Relevance/Coherence/Groundedness/Similarity are LLM-based through Azure AI Evaluation SDK classes. Exact/Partial/Citations are custom non-LLM evaluators. | EvalScore should implement distinct provider-backed rubrics for Relevance, Coherence, Groundedness, and Similarity using the Azure AI Evaluation SDK definitions as reference material while preserving EvalScore's 0-100 scale. PowerShell should keep parity with Node, including `EvalGenAssertions`. |
| Per-item evaluator overrides | EvalScore has a CLI-level `--evaluators` setting and can preserve metric/assertion metadata. It does not yet implement per-row `evaluators` + `evaluators_mode`. | Schema supports file-level `default_evaluators`, per-item/per-turn `evaluators`, and `evaluators_mode` with `extend` or `replace`. | High-priority gap. Add evaluator resolution compatible with m365 schema so mixed eval documents run as authored. |
| Assertions | EvalScore keeps EvalGen assertions and can score them deterministically in Node. | m365-copilot-eval has evaluator maps but not EvalScore's EvalGen sidecar assertion model. | EvalScore advantage. Keep assertions and include assertion results in any schema output extensions. |
| Input formats | Node supports CSV, TSV, XLSX, JSON, EvalGen sidecar/evalset, and m365-style JSON `items[]` / `turns[]` input. PowerShell supports the common file formats. | Primarily JSON eval documents, inline prompts, interactive prompts, and auto-discovered prompt files. | EvalScore is stronger for spreadsheet workflows. It should become stronger for schema fidelity by preserving m365 document structure instead of flattening it. |
| Multi-turn execution | EvalScore can read `turns[]`, flatten turns into rows, and pass `conversationId`/A2A `contextId` between requests when present. | Schema-native multi-turn threads support 1-20 ordered turns, per-turn evaluators, shared conversation context, and thread summaries. | High-priority gap. EvalScore needs a first-class thread runner and thread-level output instead of flattening turns. |
| Citations | Node extracts/stores citation-like metadata best-effort and has a deterministic `Citations` metric based on citations or source text. | A2A citation marker parsing and a custom Citations evaluator are implemented; schema has citation arrays on items/turns. | Improve citation normalization to match m365 schema and support richer source attribution. |
| Concurrency | Node has bounded worker concurrency and per-worker delay. MCP WorkIQ generation/scoring is serialized because the persistent MCP process is not safe for parallel calls; A2A and non-WorkIQ scoring can use concurrency. PowerShell accepts concurrency but remains sequential. | Python CLI clamps concurrency to 1-5 and uses a parallel executor plus throttle coordination. | Node is adequate for high-volume A2A/non-WorkIQ judging but lacks a global service throttle gate. PowerShell should either implement concurrency or remove/mark the parameter as reserved. |
| Retry/backoff/throttling | Node retries retryable WorkIQ/A2A failures with exponential backoff, jitter, and `retry-after` parsing. PowerShell has simpler behavior and no structured retry gate. | Retry policy handles 429/503/504, uses capped exponential backoff, parses `Retry-After`, and has a thread-safe `ThrottleGate` that pauses workers for active 429 windows. | Add a shared global throttle gate to Node and structured retry/backoff to PowerShell. |
| Checkpoint/resume | Node writes checkpoint JSON after row completion and skips rows with existing actual answers/scores. PowerShell skips completed fields but does not have checkpoint file support. | No obvious checkpoint/resume feature in the inspected package output flow. | EvalScore advantage in Node. Bring checkpointing to PowerShell if PowerShell remains supported for high-volume evals. |
| Output formats | EvalScore writes results in the input format (CSV/TSV/XLSX/JSON) plus Markdown report. JSON output is an array of flattened EvalScore rows with metrics/citations/assertions. | Outputs JSON, CSV, and HTML. JSON is schema-compliant `{ schemaVersion, metadata, default_evaluators, items }`; CSV separates single-turn and multi-turn sections; HTML auto-opens when selected. | High-priority gap. Make schema-native JSON the canonical/default output because there are no existing users to preserve compatibility for. Keep CSV/XLSX/Markdown as optional export views. |
| Status and errors | EvalScore stores failed WorkIQ calls as `[ERROR: message]`, assigns score 0 to error rows, and reports pass/fail using a threshold. | Uses structured item/turn status values `pass`, `fail`, `partial`, `error`, structured error objects, and thread rollups. | Add structured status/error fields while preserving legacy `[ERROR: ...]` compatibility. |
| Reports | Markdown report includes summary, target, judge provider, evaluators, score distribution, question details, metrics, and assertions. | HTML/CSV/JSON reporting includes schema metadata, scores, status, errors, and multi-turn summaries. | Add HTML output and m365-compatible JSON report metadata; Markdown remains useful for CLI workflows. |
| EULA/setup/cache | EvalScore manages WorkIQ EULA acceptance and preflight checks. | Node wrapper manages its own EULA, Python runtime cache, venv cache, cache-info/cache-clear/cache-dir, and version checks. | EvalScore should not manage Python cache. Add version/config diagnostics only if needed for supportability. |
| PowerShell parity | PowerShell mirrors many core options: agent ID, connector prompt hint, judge provider, evaluators, concurrency/delay parameters, and deterministic metrics. | No PowerShell implementation. | EvalScore advantage, but PowerShell now lags Node on checkpointing, concurrency, richer evaluator set, and retry behavior. Keep PowerShell and Node at feature parity unless a future feature is impossible or unsafe to implement in PowerShell. |

## Gap Priorities

### P0 - Required for closer schema parity

1. **Schema-native output writer**
   - Make schema-native JSON the default/canonical output, writing `schemaVersion`, `metadata`, `default_evaluators`, and `items`.
   - Preserve single-turn items and multi-turn threads instead of flattening all rows.
   - Store EvalScore-only fields such as `similarityScore`, `assertions`, `assertionResults`, judge provider, rubric version, and 0-100 scale under `extensions` when they do not map cleanly to the m365 schema.
   - Keep CSV, XLSX, and Markdown as optional exports rather than the canonical persisted result.

2. **First-class multi-turn runner**
   - Maintain a thread model in memory.
   - Execute turns in order per thread while preserving returned `conversationId`/`contextId`.
   - Support per-turn response, score, status, error, and rollup summaries.

3. **Evaluator override resolution**
   - Implement file-level `default_evaluators`.
   - Implement per-item/per-turn `evaluators`.
   - Implement `evaluators_mode: extend|replace`.
   - Validate unknown evaluator names with clear errors.

### P1 - Required for high-volume robustness and operational parity

4. **Global throttle gate**
   - Add a process-wide or provider-wide gate that pauses all workers when any request receives 429 with `Retry-After`.
   - Keep per-request exponential backoff for retryable transient failures.

5. **A2A discovery and token refresh**
   - Add `/.agents` discovery to list available agents.
   - Add EvalScore-owned token acquisition and refresh for A2A so high-volume evaluations do not require manual re-authentication mid-run.
   - Refresh proactively before token expiry when possible and retry exactly once on HTTP 401 after refreshing.
   - Cache resolved agent URLs per run, which Node already partially does.

6. **Structured status/error model**
   - Add status values compatible with m365 schema: `pass`, `fail`, `partial`, `error`.
   - Add structured error objects for response-generation failures and scoring failures.
   - Preserve legacy `[ERROR: ...]` actual answers for existing CSV/XLSX workflows.

### P2 - Usability and reporting

7. **HTML report output**
   - Add an optional HTML report with aggregate statistics, score distribution, item details, citations, errors, and multi-turn summaries.

8. **Distinct LLM evaluator rubrics**
   - Implement separate judge prompts for `Relevance`, `Coherence`, and `Groundedness`.
   - Also map EvalScore `SemanticSimilarity` to the m365/Azure AI `Similarity` concept when schema compatibility is needed.
   - Continue to allow WorkIQ, GitHub Copilot, or Azure OpenAI as the provider behind those rubrics.
   - Use the m365 package's evaluator registry/call signatures and Microsoft Learn Azure AI Evaluation definitions as the initial rubric source. No user-provided rubric is required for the default implementation.

9. **PowerShell parity cleanup**
   - Implement actual concurrency rather than treating `-Concurrency` as reserved/no-op.
   - Add checkpoint-file support.
   - Add structured retry/backoff and, if desired, `EvalGenAssertions` metric parity.

## Recommended Next Implementation Plan

1. Introduce an internal `EvalDocument` model that can represent both flattened EvalScore rows and m365 schema items/threads.
2. Refactor JSON reading so m365 eval documents preserve item/thread boundaries while still supporting legacy row arrays.
3. Add evaluator resolution from document defaults and item/turn overrides.
4. Add schema-native JSON writing as the default persisted output.
5. Add status/error derivation and thread summaries.
6. Add a global throttle gate used by response generation and judge scoring.
7. Add A2A discovery and EvalScore-owned token refresh.
8. Add HTML reporting.

## Resolved Decisions

- **Default output shape:** Use schema-native m365 eval-document JSON as the canonical/default output. There are no existing users, so backwards compatibility should not block adopting the better long-term shape now. CSV/XLSX/Markdown should remain optional exports for analysis and reporting.
- **PowerShell scope:** Maintain PowerShell and Node feature parity. Node can remain the first implementation target for complex changes, but PowerShell should receive matching behavior before a feature is considered complete.
- **LLM evaluator semantics:** Implement separate evaluator rubrics for `Relevance`, `Coherence`, `Groundedness`, and `Similarity` rather than routing them all through generic semantic similarity. The user does not need to provide initial rubrics. The m365 package shows which Azure AI Evaluation SDK evaluators are used and which inputs they receive: `RelevanceEvaluator(query, response)`, `CoherenceEvaluator(query, response)`, `GroundednessEvaluator(response, context)`, and `SimilarityEvaluator(query, response, ground_truth)`. Microsoft Learn defines these evaluators and their 1-5 scoring intent; EvalScore should adapt those definitions into provider-neutral 0-100 judge prompts.
- **A2A auth ownership:** EvalScore should own token acquisition/refresh for A2A. Long-running high-volume evaluations should refresh tokens automatically and should not require manual re-authentication during a run.

## Rubric Reference Notes

The m365 package does not include full prompt text for the Azure AI LLM evaluators; it delegates those evaluator semantics to the Azure AI Evaluation SDK. The package does provide the evaluator registry, defaults, thresholds, and call signatures:

- Defaults are `Relevance` and `Coherence`.
- LLM evaluators are `Relevance`, `Coherence`, `Groundedness`, and `Similarity`.
- Non-LLM/custom evaluators are `Citations`, `ExactMatch`, and `PartialMatch`.
- `Relevance` and `Coherence` receive the user query and actual response.
- `Groundedness` receives the actual response and source/expected context.
- `Similarity` receives the user query, actual response, and ground-truth expected response.

Microsoft Learn's Azure AI Evaluation documentation describes the scoring intent:

- **Relevance:** Measures whether the response addresses the user's query and captures the important points. Low scores indicate off-topic, missing, or insufficient answers.
- **Coherence:** Measures whether the response is logically organized, fluent enough to follow, and internally consistent.
- **Groundedness:** Measures whether claims in the response are supported by the provided context/source material.
- **Similarity:** Measures semantic alignment between the generated response and a ground-truth answer.

EvalScore should use those definitions as default rubrics, convert the result to EvalScore's 0-100 scale, and allow future rubric customization only as an optional advanced feature.
