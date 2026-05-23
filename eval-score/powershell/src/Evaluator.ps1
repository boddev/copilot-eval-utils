# Evaluator.ps1 — Prompt evaluation loop

function Invoke-Evaluation {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [string]$SystemPrompt,
        [string]$TenantId,
        [string]$M365AgentId,
        [string]$ConnectorId,
        [bool]$ConnectorPromptHint = $true,
        [scriptblock]$AskClient,
        [int]$DelayMs = 500
    )

    $total = $Rows.Count
    $requestCounter = 0

    for ($i = 0; $i -lt $total; $i++) {
        $row = $Rows[$i]
        $num = $i + 1

        # Skip rows that already have an answer (resumability)
        if ($row.ActualAnswer) {
            Write-Host "`rProcessing prompt $num/$total..." -NoNewline
            continue
        }

        $fullPrompt = Build-Prompt -Question $row.Prompt -SystemPrompt $SystemPrompt -ConnectorId $ConnectorId -ConnectorPromptHint $ConnectorPromptHint

        try {
            if ($AskClient) {
                $response = $AskClient.Invoke($fullPrompt)
            } elseif ($M365AgentId) {
                $a2aResponse = Send-WorkIQA2ARequest -Question $fullPrompt -AgentId $M365AgentId -ConversationId $row.ConversationId
                $response = $a2aResponse.Text
                $row.ConversationId = "$($a2aResponse.ConversationId)"
                $row.ResponseMetadata = $a2aResponse.Raw
                $row.Citations = @($a2aResponse.Citations)
            } else {
                $requestCounter++
                $sendParams = @{
                    Question  = $fullPrompt
                    RequestId = $requestCounter
                }
                if ($TenantId) { $sendParams['TenantId'] = $TenantId }
                $response = Send-WorkIQRequest @sendParams
            }
            $row.ActualAnswer = "$response".Trim()
        } catch {
            $row.ActualAnswer = "[ERROR: $($_.Exception.Message)]"
        }

        Write-Host "`rProcessing prompt $num/$total..." -NoNewline

        # Sleep between requests, skip after last
        if ($i -lt ($total - 1)) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    Write-Host ""
    return $Rows
}
