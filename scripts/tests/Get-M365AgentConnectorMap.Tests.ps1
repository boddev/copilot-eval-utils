BeforeAll {
    . "$PSScriptRoot\..\Get-M365AgentConnectorMap.ps1"

    function New-TestPackage {
        param(
            [string]$DefinitionJson,
            [string]$PackageId = 'pkg-1',
            [string]$PackageName = 'Climate Package',
            [string]$ElementId = 'graph-agent-1'
        )

        [PSCustomObject]@{
            id             = $PackageId
            displayName    = $PackageName
            manifestId     = 'manifest-1'
            appId          = 'app-1'
            publisher      = 'Contoso'
            supportedHosts = @('Copilot')
            elementTypes   = @('declarativeAgent')
            availableTo    = 'organization'
            deployedTo     = 'organization'
            isBlocked      = $false
            elementDetails = @(
                [PSCustomObject]@{
                    elementType = 'declarativeAgent'
                    elements    = @(
                        [PSCustomObject]@{
                            id         = $ElementId
                            definition = $DefinitionJson
                        }
                    )
                }
            )
        }
    }

    function New-DefinitionJson {
        param(
            [string]$Name = 'Climate Agent',
            [object[]]$Connections
        )

        $capability = @{ name = 'GraphConnectors' }
        if ($PSBoundParameters.ContainsKey('Connections')) {
            $capability.connections = $Connections
        }

        @{
            name         = $Name
            capabilities = @($capability)
        } | ConvertTo-Json -Depth 20 -Compress
    }
}

Describe 'New-AgentConnectorMap' {
    It 'creates an EvalScore-ready match for explicit connector references resolved through A2A' {
        $connections = @([PSCustomObject]@{ connectorId = 'conn-1'; connectorName = 'Climate Connector'; connectorDescription = '' })
        $definition = New-DefinitionJson -Connections @([PSCustomObject]@{ connection_id = 'conn-1' })
        $packages = @(New-TestPackage -DefinitionJson $definition)
        $a2aAgents = @([PSCustomObject]@{ id = 'a2a-agent-1'; name = 'Climate Agent'; url = 'https://workiq.example/agents/a2a-agent-1' })

        $map = New-AgentConnectorMap -TenantId 'tenant-1' -Connections $connections -PackageDetails $packages -A2AAgents $a2aAgents

        $map.matches.Count | Should -Be 1
        $map.matches[0].connectorId | Should -Be 'conn-1'
        $map.matches[0].agentId | Should -Be 'a2a-agent-1'
        $map.matches[0].agentName | Should -Be 'Climate Agent'
    }

    It 'keeps all-connector agents separate from explicit connector matches' {
        $connections = @([PSCustomObject]@{ connectorId = 'conn-1'; connectorName = 'Climate Connector'; connectorDescription = '' })
        $definition = New-DefinitionJson
        $packages = @(New-TestPackage -DefinitionJson $definition)
        $a2aAgents = @([PSCustomObject]@{ id = 'a2a-agent-1'; name = 'Climate Agent' })

        $map = New-AgentConnectorMap -TenantId 'tenant-1' -Connections $connections -PackageDetails $packages -A2AAgents $a2aAgents

        $map.matches.Count | Should -Be 0
        $map.allConnectorAgents.Count | Should -Be 1
        $map.allConnectorAgents[0].agentId | Should -Be 'a2a-agent-1'
    }

    It 'records malformed declarative agent definition JSON without aborting the run' {
        $connections = @([PSCustomObject]@{ connectorId = 'conn-1'; connectorName = 'Climate Connector'; connectorDescription = '' })
        $packages = @(New-TestPackage -DefinitionJson '{ not-json')

        $map = New-AgentConnectorMap -TenantId 'tenant-1' -Connections $connections -PackageDetails $packages -A2AAgents @()

        $map.errors.Count | Should -Be 1
        $map.errors[0].message | Should -Match 'Failed to parse declarative agent definition JSON'
    }

    It 'records connector references that are absent from the external connection inventory' {
        $connections = @([PSCustomObject]@{ connectorId = 'conn-1'; connectorName = 'Climate Connector'; connectorDescription = '' })
        $definition = New-DefinitionJson -Connections @([PSCustomObject]@{ connection_id = 'missing-conn' })
        $packages = @(New-TestPackage -DefinitionJson $definition)
        $a2aAgents = @([PSCustomObject]@{ id = 'a2a-agent-1'; name = 'Climate Agent' })

        $map = New-AgentConnectorMap -TenantId 'tenant-1' -Connections $connections -PackageDetails $packages -A2AAgents $a2aAgents

        $map.matches.Count | Should -Be 0
        $map.unresolvedConnectorReferences.Count | Should -Be 1
        $map.unresolvedConnectorReferences[0].connectorReference | Should -Be 'missing-conn'
    }

    It 'does not emit automation-ready matches when A2A resolution is ambiguous' {
        $connections = @([PSCustomObject]@{ connectorId = 'conn-1'; connectorName = 'Climate Connector'; connectorDescription = '' })
        $definition = New-DefinitionJson -Connections @([PSCustomObject]@{ connection_id = 'conn-1' })
        $packages = @(New-TestPackage -DefinitionJson $definition)
        $a2aAgents = @(
            [PSCustomObject]@{ id = 'a2a-agent-1'; name = 'Climate Agent' }
            [PSCustomObject]@{ id = 'a2a-agent-2'; name = 'Climate Agent' }
        )

        $map = New-AgentConnectorMap -TenantId 'tenant-1' -Connections $connections -PackageDetails $packages -A2AAgents $a2aAgents

        $map.matches.Count | Should -Be 0
        $map.ambiguousAgentMatches.Count | Should -Be 1
        $map.ambiguousAgentMatches[0].candidates.Count | Should -Be 2
    }
}

Describe 'New-GraphPreflightFailureMessage' {
    It 'adds package catalog remediation details for 403 responses' {
        $check = @{ Name = 'Copilot package catalog'; Uri = 'https://graph.microsoft.com/beta/copilot/admin/catalog/packages?$top=1' }
        $errorRecord = [PSCustomObject]@{
            Exception = [PSCustomObject]@{
                Message  = 'Response status code does not indicate success: Forbidden (Forbidden).'
                Response = [PSCustomObject]@{ StatusCode = [System.Net.HttpStatusCode]::Forbidden }
            }
        }
        $graphContext = [PSCustomObject]@{ Scopes = @('ExternalConnection.Read.All', 'CopilotPackages.Read.All') }

        $message = New-GraphPreflightFailureMessage -Check $check -ErrorRecord $errorRecord -GraphContext $graphContext

        $message | Should -Match 'Copilot package catalog access was forbidden'
        $message | Should -Match 'Microsoft Agent 365 licensing'
        $message | Should -Match 'Current Microsoft Graph scopes: ExternalConnection.Read.All, CopilotPackages.Read.All'
        $message | Should -Match 'Disconnect-MgGraph'
    }
}
