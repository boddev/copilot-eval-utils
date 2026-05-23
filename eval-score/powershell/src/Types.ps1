# Types.ps1 — Shared classes for the EvalScore

class EvalRow {
    [string]$Prompt
    [string]$ExpectedAnswer
    [string]$SourceLocation
    [string]$ActualAnswer
    [Nullable[int]]$SimilarityScore
    [object[]]$Metrics
    [object[]]$Citations
    [object]$ResponseMetadata
    [string]$ConversationId
    [string]$Id
    [Nullable[int]]$ItemIndex
    [Nullable[int]]$TurnIndex
    [string]$ThreadId
    [string]$ThreadName
    [string]$ThreadDescription
    [string]$Context
    [hashtable]$DocumentDefaultEvaluators
    [hashtable]$EvaluatorsMap
    [string]$EvaluatorsMode
    [string]$Status
    [object]$Error

    EvalRow() {
        $this.Prompt = ''
        $this.ExpectedAnswer = ''
        $this.SourceLocation = ''
        $this.ActualAnswer = ''
        $this.SimilarityScore = $null
        $this.Metrics = @()
        $this.Citations = @()
        $this.ResponseMetadata = $null
        $this.ConversationId = ''
        $this.Id = ''
        $this.ItemIndex = $null
        $this.TurnIndex = $null
        $this.ThreadId = ''
        $this.ThreadName = ''
        $this.ThreadDescription = ''
        $this.Context = ''
        $this.DocumentDefaultEvaluators = @{}
        $this.EvaluatorsMap = @{}
        $this.EvaluatorsMode = ''
        $this.Status = ''
        $this.Error = $null
    }

    EvalRow([string]$prompt, [string]$expectedAnswer, [string]$sourceLocation, [string]$actualAnswer) {
        $this.Prompt = $prompt
        $this.ExpectedAnswer = $expectedAnswer
        $this.SourceLocation = $sourceLocation
        $this.ActualAnswer = $actualAnswer
        $this.SimilarityScore = $null
        $this.Metrics = @()
        $this.Citations = @()
        $this.ResponseMetadata = $null
        $this.ConversationId = ''
        $this.Id = ''
        $this.ItemIndex = $null
        $this.TurnIndex = $null
        $this.ThreadId = ''
        $this.ThreadName = ''
        $this.ThreadDescription = ''
        $this.Context = ''
        $this.DocumentDefaultEvaluators = @{}
        $this.EvaluatorsMap = @{}
        $this.EvaluatorsMode = ''
        $this.Status = ''
        $this.Error = $null
    }
}

class EvalResult {
    [EvalRow[]]$Rows
    [string]$InputFile
    [string]$InputFormat  # csv, tsv, xlsx, json
    [string]$Timestamp    # ISO 8601
    [string]$SystemPrompt
    [string]$TargetType
    [string]$AgentId
    [string]$ConnectorId
    [string]$JudgeProvider
    [string[]]$Evaluators

    EvalResult() {
        $this.Rows = @()
        $this.InputFile = ''
        $this.InputFormat = ''
        $this.Timestamp = (Get-Date -Format 'o')
        $this.SystemPrompt = ''
        $this.TargetType = 'workiq'
        $this.AgentId = ''
        $this.ConnectorId = ''
        $this.JudgeProvider = 'workiq'
        $this.Evaluators = @('Relevance', 'Coherence')
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
    [string]$M365AgentId
    [string]$ConnectorId
    [string]$JudgeProvider
    [string]$Evaluators
    [int]$Concurrency
    [int]$DelayMs

    CliOptions() {
        $this.Input = ''
        $this.SystemPrompt = ''
        $this.SystemPromptFile = ''
        $this.OutputDir = './output'
        $this.Threshold = 70
        $this.TenantId = ''
        $this.M365AgentId = ''
        $this.ConnectorId = ''
        $this.JudgeProvider = 'workiq'
        $this.Evaluators = 'Relevance,Coherence'
        $this.Concurrency = 1
        $this.DelayMs = 500
    }
}
