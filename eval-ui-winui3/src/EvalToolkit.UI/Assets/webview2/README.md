# WebView2 Evergreen Bootstrapper bundling

This folder is intentionally empty in source control. The production
packaging pipeline (slice 29 — `msix-packaging`) downloads
`MicrosoftEdgeWebview2Setup.exe` from Microsoft's public fwlink and
places it here before `dotnet publish` produces the MSIX / portable ZIP.

## What goes here

A single file: **`MicrosoftEdgeWebview2Setup.exe`** (~1.7 MB), the
Evergreen Bootstrapper distributed by Microsoft.

- Stable download URL: <https://go.microsoft.com/fwlink/p/?LinkId=2124703>
- Distribution guidance:
  <https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution>

## Why isn't it committed?

1. It's a binary owned by Microsoft, not by this repo, and it rolls
   forward as the Evergreen channel updates. Committing it would
   pin a stale copy.
2. The `eval-ui/` Electron app and the `eval-ui-winui3/` portable ZIP
   target both have to pick up the *current* installer at build time.

## Local dev

For most dev work this folder can stay empty. The WinUI app detects
WebView2 at first use of the Step 5 report viewer and:

- If the runtime is already installed (Windows 11, or a previous
  Visual Studio / Edge install), nothing happens — the report
  renders normally.
- If the runtime is missing AND this folder is empty, the in-app
  fallback panel disables the "Install" button and surfaces the
  "Get installer" button, which opens the fwlink in the user's
  default browser.

If you want to test the in-app install flow locally, download the
bootstrapper from the fwlink above into this folder before launching
the app.

## Packaging

`packaging/msix/` and `packaging/portable/` consume this folder via
the conditional `<Content Include="Assets\webview2\MicrosoftEdgeWebview2Setup.exe" ... />`
item in `EvalToolkit.UI.csproj`. The Content item uses a `Condition`
on `Exists(...)` so dev builds work without the asset; production
builds populate the folder first.

The CI workflow added in slice 31 (`ci-workflow`) downloads the
bootstrapper as a build step before invoking the packaging target.
