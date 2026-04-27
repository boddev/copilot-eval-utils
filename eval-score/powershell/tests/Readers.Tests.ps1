BeforeAll {
    . "$PSScriptRoot\..\src\Types.ps1"
    . "$PSScriptRoot\..\src\Readers.ps1"
}

Describe 'Read-CsvEval' {
    It 'reads CSV with standard headers and returns correct row count and values' {
        $csv = @(
            'prompt,expected_answer,source_location,actual_answer'
            '"What is 2+2?","4","math.md",""'
            '"Capital of France?","Paris","geo.md",""'
        )
        Set-Content -Path "$TestDrive\test.csv" -Value $csv

        $rows = Read-CsvEval -Path "$TestDrive\test.csv"

        $rows.Count | Should -Be 2
        $rows[0].Prompt | Should -Be 'What is 2+2?'
        $rows[0].ExpectedAnswer | Should -Be '4'
        $rows[0].SourceLocation | Should -Be 'math.md'
        $rows[1].Prompt | Should -Be 'Capital of France?'
        $rows[1].ExpectedAnswer | Should -Be 'Paris'
        $rows[1].SourceLocation | Should -Be 'geo.md'
    }

    It 'reads TSV with tab delimiter' {
        $tsv = @(
            "prompt`texpected_answer`tsource_location`tactual_answer"
            "What is 2+2?`t4`tmath.md`t"
            "Capital of France?`tParis`tgeo.md`t"
        )
        Set-Content -Path "$TestDrive\test.tsv" -Value $tsv

        $rows = Read-CsvEval -Path "$TestDrive\test.tsv" -Delimiter "`t"

        $rows.Count | Should -Be 2
        $rows[0].Prompt | Should -Be 'What is 2+2?'
        $rows[0].ExpectedAnswer | Should -Be '4'
        $rows[1].Prompt | Should -Be 'Capital of France?'
        $rows[1].ExpectedAnswer | Should -Be 'Paris'
    }
}

Describe 'Read-JsonEval' {
    It 'reads JSON with camelCase keys' {
        $json = @(
            [PSCustomObject]@{ prompt = 'What is 2+2?'; expectedAnswer = '4'; sourceLocation = 'math.md'; actualAnswer = '' }
            [PSCustomObject]@{ prompt = 'Capital of France?'; expectedAnswer = 'Paris'; sourceLocation = 'geo.md'; actualAnswer = '' }
        ) | ConvertTo-Json -Depth 10
        Set-Content -Path "$TestDrive\test.json" -Value $json

        $rows = Read-JsonEval -Path "$TestDrive\test.json"

        $rows.Count | Should -Be 2
        $rows[0].Prompt | Should -Be 'What is 2+2?'
        $rows[0].ExpectedAnswer | Should -Be '4'
        $rows[0].SourceLocation | Should -Be 'math.md'
        $rows[1].Prompt | Should -Be 'Capital of France?'
        $rows[1].ExpectedAnswer | Should -Be 'Paris'
    }
}

Describe 'ConvertTo-NormalizedHeaderName' {
    It 'maps alternate header names to canonical names' {
        $csv = @(
            'Question,Expected Answer,Source Location,Actual Answer'
            '"What is 2+2?","4","math.md",""'
        )
        Set-Content -Path "$TestDrive\alt.csv" -Value $csv

        $rows = Read-CsvEval -Path "$TestDrive\alt.csv"

        $rows.Count | Should -Be 1
        $rows[0].Prompt | Should -Be 'What is 2+2?'
        $rows[0].ExpectedAnswer | Should -Be '4'
        $rows[0].SourceLocation | Should -Be 'math.md'
    }

    It 'returns $null for unrecognized headers' {
        $result = ConvertTo-NormalizedHeaderName -RawHeader 'unknown_column'
        $result | Should -BeNullOrEmpty
    }
}

Describe 'Read-CsvEval - missing columns' {
    It 'throws when prompt column is missing' {
        $csv = @(
            'expected_answer,source_location,actual_answer'
            '"4","math.md",""'
        )
        Set-Content -Path "$TestDrive\missing.csv" -Value $csv

        { Read-CsvEval -Path "$TestDrive\missing.csv" } | Should -Throw '*Missing required column*'
    }
}

Describe 'Read-EvalFile' {
    It 'detects CSV format from extension' {
        $csv = @(
            'prompt,expected_answer,source_location,actual_answer'
            '"What is 2+2?","4","math.md",""'
        )
        Set-Content -Path "$TestDrive\detect.csv" -Value $csv

        $result = Read-EvalFile -Path "$TestDrive\detect.csv"

        $result.Format | Should -Be 'csv'
        $result.Rows.Count | Should -Be 1
    }

    It 'throws for unsupported file extension' {
        Set-Content -Path "$TestDrive\test.txt" -Value 'some content'

        { Read-EvalFile -Path "$TestDrive\test.txt" } | Should -Throw '*Unsupported file extension*'
    }
}
