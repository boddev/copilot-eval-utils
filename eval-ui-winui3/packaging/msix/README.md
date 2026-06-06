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

