<#
.SYNOPSIS
    Evaluates WorkIQ (M365 Copilot) responses against a known-correct dataset.
.DESCRIPTION
    Reads an evaluation dataset, sends prompts to WorkIQ via the workiq CLI,
    scores semantic similarity, and produces a detailed markdown report plus
    completed evaluation file.
.PARAMETER InputFile
    Path to the evaluation dataset (CSV, TSV, XLSX, or JSON). Required.
.PARAMETER SystemPrompt
    Inline system prompt to prepend to each question. Optional.
.PARAMETER SystemPromptFile
    Path to a file containing the system prompt. Optional.
.PARAMETER OutputDir
    Output directory for results and report. Default: ./output
.PARAMETER Threshold
    Pass/fail score threshold (0-100). Default: 70
.PARAMETER TenantId
    Microsoft 365 tenant ID to target for WorkIQ queries. Optional.
.PARAMETER Setup
    Run only the setup/preflight checks without starting the evaluation.
.PARAMETER SkipPreflight
    Skip preflight checks and start the evaluation immediately.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$InputFile,

    [string]$SystemPrompt,

    [string]$SystemPromptFile,

    [string]$OutputDir = './output',

    [ValidateRange(0, 100)]
    [int]$Threshold = 70,

    [string]$TenantId,

    [switch]$Setup,

    [switch]$SkipPreflight
)

try {
    # 1. Dot-source all modules (Types.ps1 first to avoid class redefinition)
    $srcDir = Join-Path $PSScriptRoot 'src'
    . (Join-Path $srcDir 'Types.ps1')
    Get-ChildItem -Path $srcDir -File -Filter '*.ps1' | Where-Object { $_.Name -ne 'Types.ps1' } | ForEach-Object {
        . $_.FullName
    }

    # 2. Handle Setup-only mode
    if ($Setup) {
        $passed = Invoke-Setup -TenantId $TenantId
        if ($passed) { exit 0 } else { exit 1 }
    }

    # 3. Validate input file
    if (-not $InputFile) {
        throw "InputFile is required. Use -InputFile <path> to specify the evaluation dataset, or -Setup to run preflight checks only."
    }
    if (-not (Test-Path -LiteralPath $InputFile)) {
        throw "Input file not found: $InputFile"
    }

    # 4. Run preflight checks (unless skipped)
    if (-not $SkipPreflight) {
        $preflightPassed = Invoke-Setup `
            -TenantId $TenantId `
            -SkipConnectivityTest

        if (-not $preflightPassed) {
            Write-Host 'Use -SkipPreflight to bypass these checks if you know your environment is configured.' -ForegroundColor Yellow
            exit 1
        }
    }

    # 5. Resolve system prompt (inline or file)
    $resolvedPrompt = Resolve-SystemPrompt -InlinePrompt $SystemPrompt -PromptFilePath $SystemPromptFile

    # 6. Print startup banner
    Write-Host '================================================'
    Write-Host '  WorkIQ EvalScore (PowerShell)'
    Write-Host '================================================'
    Write-Host "  Input file:    $InputFile"
    Write-Host "  Output dir:    $OutputDir"
    Write-Host "  Threshold:     $Threshold"
    if ($resolvedPrompt) {
        $preview = if ($resolvedPrompt.Length -gt 60) { $resolvedPrompt.Substring(0, 60) + '...' } else { $resolvedPrompt }
        Write-Host "  System prompt: $preview"
    }
    if ($TenantId) {
        Write-Host "  Tenant ID:     $TenantId"
    }
    Write-Host '================================================'
    Write-Host ''

    # 7. Create output directory if needed
    if (-not (Test-Path -LiteralPath $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    # 8. Read input file
    Write-Host 'Reading evaluation dataset...'
    $dataset = Read-EvalFile -Path $InputFile
    $rows = $dataset.Rows
    $format = $dataset.Format
    Write-Host "  Loaded $($rows.Count) rows (format: $format)"
    Write-Host ''

    # 9. Start persistent WorkIQ MCP session (auth once)
    Write-Host 'Starting WorkIQ session...'
    Start-WorkIQSession -TenantId $TenantId
    Write-Host ''

    try {
        # 10. Run evaluation
        Write-Host 'Running evaluation (sending prompts to WorkIQ)...'
        $evalParams = @{
            Rows         = $rows
            SystemPrompt = $resolvedPrompt
        }
        if ($TenantId) { $evalParams['TenantId'] = $TenantId }
        $rows = Invoke-Evaluation @evalParams
        Write-Host '  Evaluation complete.'
        Write-Host ''

        # 11. Run scoring
        Write-Host 'Scoring responses...'
        $scoreParams = @{
            Rows = $rows
        }
        if ($TenantId) { $scoreParams['TenantId'] = $TenantId }
        $rows = Invoke-Scoring @scoreParams
        Write-Host '  Scoring complete.'
        Write-Host ''
    } finally {
        # Always clean up the MCP session
        Stop-WorkIQSession
    }

    # 11. Calculate scoring result
    $scoringResult = Get-ScoringResult -Rows $rows -PassThreshold $Threshold

    # 12. Build EvalResult object
    $evalResult = [EvalResult]::new()
    $evalResult.Rows = $rows
    $evalResult.InputFile = $InputFile
    $evalResult.InputFormat = $format
    $evalResult.Timestamp = (Get-Date).ToUniversalTime().ToString('o')
    $evalResult.SystemPrompt = $resolvedPrompt

    # 13. Generate and write report
    Write-Host 'Generating report...'
    $report = New-EvalReport -EvalResult $evalResult -ScoringResult $scoringResult
    $reportPath = Write-EvalReport -Report $report -OutputDir $OutputDir -InputFile $InputFile
    Write-Host "  Report written to: $reportPath"

    # 14. Write eval file
    $evalFilePath = Write-EvalFile -Rows $rows -InputFile $InputFile -OutputDir $OutputDir -Format $format
    Write-Host "  Eval file written to: $evalFilePath"
    Write-Host ''

    # 15. Print summary
    Write-Host '================================================'
    Write-Host '  Results Summary'
    Write-Host '================================================'
    Write-Host "  Total questions: $($scoringResult.TotalQuestions)"
    Write-Host "  Average score:   $([math]::Round($scoringResult.AverageScore, 1))"
    Write-Host "  Pass/Fail:       $($scoringResult.PassCount)/$($scoringResult.FailCount) (threshold: $Threshold)"
    Write-Host "  Min score:       $($scoringResult.MinScore)"
    Write-Host "  Max score:       $($scoringResult.MaxScore)"
    Write-Host ''
    Write-Host "  Report:          $reportPath"
    Write-Host "  Eval file:       $evalFilePath"
    Write-Host '================================================'

    # 16. Exit with appropriate code
    if ($scoringResult.FailCount -gt 0) {
        Write-Host ''
        Write-Host "FAIL: $($scoringResult.FailCount) question(s) below threshold." -ForegroundColor Red
        exit 1
    }
    else {
        Write-Host ''
        Write-Host 'PASS: All questions met the threshold.' -ForegroundColor Green
        exit 0
    }
}
catch {
    [Console]::Error.WriteLine("ERROR: $_")
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 2
}
