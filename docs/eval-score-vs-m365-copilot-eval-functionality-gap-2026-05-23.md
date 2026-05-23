# EvalScore vs. @microsoft/m365-copilot-eval Functionality Gap Analysis

Last reviewed: 2026-05-23

## Executive summary

EvalScore has closed many of the previously identified parity gaps. It now has WorkIQ-first response generation, optional M365 agent ID targeting, optional WorkIQ/GitHub Copilot/Azure OpenAI judging, m365-style `Relevance,Coherence` defaults, schema-shaped JSON output, multi-turn grouping, evaluator override names, structured statuses/errors, HTML reports, and Node/PowerShell coverage.

The remaining gaps are no longer basic output-shape parity. They are mostly operational and semantic fidelity gaps: interactive agent selection, exact m365 multi-turn failure semantics, evaluator option validation, global service throttle behavior, formal schema validation, cache/signout UX, and PowerShell high-volume parity.

## Sources reviewed

### EvalScore current implementation

- Node CLI options: `eval-score\node\src\index.ts:20-42`
- Node response generation and multi-turn job execution: `eval-score\node\src\evaluator.ts:30-99`
- Node schema document writer/model: `eval-score\node\src\eval-document.ts:12-31`, `eval-score\node\src\eval-document.ts:55-89`, `eval-score\node\src\eval-document.ts:134-208`
- Node A2A WorkIQ client: `eval-score\node\src\workiq-client.ts:365-535`
- Node throttle gate: `eval-score\node\src\throttle-gate.ts:1-40`
- Node scoring loop: `eval-score\node\src\scorer.ts:14-80`
- Node report/output orchestration: `eval-score\node\src\index.ts:230-291`
- PowerShell CLI options: `eval-score\powershell\Invoke-Evaluation.ps1:26-60`
- PowerShell evaluation loop: `eval-score\powershell\src\Evaluator.ps1:3-69`
- PowerShell A2A token/discovery path: `eval-score\powershell\src\WorkIQClient.ps1:311-406`
- PowerShell schema output: `eval-score\powershell\src\Writers.ps1:50-120`

### @microsoft/m365-copilot-eval 1.8.0-preview.1 reference package

Reference package extracted under the session artifact folder at `files\m365-eval-pkg\package`.

- Eval document schema: `schema\v1\eval-document.schema.json:1-125`, `schema\v1\eval-document.schema.json:144-243`
- Node wrapper CLI: `src\clients\node-js\bin\runevals.js:182-210`
- Python main flow and Azure OpenAI configuration: `src\clients\cli\main.py:83-105`, `src\clients\cli\main.py:151-207`
- A2A discovery, request, and token refresh: `src\clients\cli\api_clients\A2A\a2a_client.py:65-177`, `src\clients\cli\api_clients\A2A\a2a_client.py:269-319`
- MSAL broker/token cache auth: `src\clients\cli\auth\auth_handler.py:30-167`
- Evaluator registry/resolution: `src\clients\cli\evaluator_resolver.py:26-120`
- Evaluator execution: `src\clients\cli\evaluation_runner.py:163-200`
- Worker concurrency/retry: `src\clients\cli\evaluation_runner.py:374-486`
- Multi-turn execution/failure semantics: `src\clients\cli\evaluation_runner.py:502-610`
- Status derivation: `src\clients\cli\status_derivation.py:29-92`
- Output writers: `src\clients\cli\result_writer.py:36-49`, `src\clients\cli\result_writer.py:332-375`

## Current functionality matrix

| Area | EvalScore current state | m365-copilot-eval current state | Current gap |
|------|--------------------------|----------------------------------|-------------|
| Primary workflow | WorkIQ-first evaluator. Node exposes input, system prompt, connector hint, M365 agent ID, judge provider, evaluators, concurrency, delay, checkpoint, sidecar, and EvalSet options (`eval-score\node\src\index.ts:20-42`). PowerShell mirrors the core options (`eval-score\powershell\Invoke-Evaluation.ps1:26-60`). | `runevals` supports inline prompts, prompt files, output selection, interactive prompt entry, concurrency, M365 agent ID, environment selection, cache commands, signout, init-only, and EULA acceptance (`src\clients\node-js\bin\runevals.js:185-210`). | EvalScore is stronger for WorkIQ-first eval pipelines, but weaker as a zero-config authoring/interactive runner. |
| Actual response generation | WorkIQ MCP remains default. M365 agent ID switches to A2A in Node/PowerShell. Node and PowerShell preserve conversation IDs across multi-turn rows (`eval-score\node\src\evaluator.ts:51-61`, `eval-score\powershell\src\Evaluator.ps1:34-42`). | A2A is the central path; an agent ID is required for requests (`src\clients\cli\api_clients\A2A\a2a_client.py:125-145`). | EvalScore now covers the required target-agent path, but does not provide the same first-run interactive agent selection UX. |
| A2A discovery | Node and PowerShell attempt `/.agents`, then agent card, then fallback URL (`eval-score\node\src\workiq-client.ts:422-482`, `eval-score\powershell\src\WorkIQClient.ps1:348-363`). | m365 fetches `/.agents`, normalizes agent cards, and prompts selection when no agent ID is provided (`src\clients\cli\main.py:151-180`, `src\clients\cli\api_clients\A2A\a2a_client.py:65-123`). | EvalScore has discovery for URL resolution but not a user-facing list/select command or interactive fallback. |
| Authentication and token refresh | WorkIQ MCP delegated auth remains the default. Node A2A now supports static bearer tokens, token commands, and explicit opt-in MSAL device-code/silent refresh with a persistent cache; token-command and MSAL paths retry once on HTTP 401. PowerShell A2A supports static tokens and token commands. | Uses MSAL public client broker auth, persistent encrypted token cache where possible, signout, and a refresh callback retried once on 401 (`src\clients\cli\auth\auth_handler.py:30-167`, `src\clients\cli\api_clients\A2A\a2a_client.py:269-319`). | The major built-in auth gap is reduced for Node. Remaining gaps are encrypted/brokered cache parity, cache diagnostics/signout, and PowerShell built-in MSAL parity. |
| Azure OpenAI requirement | Not required by default. Azure OpenAI is only an optional judge path. | Azure OpenAI model configuration is built into the standard LLM evaluator path (`src\clients\cli\main.py:192-207`, `src\clients\cli\evaluation_runner.py:163-174`). | Intentional product difference. EvalScore should preserve WorkIQ as default while documenting that its m365-style LLM rubrics are not Azure AI SDK-native unless `azure-openai` is selected. |
| GitHub Copilot judging | Optional judge provider in EvalScore. | Not present in the inspected m365 package. | EvalScore advantage. Needs more user-facing examples and operational guidance for command configuration. |
| Schema input/output | EvalScore writes schema-shaped JSON with `schemaVersion: "1.4.0"`, metadata, `default_evaluators`, items, scores, statuses, and EvalScore extensions (`eval-score\node\src\eval-document.ts:55-89`, `eval-score\node\src\eval-document.ts:134-208`, `eval-score\powershell\src\Writers.ps1:50-99`). | Schema requires `schemaVersion` and `items`, supports single-turn and multi-turn items, statuses, errors, scores, evaluator maps, and max 20 turns (`schema\v1\eval-document.schema.json:1-125`, `schema\v1\eval-document.schema.json:144-243`). | EvalScore is schema-shaped but not formally schema-validated in the repo; no local schema validator was found. |
| Score scale | EvalScore keeps 0-100 canonical score and maps LLM scores into m365 1-5 score entries with `score_0_100` metadata (`eval-score\node\src\eval-document.ts:199-208`). | m365 schema LLM scores are 1-5; partial match is 0-1; citations are counts (`schema\v1\eval-document.schema.json:158-230`). | Acceptable by design, but EvalScore should explicitly validate/document its extension fields so consumers do not confuse canonical 0-100 with m365 native 1-5 scores. |
| Evaluator defaults | EvalScore defaults to `Relevance,Coherence` (`eval-score\node\src\eval-document.ts:12`, `eval-score\powershell\Invoke-Evaluation.ps1:51`). | System defaults are `Relevance` and `Coherence`; registry covers Relevance, Coherence, Groundedness, Similarity, Citations, ExactMatch, PartialMatch (`src\clients\cli\evaluator_resolver.py:26-35`). | Default parity is closed. |
| Evaluator resolution | EvalScore resolves default evaluator names and per-row override names with `extend`/`replace` semantics (`eval-score\node\src\eval-document.ts:25-31`). | m365 resolves file defaults, per-prompt evaluator maps, and `extend`/`replace`, preserving evaluator option dictionaries (`src\clients\cli\evaluator_resolver.py:73-120`). | EvalScore currently resolves names, but evaluator option dictionaries are not fully honored for behavior such as thresholds, citation formats, and case sensitivity. Unknown evaluator validation also needs to be stricter. |
| Evaluator execution | EvalScore runs LLM-style rubrics through the configured judge and deterministic metrics locally (`eval-score\node\src\scorer.ts:69-80`). PowerShell implements comparable scoring paths. | m365 calls Azure AI Evaluation SDK evaluators directly for Relevance, Coherence, Groundedness, and Similarity, plus local citation/exact/partial evaluators (`src\clients\cli\evaluation_runner.py:163-200`). | EvalScore behavior is provider-prompt based, not identical to Azure AI Evaluation SDK semantics. This is acceptable for WorkIQ/GitHub Copilot judging, but should be called out as a semantic difference. |
| Assertions | EvalScore preserves EvalGen assertions and assertion results in extensions. | m365 has evaluator maps but not EvalScore's sidecar assertion model. | EvalScore advantage. |
| Multi-turn execution | EvalScore groups thread rows into jobs, executes turns sequentially inside a thread, and runs independent jobs concurrently (`eval-score\node\src\evaluator.ts:82-99`). PowerShell preserves thread conversation context in a sequential loop (`eval-score\powershell\src\Evaluator.ps1:17-40`). | m365 executes turns sequentially per thread, retries only 429 in multi-turn mode, stops the thread on failure, and marks downstream turns skipped (`src\clients\cli\evaluation_runner.py:502-610`). | EvalScore has basic thread continuity, but not exact m365 failure semantics. A failed turn does not currently cause remaining turns in that thread to be marked `turnSkipped`. |
| Thread limits | EvalScore reads/writes threads but does not enforce the schema max of 20 turns. | Schema caps turns at 20, and runner validates the cap (`schema\v1\eval-document.schema.json:93`, `src\clients\cli\evaluation_runner.py:416-423`). | Add input validation for max turns and clearer warnings for long threads. |
| Status derivation | EvalScore derives `pass`, `fail`, `partial`, and `error` from row errors and metric pass states (`eval-score\node\src\eval-document.ts:33-53`). | m365 uses explicit rules: no evaluators or all pass => pass; evaluator errors => partial; any evaluator fail => fail; thread rollup has error/partial/pass/fail priority (`src\clients\cli\status_derivation.py:29-92`). | Mostly aligned, but EvalScore should review vacuous-pass and evaluator-error-to-partial behavior for exact parity. |
| Retry/backoff/throttling | Node has a simple concurrency throttle capped at 5 (`eval-score\node\src\throttle-gate.ts:1-40`) and A2A retry wrapping (`eval-score\node\src\workiq-client.ts:405-410`). PowerShell retries 401 token refresh but lacks general retry/backoff. | m365 clamps workers to 5, applies a throttle gate for Retry-After, retries single-turn 429/503/504, and uses 429-only retry for multi-turn to avoid duplicates (`src\clients\cli\evaluation_runner.py:374-486`, `src\clients\cli\evaluation_runner.py:509-563`). | Node still lacks a global Retry-After gate that pauses all workers. PowerShell lacks high-volume retry/backoff beyond 401 refresh. |
| Concurrency | Node supports concurrent jobs; PowerShell exposes `-Concurrency` but the evaluator loop is serial (`eval-score\powershell\src\Evaluator.ps1:19-65`). | m365 parallelizes items with bounded workers (`src\clients\cli\evaluation_runner.py:374-441`). | PowerShell high-volume parity remains incomplete. |
| Output formats and UX | EvalScore writes canonical JSON plus Markdown and HTML from the CLI (`eval-score\node\src\index.ts:270-291`). PowerShell also writes schema JSON and reports. | m365 chooses JSON, CSV, or HTML based on output file and writes schema-compliant JSON metadata (`src\clients\cli\result_writer.py:36-49`, `src\clients\cli\result_writer.py:332-375`). | EvalScore has reports, but lacks a clean output-format switch for JSON vs CSV/XLSX export views after canonical JSON became default. |
| Runtime setup | EvalScore has no Python runtime management. | m365 Node package manages Python runtime/venv/cache as part of `runevals`. | Intentional difference. Do not close unless EvalScore decides to embed Azure AI Evaluation SDK directly. |
| Structured diagnostics | EvalScore uses CLI progress and reports. | m365 emits structured logs across operations and errors. | Add structured run diagnostics if high-volume support/debuggability becomes a priority. |
| PowerShell parity | PowerShell now supports schema output, HTML reports, A2A token command/401 refresh, and m365-style defaults. | m365 has no PowerShell implementation. | EvalScore advantage, but PowerShell still lags Node on actual concurrency, checkpoint files, and full retry/backoff. |

## Closed or substantially reduced gaps since the previous analysis

1. **Canonical schema-shaped JSON output**: EvalScore now writes `{ schemaVersion, metadata, default_evaluators, items }` in Node and PowerShell.
2. **First-class-ish multi-turn grouping**: Node groups turns by thread and serializes turns per thread; PowerShell preserves conversation context sequentially.
3. **Evaluator defaults and override names**: `Relevance,Coherence` are now defaults, with `extend`/`replace` name resolution.
4. **A2A URL discovery and token-command refresh**: EvalScore now attempts `/.agents`, falls back to agent card/fallback URL, and retries on 401 when a token command is available.
5. **Structured status/error fields**: Rows and schema output now include m365-style statuses/errors while preserving EvalScore's `[ERROR: ...]` behavior.
6. **HTML reporting**: Both implementations produce HTML reports in addition to Markdown.
7. **Optional judge providers**: WorkIQ remains default; GitHub Copilot and Azure OpenAI remain optional scoring paths.

## Remaining functionality gaps and priorities

### P0 - High-volume correctness and service resilience

1. **Global Retry-After throttle gate**
   - Add a provider-wide gate that pauses all Node workers when any worker receives a retryable 429 with `Retry-After`.
   - Mirror the behavior in PowerShell or explicitly mark PowerShell high-volume concurrency as unsupported.
   - Preserve the m365 distinction between single-turn retryable statuses and multi-turn 429-only retry to avoid duplicate conversational turns.

2. **PowerShell real concurrency and retry/backoff**
   - Implement actual worker concurrency for PowerShell, not only `-Concurrency` CLI exposure.
   - Add exponential backoff for 429/503/504 where safe.
   - Add the multi-turn rule: retry only 429 inside a thread; stop the thread on ambiguous transient errors.

3. **Thread failure semantics**
   - When a multi-turn turn fails, mark that turn `agentRequestFailed` and all downstream turns `turnSkipped`, matching m365 behavior.
   - Roll up the thread summary after skipped turns are inserted.

### P1 - Schema and evaluator fidelity

4. **Schema validation**
   - Add a validation step against `eval-document.schema.json` for JSON input and generated JSON output.
   - Enforce max 20 turns per thread.
   - Validate status/error/score shapes and evaluator names before running.

5. **Evaluator option support**
   - Preserve and apply evaluator option dictionaries, not only evaluator names.
   - Support per-evaluator thresholds, `ExactMatch`/`PartialMatch` case sensitivity, citation format options, and future evaluator-specific settings.
   - Add "did you mean" errors for unknown evaluator names, similar to m365.

6. **Vacuous-pass and partial-error status parity**
   - Align row status derivation with m365's explicit behavior: no evaluators run => pass, evaluator execution errors => partial, response-generation errors => error.
   - Keep EvalScore extensions for 0-100 canonical scores.

### P2 - Auth, setup, and user experience

7. **A2A auth management UX**
   - Node now has first-party MSAL token acquisition and refresh as an explicit opt-in path.
   - Remaining work: encrypted/brokered cache parity, signout/cache diagnostics comparable to m365, and a decision on whether PowerShell should add built-in MSAL or continue using token-command auth.

8. **Agent discovery command / interactive selection**
   - Add a setup or list command that displays available A2A agents from `/.agents`.
   - Optionally prompt the user when no `--m365-agent-id`/`-M365AgentId` is provided.

9. **Output export UX**
   - Keep schema JSON as canonical.
   - Add explicit export switches for CSV/TSV/XLSX flattened views so spreadsheet users do not lose the old workflow.
   - Document the distinction between canonical result JSON and export/report formats.

10. **Prompt authoring UX**
    - Consider m365-style inline prompts, prompt file discovery, and interactive prompt entry only if EvalScore is intended to become an authoring runner rather than a dataset-driven evaluator.

## Recommended next implementation order

1. Add schema validation and evaluator-name validation first; these are low-risk and prevent invalid eval documents from entering high-volume runs.
2. Implement exact multi-turn failure/skip semantics in Node, then mirror in PowerShell.
3. Replace the simple throttle with a Retry-After-aware global gate; add PowerShell retry/backoff.
4. Decide whether EvalScore should own built-in MSAL auth or standardize on token-command auth as the supported enterprise path.
5. Add output export switches and agent discovery UX once core high-volume behavior is stable.

## Product direction recommendation

Keep EvalScore differentiated from `@microsoft/m365-copilot-eval` rather than cloning it completely. EvalScore should remain WorkIQ-first, keep GitHub Copilot as an optional judge, preserve EvalGen assertions, and support spreadsheet-friendly workflows. The best parity target is not Python runtime or Azure AI SDK internals; it is reliable interoperability with the m365 eval-document schema and operationally safe high-volume M365 agent evaluation.
