# Setup.ps1 — Preflight checks and setup for the EvalScore

$script:EulaMarkerFile= Join-Path ([Environment]::GetFolderPath('UserProfile')) '.workiq-eula-accepted'
$script:EulaUrl = 'https://github.com/microsoft/work-iq-mcp'

function Test-WorkIQEula {
    <#
    .SYNOPSIS
        Checks if the WorkIQ EULA has been accepted.
    .OUTPUTS
        $true if accepted, $false otherwise.
    #>

    return Test-Path -LiteralPath $script:EulaMarkerFile
}

function Approve-WorkIQEula {
    <#
    .SYNOPSIS
        Walks the user through accepting the WorkIQ EULA.
    .OUTPUTS
        $true if accepted, $false if declined.
    #>

    Write-Host ''
    Write-Host '┌──────────────────────────────────────────────────┐' -ForegroundColor Yellow
    Write-Host '│  WorkIQ End User License Agreement               │' -ForegroundColor Yellow
    Write-Host '└──────────────────────────────────────────────────┘' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  Before using WorkIQ, you must accept the EULA."
    Write-Host "  Review the terms at: $script:EulaUrl"
    Write-Host ''

    $response = Read-Host "  Do you accept the WorkIQ EULA? (yes/no)"

    if ($response -match '^(y|yes)$') {
        $eulaDir = Split-Path $script:EulaMarkerFile -Parent
        if (-not (Test-Path $eulaDir)) {
            New-Item -ItemType Directory -Path $eulaDir -Force | Out-Null
        }
        Set-Content -Path $script:EulaMarkerFile -Value "Accepted on $(Get-Date -Format 'o') for $script:EulaUrl"
        Write-Host '  ✅ EULA accepted.' -ForegroundColor Green
        return $true
    } else {
        Write-Host '  ❌ EULA declined. WorkIQ cannot be used without accepting the EULA.' -ForegroundColor Red
        return $false
    }
}

function Test-WorkIQConnectivity {
    <#
    .SYNOPSIS
        Starts a WorkIQ MCP session and sends a test prompt to verify connectivity.
    .OUTPUTS
        Hashtable with: Connected, Message, ResponseTime
    #>
    param(
        [string]$TenantId,
        [scriptblock]$AskClient
    )

    $result = @{
        Connected    = $false
        Message      = ''
        ResponseTime = 0
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        if ($AskClient) {
            $response = & $AskClient "Reply with the word 'connected' to confirm you are working."
        } else {
            Start-WorkIQSession -TenantId $TenantId
            $response = Send-WorkIQRequest `
                -Question "Say hello" `
                -RequestId 1 `
                -TenantId $TenantId
            Stop-WorkIQSession
        }

        $sw.Stop()
        $result.Connected = $true
        $result.ResponseTime = $sw.ElapsedMilliseconds
        $result.Message = "WorkIQ responded in $([math]::Round($sw.ElapsedMilliseconds / 1000, 1))s"
    } catch {
        $sw.Stop()
        Stop-WorkIQSession
        $result.ResponseTime = $sw.ElapsedMilliseconds
        $result.Message = "WorkIQ connectivity test failed: $($_.Exception.Message)`n" +
            "        Verify workiq works by running: workiq -t <tenantId> ask -q `"Say hello`""
    }

    return $result
}

function Invoke-Setup {
    <#
    .SYNOPSIS
        Runs all preflight checks: EULA and connectivity.
    .OUTPUTS
        $true if all checks pass, $false otherwise.
    #>
    param(
        [string]$TenantId,
        [scriptblock]$AskClient,
        [switch]$SkipConnectivityTest
    )

    $allPassed = $true

    Write-Host ''
    Write-Host '╔══════════════════════════════════════════════════╗'
    Write-Host '║  Preflight Checks                                ║'
    Write-Host '╚══════════════════════════════════════════════════╝'
    Write-Host ''

    # 1. WorkIQ EULA
    Write-Host '  [1/2] Checking WorkIQ EULA acceptance...' -NoNewline
    if (Test-WorkIQEula) {
        Write-Host ' ✅ EULA accepted' -ForegroundColor Green
    } else {
        Write-Host ' ⚠️  Not yet accepted' -ForegroundColor Yellow
        if (-not (Approve-WorkIQEula)) {
            $allPassed = $false
        }
    }

    # 2. Connectivity test
    if (-not $SkipConnectivityTest) {
        Write-Host '  [2/2] Testing WorkIQ connectivity...' -NoNewline
        $connResult = Test-WorkIQConnectivity `
            -TenantId $TenantId `
            -AskClient $AskClient

        if ($connResult.Connected) {
            Write-Host " ✅ $($connResult.Message)" -ForegroundColor Green
        } else {
            Write-Host ' ❌ FAILED' -ForegroundColor Red
            Write-Host "        $($connResult.Message)" -ForegroundColor Yellow
            $allPassed = $false
        }
    } else {
        Write-Host '  [2/2] Connectivity test... ⏭️  Skipped'
    }

    Write-Host ''

    if (-not $allPassed) {
        Write-Host '  ──────────────────────────────────────────' -ForegroundColor Red
        Write-Host '  One or more preflight checks failed.' -ForegroundColor Red
        Write-Host '  Fix the issues above and try again.' -ForegroundColor Red
        Write-Host '  ──────────────────────────────────────────' -ForegroundColor Red
        Write-Host ''
    } else {
        Write-Host '  All preflight checks passed.' -ForegroundColor Green
        Write-Host ''
    }

    return $allPassed
}
