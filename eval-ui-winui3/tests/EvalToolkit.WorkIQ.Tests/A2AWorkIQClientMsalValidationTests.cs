using EvalToolkit.Core;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.WorkIQ.Tests;

/// <summary>
/// Tests for the MSAL config validation path added to
/// <see cref="A2AWorkIQClient"/> by the auth-port slice. Covers
/// Opus-4.8 plan-stage review item M4 (explicit-provider injection
/// skips validation) plus parity with TS
/// <c>validateMsalConfig()</c>.
/// </summary>
[Collection("EnvVarSerial")]
public class A2AWorkIQClientMsalValidationTests : IDisposable
{
    private static readonly string[] AllEnvVars = new[]
    {
        EnvVars.WorkIqA2aEndpoint,
        EnvVars.WorkIqA2aAccessToken,
        EnvVars.WorkIqA2aTokenCommand,
        EnvVars.EvalScoreA2aTokenCommand,
        EnvVars.EvalScoreA2aAuthMode,
        EnvVars.WorkIqA2aAuthMode,
        EnvVars.EvalScoreA2aAuth,
        EnvVars.WorkIqA2aAuth,
        EnvVars.EvalScoreA2aClientId,
        EnvVars.WorkIqA2aClientId,
        EnvVars.EvalScoreA2aTenantId,
        EnvVars.WorkIqA2aTenantId,
        EnvVars.EvalScoreTenantId,
        EnvVars.TenantId,
        EnvVars.EvalScoreA2aScopes,
        EnvVars.WorkIqA2aScopes,
        EnvVars.EvalScoreA2aTokenCachePath,
        EnvVars.WorkIqA2aTokenCachePath,
        EnvVars.EvalScoreA2aAllowDeviceCode,
    };

    private readonly Dictionary<string, string?> _snapshot = new();

    public A2AWorkIQClientMsalValidationTests()
    {
        foreach (string name in AllEnvVars)
        {
            _snapshot[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> kvp in _snapshot)
        {
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StartAsync_MsalModeMissingAllFields_ThrowsExactTsMessage()
    {
        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://example.invalid",
            AuthMode = "msal",
        });

        WorkIQException ex = await Assert.ThrowsAsync<WorkIQException>(() => client.StartAsync());
        Assert.Equal(
            "MSAL A2A auth requires client ID, tenant ID, scopes. " +
            "Set EVALSCORE_A2A_CLIENT_ID, EVALSCORE_A2A_TENANT_ID or --tenant-id, and EVALSCORE_A2A_SCOPES.",
            ex.Message);
    }

    [Fact]
    public async Task StartAsync_MsalModeMissingOnlyTenant_ThrowsListingOnlyMissingField()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aClientId, "11111111-1111-1111-1111-111111111111");
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aScopes, "https://example.invalid/.default");

        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://example.invalid",
            AuthMode = "msal",
        });

        WorkIQException ex = await Assert.ThrowsAsync<WorkIQException>(() => client.StartAsync());
        Assert.Contains("MSAL A2A auth requires tenant ID.", ex.Message);
    }

    [Fact]
    public async Task StartAsync_MsalModeTenantIdFromStartTenantArg_PassesValidation()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aClientId, "11111111-1111-1111-1111-111111111111");
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aScopes, "https://example.invalid/.default");

        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://example.invalid",
            AuthMode = "msal",
        });

        // tenantId passed to StartAsync satisfies the missing-tenant
        // check (mirrors TS where --tenant-id is the highest-precedence
        // source of tenant id for MSAL config building).
        await client.StartAsync(tenantId: "22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public async Task StartAsync_MsalModeWithExplicitProvider_SkipsMsalValidation()
    {
        // Per Opus-4.8 plan-stage review (M4): explicit provider
        // injection bypasses validateMsalConfig in TS — match here.
        var explicitProvider = new StaticTokenA2ATokenProvider("explicit-token");
        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://example.invalid",
            AuthMode = "msal",
            TokenProvider = explicitProvider,
        });

        // Should NOT throw despite ClientId/TenantId/Scopes being unset.
        await client.StartAsync();
    }

    [Fact]
    public async Task StartAsync_MsalModeReturnsBeforeNoopAccessTokenCheck()
    {
        // Per Opus-4.8 plan-stage review (M4): the msal branch in TS
        // `validateConfig` returns unconditionally so when validation
        // succeeds, the access-token-required check should NOT fire.
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aClientId, "11111111-1111-1111-1111-111111111111");
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aTenantId, "22222222-2222-2222-2222-222222222222");
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aScopes, "https://example.invalid/.default");

        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://example.invalid",
            AuthMode = "msal",
        });

        await client.StartAsync();
    }

    [Fact]
    public async Task StartAsync_NonMsalModeWithoutAuth_ThrowsAccessTokenRequiredMessage()
    {
        // Pre-existing behavior: the non-msal branch still throws the
        // generic access-token-required message when no token / no
        // command is configured.
        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://example.invalid",
            AuthMode = "auto",
        });

        WorkIQException ex = await Assert.ThrowsAsync<WorkIQException>(() => client.StartAsync());
        Assert.Equal(
            "M365 agent ID targeting requires WORK_IQ_A2A_ACCESS_TOKEN, WORK_IQ_A2A_TOKEN_COMMAND, or EVALSCORE_A2A_AUTH_MODE=msal.",
            ex.Message);
    }
}
