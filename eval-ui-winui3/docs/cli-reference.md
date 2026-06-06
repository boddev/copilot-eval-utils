# CLI reference

EvalToolkit is primarily a GUI app, but it accepts a small set of
command-line flags so it can be smoke-tested headlessly in CI and so
the Windows taskbar jump list can drive it.

## Public CLI

### `--diagnostics`

Synonym: `/diagnostics`

Runs the same battery of environment checks the Diagnostics view runs
in-app, then exits. Detected **before** single-instance arbitration,
so launching the diagnostics from a CI runner does not redirect to a
GUI already running on the user's session.

By default the report is written as JSON to standard output. Capturing
stdout from a WinExe binary across `Start-Process` is unreliable on
Windows; for any non-interactive use case, pair with
`--diagnostics-out` (below).

Exit codes:

| Exit code | Meaning |
|---|---|
| `0` | Green or Yellow — no blocking issue. Yellow warnings (e.g. missing optional env var, deferred auth) are present in the JSON. |
| `1` | Red — at least one blocking issue prevents generation or scoring. The JSON payload identifies which check failed. |
| `2` | A diagnostic collector itself threw — the report could not be produced. |

Example (PowerShell):

```powershell
& "$((Get-AppxPackage EvalToolkit.UI).InstallLocation)\EvalToolkit.UI.exe" --diagnostics --diagnostics-out diag.json
```

### `--diagnostics-out <path>`

Forms accepted: `--diagnostics-out <path>`, `/diagnostics-out <path>`,
`--diagnostics-out=<path>`, `/diagnostics-out=<path>`.

Writes the diagnostics JSON to `<path>` on disk instead of standard
output. The path can be absolute or relative; relative paths are
resolved against the current working directory via `Path.GetFullPath`.

This flag is only meaningful in combination with `--diagnostics`;
specifying it alone does **not** activate headless mode — the GUI
launches normally and the flag is ignored.

Example (CI smoke test):

```powershell
& "$((Get-AppxPackage EvalToolkit.UI).InstallLocation)\EvalToolkit.UI.exe" --diagnostics --diagnostics-out "$env:RUNNER_TEMP\diag.json"
if ($LASTEXITCODE -ne 0) {
  throw "Diagnostics failed (exit $LASTEXITCODE). See $env:RUNNER_TEMP\diag.json."
}
```

## Internal CLI (jump-list activation arguments)

EvalToolkit's taskbar jump list invokes the app with a small set of
internal verbs. These are not part of a stable public CLI and may
change between releases. They are documented here only so that
behavior is predictable when you see them in process command-lines.

| Verb | Purpose | Source |
|---|---|---|
| `--new-evaluation` | Navigates the running shell to the wizard's first step. Used by the jump list's **New evaluation** task. | `Services/JumpListService.cs` |
| `--job-id "<jobId>"` | Locates the job in the workspace and opens its folder in File Explorer via `Process.Start` with the `open` shell verb. Used by the jump list's recent-jobs entries. | `Services/JumpListService.cs` + `App.HandleVerb` |
| `--open-file "<path>"` | Routes the given file path through `FileActivationRouter` exactly as if the file had been opened from Windows Explorer. Used internally by the MSIX file-type-association activation pipeline. | `App.HandleVerb` |

These verbs are not honored before single-instance arbitration; if a
running primary exists, they are forwarded to it. If no primary
exists, the cold-start activation pipeline routes them after the
shell window has finished initializing.
