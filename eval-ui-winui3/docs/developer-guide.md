# Developer guide

How to build, test, package, and sign EvalToolkit (WinUI 3) locally,
and how the CI pipelines mirror those steps.

For Azure Trusted Signing one-time setup (OIDC, environment
protection, GitHub variables/secrets), see
[ci-release-setup.md](ci-release-setup.md).

## Prerequisites

- **Windows 10 1809 (build 17763) or later.** Windows 11 recommended
  for Mica/Acrylic theming.
- **.NET 10 SDK** (LTS, November 2025).
- **Node.js 20+** — only required if you run the parity test harness,
  which shells out to the original TypeScript `eval-gen` to compare
  reader / writer output byte-for-byte.
- **Windows App SDK 1.5+** runtime is bundled via NuGet; no separate
  install required for builds.

## Solution layout

See [`../README.md` → Develop → Project layout`](../README.md#project-layout)
for the table of projects. Briefly:

```
eval-ui-winui3/
├── src/
│   ├── EvalToolkit.Core/         Models, env-var helpers, job storage, log sinks
│   ├── EvalToolkit.WorkIQ/       WorkIQ MCP/CLI + A2A clients (incl. MSAL cache plugin)
│   ├── EvalToolkit.EvalGen/      C# port of the eval-gen engine
│   ├── EvalToolkit.EvalScore/    C# port of the eval-score engine
│   ├── EvalToolkit.Cli/          Native CLI shims (eval-gen-native.exe, eval-score-native.exe)
│   └── EvalToolkit.UI/           WinUI 3 head
├── tests/
│   ├── EvalToolkit.EvalGen.Tests/    xUnit
│   ├── EvalToolkit.EvalScore.Tests/  xUnit
│   └── EvalToolkit.Parity.Tests/     Cross-runtime diff harness vs. TS impl
├── packaging/
│   ├── msix/                     Single-project MSIX build + signing scripts
│   └── portable/                 (placeholder for future portable ZIP layout)
└── docs/                         You are here
```

## Build and test

From a developer PowerShell:

```powershell
cd eval-ui-winui3
dotnet restore
dotnet build
dotnet test
```

The first `dotnet test` run is slower because the parity harness
installs and restores the upstream TypeScript `eval-gen` project on
demand.

To run a single test project:

```powershell
dotnet test tests\EvalToolkit.EvalGen.Tests
```

To run a single test by filter:

```powershell
dotnet test --filter "FullyQualifiedName~CsvReader"
```

## Package an MSIX locally

The MSIX build is driven by `packaging/msix/build-msix.ps1`. It
invokes `dotnet build` with the WinUI 3 single-project MSIX
properties set, then produces an **unsigned** `.msix` under
`packaging/msix/dist/<arch>/`. Signing is a separate step (see
below).

```powershell
.\packaging\msix\build-msix.ps1 -Configuration Release -Arch x64
```

Override the manifest version with `-PackageVersion 0.1.2.0` when
preparing a release candidate. For ARM64 pass `-Arch arm64`. The
default `-Arch both` builds both architectures sequentially.

## Sign an MSIX locally

`packaging/msix/sign-msix.ps1` supports two modes:

### Developer self-signed (`-Mode SelfSigned`)

For local smoke tests and the PR-gate workflow. Generates or reuses
a developer code-signing cert under `Cert:\CurrentUser\My`, signs
the MSIX, and (with `-TrustDevCert`) imports the public cert into
`Cert:\LocalMachine\TrustedPeople` so `Add-AppxPackage` will accept
it.

```powershell
.\packaging\msix\sign-msix.ps1 `
  -MsixPath .\packaging\msix\dist\x64\EvalToolkit.UI_0.1.0.0_x64.msix `
  -Mode SelfSigned `
  -TrustDevCert `
  -VerifyInstall
```

`-TrustDevCert` requires an elevated PowerShell (writing to
`LocalMachine` requires admin). `-VerifyInstall` adds a post-sign
`Add-AppxPackage` / `Remove-AppxPackage` round-trip to confirm the
signed package can install on this machine.

### Azure Trusted Signing (`-Mode AzureTrustedSigning`)

For CI release builds. Defers signing to the Microsoft Trusted
Signing dlib called by `signtool sign /dlib`. Requires Trusted
Signing OIDC and the publisher subject:

```powershell
.\packaging\msix\sign-msix.ps1 `
  -MsixPath .\packaging\msix\dist\x64\EvalToolkit.UI_0.1.0.0_x64.msix `
  -Mode AzureTrustedSigning `
  -SigningPublisher 'CN=Contoso, O=Contoso Corp, L=Redmond, S=WA, C=US' `
  -TrustedSigningMetadataPath .\trusted-signing-metadata.json
```

Local devs almost never run this mode — it requires an active Azure
session bound to an OIDC-federated identity that the CI workflow
sets up automatically. See [ci-release-setup.md](ci-release-setup.md).

## Local end-to-end smoke

For routine "I just want to try the app on my own machine" the fastest
path is the self-elevating one-shot helper. From a normal (non-elevated)
PowerShell:

```powershell
.\packaging\msix\build-msix.ps1 -Configuration Release -Arch x64
.\packaging\msix\sign-msix.ps1 -MsixPath .\packaging\msix\dist\x64\EvalToolkit.UI_0.1.0.0_x64.msix -Mode SelfSigned
.\packaging\msix\install-locally.ps1
```

`install-locally.ps1` picks the most recent signed MSIX matching your
host architecture, prompts UAC, imports the dev cert into
`LocalMachine\TrustedPeople`, runs `Add-AppxPackage
-ForceApplicationShutdown`, and prints the resulting package entry.

To run each step manually instead (e.g. to capture intermediate
output or pin a specific MSIX):

```powershell
# 1. Build
.\packaging\msix\build-msix.ps1 -Configuration Release -Arch x64

# 2. Sign + trust dev cert (elevated PowerShell)
.\packaging\msix\sign-msix.ps1 `
  -MsixPath .\packaging\msix\dist\x64\EvalToolkit.UI_0.1.0.0_x64.msix `
  -Mode SelfSigned -TrustDevCert -VerifyInstall

# 3. Install
Add-AppxPackage .\packaging\msix\dist\x64\EvalToolkit.UI_0.1.0.0_x64.msix -ForceApplicationShutdown

# 4. Headless smoke
$exe = Join-Path (Get-AppxPackage EvalToolkit.UI).InstallLocation 'EvalToolkit.UI.exe'
& $exe --diagnostics --diagnostics-out "$env:TEMP\diag.json"
if ($LASTEXITCODE -ne 0) { throw "Diagnostics exit $LASTEXITCODE" }

# 5. GUI launch
$aumid = (Get-AppxPackage EvalToolkit.UI).PackageFamilyName + '!App'
Start-Process "shell:AppsFolder\$aumid"

# 6. Uninstall when done
Get-AppxPackage EvalToolkit.UI | Remove-AppxPackage
```

## CI pipelines

The repo ships two workflow files under `.github/workflows/`.

### `build-evaltoolkit-winui3.yml` — PR gate

Runs on every PR and push to `main`. Two jobs:

1. **build** — restore / build / test, then produce an unsigned x64
   MSIX uploaded as the `evaltoolkit-msix-x64-unsigned-pr` artifact.
2. **ui-smoke** — downloads the unsigned MSIX from job 1, signs it
   with a dev cert via `sign-msix.ps1 -Mode SelfSigned -TrustDevCert`,
   installs it on the runner, exercises the `--diagnostics` headless
   flag, verifies the GUI process launches and stays alive for 15
   seconds, and optionally runs a WinAppDriver UIA window-title
   check. Diagnostics JSON is uploaded as an artifact on failure.

### `release-evaltoolkit-winui3.yml` — tag-driven release

Triggered by pushing a tag matching `eval-ui-winui3-v*`. Three
sequential jobs:

1. **build** — matrix over `x64` + `arm64`. Restore / build / test
   for x64; build unsigned MSIXes for both arches; upload as
   per-arch artifacts.
2. **sign** — matrix over `x64` + `arm64`. Each runs under the
   `release` GitHub environment (which provides required reviewers,
   OIDC federation, and Trusted Signing credentials), signs the
   matching MSIX via `sign-msix.ps1 -Mode AzureTrustedSigning`, and
   uploads as a `*-signed` artifact.
3. **publish** — downloads both signed MSIXes, fails closed if a
   release with the same tag already exists, and creates the GitHub
   release with both MSIXes attached.

Repo-admin one-time setup (federated identity credential, GitHub
variables / secrets, environment protection rules) is documented in
[ci-release-setup.md](ci-release-setup.md).

## Releasing a new version

1. Bump `<Version>` in `src/EvalToolkit.UI/Package.appxmanifest`'s
   `<Identity>` element. Use a four-part `MAJOR.MINOR.PATCH.0`
   number per MSIX rules; the final segment must remain `0`.
2. Commit the version bump to `main` and let the PR-gate run.
3. Tag the commit `git tag eval-ui-winui3-v<MAJOR>.<MINOR>.<PATCH>`
   (note the `v` prefix and that the tag does **not** include the
   trailing `.0`).
4. `git push origin <tag>`. The release pipeline takes over.
5. Once the release is published, link it from any internal
   changelog or release notes you keep outside this repo.

## Updating documentation

User-facing documentation lives under `eval-ui-winui3/docs/`. When
adding a doc, also add a row to [docs/README.md](README.md)'s index.
Top-level `README.md` and `eval-ui-winui3/README.md` should remain
short-and-orienting; deep content lives in `docs/`.
