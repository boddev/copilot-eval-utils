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
5. Optionally run EvalScore and download the scored CSV and report.

Each run is saved under:

```text
eval-ui\workspace\jobs
```

The UI server only listens on `127.0.0.1`, so it is available from the local computer only.

## WorkIQ timeout reliability

EvalGen and EvalScore call WorkIQ / Microsoft 365 Copilot many times during a run. The UI gives those calls longer defaults than the command line and retries transient MCP failures:

```text
EVALGEN_LLM_TIMEOUT_MS=600000
EVALGEN_LLM_MAX_ATTEMPTS=5
EVALSCORE_WORKIQ_TIMEOUT_MS=600000
EVALSCORE_WORKIQ_MAX_ATTEMPTS=5
```

You can set those environment variables before starting the UI if your tenant or connector needs a different timeout or retry count.
