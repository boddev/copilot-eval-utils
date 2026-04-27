BeforeAll {
    . "$PSScriptRoot\..\src\Types.ps1"
    . "$PSScriptRoot\..\src\WorkIQClient.ps1"
    . "$PSScriptRoot\..\src\Evaluator.ps1"
}

Describe 'Invoke-Evaluation' {
    It 'populates ActualAnswer using mock client' {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', ''),
            [EvalRow]::new('Capital of France?', 'Paris', 'geo.md', '')
        )
        $mockClient = { param($q) "Mock answer for: $q" }

        $result = Invoke-Evaluation -Rows $rows -AskClient $mockClient -DelayMs 0

        $result.Count | Should -Be 2
        $result[0].ActualAnswer | Should -BeLike 'Mock answer for:*'
        $result[1].ActualAnswer | Should -BeLike 'Mock answer for:*'
    }

    It 'skips rows that already have an ActualAnswer (resumability)' {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', 'Already answered'),
            [EvalRow]::new('Capital of France?', 'Paris', 'geo.md', '')
        )
        $mockClient = { param($q) "New answer" }

        $result = Invoke-Evaluation -Rows $rows -AskClient $mockClient -DelayMs 0

        $result[0].ActualAnswer | Should -Be 'Already answered'
        $result[1].ActualAnswer | Should -Be 'New answer'
    }

    It 'captures errors as [ERROR: ...] in ActualAnswer' {
        $rows = @(
            [EvalRow]::new('Failing question', 'Answer', 'source.md', '')
        )
        $errorClient = { param($q) throw "Simulated error" }

        $result = Invoke-Evaluation -Rows $rows -AskClient $errorClient -DelayMs 0

        $result[0].ActualAnswer | Should -Match '^\[ERROR:'
        $result[0].ActualAnswer | Should -Match 'Simulated error'
    }
}
