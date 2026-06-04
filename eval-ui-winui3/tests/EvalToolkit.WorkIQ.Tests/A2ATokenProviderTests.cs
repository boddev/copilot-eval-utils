using EvalToolkit.WorkIQ;

namespace EvalToolkit.WorkIQ.Tests;

public class A2ATokenProviderTests
{
    [Fact]
    public void Factory_UsesExplicitProviderFirst()
    {
        var explicitProvider = new NoopA2ATokenProvider();
        IA2ATokenProvider provider = A2ATokenProviderFactory.Create(
            "static-token",
            "echo command-token",
            "msal",
            explicitProvider);
        Assert.Same(explicitProvider, provider);
    }

    [Fact]
    public void Factory_SelectsStaticBeforeCommandAndMsal()
    {
        IA2ATokenProvider provider = A2ATokenProviderFactory.Create(
            "static-token",
            "echo command-token",
            "msal");
        Assert.IsType<StaticTokenA2ATokenProvider>(provider);
    }

    [Fact]
    public void Factory_SelectsCommandBeforeMsal()
    {
        IA2ATokenProvider provider = A2ATokenProviderFactory.Create(
            null,
            "echo command-token",
            "msal");
        Assert.IsType<TokenCommandA2ATokenProvider>(provider);
    }

    [Fact]
    public void Factory_SelectsMsalProviderWhenRequestedAndNoEarlierProviderExists()
    {
        var config = new MsalA2ATokenProviderConfig(
            ClientId: "11111111-1111-1111-1111-111111111111",
            TenantId: "22222222-2222-2222-2222-222222222222",
            Scopes: new[] { "https://example.invalid/.default" },
            CachePath: Path.Combine(Path.GetTempPath(), "auth-port-test-cache.json"),
            AllowDeviceCode: false);
        IA2ATokenProvider provider = A2ATokenProviderFactory.Create(
            accessToken: null,
            tokenCommand: null,
            authMode: "msal",
            msalConfig: config,
            msalBroker: null);
        Assert.IsType<LazyMsalA2ATokenProvider>(provider);
    }

    [Fact]
    public void Factory_SelectsNoopForAutoWithoutConfiguration()
    {
        IA2ATokenProvider provider = A2ATokenProviderFactory.Create(null, null, "auto");
        Assert.IsType<NoopA2ATokenProvider>(provider);
    }

    [Fact]
    public async Task StaticTokenProvider_ReturnsConfiguredToken()
    {
        var provider = new StaticTokenA2ATokenProvider("abc");
        Assert.Equal("abc", await provider.GetTokenAsync(cancellationToken: CancellationToken.None));
        Assert.Equal("abc", await provider.GetTokenAsync(forceRefresh: true, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task MsalProvider_WithoutConfig_ThrowsArgumentException()
    {
        // Verifies the placeholder NotSupportedException was replaced
        // with proper config validation. The auth-port slice swapped
        // the throw-stub for a real PCA-backed implementation.
        Assert.Throws<ArgumentNullException>(() => new MsalA2ATokenProvider(config: null!));
        await Task.CompletedTask;
    }
}
