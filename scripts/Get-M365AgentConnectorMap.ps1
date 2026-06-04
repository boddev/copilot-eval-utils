<#
.SYNOPSIS
    Discovers Microsoft 365 Copilot connector-to-agent mappings for EvalScore.
.DESCRIPTION
    Enumerates Microsoft Graph external connections, inspects Copilot package
    declarative agent definitions for GraphConnectors capabilities, resolves the
    package agents against the WorkIQ A2A agent registry, and writes reusable
    JSON/JSONL/CSV outputs under eval-output.
#>

[CmdletBinding()]
param(
    [string]$TenantId,
    [string]$OutputPath = (Join-Path (Join-Path $PSScriptRoot '..') 'eval-output\agent-connectors.json'),
    [string]$CsvOutputPath = (Join-Path (Join-Path $PSScriptRoot '..') 'eval-output\agent-connectors.csv'),
    [string]$JsonlOutputPath = (Join-Path (Join-Path $PSScriptRoot '..') 'eval-output\agent-connectors.jsonl'),
    [string]$GraphAccessToken,
    [string]$GraphTokenCommand,
    [string]$WorkIqEndpoint = $(if ($env:WORK_IQ_A2A_ENDPOINT) { $env:WORK_IQ_A2A_ENDPOINT } else { 'https://workiq.svc.cloud.microsoft/a2a' }),
    [string]$WorkIqAccessToken = $env:WORK_IQ_A2A_ACCESS_TOKEN,
    [string]$WorkIqTokenCommand,
    [switch]$SkipA2AResolution,
    [switch]$ValidateAgentCards,
    [switch]$IncludeCatalogOnly,
    [switch]$SkipPackageCatalog
)

Set-StrictMode -Version Latest

$script:GraphBaseUrl = 'https://graph.microsoft.com'
$script:RequiredGraphScopes = @('ExternalConnection.Read.All', 'CopilotPackages.Read.All')
$script:DefaultWorkIqA2AEndpoint = 'https://workiq.svc.cloud.microsoft/a2a'
$script:WorkIqAppId = 'fdcc1f02-fc51-4226-8753-f668596af7f7'
$script:WorkIqAskScope = "$script:WorkIqAppId/WorkIQAgent.Ask"
$script:AzPowerShellClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$script:WorkIqTokenCacheDir = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.copilot-eval-utils'
$script:WorkIqRefreshCachePath = Join-Path $script:WorkIqTokenCacheDir 'workiq-a2a-refresh.dat'
$script:PackageCatalogTroubleshooting = @(
    'The Copilot package catalog API is Microsoft Graph beta and requires a work/school delegated token with CopilotPackages.Read.All or CopilotPackages.ReadWrite.All.',
    'Access to this API also requires Microsoft Agent 365 licensing and is currently available only in the global Microsoft Graph cloud.',
    'If your tenant blocks user consent or your account cannot access package management, ask an administrator to grant the Graph permission and verify your admin/package-management access.',
    'If you have multiple tenants or a stale Graph session, run Disconnect-MgGraph and rerun this script with -TenantId <tenant-id>.'
)

function ConvertTo-Array {
    param([object]$Value)
    if ($null -eq $Value) { return @() }
    if ($Value -is [string]) { return @($Value) }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [System.Collections.IDictionary]) {
        return @($Value)
    }
    return @($Value)
}

function Get-PropertyValue {
    param(
        [object]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains($Name)) { return $InputObject[$Name] }
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Get-FirstPropertyValue {
    param(
        [object]$InputObject,
        [Parameter(Mandatory)][string[]]$Names
    )

    foreach ($name in $Names) {
        $value = Get-PropertyValue -InputObject $InputObject -Name $name
        if ($null -ne $value -and [string]$value -ne '') { return $value }
    }
    return $null
}

function ConvertFrom-JwtPayload {
    param([string]$Token)

    if (-not $Token -or ($Token -split '\.').Count -lt 2) { return $null }
    $payload = ($Token -split '\.')[1].Replace('-', '+').Replace('_', '/')
    switch ($payload.Length % 4) {
        2 { $payload += '==' }
        3 { $payload += '=' }
    }

    try {
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
        return $json | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Test-GraphTokenScopes {
    param(
        [string]$Token,
        [string[]]$RequiredScopes = $script:RequiredGraphScopes
    )

    $payload = ConvertFrom-JwtPayload -Token $Token
    if (-not $payload) {
        return [PSCustomObject]@{ CanInspect = $false; MissingScopes = @(); PresentScopes = @() }
    }

    $scp = Get-PropertyValue -InputObject $payload -Name 'scp'
    $roles = ConvertTo-Array (Get-PropertyValue -InputObject $payload -Name 'roles')
    $present = @()
    if ($scp) { $present += @(([string]$scp) -split ' ') }
    $present += @($roles | ForEach-Object { [string]$_ })
    $missing = @($RequiredScopes | Where-Object { $_ -notin $present })

    return [PSCustomObject]@{
        CanInspect    = $true
        MissingScopes = $missing
        PresentScopes = $present
    }
}

function Invoke-TokenCommand {
    param([Parameter(Mandatory)][string]$Command)

    $token = (& $env:ComSpec /c $Command).Trim()
    if (-not $token) { throw "Token command returned an empty access token." }
    return $token
}

function New-GraphContext {
    param(
        [string]$TenantId,
        [string]$AccessToken,
        [string]$TokenCommand
    )

    if ($TokenCommand) {
        $AccessToken = Invoke-TokenCommand -Command $TokenCommand
    }

    if ($AccessToken) {
        $scopeCheck = Test-GraphTokenScopes -Token $AccessToken
        if ($scopeCheck.CanInspect -and $scopeCheck.MissingScopes.Count -gt 0) {
            Write-Warning ("Graph token appears to be missing scope(s): {0}" -f ($scopeCheck.MissingScopes -join ', '))
        }
        return [PSCustomObject]@{
            Mode    = 'BearerToken'
            Headers = @{ Authorization = "Bearer $AccessToken" }
            Scopes  = $scopeCheck.PresentScopes
        }
    }

    $module = Get-Module -ListAvailable -Name Microsoft.Graph.Authentication | Select-Object -First 1
    if (-not $module) {
        throw "Microsoft.Graph.Authentication is not installed. Install it with 'Install-Module Microsoft.Graph.Authentication -Scope CurrentUser', or pass -GraphAccessToken / -GraphTokenCommand."
    }

    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
    $connectParams = @{ Scopes = $script:RequiredGraphScopes; NoWelcome = $true }
    if ($TenantId) { $connectParams['TenantId'] = $TenantId }
    Connect-MgGraph @connectParams | Out-Null
    $mgContext = Get-MgContext

    return [PSCustomObject]@{
        Mode    = 'MgGraph'
        Headers = $null
        Scopes  = if ($mgContext) { @($mgContext.Scopes) } else { @() }
    }
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [int]$MaxAttempts = 4
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            return & $ScriptBlock
        } catch {
            $lastError = $_
            $statusCode = $null
            $retryAfter = $null
            if ($_.Exception.Response) {
                try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { }
                try { $retryAfter = $_.Exception.Response.Headers['Retry-After'] } catch { }
            }
            $isWrappedRateLimit = ($_.Exception.Message -match 'Too Many Requests' -or $_.Exception.Message -match '"StatusCode":429')
            $isRetryable = ($statusCode -in @(424, 429, 500, 502, 503, 504)) -or $isWrappedRateLimit
            if (-not $isRetryable -or $attempt -eq $MaxAttempts) { throw }

            $delaySeconds = [Math]::Min(30, [int][Math]::Pow(2, $attempt))
            if ($retryAfter) {
                [int]$parsedRetry = 0
                if ([int]::TryParse([string]$retryAfter, [ref]$parsedRetry) -and $parsedRetry -gt 0) {
                    $delaySeconds = $parsedRetry
                }
            }
            Start-Sleep -Seconds $delaySeconds
        }
    }

    throw $lastError
}

function Invoke-GraphApi {
    param(
        [Parameter(Mandatory)][object]$GraphContext,
        [Parameter(Mandatory)][string]$Uri
    )

    Invoke-WithRetry -ScriptBlock {
        if ($GraphContext.Mode -eq 'MgGraph') {
            return Invoke-MgGraphRequest -Method GET -Uri $Uri -ErrorAction Stop
        }
        return Invoke-RestMethod -Method GET -Uri $Uri -Headers $GraphContext.Headers -ErrorAction Stop
    }
}

function Get-GraphErrorStatusCode {
    param([object]$ErrorRecord)

    if ($null -eq $ErrorRecord) { return $null }
    $ex = $ErrorRecord.Exception
    if ($null -ne $ex) {
        $response = $null
        try { $response = $ex.Response } catch { $response = $null }
        if ($null -ne $response) {
            try { return [int]$response.StatusCode } catch { }
        }
        $message = [string]$ex.Message
        if ($message) {
            $m = [regex]::Match($message, 'HTTP/[\d\.]+\s+(\d{3})')
            if ($m.Success) { return [int]$m.Groups[1].Value }
            $m2 = [regex]::Match($message, '"StatusCode"\s*:\s*(\d{3})')
            if ($m2.Success) { return [int]$m2.Groups[1].Value }
            if ($message -match '\bForbidden\b') { return 403 }
            if ($message -match '\bUnauthorized\b') { return 401 }
            if ($message -match '\bNotFound\b') { return 404 }
            if ($message -match '\bToo Many Requests\b') { return 429 }
        }
    }
    return $null
}

function Get-GraphContextScopeText {
    param([object]$GraphContext)

    if ($null -eq $GraphContext) { return $null }
    $scopes = Get-PropertyValue -InputObject $GraphContext -Name 'Scopes'
    $scopes = @((ConvertTo-Array $scopes) | Where-Object { $_ } | ForEach-Object { [string]$_ })
    if ($scopes.Count -eq 0) { return $null }
    return ($scopes -join ', ')
}

function New-GraphPreflightFailureMessage {
    param(
        [Parameter(Mandatory)][hashtable]$Check,
        [Parameter(Mandatory)][object]$ErrorRecord,
        [Parameter(Mandatory)][object]$GraphContext
    )

    $message = "Graph preflight failed for $($Check.Name): $($ErrorRecord.Exception.Message)"
    $statusCode = Get-GraphErrorStatusCode -ErrorRecord $ErrorRecord
    $scopeText = Get-GraphContextScopeText -GraphContext $GraphContext

    if ($scopeText) {
        $message += "`nCurrent Microsoft Graph scopes: $scopeText"
    }

    if ($Check.Name -eq 'Copilot package catalog' -and $statusCode -eq 403) {
        $message += "`n`nCopilot package catalog access was forbidden. To fix this:"
        foreach ($line in $script:PackageCatalogTroubleshooting) {
            $message += "`n- $line"
        }
        $message += "`n`nManual verification:"
        $message += "`n  Disconnect-MgGraph"
        $message += "`n  Connect-MgGraph -TenantId <tenant-id> -Scopes ExternalConnection.Read.All,CopilotPackages.Read.All -NoWelcome"
        $message += "`n  Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/beta/copilot/admin/catalog/packages?`$top=1'"
        $message += "`n`nIf you just need the deployed agent IDs and cannot resolve the licensing right now, rerun this script with -SkipPackageCatalog to list WorkIQ A2A agents directly."
    }

    return $message
}

function Invoke-PagedGraphApi {
    param(
        [Parameter(Mandatory)][object]$GraphContext,
        [Parameter(Mandatory)][string]$Uri,
        [switch]$TolerateMidPageFailure
    )

    $items = @()
    $next = $Uri
    while ($next) {
        try {
            $response = Invoke-GraphApi -GraphContext $GraphContext -Uri $next
        } catch {
            if ($TolerateMidPageFailure) {
                $status = Get-GraphErrorStatusCode -ErrorRecord $_
                Write-Warning "Paged Graph request failed at $next [http-$status]; returning partial results ($($items.Count) items so far)."
                break
            }
            throw
        }
        $value = Get-PropertyValue -InputObject $response -Name 'value'
        if ($null -ne $value) {
            $items += @(ConvertTo-Array $value)
        } else {
            $items += $response
        }
        $next = Get-PropertyValue -InputObject $response -Name '@odata.nextLink'
    }
    return $items
}

function Invoke-GraphPreflight {
    param(
        [Parameter(Mandatory)][object]$GraphContext,
        [switch]$SkipPackageCatalog
    )

    $checks = @(
        @{ Name = 'Microsoft Graph profile'; Uri = "$script:GraphBaseUrl/v1.0/me" },
        @{ Name = 'External connections'; Uri = "$script:GraphBaseUrl/v1.0/external/connections?`$top=1" }
    )

    if (-not $SkipPackageCatalog) {
        $checks += @{ Name = 'Copilot package catalog'; Uri = "$script:GraphBaseUrl/beta/copilot/admin/catalog/packages?`$top=1" }
    }

    foreach ($check in $checks) {
        try {
            Invoke-GraphApi -GraphContext $GraphContext -Uri $check.Uri | Out-Null
            Write-Host "  $($check.Name): OK" -ForegroundColor Green
        } catch {
            throw (New-GraphPreflightFailureMessage -Check $check -ErrorRecord $_ -GraphContext $GraphContext)
        }
    }
}

function Get-ExternalConnections {
    param([Parameter(Mandatory)][object]$GraphContext)

    $uri = "$script:GraphBaseUrl/v1.0/external/connections?`$select=id,name,description"
    $connections = Invoke-PagedGraphApi -GraphContext $GraphContext -Uri $uri
    return @($connections | ForEach-Object {
        [PSCustomObject]@{
            connectorId          = [string](Get-PropertyValue -InputObject $_ -Name 'id')
            connectorName        = [string](Get-PropertyValue -InputObject $_ -Name 'name')
            connectorDescription = [string](Get-PropertyValue -InputObject $_ -Name 'description')
        }
    })
}

function Test-IsCopilotPackage {
    param([object]$Package)

    $hosts = ConvertTo-Array (Get-PropertyValue -InputObject $Package -Name 'supportedHosts')
    return [bool]($hosts | Where-Object { [string]$_ -ieq 'Copilot' })
}

function Test-IsPackageDeployed {
    param([object]$Package)

    $isBlocked = Get-PropertyValue -InputObject $Package -Name 'isBlocked'
    if ($true -eq $isBlocked) { return $false }

    $deployedTo = Get-PropertyValue -InputObject $Package -Name 'deployedTo'
    if ($null -ne $deployedTo -and [string]$deployedTo -ieq 'none') { return $false }

    return $true
}

function Get-CopilotPackages {
    param(
        [Parameter(Mandatory)][object]$GraphContext,
        [switch]$IncludeCatalogOnly,
        [int]$DetailDelayMs = 250
    )

    $filteredUri = "$script:GraphBaseUrl/beta/copilot/admin/catalog/packages?`$filter=supportedHosts/any(h:h eq 'Copilot')"
    $listForbidden = $false
    try {
        $packages = Invoke-PagedGraphApi -GraphContext $GraphContext -Uri $filteredUri -TolerateMidPageFailure
    } catch {
        $status = Get-GraphErrorStatusCode -ErrorRecord $_
        if ($status -eq 403) {
            Write-Warning "Copilot package list is forbidden (Agent 365 license gate). Falling back to A2A inventory only. $($_.Exception.Message)"
            $listForbidden = $true
            $packages = @()
        } else {
            Write-Warning "Copilot package server-side filter failed; falling back to client-side filtering. $($_.Exception.Message)"
            $allPackagesUri = "$script:GraphBaseUrl/beta/copilot/admin/catalog/packages"
            try {
                $packages = @(Invoke-PagedGraphApi -GraphContext $GraphContext -Uri $allPackagesUri -TolerateMidPageFailure | Where-Object { Test-IsCopilotPackage -Package $_ })
            } catch {
                $status2 = Get-GraphErrorStatusCode -ErrorRecord $_
                if ($status2 -eq 403) {
                    Write-Warning "Copilot package list (fallback) is also forbidden. $($_.Exception.Message)"
                    $listForbidden = $true
                    $packages = @()
                } else { throw }
            }
        }
    }

    $deployedPackages = @($packages | Where-Object { Test-IsPackageDeployed -Package $_ })
    $catalogOnlyPackages = @($packages | Where-Object { -not (Test-IsPackageDeployed -Package $_) })

    $deployedDetails = @()
    $catalogOnlyDetails = @()
    $packageErrors = @()
    $forbiddenCount = 0
    $detailDelaySeconds = [Math]::Max(0, [Math]::Round($DetailDelayMs / 1000.0, 2))

    foreach ($package in $deployedPackages) {
        $packageId = [string](Get-PropertyValue -InputObject $package -Name 'id')
        if (-not $packageId) { continue }
        $detailUri = "$script:GraphBaseUrl/beta/copilot/admin/catalog/packages/$([uri]::EscapeDataString($packageId))"
        try {
            $deployedDetails += Invoke-GraphApi -GraphContext $GraphContext -Uri $detailUri
        } catch {
            $status = Get-GraphErrorStatusCode -ErrorRecord $_
            $reason = if ($status -eq 403) { 'license-forbidden' } elseif ($status) { "http-$status" } else { 'error' }
            if ($status -eq 403) { $forbiddenCount++ }
            $packageErrors += [PSCustomObject]@{
                packageId          = $packageId
                packageDisplayName = [string](Get-PropertyValue -InputObject $package -Name 'displayName')
                statusCode         = $status
                reason             = $reason
                message            = [string]$_.Exception.Message
            }
        }
        if ($detailDelaySeconds -gt 0) { Start-Sleep -Seconds $detailDelaySeconds }
    }

    if ($IncludeCatalogOnly) {
        foreach ($package in $catalogOnlyPackages) {
            $packageId = [string](Get-PropertyValue -InputObject $package -Name 'id')
            if (-not $packageId) { continue }
            $detailUri = "$script:GraphBaseUrl/beta/copilot/admin/catalog/packages/$([uri]::EscapeDataString($packageId))"
            try {
                $catalogOnlyDetails += Invoke-GraphApi -GraphContext $GraphContext -Uri $detailUri
            } catch {
                $status = Get-GraphErrorStatusCode -ErrorRecord $_
                $packageErrors += [PSCustomObject]@{
                    packageId          = $packageId
                    packageDisplayName = [string](Get-PropertyValue -InputObject $package -Name 'displayName')
                    statusCode         = $status
                    reason             = if ($status) { "http-$status" } else { 'error' }
                    message            = [string]$_.Exception.Message
                    catalogOnly        = $true
                }
            }
            if ($detailDelaySeconds -gt 0) { Start-Sleep -Seconds $detailDelaySeconds }
        }
    }

    return [PSCustomObject]@{
        DeployedDetails    = @($deployedDetails)
        CatalogOnlyDetails = @($catalogOnlyDetails)
        DeployedSummary    = @($deployedPackages)
        PackageErrors      = @($packageErrors)
        DetailForbidden    = ($listForbidden -or ($forbiddenCount -gt 0 -and $deployedDetails.Count -eq 0))
        DetailPartial      = (-not $listForbidden -and $forbiddenCount -gt 0 -and $deployedDetails.Count -gt 0)
        ListForbidden      = $listForbidden
    }
}

function Save-WorkIqRefreshCache {
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$RefreshToken
    )

    if (-not (Test-Path $script:WorkIqTokenCacheDir)) {
        New-Item -ItemType Directory -Path $script:WorkIqTokenCacheDir -Force | Out-Null
    }

    $secure = ConvertTo-SecureString $RefreshToken -AsPlainText -Force
    $encrypted = ConvertFrom-SecureString $secure
    $entry = [PSCustomObject]@{ tenantId = $TenantId; refresh = $encrypted } | ConvertTo-Json -Compress
    Set-Content -LiteralPath $script:WorkIqRefreshCachePath -Value $entry -Encoding UTF8
}

function Read-WorkIqRefreshCache {
    param([Parameter(Mandatory)][string]$TenantId)

    if (-not (Test-Path $script:WorkIqRefreshCachePath)) { return $null }
    try {
        $entry = Get-Content -LiteralPath $script:WorkIqRefreshCachePath -Raw | ConvertFrom-Json
        if ($entry.tenantId -ne $TenantId) { return $null }
        $secure = ConvertTo-SecureString $entry.refresh
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        } finally {
            [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    } catch {
        return $null
    }
}

function Invoke-WorkIqRefreshGrant {
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$RefreshToken
    )

    $body = @{
        grant_type    = 'refresh_token'
        client_id     = $script:AzPowerShellClientId
        refresh_token = $RefreshToken
        scope         = "$script:WorkIqAskScope offline_access"
    }
    return Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body $body -ErrorAction Stop
}

function Invoke-WorkIqDeviceCodeFlow {
    param([Parameter(Mandatory)][string]$TenantId)

    $body = @{ client_id = $script:AzPowerShellClientId; scope = "$script:WorkIqAskScope offline_access" }
    $dc = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode" -Body $body -ErrorAction Stop

    Write-Host ''
    Write-Host '  ┌──────────────────────────────────────────────────────────────┐' -ForegroundColor Cyan
    Write-Host ("  │  Go to: {0}" -f $dc.verification_uri) -ForegroundColor Cyan
    Write-Host ("  │  Code:  {0}" -f $dc.user_code) -ForegroundColor Yellow
    Write-Host '  └──────────────────────────────────────────────────────────────┘' -ForegroundColor Cyan
    Write-Host '  Waiting for sign-in...'

    $pollBody = @{
        grant_type  = 'urn:ietf:params:oauth:grant-type:device_code'
        client_id   = $script:AzPowerShellClientId
        device_code = $dc.device_code
    }
    $deadline = (Get-Date).AddSeconds($dc.expires_in)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $dc.interval
        try {
            return Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body $pollBody -ErrorAction Stop
        } catch {
            $err = $null
            try { $err = $_.ErrorDetails.Message | ConvertFrom-Json } catch { }
            if (-not $err -or $err.error -eq 'authorization_pending' -or $err.error -eq 'slow_down') {
                continue
            }
            if ($err.error -eq 'invalid_client' -and $err.error_description -match 'AADSTS650052') {
                throw [System.Exception]::new("WORKIQ_SP_NOT_PROVISIONED: $($err.error_description)")
            }
            throw "Device code error: $($err.error) - $($err.error_description)"
        }
    }
    throw 'Device code expired before sign-in completed.'
}

function Invoke-WorkIqServicePrincipalProvision {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "Work IQ service principal ($script:WorkIqAppId) is not provisioned in this tenant and the Azure CLI (az) is not available to provision it. Run: az ad sp create --id $script:WorkIqAppId  (requires Application Administrator or Global Administrator)."
    }
    Write-Host "  Provisioning Work IQ service principal ($script:WorkIqAppId) via az..." -ForegroundColor Yellow
    & az ad sp create --id $script:WorkIqAppId --only-show-errors 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to provision Work IQ service principal. Run manually: az ad sp create --id $script:WorkIqAppId"
    }
    Write-Host '  Service principal provisioned.' -ForegroundColor Green
}

function Get-WorkIqInteractiveToken {
    param([Parameter(Mandatory)][string]$TenantId)

    $refresh = Read-WorkIqRefreshCache -TenantId $TenantId
    if ($refresh) {
        try {
            Write-Host '  Refreshing cached WorkIQ A2A token...' -ForegroundColor DarkGray
            $tok = Invoke-WorkIqRefreshGrant -TenantId $TenantId -RefreshToken $refresh
            if ($tok.refresh_token) { Save-WorkIqRefreshCache -TenantId $TenantId -RefreshToken $tok.refresh_token }
            return $tok.access_token
        } catch {
            Write-Host '  Cached refresh token is no longer valid; falling back to device-code sign-in.' -ForegroundColor DarkYellow
        }
    }

    Write-Host '  Acquiring WorkIQ A2A token via device-code sign-in...' -ForegroundColor Cyan
    try {
        $tok = Invoke-WorkIqDeviceCodeFlow -TenantId $TenantId
    } catch {
        if ($_.Exception.Message -match 'WORKIQ_SP_NOT_PROVISIONED') {
            Invoke-WorkIqServicePrincipalProvision
            $tok = Invoke-WorkIqDeviceCodeFlow -TenantId $TenantId
        } else {
            throw
        }
    }
    if ($tok.refresh_token) { Save-WorkIqRefreshCache -TenantId $TenantId -RefreshToken $tok.refresh_token }
    return $tok.access_token
}

function Get-WorkIqToken {
    param(
        [string]$AccessToken,
        [string]$TokenCommand,
        [string]$TenantId
    )

    if ($TokenCommand) { return Invoke-TokenCommand -Command $TokenCommand }
    if ($AccessToken) { return $AccessToken }
    if ($env:WORK_IQ_A2A_TOKEN_COMMAND) { return Invoke-TokenCommand -Command $env:WORK_IQ_A2A_TOKEN_COMMAND }
    if ($env:EVALSCORE_A2A_TOKEN_COMMAND) { return Invoke-TokenCommand -Command $env:EVALSCORE_A2A_TOKEN_COMMAND }
    if ($env:WORK_IQ_A2A_ACCESS_TOKEN) { return $env:WORK_IQ_A2A_ACCESS_TOKEN }

    if ($TenantId) {
        return Get-WorkIqInteractiveToken -TenantId $TenantId
    }

    throw "WorkIQ A2A resolution requires -TenantId for interactive sign-in, or -WorkIqAccessToken / -WorkIqTokenCommand / WORK_IQ_A2A_ACCESS_TOKEN / WORK_IQ_A2A_TOKEN_COMMAND / EVALSCORE_A2A_TOKEN_COMMAND. Use -SkipA2AResolution only if you do not need EvalScore-ready agent IDs."
}

function Get-WorkIqAgents {
    param(
        [Parameter(Mandatory)][string]$Endpoint,
        [string]$AccessToken,
        [string]$TokenCommand,
        [string]$TenantId
    )

    $token = Get-WorkIqToken -AccessToken $AccessToken -TokenCommand $TokenCommand -TenantId $TenantId
    $base = $Endpoint.TrimEnd('/')
    $headers = @{ Authorization = "Bearer $token"; 'X-variants' = 'feature.EnableA2AServer' }
    $response = Invoke-WithRetry -ScriptBlock {
        Invoke-RestMethod -Method GET -Uri "$base/.agents" -Headers $headers -ErrorAction Stop
    }
    $agents = Get-PropertyValue -InputObject $response -Name 'agents'
    if ($null -eq $agents) { $agents = $response }
    return @(ConvertTo-Array $agents)
}

function Get-ConnectorCapability {
    param([object]$Definition)

    $warnings = @()
    $capabilities = ConvertTo-Array (Get-PropertyValue -InputObject $Definition -Name 'capabilities')
    $graphCapabilities = @($capabilities | Where-Object {
        [string](Get-PropertyValue -InputObject $_ -Name 'name') -ieq 'GraphConnectors'
    })

    if ($graphCapabilities.Count -eq 0) {
        return [PSCustomObject]@{ Scope = 'none'; ConnectorIds = @(); Warnings = @() }
    }

    $connectorIds = @()
    foreach ($capability in $graphCapabilities) {
        $connections = Get-PropertyValue -InputObject $capability -Name 'connections'
        if ($null -eq $connections) {
            continue
        }

        foreach ($connection in (ConvertTo-Array $connections)) {
            $connectionId = Get-FirstPropertyValue -InputObject $connection -Names @('connection_id', 'connectionId')
            if ($connectionId) {
                $connectorIds += [string]$connectionId
                continue
            }
            $warnings += 'GraphConnectors connection entry did not contain connection_id or connectionId.'
        }

        $connectionIds = Get-FirstPropertyValue -InputObject $capability -Names @('connection_ids', 'connectionIds')
        foreach ($connectionId in (ConvertTo-Array $connectionIds)) {
            if ($connectionId) {
                $connectorIds += [string]$connectionId
            }
        }
    }

    $connectorIds = @($connectorIds | Where-Object { $_ } | Select-Object -Unique)
    if ($connectorIds.Count -eq 0) {
        return [PSCustomObject]@{ Scope = 'all'; ConnectorIds = @(); Warnings = $warnings }
    }

    return [PSCustomObject]@{ Scope = 'explicit'; ConnectorIds = $connectorIds; Warnings = $warnings }
}

function ConvertFrom-CopilotPackageDetail {
    param([Parameter(Mandatory)][object]$PackageDetail)

    $agents = @()
    $warnings = @()
    $errors = @()
    $skippedElementTypes = @()

    $packageId = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'id')
    $packageDisplayName = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'displayName')
    $elementDetails = ConvertTo-Array (Get-PropertyValue -InputObject $PackageDetail -Name 'elementDetails')

    foreach ($elementDetail in $elementDetails) {
        $elementType = [string](Get-PropertyValue -InputObject $elementDetail -Name 'elementType')
        if ($elementType -inotin @('declarativeAgent', 'DeclarativeAgent', 'declarativeCopilots', 'DeclarativeCopilots', 'declarativeCopilot', 'DeclarativeCopilot')) {
            if ($elementType) { $skippedElementTypes += $elementType }
            continue
        }

        foreach ($element in (ConvertTo-Array (Get-PropertyValue -InputObject $elementDetail -Name 'elements'))) {
            $elementId = [string](Get-PropertyValue -InputObject $element -Name 'id')
            $definitionText = [string](Get-PropertyValue -InputObject $element -Name 'definition')
            if (-not $definitionText) {
                $errors += [PSCustomObject]@{
                    packageId = $packageId
                    elementId = $elementId
                    message   = 'Declarative agent element has no definition JSON.'
                }
                continue
            }

            try {
                $definition = $definitionText | ConvertFrom-Json -ErrorAction Stop
            } catch {
                $errors += [PSCustomObject]@{
                    packageId = $packageId
                    elementId = $elementId
                    message   = "Failed to parse declarative agent definition JSON: $($_.Exception.Message)"
                }
                continue
            }

            $capability = Get-ConnectorCapability -Definition $definition
            foreach ($warning in $capability.Warnings) {
                $warnings += [PSCustomObject]@{
                    packageId = $packageId
                    elementId = $elementId
                    message   = $warning
                }
            }

            $agentName = [string](Get-FirstPropertyValue -InputObject $definition -Names @('name', 'displayName'))
            if (-not $agentName) { $agentName = $packageDisplayName }

            $agents += [PSCustomObject]@{
                graphPackageId                  = $packageId
                graphDeclarativeAgentElementId  = $elementId
                packageDisplayName              = $packageDisplayName
                agentName                       = $agentName
                manifestId                      = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'manifestId')
                appId                           = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'appId')
                publisher                       = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'publisher')
                supportedHosts                  = @(ConvertTo-Array (Get-PropertyValue -InputObject $PackageDetail -Name 'supportedHosts'))
                elementTypes                    = @(ConvertTo-Array (Get-PropertyValue -InputObject $PackageDetail -Name 'elementTypes'))
                availableTo                     = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'availableTo')
                deployedTo                      = [string](Get-PropertyValue -InputObject $PackageDetail -Name 'deployedTo')
                isBlocked                       = [bool](Get-PropertyValue -InputObject $PackageDetail -Name 'isBlocked')
                connectorScope                  = $capability.Scope
                connectorIds                    = @($capability.ConnectorIds)
            }
        }
    }

    return [PSCustomObject]@{
        Agents              = $agents
        Warnings            = $warnings
        Errors              = $errors
        SkippedElementTypes = @($skippedElementTypes | Select-Object -Unique)
    }
}

function Resolve-A2AAgent {
    param(
        [Parameter(Mandatory)][object]$GraphAgent,
        [object[]]$A2AAgents
    )

    if (-not $A2AAgents) {
        return [PSCustomObject]@{ Status = 'unresolved'; Agent = $null; Candidates = @(); Reason = 'No A2A agent registry was available.' }
    }

    $elementId = [string](Get-PropertyValue -InputObject $GraphAgent -Name 'graphDeclarativeAgentElementId')
    $agentName = [string](Get-PropertyValue -InputObject $GraphAgent -Name 'agentName')
    $packageDisplayName = [string](Get-PropertyValue -InputObject $GraphAgent -Name 'packageDisplayName')

    $candidates = @($A2AAgents | Where-Object {
        $id = [string](Get-PropertyValue -InputObject $_ -Name 'id')
        $agentId = [string](Get-PropertyValue -InputObject $_ -Name 'agentId')
        $name = [string](Get-PropertyValue -InputObject $_ -Name 'name')
        ($elementId -and ($id -eq $elementId -or $agentId -eq $elementId)) -or
        ($agentName -and $name -eq $agentName) -or
        ($packageDisplayName -and $name -eq $packageDisplayName)
    })

    $unique = @{}
    foreach ($candidate in $candidates) {
        $key = [string](Get-FirstPropertyValue -InputObject $candidate -Names @('agentId', 'id', 'name', 'url', 'endpoint', 'agentUrl'))
        if ($key -and -not $unique.ContainsKey($key)) {
            $unique[$key] = $candidate
        }
    }
    $resolvedCandidates = @($unique.Values)

    if ($resolvedCandidates.Count -eq 1) {
        return [PSCustomObject]@{ Status = 'resolved'; Agent = $resolvedCandidates[0]; Candidates = $resolvedCandidates; Reason = $null }
    }
    if ($resolvedCandidates.Count -gt 1) {
        return [PSCustomObject]@{ Status = 'ambiguous'; Agent = $null; Candidates = $resolvedCandidates; Reason = 'Multiple A2A agents matched by ID or name.' }
    }

    return [PSCustomObject]@{ Status = 'unresolved'; Agent = $null; Candidates = @(); Reason = 'No A2A agent matched by Graph element ID or name.' }
}

function Get-A2AAgentId {
    param([object]$A2AAgent)
    return [string](Get-FirstPropertyValue -InputObject $A2AAgent -Names @('agentId', 'id', 'name'))
}

function Get-A2AAgentUrl {
    param([object]$A2AAgent)
    return [string](Get-FirstPropertyValue -InputObject $A2AAgent -Names @('url', 'endpoint', 'agentUrl'))
}

function Test-WorkIqAgentCard {
    param(
        [Parameter(Mandatory)][string]$Endpoint,
        [Parameter(Mandatory)][string]$AgentId,
        [string]$AgentUrl,
        [string]$AccessToken,
        [string]$TokenCommand,
        [string]$TenantId
    )

    $token = Get-WorkIqToken -AccessToken $AccessToken -TokenCommand $TokenCommand -TenantId $TenantId
    $base = $Endpoint.TrimEnd('/')
    $target = if ($AgentUrl) { $AgentUrl.TrimEnd('/') } else { "$base/$([uri]::EscapeDataString($AgentId))" }
    $headers = @{ Authorization = "Bearer $token"; 'X-variants' = 'feature.EnableA2AServer' }

    try {
        Invoke-RestMethod -Method GET -Uri "$target/.well-known/agent-card.json" -Headers $headers -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

function New-AgentConnectorMap {
    param(
        [string]$TenantId,
        [object[]]$Connections,
        [object[]]$PackageDetails,
        [object[]]$A2AAgents
    )

    $warnings = @()
    $errors = @()
    $agents = @()
    $skippedElementTypes = @()

    foreach ($packageDetail in $PackageDetails) {
        $parsed = ConvertFrom-CopilotPackageDetail -PackageDetail $packageDetail
        $agents += @($parsed.Agents)
        $warnings += @($parsed.Warnings)
        $errors += @($parsed.Errors)
        $skippedElementTypes += @($parsed.SkippedElementTypes)
    }

    $connectionById = @{}
    $connectionByName = @{}
    foreach ($connection in $Connections) {
        if ($connection.connectorId) { $connectionById[$connection.connectorId] = $connection }
        if ($connection.connectorName) { $connectionByName[$connection.connectorName] = $connection }
    }

    $matches = @()
    $allConnectorAgents = @()
    $unresolvedForEvalScore = @()
    $ambiguousAgentMatches = @()
    $unresolvedConnectorReferences = @()

    foreach ($agent in $agents) {
        $resolution = Resolve-A2AAgent -GraphAgent $agent -A2AAgents $A2AAgents
        $resolvedAgentId = $null
        $resolvedAgentUrl = $null
        if ($resolution.Status -eq 'resolved') {
            $resolvedAgentId = Get-A2AAgentId -A2AAgent $resolution.Agent
            $resolvedAgentUrl = Get-A2AAgentUrl -A2AAgent $resolution.Agent
        } elseif ($resolution.Status -eq 'ambiguous') {
            $ambiguousAgentMatches += [PSCustomObject]@{
                agentName                      = $agent.agentName
                graphPackageId                 = $agent.graphPackageId
                graphDeclarativeAgentElementId = $agent.graphDeclarativeAgentElementId
                candidates                     = @($resolution.Candidates | ForEach-Object {
                    [PSCustomObject]@{
                        agentId = Get-A2AAgentId -A2AAgent $_
                        name    = [string](Get-PropertyValue -InputObject $_ -Name 'name')
                        url     = Get-A2AAgentUrl -A2AAgent $_
                    }
                })
            }
        } else {
            $unresolvedForEvalScore += [PSCustomObject]@{
                agentName                      = $agent.agentName
                graphPackageId                 = $agent.graphPackageId
                graphDeclarativeAgentElementId = $agent.graphDeclarativeAgentElementId
                reason                         = $resolution.Reason
            }
        }

        $agent | Add-Member -NotePropertyName 'agentId' -NotePropertyValue $resolvedAgentId -Force
        $agent | Add-Member -NotePropertyName 'a2aAgentUrl' -NotePropertyValue $resolvedAgentUrl -Force
        $agent | Add-Member -NotePropertyName 'evalScoreResolutionStatus' -NotePropertyValue $resolution.Status -Force

        if ($agent.connectorScope -eq 'all') {
            $allConnectorAgents += $agent
            continue
        }
        if ($agent.connectorScope -ne 'explicit') {
            continue
        }

        foreach ($connectorId in $agent.connectorIds) {
            $connection = $connectionById[$connectorId]
            $matchedBy = 'id'
            if (-not $connection -and $connectionByName.ContainsKey($connectorId)) {
                $connection = $connectionByName[$connectorId]
                $matchedBy = 'name'
                $warnings += [PSCustomObject]@{
                    packageId = $agent.graphPackageId
                    elementId = $agent.graphDeclarativeAgentElementId
                    message   = "Connector reference '$connectorId' matched an external connection by name rather than id."
                }
            }

            if (-not $connection) {
                $unresolvedConnectorReferences += [PSCustomObject]@{
                    connectorReference             = $connectorId
                    agentName                      = $agent.agentName
                    graphPackageId                 = $agent.graphPackageId
                    graphDeclarativeAgentElementId = $agent.graphDeclarativeAgentElementId
                }
                continue
            }

            if (-not $resolvedAgentId) {
                continue
            }

            $matches += [PSCustomObject]@{
                tenantId                       = $TenantId
                connectorId                    = $connection.connectorId
                connectorName                  = $connection.connectorName
                connectorMatchedBy             = $matchedBy
                agentId                        = $resolvedAgentId
                agentName                      = $agent.agentName
                graphPackageId                 = $agent.graphPackageId
                graphDeclarativeAgentElementId = $agent.graphDeclarativeAgentElementId
                manifestId                     = $agent.manifestId
                appId                          = $agent.appId
                publisher                      = $agent.publisher
            }
        }
    }

    if ($skippedElementTypes.Count -gt 0) {
        $warnings += [PSCustomObject]@{
            packageId = $null
            elementId = $null
            message   = "Skipped non-declarative package element type(s): $((@($skippedElementTypes | Select-Object -Unique)) -join ', ')."
        }
    }

    return [PSCustomObject]@{
        schemaVersion                 = 1
        tenantId                      = $TenantId
        generatedAt                   = (Get-Date).ToUniversalTime().ToString('o')
        graphEndpoints                = [PSCustomObject]@{
            externalConnections = "$script:GraphBaseUrl/v1.0/external/connections"
            copilotPackages     = "$script:GraphBaseUrl/beta/copilot/admin/catalog/packages"
        }
        connections                   = @($Connections)
        agents                        = @($agents)
        matches                       = @($matches)
        allConnectorAgents            = @($allConnectorAgents)
        unresolvedForEvalScore        = @($unresolvedForEvalScore)
        ambiguousAgentMatches         = @($ambiguousAgentMatches)
        unresolvedConnectorReferences = @($unresolvedConnectorReferences)
        catalogOnlyPackages           = @()
        warnings                      = @($warnings)
        errors                        = @($errors)
    }
}

function Write-AgentConnectorOutputs {
    param(
        [Parameter(Mandatory)][object]$Map,
        [Parameter(Mandatory)][string]$OutputPath,
        [string]$CsvOutputPath,
        [string]$JsonlOutputPath
    )

    $outputDir = Split-Path -Parent $OutputPath
    if ($outputDir) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
    $Map | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

    if ($CsvOutputPath) {
        $csvDir = Split-Path -Parent $CsvOutputPath
        if ($csvDir) { New-Item -ItemType Directory -Path $csvDir -Force | Out-Null }
        $Map.matches | Export-Csv -LiteralPath $CsvOutputPath -NoTypeInformation -Encoding UTF8
    }

    if ($JsonlOutputPath) {
        $jsonlDir = Split-Path -Parent $JsonlOutputPath
        if ($jsonlDir) { New-Item -ItemType Directory -Path $jsonlDir -Force | Out-Null }
        $lines = @($Map.matches | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 20 })
        Set-Content -LiteralPath $JsonlOutputPath -Value $lines -Encoding UTF8
    }
}

function Invoke-M365AgentConnectorMap {
    [CmdletBinding()]
    param(
        [string]$TenantId,
        [string]$OutputPath,
        [string]$CsvOutputPath,
        [string]$JsonlOutputPath,
        [string]$GraphAccessToken,
        [string]$GraphTokenCommand,
        [string]$WorkIqEndpoint,
        [string]$WorkIqAccessToken,
        [string]$WorkIqTokenCommand,
        [switch]$SkipA2AResolution,
        [switch]$ValidateAgentCards,
        [switch]$IncludeCatalogOnly,
        [switch]$SkipPackageCatalog
    )

    Write-Host 'Starting Microsoft 365 agent connector discovery...'

    $graphContext = New-GraphContext -TenantId $TenantId -AccessToken $GraphAccessToken -TokenCommand $GraphTokenCommand
    Invoke-GraphPreflight -GraphContext $graphContext -SkipPackageCatalog:$SkipPackageCatalog

    $a2aAgents = @()
    if (-not $SkipA2AResolution) {
        if (-not $WorkIqEndpoint) {
            throw 'WorkIQ A2A resolution requires -WorkIqEndpoint or WORK_IQ_A2A_ENDPOINT. Use -SkipA2AResolution only for Graph-only inventory.'
        }
        $a2aAgents = Get-WorkIqAgents -Endpoint $WorkIqEndpoint -AccessToken $WorkIqAccessToken -TokenCommand $WorkIqTokenCommand -TenantId $TenantId
        Write-Host "  WorkIQ A2A agents: $($a2aAgents.Count)" -ForegroundColor Green
    }

    $connections = Get-ExternalConnections -GraphContext $graphContext
    Write-Host "  External connections: $($connections.Count)" -ForegroundColor Green

    $packages = @()
    $packageInventory = $null
    $packageDetailForbidden = $false
    $packageDetailPartial = $false
    if (-not $SkipPackageCatalog) {
        $packageInventory = Get-CopilotPackages -GraphContext $graphContext -IncludeCatalogOnly:$IncludeCatalogOnly
        $packages = @($packageInventory.DeployedDetails)
        $packageDetailForbidden = [bool]$packageInventory.DetailForbidden
        $packageDetailPartial = [bool]$packageInventory.DetailPartial
        if ($packageDetailForbidden) {
            Write-Host '  Copilot package details: FORBIDDEN (Agent 365 license gate on all deployed packages)' -ForegroundColor Yellow
        } elseif ($packageDetailPartial) {
            Write-Host "  Copilot package details: $($packages.Count) (some packages skipped due to per-package errors)" -ForegroundColor Yellow
        } else {
            Write-Host "  Copilot package details: $($packages.Count)" -ForegroundColor Green
        }
    } else {
        Write-Host '  Copilot package catalog: SKIPPED' -ForegroundColor Yellow
    }

    $map = New-AgentConnectorMap -TenantId $TenantId -Connections $connections -PackageDetails $packages -A2AAgents $a2aAgents
    if ($packageInventory) {
        $map.catalogOnlyPackages = @($packageInventory.CatalogOnlyDetails)
        if ($packageInventory.PackageErrors -and $packageInventory.PackageErrors.Count -gt 0) {
            foreach ($pe in $packageInventory.PackageErrors) {
                $map.errors += [PSCustomObject]@{
                    packageId = $pe.packageId
                    elementId = $null
                    message   = "Package detail fetch failed for '$($pe.packageDisplayName)' [$($pe.reason)]: $($pe.message)"
                }
            }
        }
    }
    if ($SkipPackageCatalog) {
        $map.warnings += [PSCustomObject]@{
            packageId = $null
            elementId = $null
            message   = 'Copilot package catalog inspection was skipped; connector-to-agent matches are not available. The A2A agent inventory below can be used to identify EvalScore agent IDs manually.'
        }
    }
    if ($packageDetailForbidden) {
        $map.warnings += [PSCustomObject]@{
            packageId = $null
            elementId = $null
            message   = 'Copilot package detail endpoint returned 403 for all deployed packages (Agent 365 license required). Use the A2A agent inventory below and the external connections list to map manually.'
        }
    } elseif ($packageDetailPartial) {
        $map.warnings += [PSCustomObject]@{
            packageId = $null
            elementId = $null
            message   = 'Some Copilot package detail reads were skipped (per-package errors in the errors list). Partial connector-to-agent matches are still emitted; fall back to the A2A agent inventory for missing packages.'
        }
    }
    if ($SkipA2AResolution) {
        $map | Add-Member -NotePropertyName 'workIqEndpoint' -NotePropertyValue $null -Force
        $map.warnings += [PSCustomObject]@{
            packageId = $null
            elementId = $null
            message   = 'A2A resolution was skipped; matches will not contain EvalScore-ready agent IDs.'
        }
    } else {
        $map | Add-Member -NotePropertyName 'workIqEndpoint' -NotePropertyValue $WorkIqEndpoint -Force
    }

    if ($ValidateAgentCards -and -not $SkipA2AResolution) {
        foreach ($match in $map.matches) {
            $agent = @($map.agents | Where-Object { $_.agentId -eq $match.agentId } | Select-Object -First 1)
            $agentUrl = if ($agent) { [string]$agent[0].a2aAgentUrl } else { $null }
            $valid = Test-WorkIqAgentCard -Endpoint $WorkIqEndpoint -AgentId $match.agentId -AgentUrl $agentUrl -AccessToken $WorkIqAccessToken -TokenCommand $WorkIqTokenCommand -TenantId $TenantId
            if (-not $valid) {
                $map.warnings += [PSCustomObject]@{
                    packageId = $match.graphPackageId
                    elementId = $match.graphDeclarativeAgentElementId
                    message   = "Resolved A2A agent '$($match.agentId)' did not expose an agent card at validation time."
                }
            }
        }
    }

    Write-AgentConnectorOutputs -Map $map -OutputPath $OutputPath -CsvOutputPath $CsvOutputPath -JsonlOutputPath $JsonlOutputPath

    Write-Host ''
    Write-Host 'Resolved connector-agent matches:'
    if ($map.matches.Count -gt 0) {
        $map.matches | Select-Object connectorName, connectorId, agentName, agentId | Format-Table -AutoSize
        Write-Host ''
        Write-Host 'Example EvalScore commands:'
        foreach ($match in $map.matches) {
            $tenantArg = if ($TenantId) { " --tenant-id $TenantId" } else { '' }
            Write-Host "  eval-score$tenantArg --m365-agent-id $($match.agentId) --input <eval-file>"
        }
    } else {
        Write-Host '  No A2A-resolved explicit connector-agent matches were found.' -ForegroundColor Yellow
    }

    if (($SkipPackageCatalog -or $packageDetailForbidden -or $packageDetailPartial) -and -not $SkipA2AResolution -and $a2aAgents.Count -gt 0) {
        Write-Host ''
        Write-Host 'WorkIQ A2A agent inventory (use these agent IDs with eval-score --m365-agent-id):'
        $a2aAgents | ForEach-Object {
            [PSCustomObject]@{
                agentId = [string](Get-FirstPropertyValue -InputObject $_ -Names @('agentId','id','name'))
                name    = [string](Get-PropertyValue -InputObject $_ -Name 'name')
                url     = [string](Get-FirstPropertyValue -InputObject $_ -Names @('url','endpoint','agentUrl'))
            }
        } | Format-Table -AutoSize

        if ($connections.Count -gt 0) {
            Write-Host 'External connections (match agents to connectors by name):'
            $connections | Select-Object connectorName, connectorId, connectorDescription | Format-Table -AutoSize
        }
    }

    Write-Host ''
    Write-Host "JSON:  $OutputPath"
    if ($CsvOutputPath) { Write-Host "CSV:   $CsvOutputPath" }
    if ($JsonlOutputPath) { Write-Host "JSONL: $JsonlOutputPath" }

    if ($map.unresolvedForEvalScore.Count -gt 0 -or $map.ambiguousAgentMatches.Count -gt 0 -or $map.errors.Count -gt 0) {
        Write-Warning "Discovery completed with unresolved or error entries. Inspect the JSON output for details."
    }

    return $map
}

if ($MyInvocation.InvocationName -ne '.') {
    $invokeParams = @{
        TenantId           = $TenantId
        OutputPath         = $OutputPath
        CsvOutputPath      = $CsvOutputPath
        JsonlOutputPath    = $JsonlOutputPath
        GraphAccessToken   = $GraphAccessToken
        GraphTokenCommand  = $GraphTokenCommand
        WorkIqEndpoint     = $WorkIqEndpoint
        WorkIqAccessToken  = $WorkIqAccessToken
        WorkIqTokenCommand = $WorkIqTokenCommand
        SkipA2AResolution  = $SkipA2AResolution
        ValidateAgentCards = $ValidateAgentCards
        IncludeCatalogOnly = $IncludeCatalogOnly
        SkipPackageCatalog = $SkipPackageCatalog
    }
    Invoke-M365AgentConnectorMap @invokeParams | Out-Null
}
