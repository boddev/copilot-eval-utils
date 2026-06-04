using EvalToolkit.Core;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.WorkIQ.Tests;

/// <summary>
/// Tests for <see cref="MsalA2ATokenProviderConfig"/>'s env-var
/// precedence chain. Mirrors the TS <c>getMsalConfig</c> reading
/// rules from <c>eval-score/node/src/workiq-client.ts</c>.
///
/// <para>
/// All tests scrub the env vars they touch on entry and exit so
/// concurrent test runners can't poison each other.
/// </para>
/// </summary>
[Collection("EnvVarSerial")]
public class MsalA2ATokenProviderConfigTests : IDisposable
{
    private static readonly string[] AllAuthEnvVars = new[]
    {
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

    public MsalA2ATokenProviderConfigTests()
    {
        foreach (string name in AllAuthEnvVars)
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
    public void FromEnvironment_AllUnset_ReturnsEmptyConfig()
    {
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment();

        Assert.Equal(string.Empty, config.ClientId);
        Assert.Equal(string.Empty, config.TenantId);
        Assert.Empty(config.Scopes);
        Assert.Equal(MsalA2ATokenProviderConfig.DefaultLegacyCachePath(), config.CachePath);
    }

    [Fact]
    public void FromEnvironment_EvalScorePrefixWinsOverWorkIqAliases()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aClientId, "primary-client");
        Environment.SetEnvironmentVariable(EnvVars.WorkIqA2aClientId, "legacy-client");
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aTenantId, "primary-tenant");
        Environment.SetEnvironmentVariable(EnvVars.WorkIqA2aTenantId, "legacy-tenant");
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aScopes, "https://primary/.default");
        Environment.SetEnvironmentVariable(EnvVars.WorkIqA2aScopes, "https://legacy/.default");

        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment();

        Assert.Equal("primary-client", config.ClientId);
        Assert.Equal("primary-tenant", config.TenantId);
        Assert.Equal(new[] { "https://primary/.default" }, config.Scopes);
    }

    [Fact]
    public void FromEnvironment_TenantIdFallsBackThroughAllAliases()
    {
        Environment.SetEnvironmentVariable(EnvVars.TenantId, "lowest-precedence");
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.Equal("lowest-precedence", config.TenantId);

        Environment.SetEnvironmentVariable(EnvVars.EvalScoreTenantId, "above-tenant-id");
        config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.Equal("above-tenant-id", config.TenantId);

        Environment.SetEnvironmentVariable(EnvVars.WorkIqA2aTenantId, "above-evalscore-tenant");
        config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.Equal("above-evalscore-tenant", config.TenantId);

        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aTenantId, "highest-env-precedence");
        config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.Equal("highest-env-precedence", config.TenantId);
    }

    [Fact]
    public void FromEnvironment_TenantOverrideBeatsEveryEnvVar()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aTenantId, "env-tenant");
        Environment.SetEnvironmentVariable(EnvVars.TenantId, "global-tenant");
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment(tenantIdOverride: "cli-override");
        Assert.Equal("cli-override", config.TenantId);
    }

    [Fact]
    public void FromEnvironment_TenantOverrideWhitespaceIsTrimmedAndPreservedOnlyWhenNonBlank()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aTenantId, "env-tenant");
        MsalA2ATokenProviderConfig configBlank = MsalA2ATokenProviderConfig.FromEnvironment(tenantIdOverride: "   ");
        Assert.Equal("env-tenant", configBlank.TenantId);

        MsalA2ATokenProviderConfig configTrim = MsalA2ATokenProviderConfig.FromEnvironment(tenantIdOverride: "  contoso.onmicrosoft.com  ");
        Assert.Equal("contoso.onmicrosoft.com", configTrim.TenantId);
    }

    [Theory]
    [InlineData("scope-a scope-b", new[] { "scope-a", "scope-b" })]
    [InlineData("scope-a,scope-b", new[] { "scope-a", "scope-b" })]
    [InlineData("scope-a, scope-b , scope-c", new[] { "scope-a", "scope-b", "scope-c" })]
    [InlineData("scope-a\nscope-b\tscope-c", new[] { "scope-a", "scope-b", "scope-c" })]
    [InlineData("  scope-a  ", new[] { "scope-a" })]
    [InlineData("", new string[0])]
    [InlineData("  ", new string[0])]
    public void ParseScopeList_HandlesWhitespaceAndCommas(string raw, string[] expected)
    {
        IReadOnlyList<string> scopes = MsalA2ATokenProviderConfig.ParseScopeList(raw);
        Assert.Equal(expected, scopes);
    }

    [Fact]
    public void FromEnvironment_CachePathDefaultsToLegacyEvalScorePath()
    {
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.EndsWith(Path.Combine(".evalscore", "msal-a2a-cache.json"), config.CachePath);
    }

    [Fact]
    public void FromEnvironment_CachePathHonorsEvalScoreOverride()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aTokenCachePath, @"C:\custom\msal-cache.json");
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.Equal(@"C:\custom\msal-cache.json", config.CachePath);
    }

    [Fact]
    public void FromEnvironment_AllowDeviceCodeFollowsEnvOverride()
    {
        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aAllowDeviceCode, "true");
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.True(config.AllowDeviceCode);

        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aAllowDeviceCode, "false");
        config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.False(config.AllowDeviceCode);

        Environment.SetEnvironmentVariable(EnvVars.EvalScoreA2aAllowDeviceCode, "1");
        config = MsalA2ATokenProviderConfig.FromEnvironment();
        Assert.True(config.AllowDeviceCode);
    }

    [Fact]
    public void GetMissingFields_NoneSet_ReturnsAllThree()
    {
        var config = new MsalA2ATokenProviderConfig(
            ClientId: string.Empty,
            TenantId: string.Empty,
            Scopes: Array.Empty<string>(),
            CachePath: "/tmp/cache.json",
            AllowDeviceCode: false);
        Assert.Equal(new[] { "client ID", "tenant ID", "scopes" }, config.GetMissingFields());
    }

    [Fact]
    public void GetMissingFields_ClientAndScopesSet_ReturnsOnlyTenant()
    {
        var config = new MsalA2ATokenProviderConfig(
            ClientId: "client-id",
            TenantId: string.Empty,
            Scopes: new[] { "scope-a" },
            CachePath: "/tmp/cache.json",
            AllowDeviceCode: false);
        Assert.Equal(new[] { "tenant ID" }, config.GetMissingFields());
    }

    [Fact]
    public void GetMissingFields_AllSet_ReturnsEmpty()
    {
        var config = new MsalA2ATokenProviderConfig(
            ClientId: "client-id",
            TenantId: "tenant-id",
            Scopes: new[] { "scope-a" },
            CachePath: "/tmp/cache.json",
            AllowDeviceCode: false);
        Assert.Empty(config.GetMissingFields());
    }
}

/// <summary>
/// Serialize env-var-mutating tests across the whole assembly: env
/// state is process-global, so concurrent fixtures can race.
/// </summary>
#pragma warning disable CA1711 // CollectionDefinition suffix is xUnit-required naming.
[CollectionDefinition("EnvVarSerial", DisableParallelization = true)]
public class EnvVarSerialCollection
{
}
#pragma warning restore CA1711
