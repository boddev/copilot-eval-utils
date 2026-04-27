# Readers.ps1 — File readers for the EvalScore (CSV, TSV, XLSX, JSON)

# Maps common header variations to canonical EvalRow property names.
$script:HeaderAliases = @{
    'prompt'           = 'Prompt'
    'question'         = 'Prompt'
    'expected_answer'  = 'ExpectedAnswer'
    'expected answer'  = 'ExpectedAnswer'
    'expectedanswer'   = 'ExpectedAnswer'
    'actual_answer'    = 'ActualAnswer'
    'actual answer'    = 'ActualAnswer'
    'actualanswer'     = 'ActualAnswer'
    'source_location'  = 'SourceLocation'
    'source location'  = 'SourceLocation'
    'sourcelocation'   = 'SourceLocation'
    'similarity_score' = 'SimilarityScore'
    'similarity score' = 'SimilarityScore'
    'similarityscore'  = 'SimilarityScore'
}

function ConvertTo-NormalizedHeaderName {
    <#
    .SYNOPSIS
        Maps a raw column header to the canonical EvalRow property name.
    .OUTPUTS
        The canonical property name, or $null if unrecognized.
    #>
    param(
        [Parameter(Mandatory)][string]$RawHeader
    )

    $key = $RawHeader.Trim().ToLower()
    if ($script:HeaderAliases.ContainsKey($key)) {
        return $script:HeaderAliases[$key]
    }
    return $null
}

function ConvertTo-EvalRow {
    <#
    .SYNOPSIS
        Converts a PSObject record into an EvalRow using the header mapping.
    #>
    param(
        [Parameter(Mandatory)][PSObject]$Record,
        [Parameter(Mandatory)][hashtable]$HeaderMap
    )

    $row = [EvalRow]::new()

    foreach ($rawHeader in $HeaderMap.Keys) {
        $canonical = $HeaderMap[$rawHeader]
        $value = $Record.$rawHeader

        switch ($canonical) {
            'Prompt'         { $row.Prompt = if ($null -ne $value) { [string]$value } else { '' } }
            'ExpectedAnswer' { $row.ExpectedAnswer = if ($null -ne $value) { [string]$value } else { '' } }
            'SourceLocation' { $row.SourceLocation = if ($null -ne $value) { [string]$value } else { '' } }
            'ActualAnswer'   { $row.ActualAnswer = if ($null -ne $value -and "$value" -ne '') { [string]$value } else { '' } }
            'SimilarityScore' {
                if ($null -ne $value -and "$value" -ne '') {
                    $row.SimilarityScore = [int]$value
                } else {
                    $row.SimilarityScore = $null
                }
            }
        }
    }

    # Validate required fields
    $missing = @()
    if ([string]::IsNullOrWhiteSpace($row.Prompt)) { $missing += 'Prompt' }
    if ([string]::IsNullOrWhiteSpace($row.ExpectedAnswer)) { $missing += 'ExpectedAnswer' }
    if ([string]::IsNullOrWhiteSpace($row.SourceLocation)) { $missing += 'SourceLocation' }

    if ($missing.Count -gt 0) {
        throw "Row is missing required field(s): $($missing -join ', ')"
    }

    return $row
}

function Read-CsvEval {
    <#
    .SYNOPSIS
        Reads an evaluation CSV (or TSV) file and returns EvalRow objects.
        Automatically detects whether the first row is a header or data.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Delimiter = ','
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "File not found: $Path"
    }

    $lines = Get-Content -LiteralPath $Path -Encoding UTF8 | Where-Object { $_.Trim() -ne '' }
    if ($null -eq $lines -or $lines.Count -lt 1) {
        throw "CSV file is empty or has no data rows: $Path"
    }

    # Parse first line to determine if it's a header row
    $firstLineFields = ($lines[0] -split [regex]::Escape($Delimiter)) | ForEach-Object { $_.Trim().Trim('"') }

    $hasHeaderRow = $false
    foreach ($field in $firstLineFields) {
        if (ConvertTo-NormalizedHeaderName -RawHeader $field) {
            $hasHeaderRow = $true
            break
        }
    }

    if ($hasHeaderRow) {
        # First row is a header — deduplicate and parse normally
        $rawHeaders = $firstLineFields

        $seen = @{}
        $dedupedHeaders = @()
        foreach ($h in $rawHeaders) {
            $key = $h.ToLower()
            if ($seen.ContainsKey($key)) {
                $seen[$key]++
                $dedupedHeaders += "$h`_dup$($seen[$key])"
            } else {
                $seen[$key] = 0
                $dedupedHeaders += $h
            }
        }

        $fixedLines = @(($dedupedHeaders | ForEach-Object { "`"$_`"" }) -join $Delimiter)
        $fixedLines += $lines[1..($lines.Count - 1)]
        $csvContent = $fixedLines -join "`n"

        $records = $csvContent | ConvertFrom-Csv -Delimiter $Delimiter
    } else {
        # No header row — assign positional column names:
        # Column 1=prompt, 2=expected_answer, 3=source_location, 4=actual_answer
        $positionalHeaders = @('prompt', 'expected_answer', 'source_location', 'actual_answer')
        $colCount = $firstLineFields.Count
        $headers = @()
        for ($c = 0; $c -lt $colCount; $c++) {
            if ($c -lt $positionalHeaders.Count) {
                $headers += $positionalHeaders[$c]
            } else {
                $headers += "column_$($c + 1)"
            }
        }

        $fixedLines = @(($headers | ForEach-Object { "`"$_`"" }) -join $Delimiter)
        $fixedLines += $lines  # All lines are data
        $csvContent = $fixedLines -join "`n"

        $records = $csvContent | ConvertFrom-Csv -Delimiter $Delimiter
    }

    if ($null -eq $records -or @($records).Count -eq 0) {
        throw "CSV file is empty or has no data rows: $Path"
    }

    # Build header map from the first record's property names
    $firstRecord = @($records)[0]
    $rawHeaders = $firstRecord.PSObject.Properties.Name
    $headerMap = @{}

    foreach ($raw in $rawHeaders) {
        $canonical = ConvertTo-NormalizedHeaderName -RawHeader $raw
        if ($null -ne $canonical) {
            $headerMap[$raw] = $canonical
        }
    }

    # Validate required columns are present
    $resolved = $headerMap.Values | Sort-Object -Unique
    $requiredFields = @('Prompt', 'ExpectedAnswer', 'SourceLocation')
    $missingCols = $requiredFields | Where-Object { $_ -notin $resolved }

    if ($missingCols.Count -gt 0) {
        throw "Missing required column(s): $($missingCols -join ', '). Found columns: [$($rawHeaders -join ', ')]"
    }

    [EvalRow[]]$rows = foreach ($record in $records) {
        ConvertTo-EvalRow -Record $record -HeaderMap $headerMap
    }

    return $rows
}

function Read-XlsxEval {
    <#
    .SYNOPSIS
        Reads an evaluation XLSX file and returns EvalRow objects.
    #>
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "File not found: $Path"
    }

    if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
        throw "The ImportExcel module is required. Install it with: Install-Module ImportExcel -Scope CurrentUser"
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path

    # Attempt to unblock the file (removes Zone.Identifier from downloaded files)
    try { Unblock-File -LiteralPath $resolvedPath -ErrorAction SilentlyContinue } catch {}

    $records = $null
    try {
        $records = Import-Excel -Path $resolvedPath
    } catch {
        # If direct import fails, try copying to a temp file first.
        # This resolves network share, file lock, and some file stream issues.
        $tempFile = Join-Path ([System.IO.Path]::GetTempPath()) "eval-import-$([guid]::NewGuid()).xlsx"
        try {
            Copy-Item -LiteralPath $resolvedPath -Destination $tempFile -Force
            Unblock-File -LiteralPath $tempFile -ErrorAction SilentlyContinue
            $records = Import-Excel -Path $tempFile
        } catch {
            $ext = [System.IO.Path]::GetExtension($resolvedPath).ToLower()
            $msg = "Failed to read Excel file: $resolvedPath`n"
            $msg += "Error: $($_.Exception.Message)`n`n"
            $msg += "Troubleshooting:`n"
            if ($ext -eq '.xls') {
                $msg += "  - This is an older .xls format. Open it in Excel and re-save as .xlsx`n"
            }
            $msg += "  - Ensure the file is not open in another application`n"
            $msg += "  - Try opening the file in Excel, then File > Save As > .xlsx to create a clean copy`n"
            $msg += "  - If downloaded from the web, right-click > Properties > Unblock`n"
            $msg += "  - Try a different ImportExcel version: Install-Module ImportExcel -RequiredVersion 7.1.0 -Force"
            throw $msg
        } finally {
            if (Test-Path -LiteralPath $tempFile) {
                Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    if ($null -eq $records -or @($records).Count -eq 0) {
        throw "XLSX file is empty or has no data rows: $Path"
    }

    $firstRecord = @($records)[0]
    $rawHeaders = $firstRecord.PSObject.Properties.Name
    $headerMap = @{}

    foreach ($raw in $rawHeaders) {
        $canonical = ConvertTo-NormalizedHeaderName -RawHeader $raw
        if ($null -ne $canonical) {
            $headerMap[$raw] = $canonical
        }
    }

    $resolved = $headerMap.Values | Sort-Object -Unique
    $requiredFields = @('Prompt', 'ExpectedAnswer', 'SourceLocation')
    $missingCols = $requiredFields | Where-Object { $_ -notin $resolved }

    if ($missingCols.Count -gt 0) {
        throw "Missing required column(s): $($missingCols -join ', '). Found columns: [$($rawHeaders -join ', ')]"
    }

    [EvalRow[]]$rows = foreach ($record in $records) {
        ConvertTo-EvalRow -Record $record -HeaderMap $headerMap
    }

    return $rows
}

function Read-JsonEval {
    <#
    .SYNOPSIS
        Reads an evaluation JSON file (array of objects) and returns EvalRow objects.
    #>
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "File not found: $Path"
    }

    $content = Get-Content -LiteralPath $Path -Raw
    $data = $content | ConvertFrom-Json

    if ($data -isnot [System.Collections.IEnumerable] -or $data -is [string]) {
        throw "JSON file must contain an array of objects: $Path"
    }

    $records = @($data)
    if ($records.Count -eq 0) {
        throw "JSON file contains an empty array: $Path"
    }

    # Collect all unique property names across all records
    $rawHeaders = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($record in $records) {
        foreach ($prop in $record.PSObject.Properties.Name) {
            [void]$rawHeaders.Add($prop)
        }
    }

    $headerMap = @{}
    foreach ($raw in $rawHeaders) {
        $canonical = ConvertTo-NormalizedHeaderName -RawHeader $raw
        if ($null -ne $canonical) {
            $headerMap[$raw] = $canonical
        }
    }

    $resolved = $headerMap.Values | Sort-Object -Unique
    $requiredFields = @('Prompt', 'ExpectedAnswer', 'SourceLocation')
    $missingCols = $requiredFields | Where-Object { $_ -notin $resolved }

    if ($missingCols.Count -gt 0) {
        throw "Missing required column(s): $($missingCols -join ', '). Found columns: [$($rawHeaders -join ', ')]"
    }

    [EvalRow[]]$rows = foreach ($record in $records) {
        ConvertTo-EvalRow -Record $record -HeaderMap $headerMap
    }

    return $rows
}

function Read-EvalFile {
    <#
    .SYNOPSIS
        Detects file format by extension and reads evaluation data using the appropriate reader.
    .OUTPUTS
        Hashtable with Rows ([EvalRow[]]) and Format ([string]).
    #>
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "File not found: $Path"
    }

    $extension = [System.IO.Path]::GetExtension($Path).ToLower()

    switch ($extension) {
        '.csv' {
            $rows = Read-CsvEval -Path $Path
            return @{ Rows = $rows; Format = 'csv' }
        }
        '.tsv' {
            $rows = Read-CsvEval -Path $Path -Delimiter "`t"
            return @{ Rows = $rows; Format = 'tsv' }
        }
        '.xlsx' {
            $rows = Read-XlsxEval -Path $Path
            return @{ Rows = $rows; Format = 'xlsx' }
        }
        '.xls' {
            $rows = Read-XlsxEval -Path $Path
            return @{ Rows = $rows; Format = 'xlsx' }
        }
        '.json' {
            $rows = Read-JsonEval -Path $Path
            return @{ Rows = $rows; Format = 'json' }
        }
        default {
            throw "Unsupported file extension '$extension'. Supported formats: .csv, .tsv, .xlsx, .json"
        }
    }
}
