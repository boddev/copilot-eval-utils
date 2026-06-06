# Troubleshooting

Common issues with EvalToolkit (WinUI 3) installs and runs.

## Install fails

### `0x800B0109` — A certificate chain processed, but terminated in a root certificate which is not trusted by the trust provider

You're installing a PR-gate or developer self-signed MSIX whose root
certificate is not in `LocalMachine\TrustedPeople`. Production
releases published to the GitHub Releases page are signed via Azure
Trusted Signing and should not produce this error.

For developer builds, the easiest path is the self-elevating helper:

```powershell
# From a normal (non-elevated) PowerShell — UAC prompt fires for you:
.\eval-ui-winui3\packaging\msix\install-locally.ps1
```

`install-locally.ps1` picks the most recent signed MSIX for your host
architecture, trusts the dev cert in `Cert:\LocalMachine\TrustedPeople`,
runs `Add-AppxPackage -ForceApplicationShutdown`, and verifies.

If you'd rather drive the steps manually (e.g. to install a specific
file or capture intermediate output), run the underlying signer
script from an **already-elevated** PowerShell instead:

```powershell
.\eval-ui-winui3\packaging\msix\sign-msix.ps1 `
    -MsixPath <path-to-signed.msix> `
    -Mode SelfSigned -TrustDevCert -VerifyInstall
```

For PR-gate artifacts you downloaded from a workflow run, install
the dev cert from the workflow's signed artifact set before
`Add-AppxPackage`. See [developer-guide.md](developer-guide.md).

### `0x800B0100` — No signature was present in the subject

The MSIX file is unsigned. Sign it before installing, or download a
signed release from the GitHub Releases page.

### `0x80073CFD` — A Prerequisite for an install could not be satisfied (architecture mismatch)

You downloaded the wrong-architecture MSIX. ARM64 Windows requires
`*_arm64.msix`; Intel/AMD64 Windows requires `*_x64.msix`.

### `0x80073CFB` — The package being deployed conflicts with an installed package, or `0x80073D06` — The deployment package is already installed at a higher version

A prior version of EvalToolkit is already installed. Either install
the new MSIX with `-ForceApplicationShutdown` (which closes any
running EvalToolkit instance and replaces it):

```powershell
Add-AppxPackage -Path .\EvalToolkit.UI_<version>_x64.msix -ForceApplicationShutdown
```

…or uninstall the existing version first and re-install fresh.

### `Add-AppxPackage` prompts about sideloading or installation from outside the Microsoft Store

Windows 10 2004+ and Windows 11 generally accept signed MSIX
installs from outside the Microsoft Store without requiring a
sideloading toggle, provided the certificate chains to a trusted
root. If a tenant policy is blocking the install, an administrator
must enable sideloading or developer mode:

- **Windows 11:** Settings → Privacy & security → For developers →
  **Developer Mode**.
- **Windows 10:** Settings → Update & Security → For developers →
  **Sideload apps** or **Developer mode**.

Production EvalToolkit releases are signed via Azure Trusted Signing
and typically do not require this for users on default-policy
machines.

## App launches then exits silently

Run EvalToolkit with `--diagnostics` from the MSIX install location
and read the JSON output:

```powershell
$exe = Join-Path (Get-AppxPackage EvalToolkit.UI).InstallLocation 'EvalToolkit.UI.exe'
& $exe --diagnostics --diagnostics-out "$env:TEMP\evaltoolkit-diag.json"
notepad "$env:TEMP\evaltoolkit-diag.json"
```

Look for any check whose `severity` is `Red` — that's the blocking
issue. `Yellow` entries are warnings only and won't prevent the GUI
from launching.

## Authentication keeps re-prompting

Force a fresh MSAL token cache by deleting the DPAPI-encrypted cache
file:

```powershell
Remove-Item "$env:LOCALAPPDATA\EvalToolkit\msal-a2a-cache.bin" -ErrorAction SilentlyContinue
```

If stale auth still returns, you may have a legacy plaintext JSON
cache from a Node CLI install that EvalToolkit imports on first
launch. Delete that too:

```powershell
Remove-Item "$env:USERPROFILE\.evalscore\msal-a2a-cache.json" -ErrorAction SilentlyContinue
```

Re-launch EvalToolkit and re-authenticate. Both deletions are safe:
they only remove cached tokens, not your account itself.

## Workspace cleanup / reset

The per-user workspace lives at `%LOCALAPPDATA%\EvalToolkit\workspace`.
Generated eval-sets, sidecars, and scored results are written under
`workspace\jobs\<jobId>\` unless you explicitly exported them
elsewhere during the wizard.

**Deleting the workspace folder removes local job history AND any
generated or scored artifacts still stored under it.** Files you
copied or exported elsewhere are unaffected.

```powershell
Remove-Item "$env:LOCALAPPDATA\EvalToolkit\workspace" -Recurse -Force
```

The next launch of EvalToolkit will recreate an empty workspace.

## Jump list shows stale or missing entries

The Windows shell caches jump-list entries per AUMID. If the list is
stale:

1. Toggle Settings → Personalization → Start → **Show recently opened
   items in Start, Jump Lists, and File Explorer** off, then on.
2. Or delete the AutomaticDestinations cache entry for
   EvalToolkit's AUMID:

   ```powershell
   Get-ChildItem "$env:APPDATA\Microsoft\Windows\Recent\AutomaticDestinations" |
       Where-Object Length -gt 0 |
       Remove-Item -Force
   ```

   (This clears every app's jump-list cache, not just EvalToolkit's;
   Windows rebuilds them on next use.)

As a last resort, `Remove-AppxPackage` + reinstall guarantees a
fresh jump list because the AUMID is re-published.

## Toast notifications never appear

Notifications respect Windows' per-app notification toggle. Check:

- Settings → System → Notifications → **EvalToolkit** is enabled.
- Focus Assist / Do Not Disturb is off.
- The notification volume is not zero.

Jobs still complete normally; only the toast is suppressed.

## File activation does not open in EvalToolkit

Only `.evalgenset`, `.evalscoreresults`, and `.evalreport` are
registered for EvalToolkit. Canonical `.csv`, `.md`, and `.json`
files keep whatever system default you've configured (typically
Excel, your markdown previewer, or VS Code).

To route a specific file through EvalToolkit, either:

- Rename it to the corresponding alias extension and place it
  beside the canonical companion file (e.g., create `foo.evalgenset`
  beside `foo.csv` + `foo.evalgen.json` and double-click it), or
- Open EvalToolkit and use the wizard's file picker.

## "App Installer says this file is missing dependencies"

EvalToolkit production builds are configured with `SelfContained=true`
and `WindowsAppSDKSelfContained=true`, so the .NET runtime and
Windows App SDK components ship inside the MSIX. If you see a
missing-dependency error from App Installer:

1. Verify you downloaded the MSIX from a real release — partially
   downloaded or corrupted files can produce confusing dependency
   errors. Re-download from the GitHub Releases page and verify the
   file size matches what GitHub reports.
2. Ensure App Installer is up to date via the Microsoft Store
   (search for "App Installer").
3. As a fallback, install via `Add-AppxPackage` directly from
   PowerShell — it produces a clearer error message than App
   Installer when something is wrong.

## "I can't find the portable EvalToolkit.UI.exe"

There is no portable WinUI 3 GUI build. The only portable
distribution under `packaging/portable/dist/` is the
**CLI bundle** containing three single-file shims:

- `EvalToolkit.Cli.exe` — full CLI front-door with `eval-gen` and
  `eval-score` subcommands.
- `eval-gen-native.exe` — eval-gen, direct entry point.
- `eval-score-native.exe` — eval-score, direct entry point.

All three run from any directory with no install. None of them open
a window — they are command-line tools. Run them from a PowerShell
prompt:

```powershell
.\EvalToolkit.Cli.exe --help
.\eval-gen-native.exe --help
.\eval-score-native.exe --help
```

The WinUI 3 GUI requires MSIX packaging (file associations, COM
toast activator, package-identity jump list) and is not distributed
as a standalone executable. Install the MSIX instead — see
[user-guide.md → Installing](user-guide.md) or the production
release at https://github.com/boddev/copilot-eval-utils/releases.

