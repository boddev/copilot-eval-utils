<#
.SYNOPSIS
    Publish EvalToolkit.Cli as a self-contained single-file Windows x64
    executable and bundle it into a portable distribution ZIP.

.DESCRIPTION
    Slice 20 (`cli-msix-or-zip`): produces the first shippable native
    artifact for the WinUI 3 companion product line — the pure CLI shims
    (`eval-gen-native.exe` and `eval-score-native.exe`) — without waiting
    for the WinUI head to land.

    Publishes EvalToolkit.Cli with `-r win-x64 --self-contained true
    -p:PublishSingleFile=true`, so users do NOT need a separate .NET 10
    runtime installed. The single output binary
    (`EvalToolkit.Cli.exe`) is then copied to the two shim names so the
    `Environment.ProcessPath`-based dispatch in `Program.cs` routes to
    the correct subcommand. Both shims share the same dependencies/PDB.

    The resulting ZIP is dropped at:
        eval-ui-winui3/packaging/portable/dist/evaltoolkit-cli-<version>-<rid>.zip

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release.

.PARAMETER Rid
    Runtime identifier. Default: win-x64. Pass `win-arm64` for ARM64
    when the .NET 10 ARM64 runtime is installed.

.PARAMETER Version
    Optional override for the version string used in the ZIP filename.
    Defaults to CoreInfo.Version (read from EvalToolkit.Core).

.PARAMETER SkipTests
    Skip the test suite before publishing. Default: false. CI should
    NEVER skip; only useful for local iteration.

.EXAMPLE
    pwsh ./build-cli-zip.ps1

.EXAMPLE
    pwsh ./build-cli-zip.ps1 -Configuration Release -Rid win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Rid = 'win-x64',

    [string]$Version,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$cliProject = Join-Path $repoRoot 'src/EvalToolkit.Cli/EvalToolkit.Cli.csproj'
$slnx = Join-Path $repoRoot 'EvalToolkit.slnx'
$publishDir = Join-Path $repoRoot "src/EvalToolkit.Cli/bin/$Configuration/net10.0/$Rid/publish"
$distDir = Join-Path $PSScriptRoot 'dist'

Write-Host "==> EvalToolkit CLI portable build" -ForegroundColor Cyan
Write-Host "    repo root:    $repoRoot"
Write-Host "    project:      $cliProject"
Write-Host "    rid:          $Rid"
Write-Host "    configuration: $Configuration"
Write-Host ""

if (-not $SkipTests) {
    Write-Host "==> Running full test suite" -ForegroundColor Cyan
    & dotnet test $slnx --nologo --configuration $Configuration -clp:NoSummary
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)." }
}
elseif ($env:GITHUB_ACTIONS -eq 'true') {
    throw "CI run detected (GITHUB_ACTIONS=true) but -SkipTests was passed. Refusing to publish an untested artifact."
}
else {
    Write-Warning "Skipping tests at user request. CI must NOT pass -SkipTests."
}

# Ensure publish directory is clean so leftover files from a prior run
# (different RID, abandoned build) cannot leak into the ZIP.
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "==> Publishing $Rid self-contained single-file binary" -ForegroundColor Cyan
& dotnet publish $cliProject `
    --configuration $Configuration `
    --runtime $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    --nologo `
    -clp:NoSummary

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path $publishDir)) { throw "Publish directory not found: $publishDir" }

$primaryExe = Join-Path $publishDir 'EvalToolkit.Cli.exe'
if (-not (Test-Path $primaryExe)) { throw "Published binary not found: $primaryExe" }

if (-not $Version) {
    $coreInfoPath = Join-Path $repoRoot 'src/EvalToolkit.Core/CoreInfo.cs'
    $coreInfoContent = Get-Content $coreInfoPath -Raw
    if ($coreInfoContent -match 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"') {
        $Version = $Matches[1]
    }
    else {
        # Refuse to publish a release artifact with a fake version — caller
        # can pass -Version explicitly if they know what they want. This
        # prevents `evaltoolkit-cli-0.0.0-*.zip` from silently shipping.
        throw "Could not parse CoreInfo.Version from $coreInfoPath. Pass -Version explicitly if you intend to override."
    }
}

$stagingDir = Join-Path $env:TEMP "evaltoolkit-cli-$Version-$Rid-$([guid]::NewGuid().ToString('N').Substring(0,8))"
$stagingPayload = Join-Path $stagingDir "evaltoolkit-cli-$Version"
New-Item -ItemType Directory -Path $stagingPayload -Force | Out-Null

Write-Host "==> Staging payload at $stagingPayload" -ForegroundColor Cyan

# Copy the single-file binary and any side-by-side runtime config / pdbs.
# PublishSingleFile usually produces just the EXE, but some assets (e.g.
# WebView2 loader fallback DLLs) get emitted alongside; bundle them all.
Get-ChildItem -Path $publishDir -File |
    Where-Object { $_.Extension -ne '.pdb' -or $Configuration -eq 'Debug' } |
    ForEach-Object { Copy-Item -Path $_.FullName -Destination $stagingPayload -Force }

# Create the two named shims as file copies so name-based dispatch routes
# `eval-gen-native --help` → `eval-gen --help` (and similarly for score).
$genShim = Join-Path $stagingPayload 'eval-gen-native.exe'
$scoreShim = Join-Path $stagingPayload 'eval-score-native.exe'
Copy-Item -Path $primaryExe -Destination $genShim -Force
Copy-Item -Path $primaryExe -Destination $scoreShim -Force
Write-Host "    eval-gen-native.exe   ✓"
Write-Host "    eval-score-native.exe ✓"

# Drop a README explaining the layout.
$readmePath = Join-Path $stagingPayload 'README.txt'
@"
EvalToolkit native CLI shims — portable ZIP distribution
========================================================

Version:        $Version
Runtime ID:     $Rid
Configuration:  $Configuration
Built:          $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))

Contents
--------
  EvalToolkit.Cli.exe       — primary binary (also dispatches by name).
  eval-gen-native.exe       — `eval-gen` subcommand shim (file copy).
  eval-score-native.exe     — `eval-score` subcommand shim (file copy).

Both shim binaries are exact byte-for-byte copies of EvalToolkit.Cli.exe.
The unified binary inspects its own name at runtime (via
Environment.ProcessPath) and dispatches to the matching subcommand, so:

  eval-gen-native.exe --help
  EvalToolkit.Cli.exe eval-gen --help

are equivalent.

Requirements
------------
This is a self-contained single-file build — no .NET runtime install
required on the target machine. Windows 10 1809 (build 17763) or later
on the matching architecture ($Rid) is sufficient.

You also need:
  workiq        — the WorkIQ CLI, installed separately, for both
                  eval-gen-native --provider m365-copilot and
                  eval-score-native (the response evaluator + judge).
                  Install: see https://github.com/microsoft/work-iq-mcp
  copilot       — (optional) GitHub Copilot CLI, used when
                  --provider github-copilot is selected, or as the
                  default fallback judge for eval-score-native.

These external dependencies match the existing Node-based eval-gen /
eval-score CLIs.

Coexistence with the Node CLIs
------------------------------
The native shim names (eval-gen-native, eval-score-native) intentionally
differ from the Node CLI names (eval-gen, eval-score) so both product
lines can coexist on the same PATH without collision. See the WinUI 3
companion plan, sections 3, 7, and 10.

Output files (.evalgen.json sidecars, -review.md, -report.md, scored
CSVs) use the same on-disk format as the Node CLIs, so artifacts move
freely between the two.

Installing on PATH (recommended)
--------------------------------
This ZIP unpacks into a single top-level directory
(``evaltoolkit-cli-$Version\``) so it cannot overwrite existing tooling
when extracted into a shared folder. To put the native CLIs on PATH:

  1. Extract into a dedicated folder, e.g. C:\Tools\evaltoolkit-cli\.
     Do NOT extract into the same folder as the Node ``eval-gen`` /
     ``eval-score`` install (typically a Node ``bin`` directory) — the
     names differ, but the README/LICENSE would clobber.
  2. Add the extracted folder to your user PATH:
        setx PATH "%PATH%;C:\Tools\evaltoolkit-cli\evaltoolkit-cli-$Version"
     (Replace the path with wherever you actually extracted.)
  3. Open a NEW terminal so the updated PATH takes effect.
  4. Verify:
        eval-gen-native --help
        eval-score-native --help

DO NOT rename the shim binaries to ``eval-gen.exe`` or ``eval-score.exe``.
Those names are owned by the Node CLIs installed via the repo's
install-tools.cmd, and renaming would cause silent PATH collisions.

Quick start
-----------
  eval-gen-native --file mydata.csv --description "Customer support FAQ" --count 30
  eval-score-native --evalset ./output/eval-set.evalgen.json --threshold 70

For full usage:
  eval-gen-native --help
  eval-score-native --help
"@ | Set-Content -Path $readmePath -Encoding UTF8

# Copy LICENSE if available.
$licenseSrc = Join-Path $repoRoot '../LICENSE'
if (Test-Path $licenseSrc) {
    Copy-Item -Path $licenseSrc -Destination (Join-Path $stagingPayload 'LICENSE') -Force
}

# Zip it.
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$zipPath = Join-Path $distDir "evaltoolkit-cli-$Version-$Rid.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host ""
Write-Host "==> Compressing $zipPath" -ForegroundColor Cyan
# Zip the parent directory (which contains the versioned payload folder)
# so the archive unpacks into a single ``evaltoolkit-cli-<ver>\`` directory
# rather than spraying files into whatever folder a user extracts into.
Compress-Archive -Path $stagingPayload -DestinationPath $zipPath -CompressionLevel Optimal

$zipInfo = Get-Item $zipPath
Write-Host ""
Write-Host "✅ ZIP built: $zipPath" -ForegroundColor Green
Write-Host "   Size:     $([math]::Round($zipInfo.Length / 1MB, 2)) MB"

# Clean up staging.
Remove-Item -Recurse -Force $stagingDir
