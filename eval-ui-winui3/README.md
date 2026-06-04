# EvalToolkit (WinUI 3 companion)

Windows-native companion to the Electron-based `eval-ui/` app. Same
on-disk file formats, independent runtime, independent release.

Architecture and rationale: see
[`plan.md`](../../.copilot/session-state/<session-id>/plan.md) (active
implementation plan held in the agent session workspace).

## Project layout

| Project | Purpose |
|---|---|
| `src/EvalToolkit.Core` | Shared models, env-var helpers, job storage, logging sink interfaces |
| `src/EvalToolkit.WorkIQ` | Shared WorkIQ MCP/CLI + A2A clients (used by EvalGen and EvalScore) |
| `src/EvalToolkit.EvalGen` | C# port of the `eval-gen` engine |
| `src/EvalToolkit.EvalScore` | C# port of the `eval-score` engine |
| `src/EvalToolkit.Cli` | Native CLI shims: `eval-gen-native.exe`, `eval-score-native.exe` |
| `src/EvalToolkit.UI` | WinUI 3 head (added in Phase B) |
| `tests/EvalToolkit.EvalGen.Tests` | xUnit tests for the EvalGen port |
| `tests/EvalToolkit.EvalScore.Tests` | xUnit tests for the EvalScore port |
| `tests/EvalToolkit.Parity.Tests` | Cross-runtime diff harness against the TS impl |

## Prerequisites

- Windows 10 1809 (build 17763) or later (Windows 11 recommended for Mica/Acrylic).
- .NET 10 SDK (LTS, released November 2025).
- Node.js 20+ (only required by the parity test harness, which shells out to the TS
  implementation to compare reader/writer output).

## Build / test

```powershell
cd eval-ui-winui3
dotnet restore
dotnet build
dotnet test
```

The first test run is slower because the parity harness installs and
restores the `eval-gen` Node project on demand.

## Coexistence with the Electron Eval UI

This app is **additive**, not a replacement. The Electron `eval-ui/` stays
the supported default. Both apps consume the same `*-eval.csv`,
`*-eval.evalgen.json`, `*-results.csv`, and `*-report.md` files. They
default to separate workspace directories
(`eval-ui/workspace/jobs` vs `%LOCALAPPDATA%\EvalToolkit\workspace`) to
avoid concurrent-write hazards. On first run the WinUI app offers to
import existing jobs from the Electron locations (copy, not link).

CLI shim names (`eval-gen-native.exe`, `eval-score-native.exe`)
deliberately differ from the existing Node `eval-gen` / `eval-score`
binaries so they don't collide on `PATH`.

## Reader / writer parity

The C# readers and writers match the TypeScript implementations:

- **Byte-exact parity:** CSV, TSV, JSON, JSONL, XLSX, TXT, MD.
- **Normalized-text parity:** DOCX, PPTX. Both sides walk Open XML
  (mammoth on TS, `DocumentFormat.OpenXml` on C#) so paragraph-walk
  details differ slightly; the parity test compares whitespace-collapsed
  paragraph sequences and chunk counts.
- **Semantic parity:** PDF. `pdf-parse` (pdf.js heuristics) and
  `UglyToad.PdfPig` extract text differently enough that byte-exact
  comparison is unrealistic; parity asserts both sides read the same
  minimum recognizable text in stable order.
