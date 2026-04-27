# WorkIQClient.ps1 — PowerShell WorkIQ client for the EvalScore project
# Uses the workiq MCP stdio server for persistent authenticated sessions.

$script:McpProcess = $null
$script:McpWriter = $null
$script:McpLineQueue = $null
$script:McpReaderRunspace = $null
$script:McpReaderPipeline = $null

function Build-Prompt {
    param(
        [Parameter(Mandatory)][string]$Question,
        [string]$SystemPrompt
    )

    if ($SystemPrompt) {
        return "$SystemPrompt`n`n$Question"
    }
    return $Question
}

function Resolve-SystemPrompt {
    param(
        [string]$InlinePrompt,
        [string]$PromptFilePath
    )

    if ($InlinePrompt) {
        return $InlinePrompt
    }
    if ($PromptFilePath) {
        return (Get-Content -Path $PromptFilePath -Raw).Trim()
    }
    return $null
}

function Start-WorkIQSession {
    <#
    .SYNOPSIS
        Starts a persistent workiq MCP server process. Auth happens once at startup.
    #>
    param(
        [string]$TenantId
    )

    if ($script:McpProcess -and -not $script:McpProcess.HasExited) {
        return  # Already running
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()

    # Resolve the workiq executable — prefer .cmd wrapper over .ps1 since
    # System.Diagnostics.Process cannot launch .ps1 scripts directly.
    $workiqPath = (Get-Command workiq -ErrorAction Stop).Source
    if ($workiqPath -like '*.ps1') {
        $cmdPath = $workiqPath -replace '\.ps1$', '.cmd'
        if (Test-Path $cmdPath) {
            $workiqPath = $cmdPath
        } else {
            $psi.FileName = 'cmd.exe'
            $psi.Arguments = '/c workiq mcp'
        }
    }

    if (-not $psi.FileName) {
        $psi.FileName = $workiqPath
    }

    if (-not $psi.Arguments) {
        # Note: -t (tenant) flag is NOT passed to MCP mode — it causes
        # ask_work_iq to fail. MCP handles tenant resolution internally.
        $psi.Arguments = 'mcp'
    }

    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $script:McpProcess = [System.Diagnostics.Process]::new()
    $script:McpProcess.StartInfo = $psi
    $script:McpProcess.Start() | Out-Null

    $script:McpWriter = $script:McpProcess.StandardInput

    # Drain stderr with a no-op event handler to prevent pipe buffer deadlocks
    Register-ObjectEvent -InputObject $script:McpProcess -EventName ErrorDataReceived -Action { } -SourceIdentifier 'WorkIQStderr' | Out-Null
    $script:McpProcess.BeginErrorReadLine()

    # Start a background thread that reads stdout lines into a thread-safe queue.
    # This avoids the "stream is currently in use" error from calling ReadLineAsync
    # multiple times, and prevents synchronous ReadLine from blocking the main thread.
    $script:McpLineQueue = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $stdoutReader = $script:McpProcess.StandardOutput

    $script:McpReaderRunspace = [runspacefactory]::CreateRunspace()
    $script:McpReaderRunspace.Open()

    $script:McpReaderPipeline = [powershell]::Create()
    $script:McpReaderPipeline.Runspace = $script:McpReaderRunspace
    $script:McpReaderPipeline.AddScript({
        param($reader, $queue)
        while ($true) {
            try {
                $line = $reader.ReadLine()
                if ($null -eq $line) { break }
                $trimmed = $line.Trim()
                if ($trimmed) { $queue.Enqueue($trimmed) }
            } catch {
                break
            }
        }
    }).AddArgument($stdoutReader).AddArgument($script:McpLineQueue) | Out-Null
    $script:McpReaderPipeline.BeginInvoke() | Out-Null

    # MCP initialize handshake
    $initRequest = @{
        jsonrpc = '2.0'
        id      = 0
        method  = 'initialize'
        params  = @{
            protocolVersion = '2024-11-05'
            capabilities    = @{}
            clientInfo      = @{ name = 'EvalScore'; version = '1.0.0' }
        }
    } | ConvertTo-Json -Compress -Depth 10

    $script:McpWriter.WriteLine($initRequest)
    $script:McpWriter.Flush()

    # Read the initialize response
    $initResponse = Read-McpResponse -ExpectedId 0
    if (-not $initResponse) {
        throw 'WorkIQ MCP server failed to respond to initialize request.'
    }

    # Send initialized notification
    $notification = @{
        jsonrpc = '2.0'
        method  = 'notifications/initialized'
    } | ConvertTo-Json -Compress -Depth 10

    $script:McpWriter.WriteLine($notification)
    $script:McpWriter.Flush()

    # Accept EULA via MCP (required before ask_work_iq will work)
    $script:McpRequestId = 1
    $eulaRequest = @{
        jsonrpc = '2.0'
        id      = $script:McpRequestId
        method  = 'tools/call'
        params  = @{
            name      = 'accept_eula'
            arguments = @{ eulaUrl = 'https://github.com/microsoft/work-iq-mcp' }
        }
    } | ConvertTo-Json -Compress -Depth 10

    $script:McpWriter.WriteLine($eulaRequest)
    $script:McpWriter.Flush()
    Read-McpResponse -ExpectedId $script:McpRequestId | Out-Null

    Write-Host '  WorkIQ MCP session started.' -ForegroundColor Green
}

function Read-McpResponse {
    <#
    .SYNOPSIS
        Polls the line queue for a JSON-RPC response with the expected id.
        Skips notifications and non-JSON lines. Uses a thread-safe queue
        populated by a background reader thread — no stream contention.
    #>
    param(
        [int]$ExpectedId,
        [int]$TimeoutSec = 300
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)

    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:McpProcess.HasExited) {
            throw "WorkIQ MCP server exited unexpectedly (code $($script:McpProcess.ExitCode))."
        }

        $line = $null
        if ($script:McpLineQueue.TryDequeue([ref]$line)) {
            try {
                $msg = $line | ConvertFrom-Json
            } catch {
                continue
            }

            # Skip notifications (no id field)
            if (-not ($msg.PSObject.Properties['id'])) {
                continue
            }

            if ($msg.id -eq $ExpectedId) {
                return $msg
            }
            # Not our id — discard and keep polling
            continue
        }

        # Queue empty — wait before polling again
        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for MCP response (id=$ExpectedId) after ${TimeoutSec}s."
}

function Send-WorkIQRequest {
    <#
    .SYNOPSIS
        Sends a question to WorkIQ via the persistent MCP session.
    #>
    param(
        [Parameter(Mandatory)][string]$Question,
        [int]$RequestId = 0,
        [string]$TenantId,
        [int]$TimeoutMs = 120000
    )

    # Ensure session is running
    if (-not $script:McpProcess -or $script:McpProcess.HasExited) {
        Start-WorkIQSession -TenantId $TenantId
    }

    # Auto-increment MCP request ID (EULA used id=1 during init)
    $script:McpRequestId++
    $mcpId = $script:McpRequestId

    $request = @{
        jsonrpc = '2.0'
        id      = $mcpId
        method  = 'tools/call'
        params  = @{
            name      = 'ask_work_iq'
            arguments = @{ question = $Question }
        }
    } | ConvertTo-Json -Compress -Depth 10

    $script:McpWriter.WriteLine($request)
    $script:McpWriter.Flush()

    $timeoutSec = [math]::Ceiling($TimeoutMs / 1000)
    $response = Read-McpResponse -ExpectedId $mcpId -TimeoutSec $timeoutSec

    if ($response.PSObject.Properties['error']) {
        throw "WorkIQ error: $($response.error.message)"
    }

    # Check for isError in the result (tool-level error)
    if ($response.result.PSObject.Properties['isError'] -and $response.result.isError) {
        $errText = $response.result.content[0].text
        throw "WorkIQ tool error: $errText"
    }

    # Extract text from MCP tool result
    $content = $response.result.content
    if ($content -and $content.Count -gt 0) {
        return $content[0].text
    }

    throw "WorkIQ returned an empty response for request $mcpId."
}

function Stop-WorkIQSession {
    <#
    .SYNOPSIS
        Gracefully shuts down the persistent workiq MCP server process.
    #>

    # Clean up stderr event subscription
    try { Unregister-Event -SourceIdentifier 'WorkIQStderr' -ErrorAction SilentlyContinue } catch { }

    if ($script:McpProcess -and -not $script:McpProcess.HasExited) {
        try { $script:McpWriter.Close() } catch { }

        if (-not $script:McpProcess.WaitForExit(5000)) {
            try { $script:McpProcess.Kill() } catch { }
        }

        try { $script:McpProcess.Dispose() } catch { }
    }

    # Clean up background reader
    if ($script:McpReaderPipeline) {
        try { $script:McpReaderPipeline.Stop() } catch { }
        try { $script:McpReaderPipeline.Dispose() } catch { }
    }
    if ($script:McpReaderRunspace) {
        try { $script:McpReaderRunspace.Close() } catch { }
        try { $script:McpReaderRunspace.Dispose() } catch { }
    }

    $script:McpProcess = $null
    $script:McpWriter = $null
    $script:McpLineQueue = $null
    $script:McpReaderRunspace = $null
    $script:McpReaderPipeline = $null
}

function New-MockWorkIQClient {
    param(
        [hashtable]$Responses = @{},
        [string]$DefaultResponse = 'Mock response'
    )

    $mockResponses = $Responses
    $mockDefault = $DefaultResponse

    return {
        param([string]$Question)
        if ($mockResponses.ContainsKey($Question)) {
            $mockResponses[$Question]
        }
        else {
            $mockDefault
        }
    }.GetNewClosure()
}
