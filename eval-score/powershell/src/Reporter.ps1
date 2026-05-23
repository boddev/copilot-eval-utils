# Reporter.ps1 — Markdown report generator for the EvalScore

function New-EvalReport {
    param(
        [Parameter(Mandatory)][EvalResult]$EvalResult,
        [Parameter(Mandatory)][ScoringResult]$ScoringResult
    )

    $sb = [System.Text.StringBuilder]::new()

    # Title
    [void]$sb.AppendLine('# Evaluation Report')
    [void]$sb.AppendLine()

    # Summary
    [void]$sb.AppendLine('## Summary')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- **Input File:** $($EvalResult.InputFile)")
    [void]$sb.AppendLine("- **Input Format:** $($EvalResult.InputFormat)")
    [void]$sb.AppendLine("- **Timestamp:** $($EvalResult.Timestamp)")

    if (-not [string]::IsNullOrEmpty($EvalResult.SystemPrompt)) {
        $truncatedPrompt = Truncate-Text -Text $EvalResult.SystemPrompt -MaxLength 200
        [void]$sb.AppendLine("- **System Prompt:** $truncatedPrompt")
    }
    if (-not [string]::IsNullOrEmpty($EvalResult.TargetType)) {
        $target = switch ($EvalResult.TargetType) {
            'm365-agent' { "M365 Agent ID: $($EvalResult.AgentId)" }
            'connector' { "Connector ID: $($EvalResult.ConnectorId)" }
            default { 'WorkIQ default' }
        }
        [void]$sb.AppendLine("- **Response Target:** $target")
    }
    [void]$sb.AppendLine("- **Judge Provider:** $($EvalResult.JudgeProvider)")
    if ($EvalResult.Evaluators -and $EvalResult.Evaluators.Count -gt 0) {
        [void]$sb.AppendLine("- **Evaluators:** $($EvalResult.Evaluators -join ', ')")
    }

    [void]$sb.AppendLine("- **Total Questions:** $($ScoringResult.TotalQuestions)")
    [void]$sb.AppendLine("- **Average Score:** $($ScoringResult.AverageScore.ToString('F1'))")
    [void]$sb.AppendLine("- **Min Score:** $($ScoringResult.MinScore)")
    [void]$sb.AppendLine("- **Max Score:** $($ScoringResult.MaxScore)")

    $passPercentage = Format-Percentage -Count $ScoringResult.PassCount -Total $ScoringResult.TotalQuestions
    [void]$sb.AppendLine("- **Pass Rate:** $($ScoringResult.PassCount)/$($ScoringResult.TotalQuestions) ($passPercentage%)")
    [void]$sb.AppendLine("- **Pass Threshold:** $($ScoringResult.PassThreshold)")
    [void]$sb.AppendLine()

    # Score Distribution
    [void]$sb.AppendLine('## Score Distribution')
    [void]$sb.AppendLine()

    $buckets = Build-ScoreBuckets -Rows $EvalResult.Rows
    $maxCount = ($buckets | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum

    foreach ($bucket in $buckets) {
        $bar = Make-Bar -Count $bucket.Count -MaxCount $maxCount
        $pct = Format-Percentage -Count $bucket.Count -Total $ScoringResult.TotalQuestions
        [void]$sb.AppendLine("$($bucket.Label) ($($bucket.Range)): $bar $($bucket.Count) ($pct%)")
    }

    [void]$sb.AppendLine()

    # Detailed Results
    [void]$sb.AppendLine('## Detailed Results')
    [void]$sb.AppendLine()

    for ($i = 0; $i -lt $EvalResult.Rows.Count; $i++) {
        $row = $EvalResult.Rows[$i]
        $n = $i + 1
        $promptPreview = Truncate-Text -Text $row.Prompt -MaxLength 60

        [void]$sb.AppendLine("### Question ${n}: $promptPreview")
        [void]$sb.AppendLine()

        if ($null -ne $row.SimilarityScore) {
            $passed = $row.SimilarityScore -ge $ScoringResult.PassThreshold
            $icon = if ($passed) { '✅' } else { '❌' }
            [void]$sb.AppendLine("**Score:** $($row.SimilarityScore)/100 $icon")
        }
        else {
            [void]$sb.AppendLine('**Score:** N/A')
        }

        if ($row.Metrics -and $row.Metrics.Count -gt 0) {
            [void]$sb.AppendLine()
            [void]$sb.AppendLine('**Metrics:**')
            foreach ($metric in $row.Metrics) {
                $value = if ($null -ne $metric.score) { "$($metric.score)/100" } else { "$($metric.passed)" }
                $icon = if ($null -eq $metric.passed) { '' } elseif ($metric.passed) { ' ✅' } else { ' ❌' }
                $provider = if ($metric.model) { "$($metric.provider)/$($metric.model)" } else { "$($metric.provider)" }
                $reason = if ($metric.reason) { " - $(Truncate-Text -Text $metric.reason -MaxLength 120)" } else { '' }
                [void]$sb.AppendLine("- **$($metric.name):** $value$icon ($provider)$reason")
            }
        }

        [void]$sb.AppendLine()
        [void]$sb.AppendLine("**Source:** $($row.SourceLocation)")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('**Prompt:**')
        [void]$sb.AppendLine("> $($row.Prompt)")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('**Expected Answer:**')
        [void]$sb.AppendLine("> $($row.ExpectedAnswer)")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('**Actual Answer:**')
        [void]$sb.AppendLine("> $($row.ActualAnswer)")
        [void]$sb.AppendLine()
    }

    return $sb.ToString()
}

function Write-EvalReport {
    param(
        [Parameter(Mandatory)][string]$Report,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][string]$InputFile
    )

    if (-not (Test-Path -Path $OutputDir)) {
        New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
    }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
    $outputPath = Join-Path -Path $OutputDir -ChildPath "$baseName-report.md"

    Set-Content -Path $outputPath -Value $Report -Encoding UTF8

    return $outputPath
}

function New-EvalHtmlReport {
    param(
        [Parameter(Mandatory)][EvalResult]$EvalResult,
        [Parameter(Mandatory)][ScoringResult]$ScoringResult
    )

    $rows = foreach ($row in $EvalResult.Rows) {
        $status = if ($row.Status) { $row.Status } elseif ($null -ne $row.SimilarityScore -and $row.SimilarityScore -ge $ScoringResult.PassThreshold) { 'pass' } else { 'fail' }
        "<tr><td>$(ConvertTo-HtmlText $status)</td><td>$($row.SimilarityScore)</td><td>$(ConvertTo-HtmlText (Truncate-Text -Text $row.Prompt -MaxLength 120))</td><td>$(ConvertTo-HtmlText (Truncate-Text -Text $row.ActualAnswer -MaxLength 200))</td></tr>"
    }

@"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>EvalScore Report</title>
<style>body{font-family:Segoe UI,Arial,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:.5rem;vertical-align:top}</style>
</head>
<body>
<h1>Evaluation Report</h1>
<p><strong>Input:</strong> $(ConvertTo-HtmlText $EvalResult.InputFile)</p>
<p><strong>Average score:</strong> $($ScoringResult.AverageScore.ToString('F1')) / 100</p>
<p><strong>Pass rate:</strong> $($ScoringResult.PassCount)/$($ScoringResult.TotalQuestions)</p>
<p><strong>Judge:</strong> $(ConvertTo-HtmlText $EvalResult.JudgeProvider)</p>
<table><thead><tr><th>Status</th><th>Score</th><th>Prompt</th><th>Actual response</th></tr></thead><tbody>
$($rows -join "`n")
</tbody></table>
</body>
</html>
"@
}

function Write-EvalHtmlReport {
    param(
        [Parameter(Mandatory)][string]$Report,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][string]$InputFile
    )

    if (-not (Test-Path -Path $OutputDir)) {
        New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
    }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
    $outputPath = Join-Path -Path $OutputDir -ChildPath "$baseName-report.html"

    Set-Content -Path $outputPath -Value $Report -Encoding UTF8

    return $outputPath
}

# --- Private helper functions ---

function Build-ScoreBuckets {
    param(
        [EvalRow[]]$Rows
    )

    $buckets = @(
        @{ Label = 'Excellent'; Range = '90-100'; Min = 90; Max = 100; Count = 0 }
        @{ Label = 'Good';      Range = '70-89';  Min = 70; Max = 89;  Count = 0 }
        @{ Label = 'Fair';      Range = '50-69';  Min = 50; Max = 69;  Count = 0 }
        @{ Label = 'Poor';      Range = '0-49';   Min = 0;  Max = 49;  Count = 0 }
    )

    foreach ($row in $Rows) {
        if ($null -eq $row.SimilarityScore) { continue }
        $score = $row.SimilarityScore
        foreach ($bucket in $buckets) {
            if ($score -ge $bucket.Min -and $score -le $bucket.Max) {
                $bucket.Count++
                break
            }
        }
    }

    return $buckets
}

function Make-Bar {
    param(
        [int]$Count,
        [int]$MaxCount
    )

    if ($MaxCount -eq 0) { return '' }
    $maxWidth = 20
    $width = [Math]::Round(($Count / $MaxCount) * $maxWidth)
    return ([string][char]0x2588) * $width
}

function Truncate-Text {
    param(
        [string]$Text,
        [int]$MaxLength
    )

    if ($Text.Length -le $MaxLength) { return $Text }
    return $Text.Substring(0, $MaxLength) + '...'
}

function Format-Percentage {
    param(
        [int]$Count,
        [int]$Total
    )

    if ($Total -eq 0) { return '0' }
    return [Math]::Round(($Count / $Total) * 100, 0).ToString('0')
}

function ConvertTo-HtmlText {
    param([string]$Text)
    if ($null -eq $Text) { return '' }
    return [System.Net.WebUtility]::HtmlEncode($Text)
}
