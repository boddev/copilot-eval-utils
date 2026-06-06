# File formats

EvalToolkit (WinUI 3) reads and writes the same on-disk file formats
as the Electron [Eval UI](../../eval-ui/README.md) and the
[`eval-gen`](../../eval-gen/README.md) / [`eval-score`](../../eval-score/README.md)
CLIs. Jobs are portable between them.

## EvalGen output

When EvalToolkit runs generation, it produces three files under
`%LOCALAPPDATA%\EvalToolkit\workspace\jobs\<jobId>\`:

| File | Purpose |
|---|---|
| `<name>.csv` | The eval set itself. Columns: `prompt`, `expected_answer`, `source_location`, `actual_answer` (empty until scoring runs). |
| `<name>.evalgen.json` | Sidecar metadata: generation parameters, per-row assertions, provider settings, run timestamps. EvalScore reads this to honor assertions during scoring. |
| `<name>-review.md` | Human-readable review markdown summarizing the generated set. |

The CSV column schema, expected-answer conventions, and assertion
shape are documented in [`eval-gen/README.md`](../../eval-gen/README.md)
— that file is the source of truth.

## EvalScore output

When EvalToolkit runs scoring, it produces two more files alongside
the EvalGen output:

| File | Purpose |
|---|---|
| `<name>-results.csv` | The original eval set with `actual_answer`, similarity score, and per-assertion pass/fail columns filled in. |
| `<name>-report.md` | Markdown report: score distribution, pass/fail summary, per-row breakdown. |

Scoring is resumable: if a run is cancelled, re-running EvalScore
picks up from the first row that still has an empty `actual_answer`.

The scoring rubric and assertion semantics are documented in
[`eval-score/README.md`](../../eval-score/README.md).

## App-owned alias extensions

EvalToolkit registers three alias file extensions via its MSIX
manifest. These are used **only** for activation routing — they tell
Windows "if a file with this extension is opened, hand it to
EvalToolkit" — and they never replace the canonical formats above.

| Alias extension | Maps to canonical | Activation behavior |
|---|---|---|
| `.evalgenset` | `<name>.csv` + `<name>.evalgen.json` (companion lookup by suffix strip) | Opens the wizard hydrated from the sidecar JSON. |
| `.evalscoreresults` | `<name>-results.csv` | Strips the alias suffix and opens the companion `-results.csv` in your default CSV handler (typically Excel or a code editor). |
| `.evalreport` | `<name>-report.md` | Strips the alias suffix and opens the companion `-report.md` in your default markdown handler. |

EvalToolkit does not generate alias files automatically. They only
exist if you create them yourself (for example, to make a desktop
shortcut that opens a specific job in EvalToolkit without changing
your system-wide handler for `.csv`). The canonical `.csv`, `.md`,
and `.json` files are never claimed by EvalToolkit at the system
level.

## Interop with the Electron Eval UI

Both apps read and write the formats above identically. To move a
job between them:

1. Close the source app (or, at minimum, ensure no editing is in
   progress).
2. Copy the entire job folder into the destination app's workspace:
   - **Electron → WinUI:** copy
     `eval-ui/workspace/jobs/<jobId>` into
     `%LOCALAPPDATA%\EvalToolkit\workspace\jobs\`.
   - **WinUI → Electron:** copy
     `%LOCALAPPDATA%\EvalToolkit\workspace\jobs\<jobId>` into
     `eval-ui/workspace/jobs/`.
3. Open the destination app — the job appears in its jobs sidebar.

There is no live-reload / cross-app file-watch behavior; do not edit
the same file in both apps concurrently.
