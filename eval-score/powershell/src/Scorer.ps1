# Scorer.ps1 — Semantic similarity scoring for evaluation rows

function Invoke-Scoring {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [string]$TenantId,
        [scriptblock]$AskClient,
        [int]$DelayMs = 500
    )

    $total = $Rows.Count
    $requestId = 0

    for ($i = 0; $i -lt $total; $i++) {
        $row = $Rows[$i]

        # Resumability: skip already-scored rows
        if ($null -ne $row.SimilarityScore) {
            continue
        }

        # Error or empty answers get score 0
        if (-not $row.ActualAnswer -or $row.ActualAnswer.StartsWith('[ERROR:')) {
            $row.SimilarityScore = 0
            Write-Host "`rScoring answer $($i + 1)/$total..." -NoNewline
            continue
        }

        Write-Host "`rScoring answer $($i + 1)/$total..." -NoNewline

        $scoringPrompt = @"
Compare the following two answers for semantic similarity. Consider whether they convey the same meaning and information, even if worded differently. Rate the similarity on a scale from 0 to 100, where 0 means completely different and 100 means identical in meaning. Respond with ONLY a single number between 0 and 100, nothing else.

Expected Answer: $($row.ExpectedAnswer)

Actual Answer: $($row.ActualAnswer)
"@

        try {
            if ($AskClient) {
                $response = & $AskClient $scoringPrompt
            } else {
                $requestId++
                $sendParams = @{
                    Question  = $scoringPrompt
                    RequestId = $requestId
                }
                if ($TenantId) { $sendParams['TenantId'] = $TenantId }
                $response = Send-WorkIQRequest @sendParams
            }

            $match = [regex]::Match($response, '\d+')
            if ($match.Success) {
                $parsed = [int]$match.Value
                $row.SimilarityScore = [Math]::Max(0, [Math]::Min(100, $parsed))
            } else {
                Write-Warning "Could not parse score from response for row $($i + 1), setting to 0"
                $row.SimilarityScore = 0
            }
        } catch {
            Write-Warning "Scoring failed for row $($i + 1): $($_.Exception.Message), setting to 0"
            $row.SimilarityScore = 0
        }

        if ($i -lt $total - 1) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    Write-Host ''
    return $Rows
}

function Get-ScoringResult {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [int]$PassThreshold = 70
    )

    $scores = $Rows | ForEach-Object {
        if ($null -ne $_.SimilarityScore) { $_.SimilarityScore } else { 0 }
    }

    $totalQuestions = $scores.Count

    if ($totalQuestions -gt 0) {
        $sum = ($scores | Measure-Object -Sum).Sum
        $avg = [Math]::Round($sum / $totalQuestions, 1)
        $minScore = ($scores | Measure-Object -Minimum).Minimum
        $maxScore = ($scores | Measure-Object -Maximum).Maximum
    } else {
        $avg = 0.0
        $minScore = 0
        $maxScore = 0
    }

    $passCount = @($scores | Where-Object { $_ -ge $PassThreshold }).Count
    $failCount = $totalQuestions - $passCount

    $result = [ScoringResult]::new()
    $result.TotalQuestions = $totalQuestions
    $result.AverageScore = $avg
    $result.MinScore = $minScore
    $result.MaxScore = $maxScore
    $result.PassCount = $passCount
    $result.FailCount = $failCount
    $result.PassThreshold = $PassThreshold

    return $result
}
