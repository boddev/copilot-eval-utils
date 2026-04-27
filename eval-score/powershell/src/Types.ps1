# Types.ps1 — Shared classes for the EvalScore

class EvalRow {
    [string]$Prompt
    [string]$ExpectedAnswer
    [string]$SourceLocation
    [string]$ActualAnswer
    [Nullable[int]]$SimilarityScore

    EvalRow() {
        $this.Prompt = ''
        $this.ExpectedAnswer = ''
        $this.SourceLocation = ''
        $this.ActualAnswer = ''
        $this.SimilarityScore = $null
    }

    EvalRow([string]$prompt, [string]$expectedAnswer, [string]$sourceLocation, [string]$actualAnswer) {
        $this.Prompt = $prompt
        $this.ExpectedAnswer = $expectedAnswer
        $this.SourceLocation = $sourceLocation
        $this.ActualAnswer = $actualAnswer
        $this.SimilarityScore = $null
    }
}

class EvalResult {
    [EvalRow[]]$Rows
    [string]$InputFile
    [string]$InputFormat  # csv, tsv, xlsx, json
    [string]$Timestamp    # ISO 8601
    [string]$SystemPrompt

    EvalResult() {
        $this.Rows = @()
        $this.InputFile = ''
        $this.InputFormat = ''
        $this.Timestamp = (Get-Date -Format 'o')
        $this.SystemPrompt = ''
    }
}

class ScoringResult {
    [int]$TotalQuestions
    [double]$AverageScore
    [int]$MinScore
    [int]$MaxScore
    [int]$PassCount
    [int]$FailCount
    [int]$PassThreshold

    ScoringResult() {
        $this.TotalQuestions = 0
        $this.AverageScore = 0
        $this.MinScore = 0
        $this.MaxScore = 0
        $this.PassCount = 0
        $this.FailCount = 0
        $this.PassThreshold = 70
    }
}

class CliOptions {
    [string]$Input
    [string]$SystemPrompt
    [string]$SystemPromptFile
    [string]$OutputDir
    [int]$Threshold
    [string]$TenantId

    CliOptions() {
        $this.Input = ''
        $this.SystemPrompt = ''
        $this.SystemPromptFile = ''
        $this.OutputDir = './output'
        $this.Threshold = 70
        $this.TenantId = ''
    }
}
