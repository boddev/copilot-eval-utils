# MSIX packaging assets

Empty until the **phase C `msix-packaging` todo** lands. Will contain:

- `Package.appxmanifest` extras (FTAs for app-owned extensions only,
  jump-list activation hooks — see plan Section 7).
- PRI resource extras (icons, scale-200 / -400 assets).
- Signing helper scripts (Azure Trusted Signing in prod; self-signed
  PFX wiring for nightly).

This file exists so the directory survives a clone (git does not track
empty directories).

---

## Slice 30: File-Type Association (FTA) manifest fragment

Slice 30 (`winui-native-plus-fta`) ships the **routing code** that
dispatches a single activated file path to the correct wizard step or
system handler — but it does NOT register the FTAs with the shell
because the app is still unpackaged through slice 30. The MSIX
packaging slice will consume this fragment and merge it into
`Package.appxmanifest`.

### App-owned extensions only

Per plan Section 7, the manifest registers **three app-owned alias
extensions** (never `.csv`, `.md`, or `.json` system-wide):

| Extension              | Companion (legacy)         | What it represents              |
| ---------------------- | -------------------------- | ------------------------------- |
| `.evalgenset`          | `<basename>.evalgen.json`  | Eval-set sidecar (JSON, opens the wizard's Step 4 row editor) |
| `.evalscoreresults`    | `<basename>-results.csv`   | Scored eval CSV (opens default CSV handler — Excel) |
| `.evalreport`          | `<basename>-report.md`     | Markdown report (opens default Markdown handler — browser) |

The slice 30 router strips the alias suffix and routes:

- `<basename>.evalgenset` → derive `<basename>.csv` sibling and open
  in-app at Step 4 (wizard editor) with the sidecar preloaded.
- `<basename>-results.evalscoreresults` → derive `<basename>-results.csv`
  sibling and shell-open it (Excel). The `-results` part is preserved
  because the alias suffix is `.evalscoreresults` only — see
  `FileActivationRouter.ReplaceTrailingExtension`. Never the alias
  itself, or we loop back into our own FTA.
- `<basename>-report.evalreport` → derive `<basename>-report.md`
  sibling and shell-open it (browser) — same `-report` preservation
  and loop-avoidance rule.

The legacy artifact suffixes (`-results.csv`, `-report.md`, and the
double-extension sidecar `.evalgen.json`) are also recognised by the
router today (for the `--open-file` CLI verb), but they are NOT
registered as FTAs to avoid claiming `.csv` / `.md` / `.json`
system-wide.

### Manifest fragment

Per GPT-5.5 slice-30 plan-review (BLOCKER #3): the `uap:Extension`
element holds **at most one** `uap:FileTypeAssociation`, so three
distinct artifact types require three separate `uap:Extension` blocks.
Drop this fragment inside `<Application>` → `<Extensions>` in
`Package.appxmanifest`.

```xml
<Extensions>
  <!-- Sidecar / eval set -->
  <uap:Extension Category="windows.fileTypeAssociation">
    <uap:FileTypeAssociation Name="evaltoolkit-evalset">
      <uap:DisplayName>EvalToolkit Eval Set</uap:DisplayName>
      <uap:Logo>Assets\evalset-logo.png</uap:Logo>
      <uap:SupportedFileTypes>
        <uap:FileType ContentType="application/vnd.evaltoolkit.evalset+json">.evalgenset</uap:FileType>
      </uap:SupportedFileTypes>
    </uap:FileTypeAssociation>
  </uap:Extension>

  <!-- Scored results CSV (alias) -->
  <uap:Extension Category="windows.fileTypeAssociation">
    <uap:FileTypeAssociation Name="evaltoolkit-results">
      <uap:DisplayName>EvalToolkit Score Results</uap:DisplayName>
      <uap:Logo>Assets\results-logo.png</uap:Logo>
      <uap:SupportedFileTypes>
        <uap:FileType ContentType="text/csv">.evalscoreresults</uap:FileType>
      </uap:SupportedFileTypes>
    </uap:FileTypeAssociation>
  </uap:Extension>

  <!-- Markdown report (alias) -->
  <uap:Extension Category="windows.fileTypeAssociation">
    <uap:FileTypeAssociation Name="evaltoolkit-report">
      <uap:DisplayName>EvalToolkit Eval Report</uap:DisplayName>
      <uap:Logo>Assets\report-logo.png</uap:Logo>
      <uap:SupportedFileTypes>
        <uap:FileType ContentType="text/markdown">.evalreport</uap:FileType>
      </uap:SupportedFileTypes>
    </uap:FileTypeAssociation>
  </uap:Extension>
</Extensions>
```

Conventions:

- `Name` is lower-case kebab and stable — Windows uses it as the ProgID
  key. Do not rename without a migration shim.
- `Logo` paths point at packaged asset PNGs (slice 31 ships the assets
  themselves).
- `ContentType` is informational on Windows but populates the MIME hint
  for some HTTP / share-target scenarios; values chosen as best-fit.

### Slice 31 to-do: alias-copy emission

Slice 30 ships the routing but does NOT modify the artifact writers to
emit alias copies. The simplest path for slice 31 (msix-packaging):

1. After `SidecarJsonWriter` finishes `<basename>.evalgen.json`, copy
   the file to `<basename>.evalgenset` (same bytes — both are JSON).
2. After `ResultsCsvWriter` finishes `<basename>-results.csv`, copy
   it to `<basename>-results.evalscoreresults` (same bytes — both are
   CSV).
3. After report rendering finishes `<basename>-report.md`, copy it to
   `<basename>-report.evalreport` (same bytes — both are Markdown).

Copies preserve Node-tool parity (legacy names unchanged) while
giving shell users an app-owned name to double-click. Hard-link or
copy-on-write is acceptable when the FS supports it; a plain
`File.Copy` is fine for the slice-31 scope.

### `--open-file` CLI verb (testable today)

Even without MSIX, slice 30's routing is exercisable via:

```pwsh
EvalToolkit.UI.exe --open-file "C:\path\to\eval-set.evalgen.json"
```

This synthesises the same `OpenEvalSetRequest` that an FTA activation
would produce, so the smoke test for slice 30 doesn't need an
installed MSIX.


---

## Slice 31: Toast notifications COM-activator manifest fragment

Slice 31 (`winui-native-plus-toasts`) hardens the **runtime** activator
plumbing  subscribe-before-register ordering, cold-start
`AppActivationArguments` routing, shared `NotificationActionRouter`
with dedupe + path validation. The MSIX packaging slice (32) consumes
the manifest fragment below so the OS toast platform can cold-start
the app when the user clicks a notification while the app is closed.

### Why a COM activator is required

`Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register()`
in an **unpackaged** dev run lazily registers a COM activator class
under the current PID, which dies with the process. For a **packaged**
app, the OS needs a permanent CLSID it can launch on demand  that
CLSID is declared in the package manifest via the
`windows.comServer` extension and tagged as the toast activator via
the `desktop:ToastNotificationActivation` extension.

### Manifest fragment

Insert into `Package.appxmanifest` under
`<Package><Applications><Application><Extensions>`:

```xml
<Extensions>
  <com:Extension Category="windows.comServer">
    <com:ComServer>
      <com:ExeServer
        Executable="EvalToolkit.UI.exe"
        DisplayName="EvalToolkit Notifications"
        Arguments="----AppNotificationActivated:">
        <com:Class
          Id="REPLACE-WITH-STABLE-GUID"
          DisplayName="EvalToolkit Toast Activator" />
      </com:ExeServer>
    </com:ComServer>
  </com:Extension>

  <desktop:Extension Category="windows.toastNotificationActivation">
    <desktop:ToastNotificationActivation
      ToastActivatorCLSID="REPLACE-WITH-STABLE-GUID" />
  </desktop:Extension>
</Extensions>
```

Key requirements (GPT-5.5 slice 31 plan-review BLOCKER #2):

- `Arguments="----AppNotificationActivated:"` on the `<com:ExeServer>`
  is **mandatory**  the Windows App SDK inspects the command-line
  prefix `----AppNotificationActivated:` to know an OS-spawned launch
  is for a toast activation and not a normal user launch. Without this
  exact prefix, cold-start toast clicks reach the app but fail to
  surface as `AppNotificationActivatedEventArgs`.
- The CLSID **must match** in both extensions. Slice 32 (packaging)
  will generate a stable GUID via `New-Guid` and substitute it into
  both `Id="..."` and `ToastActivatorCLSID="..."`. The GUID never
  changes across versions  bumping it would orphan toasts already
  pinned in Action Center.
- `Executable="EvalToolkit.UI.exe"` is relative to the package root,
  matching the executable name the slice-32 publish step produces.

### Required namespaces

The fragment uses `com:` and `desktop:` namespace prefixes  add them
to the manifest root element if not already present:

```xml
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  IgnorableNamespaces="com desktop ...">
```

### How the slice-31 runtime consumes this

When the OS toast platform launches the app via this COM activator
class (cold-start toast click), the WAS runtime spins up the process
and synthesizes an `AppActivationArguments` of kind `AppNotification`
with the toast's arguments dictionary. Slice 31's flow:

1. `Program.Main` enters single-instance arbitration. The **primary**
   path no longer calls `GetActivatedEventArgs()` (BLOCKER #1 fix 
   WAS guidance: `Register()` must precede `GetActivatedEventArgs()`).
2. `App.OnLaunched` constructs `NotificationActionRouter` then
   `TrayIconService`, whose `Initialize` subscribes
   `NotificationInvoked` BEFORE calling `Register()`.
3. **AFTER** `Register()` completes, `OnLaunched` calls
   `AppInstance.GetCurrent().GetActivatedEventArgs()` and routes any
   non-Launch cold-start activation (AppNotification, File, Protocol)
   through `HandleActivation`.
4. `HandleActivation` casts `args.Data` to
   `AppNotificationActivatedEventArgs` and delegates to
   `NotificationActionRouter.Route(args.Arguments, "cold-AppActivation")`,
   which dedupes against any warm-start `NotificationInvoked` fire of
   the same payload.

### Smoke verification today (unpackaged)

Cold-start toast activation is not directly testable without the MSIX,
but the warm-start path is exercised whenever a job reaches a
terminal state while the shell window is hidden or backgrounded. The
`NotificationActionRouter`'s path-validation behavior is also
exercised by any toast  paths outside the workspace root are
rejected even in unpackaged dev. See `Smoke.ps1 -OpenFile` (slice 30)
for related coverage; slice 32 will add a packaged-app toast-click
manual smoke checklist.


---

## Slice 32: `--diagnostics` CLI verb

Slice 32 (`winui-diagnostics`) ships an in-app Diagnostics view plus a
headless `--diagnostics` verb that runs the same probes from CI without
launching the WinUI shell. The verb is dispatched **before**
single-instance arbitration, so running it never disturbs a primary
GUI instance.

### Usage

Two output modes — both produce identical camelCase JSON:

**File mode (recommended for CI).** Writes the report to a file,
independent of any console capture quirks. Works from any host (pwsh,
cmd, detached service, scheduled task):

```pwsh
EvalToolkit.UI.exe --diagnostics --diagnostics-out C:\path\to\diag.json
$report = Get-Content C:\path\to\diag.json -Raw | ConvertFrom-Json
```

Equivalent forms accepted: `--diagnostics-out=<path>`,
`/diagnostics-out <path>`, `/diagnostics-out=<path>`. Relative paths
are resolved against the current working directory. Missing parent
directories are created.

**Stdout mode (interactive use).** Writes JSON to the parent console,
captured by standard shell redirect:

```cmd
EvalToolkit.UI.exe --diagnostics > diag.json
```

```pwsh
# pwsh: use Start-Process -Wait or cmd, because `& exe` does not block
# on WinExe binaries.
Start-Process -FilePath EvalToolkit.UI.exe -ArgumentList @('--diagnostics') `
  -RedirectStandardOutput diag.json -Wait
```

Internally the runner calls `AttachConsole(ATTACH_PARENT_PROCESS)` only
when no inherited stdout handle is present, so cmd's `> file` redirect
(which preassigns stdout to the file handle) is preserved.

### Output schema

```jsonc
{
  "generatedAtUtc": "2026-06-06T14:07:08.7657245+00:00",
  "appVersion": "1.0.0.0",
  "workspace": {
    "path": "...",
    "exists": true, "writable": true, "creatable": false,
    "health": "green|yellow|red",
    "note": null
  },
  "webView2": {
    "runtimeAvailable": true,
    "bundledInstallerPresent": false,
    "bundledInstallerPath": "...",
    "manualInstallerUrl": "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
    "health": "green|yellow|red",
    "note": null
  },
  "notifications": {
    "registered": false,
    "health": "green|yellow|red",
    "note": "..."
  },
  "jumpList": {
    "initialized": false,
    "lastRefreshSucceeded": false,
    "lastRefreshUtc": null,
    "health": "green|yellow|red",
    "note": "..."
  },
  "process": {
    "pid": 13124,
    "exePath": "...",
    "configuredAumid": "EvalToolkit.UI",
    "actualAumid": null,
    "aumidError": "GetCurrentProcessExplicitAppUserModelID hr=0x80004005",
    "health": "green"
  },
  "overallHealth": "green|yellow|red"
}
```

`overallHealth` is the **worst-of** any section's health. In an
unpackaged dev build the report is typically `yellow` because
notifications register lazily (requires package identity) and the
headless probe never initializes the jump list. The packaged MSIX
build resolves both to `green`.

### Exit codes

| Code | Meaning |
| ---- | ------- |
| 0    | `overallHealth` is `green` or `yellow` (no blocking failures). |
| 1    | `overallHealth` is `red` (workspace unwritable, WebView2 missing with no bootstrapper, etc.). |
| 2    | The diagnostics collector itself threw. Error JSON written to stderr (and the `--diagnostics-out` file, if specified). |

### Why the verb dispatches before single-instance arbitration

`AppInstance.FindOrRegisterForKey` always returns the existing primary
when one is already running, so a secondary invocation that reached
that line would redirect activation to the GUI and exit without
emitting JSON. Slice 32 detects `--diagnostics` in `Program.Main`
before calling `FindOrRegisterForKey`, runs `HeadlessDiagnosticsRunner`
inline, and returns — `Application.Start` is never called, the GUI is
not affected, and the existing primary continues running undisturbed.

---

## Slice 33: MSIX packaging — single-project build

Slice 33 (`msix-packaging`) takes the runtime work shipped through
slices 21–32 and produces an **installable MSIX** from the existing
`src/EvalToolkit.UI/EvalToolkit.UI.csproj` via single-project MSIX
(no separate `.wapproj`). The package assembles the FTA / COM
activator fragments above into a real `Package.appxmanifest`, embeds
the WinUI 3 shell + self-contained WinAppSDK + .NET 10 runtime, and
emits one `.msix` per architecture into `packaging/msix/dist/<arch>/`.

### What slice 33 ships

| Artefact | Purpose |
| --- | --- |
| `src/EvalToolkit.UI/Package.appxmanifest` | Real manifest assembling Identity, FTAs, COM activator, toast activation, `runFullTrust` capability, and visual assets. |
| `src/EvalToolkit.UI/Services/NotificationActivatorIds.cs` | Compile-time constant for the stable toast-activator CLSID. Mirrors the GUID in the manifest — **do not change**, bumping orphans pinned Action Center toasts. |
| `src/EvalToolkit.UI/Assets/Packaging/*.png` | Placeholder logos (Square44x44, Square150x150, Wide310x150, StoreLogo, SplashScreen, 3 FTA icons) auto-generated by `build-msix.ps1` if missing. Slice 34 / branding will replace with real artwork. |
| `packaging/msix/build-msix.ps1` | One-shot build script: synthesises placeholder assets, cleans stale `AppPackages\`, drives `dotnet build /p:WindowsPackageType=MSIX` per architecture, copies the `.msix` into `dist/<arch>/`, prints SHA-256 + size. |

### Stable Toast Activator CLSID

> **`15FDE3FE-65CF-4E3D-8B0B-3C4B8B1BD68F`**

This GUID is **permanent** for the lifetime of the EvalToolkit MSIX
identity. It is referenced in three places — `Package.appxmanifest`
(`com:Class Id`, `desktop:ToastNotificationActivation
ToastActivatorCLSID`) and `Services/NotificationActivatorIds.cs`.
The Windows App SDK runtime auto-discovers it from the manifest at
`AppNotificationManager.Default.Register()` time; no managed code
passes the CLSID explicitly today. The constant exists so future
changes (e.g. a `WinRT.Activation.Register` P/Invoke for an
unpackaged toast fallback) have a single source of truth.

### How to build

Prereqs:

- .NET 10 SDK (the script uses `dotnet build`, NOT `msbuild.exe`
  from VS 2022 — the latter ships MSBuild 17.x which is too old for
  the .NET 10 SDK).
- Windows 10/11 with the Windows App SDK 2.x build prerequisites
  (Windows 10 SDK 10.0.26100 or newer for `makeappx.exe` validation).

```pwsh
# Both architectures (default):
pwsh .\packaging\msix\build-msix.ps1

# Single architecture:
pwsh .\packaging\msix\build-msix.ps1 -Arch x64

# Skip placeholder asset synthesis (CI with committed artwork):
pwsh .\packaging\msix\build-msix.ps1 -Arch x64 -SkipAssets
```

Outputs land at `packaging\msix\dist\x64\EvalToolkit.UI_*_x64.msix`
(and `arm64\...` when both architectures are requested). SHA-256
hashes and byte sizes are printed at the end for release-asset
recording.

### How to validate a produced MSIX

Slice 33 produces **unsigned, structurally-valid** MSIX. `makeappx
unpack` can extract and verify the manifest is well-formed, all
declared assets are present, and the EXE is at the package root:

```pwsh
$msix = 'packaging\msix\dist\x64\EvalToolkit.UI_0.1.0.0_x64.msix'
$makeappx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
& $makeappx unpack /nv /p $msix /d _unpack

# Inspect Identity / FTAs / COM activator / Capabilities:
[xml]$x = Get-Content _unpack\AppxManifest.xml -Raw
$ns = New-Object Xml.XmlNamespaceManager $x.NameTable
$ns.AddNamespace('m','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$x.SelectSingleNode('//m:Identity', $ns).OuterXml
```

### Installation: deferred to slice 34

`Add-AppxPackage` rejects unsigned MSIX by default. Slice 34
(`signing`) wires the dev self-signed cert helper and Trusted
Signing CI config so the produced packages become installable
without weakening the OS trust store. Trying to install slice-33
output via `Add-AppxPackage` will fail with HRESULT `0x800B0109`
("a certificate chain processed... terminated in a root certificate
which is not trusted") — that is expected for this slice.

For an **internal smoke / first-run experience** today you can:

1. Build the MSIX (`build-msix.ps1 -Arch x64`).
2. `makeappx unpack` to a working folder.
3. Run `EvalToolkit.UI.exe` directly from the unpacked folder — the
   self-contained runtime makes the package portable. The shell
   launches in **unpackaged mode** (no package identity, no FTAs,
   no toasts/jumplist auto-pin), which is exactly what slice 32's
   `--diagnostics` verb is designed to probe.

### MSBuild glue under the hood

Two pieces of build-script glue (in `EvalToolkit.UI.csproj`) make
this work:

1. **`AppxManifest` / `Content` / `GenerateAppxPackageOnBuild` are
   conditioned on `'$(WindowsPackageType)'=='MSIX'`.** Unpackaged dev
   (`dotnet build`, `dotnet run`) keeps the default
   `WindowsPackageType=None`, which would otherwise error out with
   "Improper project configuration: WindowsPackageType is set to
   None, but an AppxManifest is specified."

2. **A `_EvalToolkitEnsureMsixTaskSysPerm` target copies
   `System.Security.Permissions.dll` 8.0.0 next to the MSIX validate
   task assembly before the task runs.** The
   `Microsoft.Windows.SDK.BuildTools.MSIX 1.7.x` package's
   `WinAppSdkValidateAppxManifestItems` task binds to
   `System.Security.Permissions, Version=8.0.0.0` but does not
   redistribute the assembly. Under the .NET 10 SDK's task host the
   assembly is not on the default probe path; without the copy the
   task fails with `MSB4018 FileNotFoundException`. The target is
   itself conditioned on `WindowsPackageType=MSIX` so it never runs
   during unpackaged dev builds.




### GPT-5.5 code review adoption (slice 33)

After the implementation built clean and both arches produced
structurally valid MSIXes, GPT-5.5 code-reviewed the slice. No
BLOCKERs were identified for slice 33; one was deferred to slice 34
(see TODO in `Package.appxmanifest`). Three non-blockers were
adopted in-slice:

- **NB #1 fail-fast SSP missing.** The
  `_EvalToolkitEnsureMsixTaskSysPerm` target now emits a clear
  `<Error>` if the System.Security.Permissions assembly is not
  present in the restored package, instead of letting the build
  fall through to the opaque `MSB4018 FileNotFoundException` raised
  later by `WinAppSdkValidateAppxManifestItems`.
- **NB #2 build-private workaround package.** The
  `System.Security.Permissions` `PackageReference` is now marked
  `PrivateAssets="all"` with `ExcludeAssets="runtime;native;contentFiles;analyzers;buildTransitive"`
  so the assembly stays a build-time payload source only and does
  not flow into the app's runtime closure. Verified via
  `makeappx unpack` on the produced MSIX  `System.Security.Permissions.dll`
  is **not** present in the package. `GeneratePathProperty="true"`
  continues to expose `$(PkgSystem_Security_Permissions)` correctly
  under CPM with `PrivateAssets="all"`.
- **NB #7 clearer asset-synth error.** `New-PlaceholderPng` in
  `build-msix.ps1` now wraps `Add-Type -AssemblyName System.Drawing`
  in try/catch and throws an actionable message pointing the user
  to either run on a Windows host or pre-commit the PNGs and pass
  `-SkipAssets`, instead of an opaque type-resolution error from
  CI / minimal images.

The remaining non-blockers (capabilities completeness, wide-tile
asset, `mspdbcmf` cosmetic warning, `_Test` AppPackages suffix) were
acknowledged as already-correct and require no action.

The deferred BLOCKER (Publisher / cert Subject handoff) is captured
as a `TODO(slice-34)` comment in `Package.appxmanifest`.