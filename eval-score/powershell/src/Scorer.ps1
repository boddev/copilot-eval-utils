# Scorer.ps1 — Semantic similarity scoring for evaluation rows

function Invoke-Scoring {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [string]$TenantId,
        [ValidateSet('workiq', 'github-copilot', 'azure-openai')]
        [string]$JudgeProvider = 'workiq',
        [string[]]$Evaluators = @('SemanticSimilarity'),
        [int]$Threshold = 70,
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
            $judgeResult = Invoke-JudgeScoring -Prompt $scoringPrompt -Provider $JudgeProvider -TenantId $TenantId -AskClient $AskClient -RequestId ([ref]$requestId)
            $row.SimilarityScore = $judgeResult.Score
            $row.Metrics = @(
                [PSCustomObject]@{
                    name          = 'SemanticSimilarity'
                    score         = $judgeResult.Score
                    passed        = ($judgeResult.Score -ge $Threshold)
                    reason        = $judgeResult.Reason
                    provider      = $JudgeProvider
                    model         = $judgeResult.Model
                    scale         = '0-100'
                    rubricVersion = 'evalscore-semantic-v1'
                    threshold     = $Threshold
                }
            ) + @(Get-DeterministicMetrics -Row $row -Evaluators $Evaluators -Threshold $Threshold)
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

function Invoke-JudgeScoring {
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [ValidateSet('workiq', 'github-copilot', 'azure-openai')][string]$Provider,
        [string]$TenantId,
        [scriptblock]$AskClient,
        [ref]$RequestId
    )

    switch ($Provider) {
        'workiq' {
            if ($AskClient) {
                $response = & $AskClient $Prompt
            } else {
                $RequestId.Value++
                $sendParams = @{
                    Question  = $Prompt
                    RequestId = $RequestId.Value
                }
                if ($TenantId) { $sendParams['TenantId'] = $TenantId }
                $response = Send-WorkIQRequest @sendParams
            }
            return ConvertFrom-JudgeResponse -Response $response -Model ''
        }
        'github-copilot' {
            $command = $env:EVALSCORE_GITHUB_COPILOT_COMMAND
            if (-not $command) {
                throw 'GitHub Copilot judging requires EVALSCORE_GITHUB_COPILOT_COMMAND.'
            }
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $env:ComSpec
            $psi.Arguments = "/c $command"
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.UseShellExecute = $false
            $proc = [System.Diagnostics.Process]::Start($psi)
            $proc.StandardInput.Write($Prompt)
            $proc.StandardInput.Close()
            $response = $proc.StandardOutput.ReadToEnd()
            $stderr = $proc.StandardError.ReadToEnd()
            $proc.WaitForExit()
            if ($proc.ExitCode -ne 0) { throw "GitHub Copilot judge command failed: $stderr" }
            return ConvertFrom-JudgeResponse -Response $response -Model $env:EVALSCORE_GITHUB_COPILOT_MODEL
        }
        'azure-openai' {
            $endpoint = if ($env:AZURE_OPENAI_ENDPOINT) { $env:AZURE_OPENAI_ENDPOINT } else { $env:AZURE_AI_OPENAI_ENDPOINT }
            $apiKey = if ($env:AZURE_OPENAI_API_KEY) { $env:AZURE_OPENAI_API_KEY } else { $env:AZURE_AI_API_KEY }
            $apiVersion = if ($env:AZURE_OPENAI_API_VERSION) { $env:AZURE_OPENAI_API_VERSION } else { $env:AZURE_AI_API_VERSION }
            $deployment = if ($env:AZURE_OPENAI_DEPLOYMENT) { $env:AZURE_OPENAI_DEPLOYMENT } else { $env:AZURE_AI_MODEL_NAME }
            if (-not $endpoint -or -not $apiKey -or -not $apiVersion -or -not $deployment) {
                throw 'Azure OpenAI judging requires AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_API_VERSION, and AZURE_OPENAI_DEPLOYMENT.'
            }
            $endpoint = $endpoint.TrimEnd('/')
            $uri = "$endpoint/openai/deployments/$([uri]::EscapeDataString($deployment))/chat/completions?api-version=$([uri]::EscapeDataString($apiVersion))"
            $body = @{
                temperature = 0
                messages    = @(
                    @{ role = 'system'; content = 'You are a strict evaluation judge. Return only valid JSON.' }
                    @{ role = 'user'; content = $Prompt }
                )
            } | ConvertTo-Json -Depth 10
            $result = Invoke-RestMethod -Uri $uri -Method Post -Headers @{ 'api-key' = $apiKey; 'Content-Type' = 'application/json' } -Body $body
            return ConvertFrom-JudgeResponse -Response $result.choices[0].message.content -Model $deployment
        }
    }
}

function ConvertFrom-JudgeResponse {
    param(
        [Parameter(Mandatory)][string]$Response,
        [string]$Model
    )

    $trimmed = $Response.Trim()
    if ($trimmed.StartsWith('{')) {
        try {
            $json = $trimmed | ConvertFrom-Json
            if ($null -ne $json.score) {
                $score = [Math]::Max(0, [Math]::Min(100, [int][Math]::Round([double]$json.score)))
                return [PSCustomObject]@{ Score = $score; Reason = $json.reason; Model = if ($json.model) { $json.model } else { $Model } }
            }
        } catch { }
    }

    $match = [regex]::Match($trimmed, '\d+')
    if (-not $match.Success) {
        throw "Could not parse score from judge response: $($trimmed.Substring(0, [Math]::Min(120, $trimmed.Length)))"
    }
    $parsed = [int]$match.Value
    return [PSCustomObject]@{
        Score  = [Math]::Max(0, [Math]::Min(100, $parsed))
        Reason = ''
        Model  = $Model
    }
}

function Get-DeterministicMetrics {
    param(
        [Parameter(Mandatory)][EvalRow]$Row,
        [string[]]$Evaluators,
        [int]$Threshold
    )

    $metrics = @()
    $actualText = if ($Row.ActualAnswer) { $Row.ActualAnswer } else { '' }
    $expectedText = if ($Row.ExpectedAnswer) { $Row.ExpectedAnswer } else { '' }
    $actual = $actualText.Trim().ToLowerInvariant()
    $expected = $expectedText.Trim().ToLowerInvariant()
    if ($Evaluators -contains 'ExactMatch') {
        $passed = $actual -eq $expected
        $metrics += [PSCustomObject]@{ name = 'ExactMatch'; score = if ($passed) { 100 } else { 0 }; passed = $passed; reason = 'Deterministic exact-match evaluation.'; provider = 'deterministic'; scale = '0-100'; threshold = $Threshold }
    }
    if ($Evaluators -contains 'PartialMatch') {
        $passed = $expected -and ($actual.Contains($expected) -or $expected.Contains($actual))
        $metrics += [PSCustomObject]@{ name = 'PartialMatch'; score = if ($passed) { 100 } else { 0 }; passed = $passed; reason = 'Deterministic partial-match evaluation.'; provider = 'deterministic'; scale = '0-100'; threshold = $Threshold }
    }
    if ($Evaluators -contains 'Citations') {
        $passed = ($Row.Citations -and $Row.Citations.Count -gt 0) -or ($Row.SourceLocation -and $Row.ActualAnswer.ToLowerInvariant().Contains($Row.SourceLocation.ToLowerInvariant()))
        $metrics += [PSCustomObject]@{ name = 'Citations'; score = if ($passed) { 100 } else { 0 }; passed = $passed; reason = 'Citation/source reference detection.'; provider = 'deterministic'; scale = '0-100'; threshold = $Threshold }
    }
    return $metrics
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
