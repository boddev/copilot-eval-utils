[CmdletBinding()]
param(
    [switch]$CleanLocal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$npmCommand = (Get-Command npm.cmd -ErrorAction Stop).Source

function Invoke-Npm {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $npmCommand @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "npm $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-CommandRemoved {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    & cmd.exe /d /c "where $Name >NUL 2>NUL"
    if ($LASTEXITCODE -eq 0) {
        Write-Warning "The '$Name' command still resolves from Command Prompt. Another installation may still be on PATH."
    }
    else {
        Write-Host "Removed '$Name' from Command Prompt."
    }
}

Push-Location $repoRoot
try {
    $packageJson = Get-Content -LiteralPath (Join-Path $repoRoot 'package.json') -Raw | ConvertFrom-Json
    $packageName = [string]$packageJson.name

    Write-Host "Unlinking $packageName..."
    Invoke-Npm -Arguments @('uninstall', '--global', $packageName)

    Test-CommandRemoved -Name 'eval-gen'
    Test-CommandRemoved -Name 'eval-score'

    if ($CleanLocal) {
        Write-Host 'Removing local dependency and build directories...'
        $pathsToRemove = @(
            'eval-gen\node_modules',
            'eval-gen\dist',
            'eval-score\node\node_modules',
            'eval-score\node\dist'
        )

        foreach ($relativePath in $pathsToRemove) {
            $fullPath = Join-Path $repoRoot $relativePath
            if (Test-Path -LiteralPath $fullPath) {
                Remove-Item -LiteralPath $fullPath -Recurse -Force
            }
        }
    }

    Write-Host ''
    Write-Host 'Uninstall complete.'
}
finally {
    Pop-Location
}
