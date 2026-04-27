# Writers.ps1 — Output file writers for the EvalScore

function ConvertFrom-EvalRowToPSObject {
    param(
        [Parameter(Mandatory)][EvalRow]$Row,
        [ValidateSet('csv', 'json')][string]$Target = 'csv'
    )

    $score = if ($null -eq $Row.SimilarityScore) {
        if ($Target -eq 'json') { $null } else { '' }
    } else {
        $Row.SimilarityScore
    }

    [PSCustomObject]@{
        prompt           = $Row.Prompt
        expected_answer  = $Row.ExpectedAnswer
        source_location  = $Row.SourceLocation
        actual_answer    = $Row.ActualAnswer
        similarity_score = $score
    }
}

function Write-CsvEval {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$OutputPath,
        [string]$Delimiter = ','
    )

    $objects = $Rows | ForEach-Object { ConvertFrom-EvalRowToPSObject -Row $_ -Target 'csv' }
    $objects | Export-Csv -Path $OutputPath -Delimiter $Delimiter -NoTypeInformation
}

function Write-XlsxEval {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$OutputPath
    )

    if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
        throw "The 'ImportExcel' module is required for XLSX output. Install it with: Install-Module ImportExcel -Scope CurrentUser"
    }

    $objects = $Rows | ForEach-Object { ConvertFrom-EvalRowToPSObject -Row $_ -Target 'csv' }
    $objects | Export-Excel -Path $OutputPath -WorksheetName 'Results' -AutoSize
}

function Write-JsonEval {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $objects = $Rows | ForEach-Object { ConvertFrom-EvalRowToPSObject -Row $_ -Target 'json' }
    $json = $objects | ConvertTo-Json -Depth 10
    # ConvertTo-Json returns a bare object instead of an array for single items
    if ($Rows.Count -eq 1) {
        $json = "[$json]"
    }
    Set-Content -Path $OutputPath -Value $json -Encoding UTF8
}

function Write-EvalFile {
    param(
        [Parameter(Mandatory)][EvalRow[]]$Rows,
        [Parameter(Mandatory)][string]$InputFile,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][ValidateSet('csv', 'tsv', 'xlsx', 'json')][string]$Format
    )

    if (-not (Test-Path -Path $OutputDir)) {
        New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
    }

    $extensionMap = @{
        csv  = '.csv'
        tsv  = '.tsv'
        xlsx = '.xlsx'
        json = '.json'
    }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
    $outputFileName = "$baseName-results$($extensionMap[$Format])"
    $outputPath = Join-Path -Path $OutputDir -ChildPath $outputFileName

    switch ($Format) {
        'csv'  { Write-CsvEval  -Rows $Rows -OutputPath $outputPath }
        'tsv'  { Write-CsvEval  -Rows $Rows -OutputPath $outputPath -Delimiter "`t" }
        'xlsx' { Write-XlsxEval -Rows $Rows -OutputPath $outputPath }
        'json' { Write-JsonEval -Rows $Rows -OutputPath $outputPath }
    }

    return $outputPath
}
