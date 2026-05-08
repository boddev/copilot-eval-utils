[CmdletBinding()]
param(
    [string]$ConnectorId = 'ngoenvironment',
    [string]$TenantId,
    [int]$Count = 50,
    [int]$Threshold = 70,
    [string]$Provider = 'm365-copilot',
    [string]$DatasetPath,
    [string]$ConnectorSchemaPath,
    [string]$SystemPromptFile,
    [string]$EvalOutputPath,
    [string]$ScoreOutputDir,
    [switch]$SkipInstall,
    [switch]$SkipEvalGen,
    [switch]$SkipEvalScore,
    [switch]$SkipPreflight,
    [switch]$NoConnectorSchema
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$description = 'Environmental datasets for the NGO environment Copilot connector, including Our World in Data CO2 and greenhouse gas metrics plus World Bank climate and environmental indicators by country or region and year.'

if (-not $DatasetPath) {
    $DatasetPath = Join-Path $repoRoot 'environment-datasets'
}
if (-not $ConnectorSchemaPath) {
    $ConnectorSchemaPath = Join-Path $repoRoot 'eval-gen\examples\environment-datasets-connector-schema.json'
}
if (-not $SystemPromptFile) {
    $SystemPromptFile = Join-Path $repoRoot 'prompts\ngo-environment-system-prompt.md'
}
if (-not $EvalOutputPath) {
    $EvalOutputPath = Join-Path $repoRoot 'eval-output\environment-datasets-eval.csv'
}
if (-not $ScoreOutputDir) {
    $ScoreOutputDir = Join-Path $repoRoot 'eval-output\environment-datasets-score'
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Script
    )

    Write-Host ''
    Write-Host "==> $Name"
    & $Script
}

function Invoke-CommandChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [string[]]$Arguments = @(),

        [int]$Attempts = 1,

        [int[]]$AllowedExitCodes = @(0)
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        & $Command @Arguments
        if ($LASTEXITCODE -in $AllowedExitCodes) {
            return
        }

        if ($attempt -lt $Attempts) {
            Write-Warning "$Command failed with exit code $LASTEXITCODE. Retrying ($($attempt + 1)/$Attempts)..."
            Start-Sleep -Seconds 10
        }
        else {
            throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
}

Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $DatasetPath)) {
        throw "Dataset path not found: $DatasetPath"
    }
    if (-not (Test-Path -LiteralPath $SystemPromptFile)) {
        throw "System prompt file not found: $SystemPromptFile"
    }
    if (-not $NoConnectorSchema -and -not (Test-Path -LiteralPath $ConnectorSchemaPath)) {
        throw "Connector schema path not found: $ConnectorSchemaPath"
    }

    if (-not $SkipInstall) {
        Invoke-Step -Name 'Installing EvalGen and EvalScore command shims' -Script {
            Invoke-CommandChecked -Command (Join-Path $repoRoot 'install-tools.cmd') -Arguments @()
        }
    }

    $evalOutputDir = Split-Path -Parent $EvalOutputPath
    New-Item -ItemType Directory -Path $evalOutputDir -Force | Out-Null
    New-Item -ItemType Directory -Path $ScoreOutputDir -Force | Out-Null

    if (-not $SkipEvalGen) {
        Invoke-Step -Name 'Generating environment eval set with EvalGen' -Script {
            $evalGenArgs = @(
                '--file', $DatasetPath,
                '--extensions', 'csv',
                '--description', $description,
                '--count', [string]$Count,
                '--provider', $Provider,
                '--output', $EvalOutputPath
            )

            if (-not $NoConnectorSchema) {
                $evalGenArgs += @('--connector-schema', $ConnectorSchemaPath)
            }

            Invoke-CommandChecked -Command 'eval-gen' -Arguments $evalGenArgs -Attempts 3
        }
    }

    $sidecarPath = [System.IO.Path]::ChangeExtension($EvalOutputPath, '.evalgen.json')
    if (-not $SkipEvalScore) {
        if (-not (Test-Path -LiteralPath $EvalOutputPath)) {
            throw "EvalGen output file not found: $EvalOutputPath"
        }
        if (-not (Test-Path -LiteralPath $sidecarPath)) {
            throw "EvalGen sidecar file not found: $sidecarPath"
        }

        Invoke-Step -Name 'Running EvalScore against the ngoenvironment connector' -Script {
            $evalScoreArgs = @(
                '--input', $EvalOutputPath,
                '--sidecar', $sidecarPath,
                '--connector-id', $ConnectorId,
                '--system-prompt-file', $SystemPromptFile,
                '--output-dir', $ScoreOutputDir,
                '--threshold', [string]$Threshold
            )

            if ($TenantId) {
                $evalScoreArgs += @('--tenant-id', $TenantId)
            }

            if ($SkipPreflight) {
                $evalScoreArgs += '--skip-preflight'
            }

            Invoke-CommandChecked -Command 'eval-score' -Arguments $evalScoreArgs -AllowedExitCodes @(0, 1)
        }
    }

    Write-Host ''
    Write-Host 'Environment connector evaluation workflow complete.'
    Write-Host "  Eval CSV:      $EvalOutputPath"
    Write-Host "  Eval sidecar:  $sidecarPath"
    Write-Host "  Score output:  $ScoreOutputDir"
    Write-Host "  Connector ID:  $ConnectorId"
    if ($TenantId) {
        Write-Host "  Tenant ID:     $TenantId"
    }
}
finally {
    Pop-Location
}
