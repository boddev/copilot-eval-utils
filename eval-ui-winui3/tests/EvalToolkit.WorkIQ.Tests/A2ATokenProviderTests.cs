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
    public void Factory_SelectsMsalPlaceholderWhenRequestedAndNoEarlierProviderExists()
    {
        IA2ATokenProvider provider = A2ATokenProviderFactory.Create(null, null, "msal");
        Assert.IsType<MsalA2ATokenProvider>(provider);
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
    public async Task MsalPlaceholder_ThrowsAuthPortTodoMessage()
    {
        var provider = new MsalA2ATokenProvider();
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await provider.GetTokenAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("auth-port", exception.Message, StringComparison.Ordinal);
    }
}
