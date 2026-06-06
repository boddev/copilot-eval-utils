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
