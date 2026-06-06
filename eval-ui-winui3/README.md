# EvalToolkit (WinUI 3 companion)

Windows-native companion to the Electron-based `eval-ui/` app. Same
on-disk file formats, independent runtime, independent release.

> If you just want to use the app, see [Install](#install). If you want
> to build or test it, see [Develop](#develop).

## Install

End-user install ships as a signed MSIX. The MSIX is self-contained: it
includes the required .NET runtime and Windows App SDK runtime
components, so no separate runtime install is needed.

1. Open the latest `eval-ui-winui3-v*` release on the repository
   [Releases page](https://github.com/boddev/copilot-eval-utils/releases).
2. Download the MSIX matching your device architecture
   (`EvalToolkit.UI_<version>_x64.msix` for Intel/AMD,
   `EvalToolkit.UI_<version>_arm64.msix` for ARM64).
3. Double-click the file to install via App Installer, or run from
   PowerShell (replace `<version>` and the architecture suffix with the
   filename you downloaded):

   ```powershell
   Add-AppxPackage -Path .\EvalToolkit.UI_<version>_x64.msix
   ```

   When upgrading from a running install, add
   `-ForceApplicationShutdown` so the prior version exits cleanly:

   ```powershell
   Add-AppxPackage -Path .\EvalToolkit.UI_<version>_x64.msix -ForceApplicationShutdown
   ```

**Requirements:** Windows 10 1809 (build 17763) or later. Windows 11
recommended for Mica/Acrylic. App Installer ships with current Windows
builds; if it is missing or outdated, update it from the Microsoft
Store, or use the `Add-AppxPackage` command above.

**Signing:** Production builds are signed via Azure Trusted Signing
(publisher subject is published in each release's notes). PR-gate
builds use a self-signed dev certificate and are never published as
releases.

**Updating:** Download and install the newer signed MSIX.

**Uninstalling:** Settings → Apps → Apps & features (Windows 10) /
Installed apps (Windows 11) → EvalToolkit → Uninstall.

## Develop

### Project layout

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

### Prerequisites

- Windows 10 1809 (build 17763) or later (Windows 11 recommended for Mica/Acrylic).
- .NET 10 SDK (LTS, released November 2025).
- Node.js 20+ (only required by the parity test harness, which shells out to the TS
  implementation to compare reader/writer output).

### Build / test

```powershell
cd eval-ui-winui3
dotnet restore
dotnet build
dotnet test
```

The first test run is slower because the parity harness installs and
restores the `eval-gen` Node project on demand.

### CI / release pipelines

- `.github/workflows/build-evaltoolkit-winui3.yml` — PR-gate: builds
  an unsigned x64 MSIX on every PR/push to `main`, then runs a UI smoke
  job that signs with a dev cert, installs the MSIX, exercises the
  `--diagnostics` headless flag, verifies the GUI launches and stays
  alive, and optionally runs a WinAppDriver window-title check.
- `.github/workflows/release-evaltoolkit-winui3.yml` — tag-driven
  3-job pipeline (build / sign with Azure Trusted Signing / publish a
  GitHub release with the signed MSIX attached). Triggered by pushing
  a `eval-ui-winui3-v*` tag.
- Repo-admin one-time setup for Trusted Signing OIDC + environment
  protection is documented in
  [`docs/ci-release-setup.md`](docs/ci-release-setup.md).

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
