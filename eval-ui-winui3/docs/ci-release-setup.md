# CI release setup — EvalToolkit (WinUI 3)

How to configure GitHub + Azure so `.github/workflows/release-evaltoolkit-winui3.yml`
can produce signed MSIX release artifacts.

This is a one-time setup. Once configured, a tag matching
`eval-ui-winui3-v*` (e.g. `eval-ui-winui3-v0.1.0`) automatically
builds, signs, and publishes a GitHub Release with x64 + arm64
signed MSIXes attached.

## Architecture overview

```
push tag eval-ui-winui3-v0.1.0
     │
     ▼
┌──────────────────────────────────────────────────┐
│ release-evaltoolkit-winui3.yml                   │
│                                                  │
│  ┌─────────────────┐    ┌─────────────────┐      │
│  │ build (matrix)  │    │ build (matrix)  │      │
│  │  arch: x64      │    │  arch: arm64    │      │
│  │  - test (x64)   │    │                 │      │
│  │  - build-msix   │    │  - build-msix   │      │
│  └────────┬────────┘    └────────┬────────┘      │
│           │ upload unsigned       │              │
│           ▼                       ▼              │
│  ┌─────────────────┐    ┌─────────────────┐      │
│  │ sign (matrix)   │    │ sign (matrix)   │      │
│  │  environment:   │    │  environment:   │      │
│  │    release      │    │    release      │      │
│  │  - OIDC login   │    │  - OIDC login   │      │
│  │  - sign-msix    │    │  - sign-msix    │      │
│  └────────┬────────┘    └────────┬────────┘      │
│           │ upload signed         │              │
│           └──────────┬────────────┘              │
│                      ▼                           │
│            ┌───────────────────┐                 │
│            │ publish           │                 │
│            │  - gh release     │                 │
│            │    create (fail   │                 │
│            │    if exists)     │                 │
│            └───────────────────┘                 │
└──────────────────────────────────────────────────┘
```

## Azure setup

### 1. Azure Trusted Signing account + certificate profile

You need an Azure subscription with:

- A **Trusted Signing account** (resource type
  `Microsoft.CodeSigning/codeSigningAccounts`).
- A **certificate profile** under that account (resource type
  `Microsoft.CodeSigning/codeSigningAccounts/certificateProfiles`).
  The profile defines the cert Subject DN that will appear in
  signed MSIX manifests.

Capture three values from the profile:

| Value                            | Where to find                                                  |
| -------------------------------- | -------------------------------------------------------------- |
| **Endpoint URL**                 | Trusted Signing account → Overview → "Account URI" (e.g. `https://eus.codesigning.azure.net`) |
| **Account name**                 | Trusted Signing account → Name                                 |
| **Certificate profile name**     | Trusted Signing account → Certificate profiles → Name          |
| **Canonical Subject DN**         | Trusted Signing account → Certificate profiles → Identity validation → Subject; e.g. `CN=Contoso, O=Contoso Corp, L=Redmond, S=WA, C=US` |

The Subject DN is the source of truth for the MSIX `Identity Publisher`
attribute and must be byte-for-byte identical to what the Trusted
Signing service issues — `sign-msix.ps1` patches the manifest with
this value and asserts post-sign that the signer cert Subject matches.

### 2. Microsoft Entra app registration with federated identity credential

The release workflow authenticates to Trusted Signing using OIDC
(workload identity federation), so **no client secrets are required**.

1. Register an application in Microsoft Entra ID (Azure AD).
2. Note the **Application (client) ID**, **Directory (tenant) ID**, and
   the **Subscription ID** of the subscription holding the Trusted
   Signing account.
3. Under the app's **Certificates & secrets → Federated credentials**,
   add a credential of type **GitHub Actions deploying Azure resources**
   with these values:

   | Field            | Value                                                 |
   | ---------------- | ----------------------------------------------------- |
   | Organization     | `boddev`                                              |
   | Repository       | `copilot-eval-utils`                                  |
   | Entity type      | **Environment**                                       |
   | Environment name | `release`                                             |
   | Name             | `github-release-env` (any non-empty value)            |

   The resulting OIDC subject claim is
   `repo:boddev/copilot-eval-utils:environment:release`. Per GPT-5.5
   slice-35 plan-review BLOCKER #1, standard FICs require an exact
   subject match — wildcards in the tag namespace (e.g.
   `refs/tags/eval-ui-winui3-v*`) are not supported by the standard
   FIC flow. The environment-bound approach is the recommended pattern.

### 3. Trusted Signing RBAC for the Entra app

Grant the Entra app the **Trusted Signing Certificate Profile Signer**
role on the certificate profile (scoped to the profile, not the
subscription).

```bash
az role assignment create \
  --role "Trusted Signing Certificate Profile Signer" \
  --assignee <APP_CLIENT_ID> \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RG>/providers/Microsoft.CodeSigning/codeSigningAccounts/<ACCOUNT>/certificateProfiles/<PROFILE>"
```

## GitHub setup

### 1. Create the `release` environment

In repository settings → Environments → **New environment** named
`release`. Configure:

- **Deployment branches and tags** → "Selected branches and tags" →
  add a rule for tag pattern `eval-ui-winui3-v*`. This ensures the
  environment (and the OIDC token bound to it) can only be requested
  by workflow runs triggered by matching tags.
- *Optional*: required reviewers, wait timer, etc.

### 2. Repository **secrets** (Settings → Secrets and variables → Actions → Secrets)

Per GPT-5.5 slice-35 plan-review NB #3, Azure identifiers go in
`secrets` for masking (they are not credentials per se, but Microsoft
docs recommend it):

| Secret name             | Value                                                                      |
| ----------------------- | -------------------------------------------------------------------------- |
| `AZURE_TENANT_ID`       | Tenant ID of the Entra app                                                 |
| `AZURE_CLIENT_ID`       | Application (client) ID of the Entra app                                   |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID containing the Trusted Signing account                     |

### 3. Repository **variables** (Settings → Secrets and variables → Actions → Variables)

Non-sensitive configuration goes in `vars`:

| Variable name                   | Value                                                                       |
| ------------------------------- | --------------------------------------------------------------------------- |
| `TRUSTED_SIGNING_ENDPOINT`      | Trusted Signing account URI (e.g. `https://eus.codesigning.azure.net`)      |
| `TRUSTED_SIGNING_ACCOUNT_NAME`  | Trusted Signing account name                                                |
| `TRUSTED_SIGNING_PROFILE_NAME`  | Certificate profile name                                                    |
| `TRUSTED_SIGNING_PUBLISHER`     | Canonical Subject DN of the certificate profile (byte-for-byte; see above)  |

## How to ship a release

```bash
# 1. Tag a commit on main.
git tag eval-ui-winui3-v0.1.0
git push origin eval-ui-winui3-v0.1.0

# 2. Watch the workflow at:
#    https://github.com/boddev/copilot-eval-utils/actions/workflows/release-evaltoolkit-winui3.yml
#
# 3. The release will be created at:
#    https://github.com/boddev/copilot-eval-utils/releases/tag/eval-ui-winui3-v0.1.0
```

## Dry-run (no signing, no publish)

Trigger the workflow manually from any branch via Actions → "Release
EvalToolkit (WinUI 3)" → **Run workflow**. The `build` job runs to
completion and produces unsigned MSIXes as workflow artifacts. The
`sign` and `publish` jobs are gated on
`startsWith(github.ref, 'refs/tags/eval-ui-winui3-v')` and skip
automatically.

This is the right way to validate workflow YAML changes before
committing to a real tag — no Azure auth, no risk of an unwanted
release.

## How to replace an existing release

Per GPT-5.5 slice-35 plan-review NB #10, the `publish` job
**fails closed** if a release with the given tag already exists. This
protects manually edited notes / assets from being clobbered by a
re-triggered workflow.

To replace a release intentionally:

```bash
# 1. Delete the existing release (this also removes the tag if
#    --cleanup-tag is passed):
gh release delete eval-ui-winui3-v0.1.0 --cleanup-tag --yes

# 2. Re-tag and push:
git tag eval-ui-winui3-v0.1.0
git push origin eval-ui-winui3-v0.1.0
```

## How to verify a release was signed correctly

After download:

```pwsh
# 1. Signer cert chain check:
Get-AuthenticodeSignature .\EvalToolkit.UI_0.1.0.0_x64.msix |
    Format-List Status, SignerCertificate, TimeStamperCertificate

# Expected: Status = Valid, Signer = CN=<your TRUSTED_SIGNING_PUBLISHER>

# 2. Cross-check manifest Publisher matches signer Subject:
$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1
$tmp = New-Item -ItemType Directory -Path "$env:TEMP\verify-$(New-Guid)"
& $sdk.FullName unpack /p .\EvalToolkit.UI_0.1.0.0_x64.msix /d $tmp.FullName /nv
[xml]$mf = Get-Content "$($tmp.FullName)\AppxManifest.xml"
$mf.Package.Identity.Publisher    # should equal TRUSTED_SIGNING_PUBLISHER

# 3. signtool /pa chain verify (requires Trusted Signing root in trust store):
& "$($sdk.FullName -replace 'makeappx', 'signtool')" verify /pa /v `
    .\EvalToolkit.UI_0.1.0.0_x64.msix
```

## Troubleshooting

| Symptom                                                | Likely cause                                                                                 |
| ------------------------------------------------------ | -------------------------------------------------------------------------------------------- |
| `azure/login` fails with "no matching FIC"             | Environment is not named `release` or FIC subject does not match `repo:.../environment:release` |
| `sign-msix.ps1` fails with "Azure.CodeSigning.Dlib.dll not found" | `Microsoft.ArtifactSigning.Client` install step failed — check `ARTIFACT_SIGNING_VERSION` is still published on nuget.org |
| `sign-msix.ps1` fails with publisher mismatch          | `TRUSTED_SIGNING_PUBLISHER` repo var does not byte-for-byte match the cert profile Subject DN |
| `gh release create` fails with "release already exists" | Intentional — see "How to replace an existing release" above                                 |
| Unsigned MSIX missing from artifacts                   | `build-msix.ps1` failed before upload — check the `Build MSIX` step log                      |
