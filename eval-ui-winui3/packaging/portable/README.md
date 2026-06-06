# Portable / unsigned CLI ZIP packaging

This directory builds the **CLI-only** portable distribution shipped
alongside the MSIX. It bundles the native single-file shims —
`EvalToolkit.Cli.exe`, `eval-gen-native.exe`, `eval-score-native.exe`
— produced by `dotnet publish -r win-x64 --self-contained true`
against `src/EvalToolkit.Cli`.

The portable bundle is **CLI only**. The WinUI 3 GUI ships
exclusively as a signed MSIX (`packaging/msix/`); there is no
unpackaged GUI distribution. Per plan Section 7 the GUI relies on
MSIX-bound capabilities (file associations, package-identity jump
list, COM toast activator) that cannot be replicated by a portable
executable.

## Layout

```
packaging/portable/
├── build-cli-zip.ps1              one-shot publish + ZIP script
├── README.md                      this file
└── dist/
    ├── evaltoolkit-cli-<ver>-win-x64/                 unzipped layout
    │   └── evaltoolkit-cli-<ver>-alpha/
    │       ├── EvalToolkit.Cli.exe                    full CLI front-door
    │       ├── eval-gen-native.exe                    eval-gen shim
    │       ├── eval-score-native.exe                  eval-score shim
    │       └── README.txt
    └── evaltoolkit-cli-<ver>-win-x64.zip              the shipped artifact
```

## Build

```pwsh
pwsh .\packaging\portable\build-cli-zip.ps1
```

Outputs both the unzipped layout and the ZIP under
`packaging/portable/dist/`. Re-running cleans the previous output.

## Use

Unzip anywhere — no install step, no dependencies, no admin. The
three EXEs are self-contained .NET 10 single-file binaries.

```pwsh
.\EvalToolkit.Cli.exe --help
.\eval-gen-native.exe   --help
.\eval-score-native.exe --help
```

## Caveats

- Unsigned. Windows SmartScreen may warn on first run.
- No GUI. For interactive use, install the MSIX from
  `packaging/msix/` (see [`../msix/README.md`](../msix/README.md))
  or grab a release from
  https://github.com/boddev/copilot-eval-utils/releases.

