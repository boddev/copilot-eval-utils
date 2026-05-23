# Writers.ps1 — Output file writers for the EvalScore

function ConvertFrom-EvalRowToPSObject {
    param(
        [Parameter(Mandatory)][EvalRow]$Row,
        [ValidateSet('csv', 'json')][string]$Target = 'csv'
    )

    $score = if ($null -eq $Row.SimilarityScore) {
        if ($Target -eq 'json') { $null } else { '' }
    } else {
        $Row.SimilarityScore
    }

    [PSCustomObject]@{
        prompt           = $Row.Prompt
        expected_answer  = $Row.ExpectedAnswer
        source_location  = $Row.SourceLocation
        actual_answer    = $Row.ActualAnswer
        similarity_score = $score
        metrics          = if ($Row.Metrics -and $Row.Metrics.Count -gt 0) { ($Row.Metrics | ConvertTo-Json -Compress -Depth 10) } else { '' }
    }
}

function Write-CsvEval {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$OutputPath,
        [string]$Delimiter = ','
    )

    $objects = $Rows | ForEach-Object { ConvertFrom-EvalRowToPSObject -Row $_ -Target 'csv' }
    $objects | Export-Csv -Path $OutputPath -Delimiter $Delimiter -NoTypeInformation
}

function Write-XlsxEval {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$OutputPath
    )

    if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
        throw "The 'ImportExcel' module is required for XLSX output. Install it with: Install-Module ImportExcel -Scope CurrentUser"
    }

    $objects = $Rows | ForEach-Object { ConvertFrom-EvalRowToPSObject -Row $_ -Target 'csv' }
    $objects | Export-Excel -Path $OutputPath -WorksheetName 'Results' -AutoSize
}

function Write-JsonEval {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $items = @()
    $groupedThreads = @{}
    $singleRows = @()
    foreach ($row in $Rows) {
        if ($null -ne $row.TurnIndex) {
            $key = if ($row.ThreadId) { $row.ThreadId } elseif ($row.Id) { $row.Id } else { "item-$($row.ItemIndex)" }
            if (-not $groupedThreads.ContainsKey($key)) { $groupedThreads[$key] = @() }
            $groupedThreads[$key] += $row
        } else {
            $singleRows += $row
        }
    }
    foreach ($row in $singleRows) {
        $items += ConvertTo-SchemaTurn -Row $row
    }
    foreach ($key in $groupedThreads.Keys) {
        $turnRows = @($groupedThreads[$key] | Sort-Object TurnIndex)
        $turns = @($turnRows | ForEach-Object { ConvertTo-SchemaTurn -Row $_ })
        $statuses = @($turns | ForEach-Object { if ($_.status) { $_.status } else { 'fail' } })
        $items += [PSCustomObject]@{
            name            = $turnRows[0].ThreadName
            description     = $turnRows[0].ThreadDescription
            conversation_id = $turnRows[0].ConversationId
            turns           = $turns
            summary         = [PSCustomObject]@{
                turns_total   = $turns.Count
                turns_passed  = @($statuses | Where-Object { $_ -eq 'pass' }).Count
                turns_failed  = @($statuses | Where-Object { $_ -eq 'fail' }).Count
                turns_partial = @($statuses | Where-Object { $_ -eq 'partial' }).Count
                turns_errored = @($statuses | Where-Object { $_ -eq 'error' }).Count
                overall_status = Get-OverallStatus -Statuses $statuses
            }
            extensions      = @{ evalscore = @{ item_id = $turnRows[0].Id } }
        }
    }
    $document = [PSCustomObject]@{
        schemaVersion      = '1.4.0'
        metadata           = @{ evaluatedAt = (Get-Date).ToUniversalTime().ToString('o'); cliVersion = 'eval-score'; extensions = @{ evalscore = @{ canonicalScoreScale = '0-100' } } }
        default_evaluators = if ($Rows.Count -gt 0) { $Rows[0].DocumentDefaultEvaluators } else { @{} }
        items              = $items
    }
    $json = $document | ConvertTo-Json -Depth 30
    Set-Content -Path $OutputPath -Value $json -Encoding UTF8
}

function Write-EvalFile {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$InputFile,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][ValidateSet('csv', 'tsv', 'xlsx', 'json')][string]$Format
    )

    if (-not (Test-Path -Path $OutputDir)) {
        New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
    }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
    $outputFileName = "$baseName-results.json"
    $outputPath = Join-Path -Path $OutputDir -ChildPath $outputFileName

    Write-JsonEval -Rows $Rows -OutputPath $outputPath

    return $outputPath
}

function ConvertTo-SchemaTurn {
    param([Parameter(Mandatory)][EvalRow]$Row)
    $metrics = @{}
    foreach ($metric in @($Row.Metrics)) {
        if (-not $metric) { continue }
        $key = switch ($metric.name) {
            'SemanticSimilarity' { 'similarity' }
            'Similarity' { 'similarity' }
            'Relevance' { 'relevance' }
            'Coherence' { 'coherence' }
            'Groundedness' { 'groundedness' }
            'ExactMatch' { 'exactMatch' }
            'PartialMatch' { 'partialMatch' }
            'Citations' { 'citations' }
            default { $null }
        }
        if ($key) {
            $metrics[$key] = @{ score_0_100 = $metric.score; result = if ($metric.passed) { 'pass' } else { 'fail' }; reason = $metric.reason; threshold = $metric.threshold }
        }
    }
    $status = if ($Row.Status) { $Row.Status } elseif ($Row.ActualAnswer -and $Row.ActualAnswer.StartsWith('[ERROR:')) { 'error' } elseif ($null -ne $Row.SimilarityScore -and $Row.SimilarityScore -ge 70) { 'pass' } else { 'fail' }
    return [PSCustomObject]@{
        prompt            = $Row.Prompt
        expected_response = $Row.ExpectedAnswer
        response          = $Row.ActualAnswer
        context           = if ($Row.Context) { $Row.Context } else { $Row.SourceLocation }
        evaluators        = $Row.EvaluatorsMap
        evaluators_mode   = $Row.EvaluatorsMode
        citations         = $Row.Citations
        scores            = $metrics
        status            = $status
        error             = $Row.Error
        extensions        = @{ evalscore = @{ item_id = $Row.Id; item_index = $Row.ItemIndex; turn_index = $Row.TurnIndex; source_location = $Row.SourceLocation; canonical_score_0_100 = $Row.SimilarityScore; response_metadata = $Row.ResponseMetadata } }
    }
}

function Get-OverallStatus {
    param([string[]]$Statuses)
    if ($Statuses -contains 'error') { return 'error' }
    if (@($Statuses | Where-Object { $_ -ne 'pass' }).Count -eq 0) { return 'pass' }
    if (@($Statuses | Where-Object { $_ -ne 'fail' }).Count -eq 0) { return 'fail' }
    return 'partial'
}
