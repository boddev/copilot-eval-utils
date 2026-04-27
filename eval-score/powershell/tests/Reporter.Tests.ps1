BeforeAll {
    . "$PSScriptRoot\..\src\Types.ps1"
    . "$PSScriptRoot\..\src\Reporter.ps1"
}

Describe 'New-EvalReport' {
    BeforeAll {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', 'Four'),
            [EvalRow]::new('Capital of France?', 'Paris', 'geo.md', 'Paris')
        )
        $rows[0].SimilarityScore = 50
        $rows[1].SimilarityScore = 90

        $evalResult = [EvalResult]::new()
        $evalResult.Rows = $rows
        $evalResult.InputFile = 'test.csv'
        $evalResult.InputFormat = 'csv'
        $evalResult.Timestamp = '2024-01-01T00:00:00'
        $evalResult.SystemPrompt = 'Be concise.'

        $scoringResult = [ScoringResult]::new()
        $scoringResult.TotalQuestions = 2
        $scoringResult.AverageScore = 70.0
        $scoringResult.MinScore = 50
        $scoringResult.MaxScore = 90
        $scoringResult.PassCount = 1
        $scoringResult.FailCount = 1
        $scoringResult.PassThreshold = 70

        $script:report = New-EvalReport -EvalResult $evalResult -ScoringResult $scoringResult
    }

    It 'contains required section headings' {
        $script:report | Should -Match '# Evaluation Report'
        $script:report | Should -Match '## Summary'
        $script:report | Should -Match '## Score Distribution'
        $script:report | Should -Match '## Detailed Results'
    }

    It 'shows pass icon for scores at or above threshold' {
        $script:report | Should -Match '✅'
    }

    It 'shows fail icon for scores below threshold' {
        $script:report | Should -Match '❌'
    }
}

Describe 'New-EvalReport - system prompt truncation' {
    It 'truncates long system prompts with ellipsis' {
        $longPrompt = 'A' * 250

        $rows = @(
            [EvalRow]::new('Q1', 'A1', 'S1', 'Ans1')
        )
        $rows[0].SimilarityScore = 80

        $evalResult = [EvalResult]::new()
        $evalResult.Rows = $rows
        $evalResult.InputFile = 'test.csv'
        $evalResult.InputFormat = 'csv'
        $evalResult.SystemPrompt = $longPrompt

        $scoringResult = [ScoringResult]::new()
        $scoringResult.TotalQuestions = 1
        $scoringResult.AverageScore = 80.0
        $scoringResult.MinScore = 80
        $scoringResult.MaxScore = 80
        $scoringResult.PassCount = 1
        $scoringResult.FailCount = 0
        $scoringResult.PassThreshold = 70

        $report = New-EvalReport -EvalResult $evalResult -ScoringResult $scoringResult

        $report | Should -Match '\.\.\.'
        $report | Should -Not -Match ('A' * 250)
    }
}
