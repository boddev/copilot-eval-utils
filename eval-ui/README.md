# Eval UI

Eval UI is a local browser app for business users who want to generate, review, edit, and optionally score evaluation sets without typing command-line instructions.

## Start

Double-click:

```text
..\Start Eval UI.cmd
```

The starter lives in the project root. It checks for Node.js, installs the small UI dependency if needed, starts the local web app from `eval-ui`, and opens the browser. Keep the command window open while using the UI.

`Eval UI.html` is included as a friendly landing page for users who try to open an HTML file first. If the local app is already running, it redirects to it. If it is not running, the page explains why the project-root starter is required: modern browsers do not allow an HTML file to start local programs automatically.

## Workflow

1. Browse to a dataset file or folder.
2. Describe the data in plain language.
3. Generate the evaluation set and watch progress in the UI.
4. Review and edit the generated prompts, expected answers, and source locations.
5. Open the generated review notes or CSV in the browser, download them, or open the output folder.
6. Optionally run EvalScore and view or download the scored CSV and report.

Each run is saved under:

```text
eval-ui\workspace\jobs
```

The UI server only listens on `127.0.0.1`, so it is available from the local computer only.

## Packaging

The project-root `Start Eval UI.cmd` launcher remains available for source checkouts where Node.js is installed.

To build click-to-launch Windows artifacts that bundle the local UI shell and the EvalGen/EvalScore tool assets:

```text
cd eval-ui
npm install
npm run package:win
```

The generated artifacts are written under:

```text
eval-ui\dist
```

The Windows package includes both an installer and a portable executable. The packaged app starts the local Eval UI server on `127.0.0.1`, opens it in an Electron window, and stores user-generated job output under the app user-data folder instead of writing into the installed application directory. EvalGen and EvalScore are bundled with the app; external authentication and WorkIQ access still depend on the user's local environment.

### What each GitHub release means

On every push to `main`, the `Build Eval UI Windows release` GitHub Actions workflow rebuilds the Windows executable artifacts and publishes them to a GitHub Release tagged as `eval-ui-<short-commit-sha>`. Each release is a snapshot of the Eval UI application at that exact commit, not a manually curated product version.

Release assets:

| Asset | Meaning | Use when |
| --- | --- | --- |
| `Eval UI-<version>-setup-x64.exe` | Windows installer for the Eval UI desktop app. | You want the app installed like a normal Windows application. |
| `Eval UI-<version>-portable-x64.exe` | Standalone portable executable. | You want to download and run the UI without installing it. |
| `Eval UI-<version>-setup-x64.exe.blockmap` | Electron updater metadata for the installer artifact. | Usually only automation needs this; most users can ignore it. |

The most recent release is marked as the latest build from `main`. Older releases remain useful for tracing which executable came from a specific commit.

## WorkIQ timeout reliability

EvalGen and EvalScore call WorkIQ / Microsoft 365 Copilot many times during a run. The UI gives those calls longer defaults than the command line and retries transient MCP failures:

```text
EVALGEN_LLM_TIMEOUT_MS=600000
EVALGEN_LLM_MAX_ATTEMPTS=5
EVALSCORE_WORKIQ_TIMEOUT_MS=600000
EVALSCORE_WORKIQ_MAX_ATTEMPTS=5
```

You can set those environment variables before starting the UI if your tenant or connector needs a different timeout or retry count.
