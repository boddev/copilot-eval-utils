# User guide

Walkthrough of EvalToolkit (WinUI 3) for end users. For install
instructions, see [the project README](../README.md#install).

## First launch

When you launch EvalToolkit for the first time, the app creates a
**workspace** under `%LOCALAPPDATA%\EvalToolkit\workspace`. All
generated eval sets, scored results, and reports are written under
`workspace\jobs\<jobId>\` unless you explicitly export them elsewhere
during the wizard. The MSAL token cache for WorkIQ / Microsoft 365
Copilot authentication is stored alongside the workspace under
`%LOCALAPPDATA%\EvalToolkit\` at `msal-a2a-cache.bin`
(DPAPI-encrypted to your Windows user account).

EvalToolkit runs as a single instance: launching it again while it is
already running brings the existing window forward and forwards any
file or jump-list activation to that window.

## Home

The home view is a landing page that shows the app name, a short
tagline, and the running version string. From there, navigate via the
shell's left-rail to the **Wizard** (to start a new evaluation) or to
**Diagnostics** (to check environment health).

## Wizard: generate and score an evaluation

The wizard runs end-to-end in three steps. Each step has a heading at
the top and Next / Back buttons at the bottom; you can return to an
earlier step at any time before the job runs.

### Step 1 — Choose a dataset

Pick the source data EvalGen should read. You can drop in a single
file (CSV / TSV / XLSX / JSON / JSONL / TXT / MD / DOCX / PPTX / PDF)
or a folder. The wizard previews what was detected (number of files,
recognized formats, estimated row count for tabular inputs).

### Step 2 — Describe the dataset and generation parameters

Provide:

- **Dataset description** — one or two paragraphs in natural language
  about what's in the dataset. Used by the LLM provider to bias
  prompt generation. Plain English; no special syntax required.
- **Number of evaluations** — how many prompt / expected-answer
  pairs to generate. Allowed range is 10–50 (default 30); values
  outside the range are clamped.
- **File types to include** — multi-select. Defaults to every
  recognized type in the source.

The Advanced options expander exposes:

- **Generation provider** — choose Microsoft 365 Copilot (WorkIQ
  MCP, default), Microsoft 365 Copilot API, WorkIQ A2A, Azure
  OpenAI, or GitHub Copilot CLI. The CLI configuration / env vars
  must already be set for the chosen provider (see
  [troubleshooting.md](troubleshooting.md) for env-var resolution
  order).
- **Model or deployment name** — the model id or Azure deployment
  name to use.
- **Microsoft 365 tenant ID** — only required for the WorkIQ
  provider.
- **Connector schema JSON** — optional schema file describing your
  Copilot connector. When provided, the generated prompts are
  grounded in the connector's exposed properties.

### Step 3 — Generate

Click Generate to run EvalGen. The progress panel shows live status
and an output-folder link. When generation completes, the wizard
shows a results table you can edit row-by-row before scoring. You
can also re-open the eval set in your default tabular editor (Excel,
VS Code) by clicking **Open folder**.

### Step 3b — Score (optional)

When the eval set is ready, click **Score** to send each prompt to
WorkIQ / Microsoft 365 Copilot and record the actual answer plus a
similarity score. Scoring runs sequentially with resumability: if
you cancel partway and re-run, EvalToolkit picks up from the first
row that still has an empty `actual_answer`.

## Jobs sidebar

The shell's left-rail jobs sidebar lists every job EvalToolkit has
generated or scored in this workspace. Clicking a job opens its
output folder in File Explorer; the same action is available from
the right-click context menu's **Open folder** entry.

## File activation

EvalToolkit registers three app-owned file-type aliases via its MSIX
manifest:

| Extension | Companion (canonical) file | Activation behavior |
|---|---|---|
| `.evalgenset` | `<name>.csv` + `<name>.evalgen.json` | Hydrates the wizard from the sidecar and opens the eval set. |
| `.evalscoreresults` | `<name>-results.csv` | Opens the companion `-results.csv` in your default CSV handler (typically Excel or your code editor). |
| `.evalreport` | `<name>-report.md` | Opens the companion `-report.md` in your default markdown handler (typically a code editor or markdown previewer). |

These aliases are **registered for activation**: when Windows opens a
file with one of these extensions, EvalToolkit launches and routes
the click. The activation router strips the alias suffix and locates
the canonical companion file beside it. EvalToolkit itself does not
generate alias files; they exist only if you create them manually
(usually to claim a specific job from File Explorer or a desktop
shortcut without affecting how Windows opens canonical `.csv` /
`.md` files system-wide).

The canonical `.csv`, `.md`, and `.json` files are **never** claimed
system-wide by EvalToolkit. Double-clicking a `.csv` opens whichever
app you've associated with CSV (typically Excel or a code editor).

## Taskbar jump list

After EvalToolkit has been launched once, right-click its taskbar
icon to see a jump list containing:

- **New evaluation** — opens the wizard at step 1.
- **Recent jobs** — up to five most recent jobs from this workspace.
  Clicking one opens the job's folder in File Explorer.

If the jump list looks stale, see
[troubleshooting.md → Jump list shows stale or missing entries](troubleshooting.md#jump-list-shows-stale-or-missing-entries).

## Toast notifications

EvalToolkit raises Windows toast notifications when long-running
generation or scoring jobs complete or fail. Clicking the toast (or
its **Open job folder** action button) opens the job's folder in
File Explorer. Toasts honor your Windows notification settings: if
notifications are muted for **EvalToolkit** under Settings → System
→ Notifications, the toasts
are suppressed but the job still completes normally.

## Diagnostics view

The Diagnostics view runs a battery of environment checks and
classifies the result as **Green** (no issues), **Yellow** (warnings
only — auth or env vars worth confirming), or **Red** (a blocking
issue prevents generation or scoring). Use it as a first stop when
a job fails or auth misbehaves.

The same checks can be run headlessly from the command line via
`--diagnostics` for CI smoke tests; see
[cli-reference.md](cli-reference.md).

## Coexistence with the Electron Eval UI

EvalToolkit and the Electron Eval UI share the same on-disk file
formats — `*-eval.csv`, `*-eval.evalgen.json`, `*-results.csv`,
`*-report.md`. Jobs generated in either app can be opened in the
other by copying the job folder into the receiving app's workspace.

The two apps default to different workspace directories
(`eval-ui/workspace/jobs` for Electron, `%LOCALAPPDATA%\EvalToolkit\workspace`
for WinUI) so the default file paths never collide. There is **no**
live-reload / cross-app file-watch behavior: do not edit the same
eval-set or results file in both apps at the same time — close one
before working in the other.

## Uninstall

To remove EvalToolkit:

1. Settings → Apps → **Apps & features** (Windows 10) or
   **Installed apps** (Windows 11) → EvalToolkit → Uninstall.
2. Uninstalling removes the application files and the per-user
   MSIX state. The workspace under
   `%LOCALAPPDATA%\EvalToolkit\workspace` is **not** removed
   automatically; delete it manually if you want to discard local
   job history and any generated artifacts still stored there.
