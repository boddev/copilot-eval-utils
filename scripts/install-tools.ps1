[CmdletBinding()]
param(
    [switch]$SkipBuild
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

function Test-CommandPromptCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    & cmd.exe /d /c "$Name --help >NUL"
    if ($LASTEXITCODE -ne 0) {
        throw "The '$Name' command was not available from Command Prompt after installation."
    }
}

Push-Location $repoRoot
try {
    Write-Host 'Installing EvalGen dependencies...'
    Invoke-Npm -Arguments @('install', '--prefix', 'eval-gen')

    Write-Host 'Installing EvalScore dependencies...'
    Invoke-Npm -Arguments @('install', '--prefix', 'eval-score\node')

    if (-not $SkipBuild) {
        Write-Host 'Building EvalGen and EvalScore...'
        Invoke-Npm -Arguments @('run', 'build')
    }

    Write-Host 'Linking eval-gen and eval-score commands...'
    Invoke-Npm -Arguments @('link')

    Write-Host 'Verifying commands from Command Prompt...'
    Test-CommandPromptCommand -Name 'eval-gen'
    Test-CommandPromptCommand -Name 'eval-score'

    Write-Host ''
    Write-Host 'Installation complete. You can now run these commands from Command Prompt:'
    Write-Host '  eval-gen --help'
    Write-Host '  eval-score --help'
}
finally {
    Pop-Location
}
