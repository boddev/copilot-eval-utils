BeforeAll {
    . "$PSScriptRoot\..\src\Types.ps1"
    . "$PSScriptRoot\..\src\WorkIQClient.ps1"
    . "$PSScriptRoot\..\src\Scorer.ps1"
}

Describe 'Invoke-Scoring' {
    It 'assigns score from mock scorer response' {
        $rows = @(
            [EvalRow]::new('Q1', 'Expected', 'source.md', 'Actual')
        )
        $mockScorer = { param($q) "85" }

        $result = Invoke-Scoring -Rows $rows -AskClient $mockScorer -DelayMs 0

        $result[0].SimilarityScore | Should -Be 85
    }

    It 'clamps score above 100 to 100' {
        $rows = @(
            [EvalRow]::new('Q1', 'Expected', 'source.md', 'Actual')
        )
        $mockScorer = { param($q) "150" }

        $result = Invoke-Scoring -Rows $rows -AskClient $mockScorer -DelayMs 0

        $result[0].SimilarityScore | Should -Be 100
    }

    It 'extracts numeric score from non-numeric response' {
        $rows = @(
            [EvalRow]::new('Q1', 'Expected', 'source.md', 'Actual')
        )
        $mockScorer = { param($q) "about 75 percent" }

        $result = Invoke-Scoring -Rows $rows -AskClient $mockScorer -DelayMs 0

        $result[0].SimilarityScore | Should -Be 75
    }

    It 'assigns score 0 for error answers' {
        $rows = @(
            [EvalRow]::new('Q1', 'Expected', 'source.md', '[ERROR: timeout]')
        )
        $mockScorer = { param($q) "85" }

        $result = Invoke-Scoring -Rows $rows -AskClient $mockScorer -DelayMs 0

        $result[0].SimilarityScore | Should -Be 0
    }
}

Describe 'Get-ScoringResult' {
    It 'computes correct statistics for known scores' {
        $rows = @(
            [EvalRow]::new('Q1', 'A1', 'S1', 'Ans1'),
            [EvalRow]::new('Q2', 'A2', 'S2', 'Ans2'),
            [EvalRow]::new('Q3', 'A3', 'S3', 'Ans3')
        )
        $rows[0].SimilarityScore = 90
        $rows[1].SimilarityScore = 60
        $rows[2].SimilarityScore = 80

        $result = Get-ScoringResult -Rows $rows -PassThreshold 70

        $result.TotalQuestions | Should -Be 3
        $result.AverageScore | Should -Be 76.7
        $result.MinScore | Should -Be 60
        $result.MaxScore | Should -Be 90
        $result.PassCount | Should -Be 2
        $result.FailCount | Should -Be 1
        $result.PassThreshold | Should -Be 70
    }
}
