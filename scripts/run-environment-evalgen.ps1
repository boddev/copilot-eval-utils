[CmdletBinding()]
param(
    [int]$Count = 50,
    [ValidateSet('m365-copilot', 'm365-copilot-api', 'workiq-a2a', 'azure-openai', 'github-copilot', 'command')]
    [string]$Provider = 'm365-copilot',
    [string]$DatasetPath,
    [string]$ConnectorSchemaPath,
    [string]$EvalOutputPath,
    [int]$LlmTimeoutMs,
    [int]$LlmMaxAttempts,
    [int]$LlmBackoffMs,
    [string]$M365TenantId,
    [string]$WorkIqToken,
    [int]$OuterAttempts = 1,
    [switch]$SkipInstall,
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
if (-not $EvalOutputPath) {
    $EvalOutputPath = Join-Path $repoRoot 'eval-output\environment-datasets-eval.csv'
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
    if (-not $NoConnectorSchema -and -not (Test-Path -LiteralPath $ConnectorSchemaPath)) {
        throw "Connector schema path not found: $ConnectorSchemaPath"
    }

    if (-not $SkipInstall) {
        Invoke-Step -Name 'Installing EvalGen command shim' -Script {
            Invoke-CommandChecked -Command (Join-Path $repoRoot 'install-tools.cmd') -Arguments @()
        }
    }

    $evalOutputDir = Split-Path -Parent $EvalOutputPath
    New-Item -ItemType Directory -Path $evalOutputDir -Force | Out-Null

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

        if ($PSBoundParameters.ContainsKey('LlmTimeoutMs') -and $LlmTimeoutMs -gt 0) {
            $env:EVALGEN_LLM_TIMEOUT_MS = [string]$LlmTimeoutMs
            Write-Host "  EVALGEN_LLM_TIMEOUT_MS=$LlmTimeoutMs"
        }
        if ($PSBoundParameters.ContainsKey('LlmMaxAttempts') -and $LlmMaxAttempts -gt 0) {
            $env:EVALGEN_LLM_MAX_ATTEMPTS = [string]$LlmMaxAttempts
            Write-Host "  EVALGEN_LLM_MAX_ATTEMPTS=$LlmMaxAttempts"
        }
        if ($PSBoundParameters.ContainsKey('LlmBackoffMs') -and $LlmBackoffMs -gt 0) {
            $env:EVALGEN_LLM_BACKOFF_MS = [string]$LlmBackoffMs
            Write-Host "  EVALGEN_LLM_BACKOFF_MS=$LlmBackoffMs"
        }
        if ($PSBoundParameters.ContainsKey('M365TenantId') -and $M365TenantId) {
            $env:EVALGEN_M365_TENANT_ID = $M365TenantId
            Write-Host "  EVALGEN_M365_TENANT_ID=$M365TenantId"
        }
        if ($PSBoundParameters.ContainsKey('WorkIqToken') -and $WorkIqToken) {
            $env:EVALGEN_WORKIQ_TOKEN = $WorkIqToken
            Write-Host "  EVALGEN_WORKIQ_TOKEN=<set>"
        }

        Invoke-CommandChecked -Command 'eval-gen' -Arguments $evalGenArgs -Attempts $OuterAttempts
    }

    $sidecarPath = [System.IO.Path]::ChangeExtension($EvalOutputPath, '.evalgen.json')

    Write-Host ''
    Write-Host 'Environment eval set generation complete.'
    Write-Host "  Eval CSV:      $EvalOutputPath"
    Write-Host "  Eval sidecar:  $sidecarPath"
}
finally {
    Pop-Location
}
