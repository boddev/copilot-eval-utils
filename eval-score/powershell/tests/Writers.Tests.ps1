BeforeAll {
    . "$PSScriptRoot\..\src\Types.ps1"
    . "$PSScriptRoot\..\src\Writers.ps1"
    . "$PSScriptRoot\..\src\Readers.ps1"
}

Describe 'Write-CsvEval - round-trip' {
    It 'writes CSV and reads back with matching data' {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', 'Four'),
            [EvalRow]::new('Capital of France?', 'Paris', 'geo.md', 'Paris')
        )
        $rows[0].SimilarityScore = 85
        $rows[1].SimilarityScore = 95

        $outPath = "$TestDrive\roundtrip.csv"
        Write-CsvEval -Rows $rows -OutputPath $outPath

        $readBack = Read-CsvEval -Path $outPath

        $readBack.Count | Should -Be 2
        $readBack[0].Prompt | Should -Be 'What is 2+2?'
        $readBack[0].ExpectedAnswer | Should -Be '4'
        $readBack[0].ActualAnswer | Should -Be 'Four'
        $readBack[0].SimilarityScore | Should -Be 85
        $readBack[1].Prompt | Should -Be 'Capital of France?'
        $readBack[1].SimilarityScore | Should -Be 95
    }
}

Describe 'Write-JsonEval' {
    It 'outputs JSON with snake_case keys' {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', 'Four')
        )
        $rows[0].SimilarityScore = 90

        $outPath = "$TestDrive\output.json"
        Write-JsonEval -Rows $rows -OutputPath $outPath

        $content = Get-Content -Path $outPath -Raw
        $parsed = $content | ConvertFrom-Json

        $first = @($parsed)[0]
        $first.prompt | Should -Be 'What is 2+2?'
        $first.expected_answer | Should -Be '4'
        $first.source_location | Should -Be 'math.md'
        $first.actual_answer | Should -Be 'Four'
        $first.similarity_score | Should -Be 90
    }
}

Describe 'Write-EvalFile' {
    It 'generates output filename with -results suffix' {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', 'Four')
        )

        $outputPath = Write-EvalFile -Rows $rows -InputFile 'test.csv' -OutputDir $TestDrive -Format 'csv'

        $outputPath | Should -Match 'test-results\.csv$'
        Test-Path $outputPath | Should -BeTrue
    }
}

Describe 'Write-CsvEval - null score handling' {
    It 'writes rows with null SimilarityScore without error' {
        $rows = @(
            [EvalRow]::new('What is 2+2?', '4', 'math.md', 'Four'),
            [EvalRow]::new('Capital of France?', 'Paris', 'geo.md', 'Paris')
        )
        # SimilarityScore defaults to $null

        $outPath = "$TestDrive\nullscore.csv"
        { Write-CsvEval -Rows $rows -OutputPath $outPath } | Should -Not -Throw
        Test-Path $outPath | Should -BeTrue
    }
}
